using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BSVRManager.Core;

/// <summary>
/// Imports profiles from BSManager's config.cfg (metadata only — instance folders are
/// referenced in place, no game files copied). Existing matching profiles are updated,
/// never duplicated.
/// </summary>
public static class BsManagerImport
{
    public class ImportResult
    {
        public int Imported;
        public int Updated;
        public List<string> Notes = new();
        public string Summary => $"{Imported} imported, {Updated} updated";
    }

    /// <summary>Returns BSManager's config.cfg path, or null when BSManager isn't installed.
    /// BSManager's "installation-folder" is the PARENT of its data folder (&lt;folder&gt;\BSManager).</summary>
    public static string FindConfig(Settings settings)
    {
        var root = settings.BsManagerRoot();
        if (root == null) return null;
        foreach (var candidate in new[]
                 {
                     System.IO.Path.Combine(root, "BSManager", "config.cfg"),
                     System.IO.Path.Combine(root, "config.cfg"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsAvailable(Settings settings) => FindConfig(settings) != null;

    /// <summary>Removes every profile that came from BSManager (imported or detected under its
    /// instances folder) so the import can start fresh. Manually created profiles and the
    /// Steam install are kept.</summary>
    public static ImportResult Reset(Settings settings, ProfileManager profiles)
    {
        var result = new ImportResult();
        var root = settings.BsManagerRoot();
        var instancesDir = root != null ? System.IO.Path.Combine(root, "BSManager", "BSInstances") : null;

        foreach (var p in profiles.Profiles.ToList())
        {
            bool fromConfig = !string.IsNullOrEmpty(p.BsmId);
            bool inBsInstances = instancesDir != null && p.Path != null &&
                p.Path.StartsWith(instancesDir, StringComparison.OrdinalIgnoreCase);
            if (fromConfig || inBsInstances)
            {
                profiles.Profiles.Remove(p);
                result.Updated++; // reused counter = number removed
            }
        }
        profiles.Save();
        result.Notes.Add("BSManager profiles removed. Use Import to pull them again.");
        return result;
    }

    public class BsmEntry
    {
        public string BSVersion { get; set; }
        public string BSManifest { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Color { get; set; }
        public string Id { get; set; }
        public string Store { get; set; }
    }

    public static List<BsmEntry> ReadEntries(string configPath)
    {
        var list = new List<BsmEntry>();
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!doc.RootElement.TryGetProperty("custom-versions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            try
            {
                var entry = new BsmEntry
                {
                    BSVersion = Str(e, "BSVersion"),
                    BSManifest = Str(e, "BSManifest"),
                    Name = Str(e, "name"),
                    Path = Str(e, "path"),
                    Color = Str(e, "color"),
                };
                if (e.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                {
                    entry.Id = Str(md, "id");
                    entry.Store = Str(md, "store", "steam");
                }
                if (!string.IsNullOrEmpty(entry.Path) || !string.IsNullOrEmpty(entry.BSVersion))
                    list.Add(entry);
            }
            catch { /* skip malformed entry */ }
        }
        return list;
    }

    static string Str(JsonElement e, string key, string def = "")
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : def;

    public static ImportResult Import(Settings settings, ProfileManager profiles)
    {
        var result = new ImportResult();
        var cfg = FindConfig(settings);
        if (cfg == null)
        {
            result.Notes.Add("BSManager not found.");
            return result;
        }
        foreach (var e in ReadEntries(cfg))
        {
            var name = string.IsNullOrEmpty(e.Name) ? e.BSVersion : e.Name;
            var path = e.Path;
            // relative safety: BSManager writes absolute paths; skip entries whose folder vanished
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                result.Notes.Add($"Skipped '{name}' — folder missing: {path}");
                continue;
            }
            var existing = profiles.FindExisting(e.Id, path, name, e.BSVersion);
            var p = new Core.Profile
            {
                Name = name,
                Version = e.BSVersion,
                Manifest = e.BSManifest,
                Path = path,
                Color = string.IsNullOrEmpty(e.Color) ? "#477cf2" : e.Color,
                Store = string.IsNullOrEmpty(e.Store) ? "steam" : e.Store,
                BsmId = e.Id ?? "",
            };
            profiles.Upsert(p);
            if (existing != null) result.Updated++; else result.Imported++;
        }
        // also scan BSInstances\ for instance folders that config.cfg doesn't mention.
        // never rename profiles already imported from config.cfg — config names are authoritative.
        var root = settings.BsManagerRoot();
        var instancesDir = root != null ? System.IO.Path.Combine(root, "BSManager", "BSInstances") : null;
        if (instancesDir != null && Directory.Exists(instancesDir))
        {
            foreach (var dir in Directory.GetDirectories(instancesDir))
            {
                var name = System.IO.Path.GetFileName(dir);
                var existing = profiles.FindExisting(null, dir, name, null);
                if (existing != null) continue;
                var v = ReadSteamVersion(dir);
                if (v == "?" || !File.Exists(System.IO.Path.Combine(dir, "Beat Saber.exe"))) continue;
                profiles.Upsert(new Profile { Name = name, Version = v, Path = dir });
                result.Imported++;
                result.Notes.Add($"Instance '{name}' ({v}) added.");
            }
        }
        // also offer the real Steam install as a (read-only-ish) profile
        var steam = settings.SteamBsPath;
        if (!string.IsNullOrEmpty(steam) && Directory.Exists(steam) && profiles.FindExisting(null, steam, "Steam install", null) == null)
        {
            var v = ReadSteamVersion(steam);
            profiles.Upsert(new Profile { Name = "Steam install", Version = v, Path = steam, Color = "#3f9d63" });
            result.Imported++;
            result.Notes.Add("Steam install added as a profile.");
        }
        return result;
    }

    public static string ReadSteamVersion(string steamBsPath)
    {
        try { return File.ReadAllText(System.IO.Path.Combine(steamBsPath, "BeatSaberVersion.txt")).Trim(); }
        catch { return "?"; }
    }
}
