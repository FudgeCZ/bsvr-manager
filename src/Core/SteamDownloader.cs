using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace BSVRManager.Core;

/// <summary>
/// Downloads a Beat Saber version with NO sign-in: copies a `download_depot` command to
/// the clipboard, lets steam_autopaste.exe type it into the logged-in Steam client's
/// console, then watches Steam libraries for the depot files and copies them into the
/// profile folder. App 620980, content depot 620981 (verified against the user's install).
/// </summary>
public class SteamConsoleDownload
{
    public const int AppId = 620980;
    public const int DepotId = 620981;

    readonly Settings _settings;
    public Action<string> OnStatus;

    public SteamConsoleDownload(Settings settings) { _settings = settings; }

    static string ClipboardCommand(string manifest) =>
        string.IsNullOrEmpty(manifest)
            ? $"download_depot {AppId} {DepotId}"
            : $"download_depot {AppId} {DepotId} {manifest}";

    /// <summary>Kicks off a console download + watcher. Returns immediately; OnStatus streams progress.</summary>
    public void Start(Profile profile, string manifest, Action<bool> onDone)
    {
        var cmd = ClipboardCommand(manifest);
        OnStatus?.Invoke("Copying Steam console command…");
        SetClipboard(cmd);
        // clear stale staged content so the watcher can't import a previous download
        foreach (var lib in SteamLibraries())
        {
            var stale = System.IO.Path.Combine(lib, "steamapps", "content", $"app_{AppId}", $"depot_{DepotId}");
            if (Directory.Exists(stale))
            {
                try { Directory.Delete(stale, true); OnStatus?.Invoke("Cleared old staged depot content."); }
                catch { }
            }
        }
        RunAutopaste(cmd);
        OnStatus?.Invoke("Sent to Steam console — download starts automatically. Watch Steam for progress.");

        var thread = new Thread(() => Watch(profile, manifest, onDone)) { IsBackground = true };
        thread.Start();
    }

    static void SetClipboard(string text)
    {
        var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"Set-Clipboard -Value '" + text + "'\"")
        { CreateNoWindow = true, UseShellExecute = false };
        try { Process.Start(psi)?.WaitForExit(5000); } catch { }
    }

    static void RunAutopaste(string cmd)
    {
        foreach (var path in AutopasteCandidates())
        {
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path, "\"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false });
                    return;
                }
                catch { }
            }
        }
        // no helper: still open the console so the user can paste manually
        try { Process.Start(new ProcessStartInfo("steam://nav/console") { UseShellExecute = true }); } catch { }
    }

    static IEnumerable<string> AutopasteCandidates()
    {
        var exeDir = SteamCmdDownload.ExeDir;
        yield return System.IO.Path.Combine(exeDir, "tools", "steam_autopaste.exe");
        yield return System.IO.Path.Combine(exeDir, "steam_autopaste.exe");
    }

    static string DownloadingMarker()
    {
        foreach (var lib in SteamLibraries())
        {
            var dl = System.IO.Path.Combine(lib, "steamapps", "downloading");
            if (!Directory.Exists(dl)) continue;
            foreach (var d in Directory.GetDirectories(dl))
                if (d.Contains($"download_depot {AppId} {DepotId}"))
                    return d;
        }
        return null;
    }

    /// <summary>Polls Steam libraries for the depot. Complete = Steam's download_depot marker gone
    /// (or never appeared) AND file count stable for 60 s AND Beat Saber.exe present.</summary>
    void Watch(Profile profile, string manifest, Action<bool> onDone)
    {
        var deadline = DateTime.UtcNow.AddHours(6);
        int lastCount = -1;
        DateTime lastChange = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            var depotDir = FindLandedDepot();
            if (depotDir != null)
            {
                var count = Directory.GetFiles(depotDir, "*", SearchOption.AllDirectories).Length;
                var marker = DownloadingMarker();
                if (count != lastCount)
                {
                    lastCount = count;
                    lastChange = DateTime.UtcNow;
                    OnStatus?.Invoke($"Depot downloading… {count} files so far (Steam shows its own progress).");
                }
                else if (DateTime.UtcNow - lastChange > TimeSpan.FromSeconds(60) && count > 100
                         && File.Exists(System.IO.Path.Combine(depotDir, "Beat Saber.exe"))
                         && marker == null)
                {
                    OnStatus?.Invoke($"Depot download finished ({count} files) — importing into profile…");
                    try
                    {
                        CopyGameFiles(depotDir, profile.Path);
                        // some depot builds ship an empty BeatSaberVersion.txt — label with the requested version
                        var vf = System.IO.Path.Combine(profile.Path, "BeatSaberVersion.txt");
                        if (!File.Exists(vf) || File.ReadAllText(vf).Trim().Length == 0)
                            File.WriteAllText(vf, profile.Version);
                        var actualVersion = File.ReadAllText(vf).Trim();
                        var expected = profile.Version;
                        var actualClean = actualVersion.Contains('_') ? actualVersion[..actualVersion.IndexOf('_')] : actualVersion;
                        if (!string.Equals(actualClean, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            OnStatus?.Invoke($"WARNING: Steam's content for the requested manifest is Beat Saber {actualVersion}, not {expected}. The version catalog entry is wrong — the profile was renamed to '{profile.Name} (actual {actualClean})' and the catalog entry was disabled.");
                            profile.Name += $" (actual {actualClean})";
                            profile.Version = actualClean;
                            OnWrongManifest?.Invoke(manifest);
                        }
                        OnStatus?.Invoke($"Imported '{profile.Name}' — Beat Saber {profile.GameVersion()}. You can now install mods.");
                        onDone?.Invoke(true);
                    }
                    catch (Exception e)
                    {
                        OnStatus?.Invoke("Import failed: " + e.Message);
                        onDone?.Invoke(false);
                    }
                    return;
                }
            }
            Thread.Sleep(3000);
        }
        OnStatus?.Invoke("Timed out waiting for the Steam depot download (6 h).");
        onDone?.Invoke(false);
    }

    /// <summary>Reported when a manifest delivered different content than requested.</summary>
    public event Action<string> OnWrongManifest;

    /// <summary>Copies depot content into the profile and verifies the version. Returns success.
    /// On mismatch the profile is renamed to the actual version and the caller should blacklist the manifest.</summary>
    public static bool ImportDepot(string depotDir, Profile profile, string expectedVersion, Action<string> onStatus, out string actualClean)
    {
        try
        {
            CopyGameFiles(depotDir, profile.Path);
            // some depot builds ship an empty BeatSaberVersion.txt — label with the requested version
            var vf = System.IO.Path.Combine(profile.Path, "BeatSaberVersion.txt");
            if (!File.Exists(vf) || File.ReadAllText(vf).Trim().Length == 0)
                File.WriteAllText(vf, expectedVersion);
            var actualVersion = File.ReadAllText(vf).Trim();
            actualClean = actualVersion.Contains('_') ? actualVersion[..actualVersion.IndexOf('_')] : actualVersion;
            if (!string.Equals(actualClean, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                onStatus?.Invoke($"WARNING: the downloaded content is Beat Saber {actualVersion}, not {expectedVersion}. The catalog entry is wrong — profile renamed and the manifest disabled.");
                profile.Name += $" (actual {actualClean})";
                profile.Version = actualClean;
                return false;
            }
            onStatus?.Invoke($"Imported '{profile.Name}' — Beat Saber {actualClean}. You can now install mods.");
            return true;
        }
        catch (Exception e)
        {
            onStatus?.Invoke("Import failed: " + e.Message);
            actualClean = "";
            return false;
        }
    }

    public static string FindLandedDepot()
    {
        foreach (var lib in SteamLibraries())
        {
            var dir = System.IO.Path.Combine(lib, "steamapps", "content", $"app_{AppId}", $"depot_{DepotId}");
            if (File.Exists(System.IO.Path.Combine(dir, "Beat Saber.exe")))
                return dir;
        }
        return null;
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var steamRoot = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(steamRoot)) candidates.Add(System.IO.Path.Combine(steamRoot, "Steam"));
        foreach (var drive in new[] { "C", "D", "E", "F", "G" })
            candidates.Add($@"{drive}:\SteamLibrary");
        foreach (var c in candidates.Distinct())
            if (Directory.Exists(c)) yield return c;
    }

    /// <summary>Copies a game tree, skipping user-content folders that shouldn't be overwritten.</summary>
    public static void CopyGameFiles(string src, string dst)
    {
        var skip = new[] { "CustomLevels", "CustomSabers", "CustomPlatforms", "CustomAvatars", "CustomNotes", "CustomWIPLevels", "Playlists", "UserData" };
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(src, dir);
            var top = rel.Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (skip.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(System.IO.Path.Combine(dst, rel));
        }
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(src, file);
            var top = rel.Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (skip.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
            var target = System.IO.Path.Combine(dst, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }
    }
}

/// <summary>
/// Background downloader: runs DepotDownloader hidden (no console window). One-time
/// authorization via QR (Steam mobile app) or username/password — credentials are cached
/// by DepotDownloader, so later downloads run without any login.
/// </summary>
public class DepotDownload
{
    readonly Settings _settings;
    public Action<string> OnStatus;
    public Action<string> OnQr;           // QR login URL -> show as QR image
    public Action<string> OnPrompt;       // input needed: "password" | "2fa"
    public Action<bool> OnDone;
    Process _process;

    public DepotDownload(Settings settings) { _settings = settings; }

    public static string FindExe()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("BSMOD_DEPOTDOWNLOADER"),
            System.IO.Path.Combine(SteamCmdDownload.ExeDir, "tools", "DepotDownloader", "DepotDownloader.exe"),
            System.IO.Path.Combine(SteamCmdDownload.ExeDir, "tools", "DepotDownloader.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "bs-manager", "resources", "assets", "scripts", "DepotDownloader.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads the latest DepotDownloader release (GitHub) when missing, then reports.
    /// The zip extracts into tools\DepotDownloader\ (self-contained).</summary>
    public static void EnsureInstalled(Action<bool> done)
    {
        if (FindExe() != null) { done(true); return; }
        new Thread(() =>
        {
            try
            {
                var toolsDir = Path.Combine(SteamCmdDownload.ExeDir, "tools");
                Directory.CreateDirectory(toolsDir);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BSVRManager/1.0");
                var rel = http.GetStringAsync("https://api.github.com/repos/SteamRE/DepotDownloader/releases/latest").GetAwaiter().GetResult();
                string url = null;
                using (var doc = System.Text.Json.JsonDocument.Parse(rel))
                {
                    foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    {
                        var name = a.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith("windows-x64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            url = a.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }
                if (url == null) { DepotLog("DepotDownloader auto-install: no windows-x64 asset"); done(false); return; }
                var zipPath = Path.Combine(toolsDir, "dd.zip");
                File.WriteAllBytes(zipPath, http.GetByteArrayAsync(url).GetAwaiter().GetResult());
                var extractDir = Path.Combine(toolsDir, "DepotDownloader");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
                File.Delete(zipPath);
                DepotLog("DepotDownloader auto-installed");
                done(FindExe() != null);
            }
            catch (Exception e)
            {
                DepotLog("DepotDownloader auto-install failed: " + e.Message);
                done(false);
            }
        }) { IsBackground = true }.Start();
    }

    public static bool HasCachedLogin()
    {
        var exe = FindExe();
        if (exe == null) return false;
        var exeDir = Path.GetDirectoryName(exe);
        // DepotDownloader caches account+config next to its own executable
        return Directory.Exists(Path.Combine(exeDir, "depotdownloader"))
            || Directory.Exists(Path.Combine(exeDir, ".depotdownloader"))
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".depotdownloader"));
    }

    public void Cancel()
    {
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
    }

    public void Start(Profile profile, string manifest, string user = null, string password = null, string code = null, Action<bool> onDone = null, bool qr = false)
    {
        var exe = FindExe();
        if (exe == null)
        {
            OnStatus?.Invoke("DepotDownloader.exe not found (checked tools and BSManager's copy).");
            onDone?.Invoke(false);
            return;
        }
        var args = $"-app {SteamConsoleDownload.AppId} -depot {SteamConsoleDownload.DepotId} -manifest {manifest} -dir \"{profile.Path}\" -remember-password";
        if (qr) args += " -qr";
        if (!string.IsNullOrEmpty(user)) args += $" -username {user}";
        if (!string.IsNullOrEmpty(code)) args += $" -code {code}";

        OnStatus?.Invoke("Starting background download…");
        var thread = new Thread(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = profile.Path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                };
                _process = Process.Start(psi);
                var last = DateTime.UtcNow;
                bool sawQr = false, sawPassword = false;
                _process.OutputDataReceived += (_, e) =>
                {
                    var line = e.Data;
                    if (line == null) return;
                    DepotLog(line);

                    if (line.Contains("[QRCode]|"))
                    {
                        sawQr = true; // every rotation refreshes the shown QR
                        OnQr?.Invoke(line.Substring(line.LastIndexOf('|') + 1));
                        return;
                    }
                    if (!sawPassword && line.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        sawPassword = true;
                        OnPrompt?.Invoke("password");
                    }
                    if (line.Contains("Two factor", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("2FA", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase))
                    {
                        OnPrompt?.Invoke("2fa");
                    }
                    if ((DateTime.UtcNow - last).TotalSeconds > 2)
                    {
                        last = DateTime.UtcNow;
                        OnStatus?.Invoke("[background] " + Truncate(line));
                    }
                };
                _process.BeginOutputReadLine();

                if (!string.IsNullOrEmpty(password))
                {
                    // answer DepotDownloader's "Password:" prompt via stdin
                    Task.Run(() =>
                    {
                        try
                        {
                            Thread.Sleep(2500);
                            _process.StandardInput.WriteLine(password);
                        }
                        catch { }
                    });
                }
                _process.WaitForExit();
                DepotLog($"[exit] code {_process.ExitCode}");

                if (File.Exists(System.IO.Path.Combine(profile.Path, "Beat Saber.exe")))
                {
                    var vf = System.IO.Path.Combine(profile.Path, "BeatSaberVersion.txt");
                    if (!File.Exists(vf) || File.ReadAllText(vf).Trim().Length == 0)
                        File.WriteAllText(vf, profile.Version);
                    OnStatus?.Invoke($"Version {profile.Version} downloaded into '{profile.Name}'.");
                    onDone?.Invoke(true);
                }
                else
                {
                    OnStatus?.Invoke("Background download ended without completing.");
                    onDone?.Invoke(false);
                }
            }
            catch (Exception e)
            {
                OnStatus?.Invoke("Background download failed: " + e.Message);
                onDone?.Invoke(false);
            }
        }) { IsBackground = true };
        thread.Start();
    }

    static readonly object _depotLogLock = new();
    internal static void DepotLog(string line)
    {
        try
        {
            lock (_depotLogLock)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSVRManager");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "depot.log"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            }
        }
        catch { }
    }

    static string Truncate(string s) => s.Length > 100 ? s[..100] : s;
}
