using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace BSVRManager.Core;

/// <summary>BeatMods API client (v1): lists mods per game version with dependencies.</summary>
public class BeatModsClient
{
    public class ModInfo
    {
        public string Name;
        public string Version;
        public string GameVersion;
        public string Author;
        public string Description;
        public string DownloadUrl;   // absolute
        public string Status;
        public string Category = "Other";
        public bool Required;
        public string Link;
        public List<(string name, string version)> Dependencies = new();
        public string Id => Name.ToLowerInvariant().Replace(" ", "");
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static BeatModsClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BSVRManager/1.0 (Beat Saber VR mod manager)");
    }

    static string Base => "https://beatmods.com";

    public List<ModInfo> GetMods(string gameVersion)
    {
        var json = Http.GetStringAsync($"{Base}/api/v1/mod?gameVersion={Uri.EscapeDataString(gameVersion)}")
            .ConfigureAwait(false).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var list = new List<ModInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            try
            {
                var m = new ModInfo
                {
                    Name = S(e, "name"),
                    Version = S(e, "version"),
                    GameVersion = S(e, "gameVersion"),
                    Author = S(e, "author"),
                    Description = S(e, "description"),
                    Status = S(e, "status"),
                    Category = string.IsNullOrWhiteSpace(S(e, "category")) ? "Other" : S(e, "category"),
                    Required = e.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True,
                    Link = S(e, "link"),
                };
                if (e.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in dl.EnumerateArray())
                    {
                        var type = S(d, "type");
                        var url = S(d, "url");
                        if (type == "universal" || type == "steam")
                        {
                            m.DownloadUrl = url.StartsWith("http") ? url : Base + url;
                            break;
                        }
                        if (m.DownloadUrl == null && !string.IsNullOrEmpty(url))
                            m.DownloadUrl = url.StartsWith("http") ? url : Base + url;
                    }
                }
                if (e.TryGetProperty("author", out var author))
                {
                    // author can be a plain string or an object with username
                    m.Author = author.ValueKind == JsonValueKind.String ? author.GetString()
                             : author.TryGetProperty("username", out var un) ? un.GetString() : "";
                }
                if (e.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
                    foreach (var d in deps.EnumerateArray())
                        m.Dependencies.Add((S(d, "name"), S(d, "version")));
                if (!string.IsNullOrEmpty(m.Name) && m.DownloadUrl != null && m.Status == "approved")
                    list.Add(m);
            }
            catch { }
        }
        return list;
    }

    static string S(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
}

/// <summary>
/// Installs/uninstalls/enables/disables BSIPA mods inside a profile.
/// Disable = rename .dll → .dll.disabled (BSManager convention; BSIPA skips those).
/// Uninstall removes the exact files recorded at install time, then prunes dependencies.
/// </summary>
public class ModInstaller
{
    readonly Settings _settings;
    readonly BeatModsClient _beatmods = new();
    public Action<string> OnStatus;

    static string InstallsPath(Profile p) => System.IO.Path.Combine(p.Path, "UserData", "BSVRManager", "modinstalls.json");
    static string BsipaMarker(Profile p) => System.IO.Path.Combine(p.Path, "winhttp.dll");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public ModInstaller(Settings settings) { _settings = settings; }

    // ---------- install ----------

    public void Install(Profile profile, BeatModsClient.ModInfo mod)
    {
        var deps = CollectMissing(profile, mod, new List<string>(), out var order);
        int i = 0;
        foreach (var m in order)
        {
            i++;
            OnStatus?.Invoke($"Downloading {m.Name} {m.Version} ({i}/{order.Count})…");
            var zip = DownloadZip(m);
            OnStatus?.Invoke($"Extracting {m.Name}…");
            var before = SnapshotFiles(profile.Path);
            ExtractOver(profile, zip);
            RecordInstall(profile, m, SnapshotFiles(profile.Path).Except(before).ToList());
            try { File.Delete(zip); } catch { }
        }
        OnStatus?.Invoke($"Installed {mod.Name} {mod.Version} (+{order.Count - 1} dependencies).");
    }

    static HashSet<string> SnapshotFiles(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return set;
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            set.Add(System.IO.Path.GetRelativePath(root, f));
        return set;
    }

    /// <summary>Installs the latest approved BSIPA for the profile's game version if it isn't already present.</summary>
    public void InstallBsipa(Profile profile)
    {
        if (File.Exists(BsipaMarker(profile)))
        {
            OnStatus?.Invoke("BSIPA is already installed in this profile.");
            return;
        }
        var mods = _beatmods.GetMods(profile.GameVersion());
        var bsipa = mods.FirstOrDefault(m => m.Name == "BSIPA");
        if (bsipa == null)
        {
            OnStatus?.Invoke("BSIPA not found on BeatMods for game version " + profile.GameVersion());
            return;
        }
        Install(profile, bsipa);
    }

    List<BeatModsClient.ModInfo> CollectMissing(Profile profile, BeatModsClient.ModInfo mod, List<string> seen, out List<BeatModsClient.ModInfo> order)
    {
        var result = new List<BeatModsClient.ModInfo>();
        var installed = ListInstalled(profile).Select(i => i.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Visit(BeatModsClient.ModInfo m)
        {
            if (seen.Contains(m.Name)) return;
            seen.Add(m.Name);
            foreach (var (depName, _) in m.Dependencies)
            {
                if (installed.Contains(depName)) continue;
                var all = _beatmods.GetMods(profile.GameVersion());
                var dep = all.FirstOrDefault(x => x.Name == depName);
                if (dep != null) Visit(dep);
            }
            if (!installed.Contains(m.Name) || m.Name == "BSIPA")
            {
                if (!result.Contains(m)) result.Add(m);
                installed.Add(m.Name);
            }
        }
        Visit(mod);
        order = result;
        return order;
    }

    string DownloadZip(BeatModsClient.ModInfo mod)
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bsvr-" + Guid.NewGuid().ToString("N") + ".zip");
        var bytes = Http.GetByteArrayAsync(mod.DownloadUrl).ConfigureAwait(false).GetAwaiter().GetResult();
        File.WriteAllBytes(tmp, bytes);
        return tmp;
    }

    static void ExtractOver(Profile profile, string zipPath)
    {
        Directory.CreateDirectory(profile.Path);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) // directory entry
                continue;
            var target = System.IO.Path.Combine(profile.Path, entry.FullName.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var insideRoot = System.IO.Path.GetFullPath(target).StartsWith(System.IO.Path.GetFullPath(profile.Path), StringComparison.OrdinalIgnoreCase);
            if (!insideRoot) continue; // zip-slip guard
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            entry.ExtractToFile(target, true);
        }
    }

    // ---------- install records + BSIPA manifests ----------

    public class InstallRecord
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public List<string> Files { get; set; } = new();
    }

    static List<InstallRecord> ListInstalledRecs(Profile profile)
    {
        try
        {
            if (File.Exists(InstallsPath(profile)))
                return JsonSerializer.Deserialize<List<InstallRecord>>(File.ReadAllText(InstallsPath(profile))) ?? new();
        }
        catch { }
        return new();
    }

    static void SaveRecs(Profile p, List<InstallRecord> recs)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(InstallsPath(p)));
        File.WriteAllText(InstallsPath(p), JsonSerializer.Serialize(recs, new JsonSerializerOptions { WriteIndented = true }));
    }

    void RecordInstall(Profile profile, BeatModsClient.ModInfo mod, List<string> addedFiles)
    {
        var recs = ListInstalledRecs(profile);
        var rec = recs.FirstOrDefault(r => r.Name == mod.Name);
        if (rec == null) { rec = new InstallRecord { Name = mod.Name }; recs.Add(rec); }
        rec.Version = mod.Version;
        rec.Files = addedFiles;
        SaveRecs(profile, recs);
    }

    public List<(string name, string version)> ListInstalled(Profile profile)
        => ListInstalledRecs(profile).Select(r => (r.Name, r.Version)).ToList();

    // ---------- enable / disable / uninstall ----------

    /// <summary>Parses a BSIPA manifest (Plugins/*.manifest): name, version and the file list it owns.</summary>
    public class BsipaManifest
    {
        public string Name = "";
        public string Id = "";
        public string Version = "";
        public List<string> Files = new();
        public string ManifestPath = "";
    }

    public static BsipaManifest ReadManifest(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var m = new BsipaManifest { ManifestPath = path };
            if (r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) m.Name = n.GetString();
            if (r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) m.Id = i.GetString();
            if (r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) m.Version = v.GetString();
            if (r.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var e in f.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) m.Files.Add(e.GetString());
            return m;
        }
        catch { return null; }
    }

    static IEnumerable<string> ModDllPaths(BsipaManifest m, string profileRoot)
    {
        foreach (var f in m.Files)
            if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                yield return System.IO.Path.Combine(profileRoot, f.Replace('/', System.IO.Path.DirectorySeparatorChar));
        // the manifest's own dll may not be listed (older style): Plugins/<manifestName>.dll
        if (!m.Files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var id = string.IsNullOrEmpty(m.Id) ? System.IO.Path.GetFileNameWithoutExtension(m.ManifestPath) : m.Id;
            yield return System.IO.Path.Combine(profileRoot, "Plugins", id + ".dll");
        }
    }

    /// <summary>Lists installed mods for the UI, driven by BSIPA manifests in Plugins\.</summary>
    public List<InstalledMod> ScanInstalled(Profile profile)
    {
        var result = new List<InstalledMod>();
        var plugins = System.IO.Path.Combine(profile.Path, "Plugins");
        if (!Directory.Exists(plugins)) return result;
        foreach (var mf in Directory.GetFiles(plugins, "*.manifest"))
        {
            var m = ReadManifest(mf);
            if (m == null || (string.IsNullOrEmpty(m.Name) && string.IsNullOrEmpty(m.Id))) continue;
            var displayName = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name;
            bool enabled = false, seen = false;
            foreach (var dll in ModDllPaths(m, profile.Path))
            {
                seen = true;
                if (File.Exists(dll)) { enabled = true; break; }
            }
            if (!seen) enabled = File.Exists(System.IO.Path.Combine(plugins, m.Id + ".dll"));
            result.Add(new InstalledMod
            {
                Files = m.Files?.ToList(),
                Name = displayName,
                Version = m.Version,
                Enabled = enabled,
                DllPath = plugins,
                Managed = true,
                FileCount = m.Files.Count,
            });
        }
        // unmanaged dlls in Plugins without a manifest
        var manifestNames = result.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in Directory.GetFiles(plugins, "*.dll"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(dll);
            if (manifestNames.Contains(name)) continue;
            result.Add(new InstalledMod
            {
                Name = name,
                Version = "",
                Enabled = File.Exists(dll),
                DllPath = dll,
                Managed = false,
            });
        }
        return result;
    }

    public class InstalledMod
    {
        public string Name;
        public string Version;
        public bool Enabled;
        public string DllPath;
        public bool Managed;
        public int FileCount;
        /// <summary>Files the mod's BSIPA manifest owns (profile-root relative), when known.</summary>
        public List<string> Files;
    }

    /// <summary>Enable/disable a mod by name: renames every dll the BSIPA manifest (or install record) owns.</summary>
    public void SetEnabled(Profile profile, string modName, bool enable)
    {
        var dlls = DllsFor(profile, modName).ToList();
        if (dlls.Count == 0) { OnStatus?.Invoke($"Couldn't find files for '{modName}'."); return; }
        foreach (var dll in dlls)
        {
            var disabled = dll + ".disabled";
            if (!enable && File.Exists(dll)) File.Move(dll, disabled, true);
            else if (enable && File.Exists(disabled)) File.Move(disabled, dll, true);
        }
        OnStatus?.Invoke($"{(enable ? "Enabled" : "Disabled")} {modName}.");
    }

    IEnumerable<string> DllsFor(Profile profile, string modName)
    {
        var plugins = System.IO.Path.Combine(profile.Path, "Plugins");
        if (!Directory.Exists(plugins)) yield break;

        // match by manifest name or id
        foreach (var mf in Directory.GetFiles(plugins, "*.manifest"))
        {
            var m = ReadManifest(mf);
            if (m == null) continue;
            if (!string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(m.Id, modName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var dll in ModDllPaths(m, profile.Path)) yield return dll;
            yield break;
        }
        // fall back to install record
        var rec = ListInstalledRecs(profile).FirstOrDefault(r => r.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
        if (rec?.Files != null)
            foreach (var f in rec.Files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                yield return System.IO.Path.Combine(profile.Path, f);
    }

    public void Uninstall(Profile profile, string modName)
    {
        var recs = ListInstalledRecs(profile);
        var rec = recs.FirstOrDefault(r => r.Name.Equals(modName, StringComparison.OrdinalIgnoreCase));
        int removed = 0;
        List<string> files = rec?.Files?.ToList() ?? new List<string>();

        if (files.Count == 0)
        {
            // manifest-driven uninstall
            var plugins = System.IO.Path.Combine(profile.Path, "Plugins");
            foreach (var mf in Directory.GetFiles(plugins, "*.manifest"))
            {
                var m = ReadManifest(mf);
                if (m == null) continue;
                if (!string.Equals(m.Name, modName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(m.Id, modName, StringComparison.OrdinalIgnoreCase)) continue;
                files = m.Files.ToList();
                files.Add(System.IO.Path.GetRelativePath(profile.Path, mf));
                break;
            }
        }
        foreach (var f in files)
        {
            foreach (var candidate in new[] { System.IO.Path.Combine(profile.Path, f), System.IO.Path.Combine(profile.Path, f) + ".disabled" })
                if (File.Exists(candidate)) { try { File.Delete(candidate); removed++; } catch { } }
        }
        if (rec != null) recs.Remove(rec);
        SaveRecs(profile, recs);

        if (removed == 0)
        {
            // not managed by us: delete matching dll(+disabled) from Plugins
            var plugins = System.IO.Path.Combine(profile.Path, "Plugins");
            if (Directory.Exists(plugins))
                foreach (var dll in Directory.GetFiles(plugins, "*.dll*", SearchOption.AllDirectories)
                         .Where(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(modName, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(dll); removed++; } catch { }
                }
        }
        OnStatus?.Invoke(removed > 0 ? $"Uninstalled {modName} ({removed} files)." : $"No files found for {modName}.");
    }
}
