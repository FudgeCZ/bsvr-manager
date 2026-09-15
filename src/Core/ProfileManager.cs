using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BSVRManager.Core;

/// <summary>A game-version profile: a folder with a (possibly modded) Beat Saber copy.</summary>
public class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string Path { get; set; } = "";
    public string Color { get; set; } = "#477cf2";
    public string Store { get; set; } = "steam";
    /// <summary>BSManager metadata id when imported, keeps update-in-place matching stable.</summary>
    public string BsmId { get; set; } = "";
    public bool IsActive { get; set; }

    public bool IsPlayable => File.Exists(System.IO.Path.Combine(Path ?? "", "Beat Saber.exe"));

    /// <summary>Clean game version like "1.40.8" (BSManager writes "1.40.8_7379" — build suffix stripped
    /// because BeatMods and comparators want the bare version).</summary>
    public string GameVersion()
    {
        try
        {
            var v = File.ReadAllText(System.IO.Path.Combine(Path, "BeatSaberVersion.txt")).Trim();
            var us = v.IndexOf('_');
            return us > 0 ? v[..us] : v;
        }
        catch { return Version; }
    }

    /// <summary>Raw version string as written on disk (may include the build suffix).</summary>
    public string GameVersionRaw()
    {
        try { return File.ReadAllText(System.IO.Path.Combine(Path, "BeatSaberVersion.txt")).Trim(); }
        catch { return Version; }
    }
}

/// <summary>Owns the profile list + active profile; persisted to %APPDATA%\BSVRManager\profiles.json.</summary>
public class ProfileManager
{
    static string Dir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSVRManager");
    static string StorePath => System.IO.Path.Combine(Dir, "profiles.json");

    public List<Profile> Profiles { get; private set; } = new();
    public string ActiveId { get; private set; } = "";

    readonly Settings _settings;

    public ProfileManager(Settings settings)
    {
        _settings = settings;
        Load();
        if (Profiles.Count > 0 && string.IsNullOrEmpty(ActiveId))
            ActiveId = Profiles[0].Id;
    }

    void Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath));
                if (store != null) { Profiles = store.Profiles ?? new(); ActiveId = store.ActiveId ?? ""; }
            }
        }
        catch { Profiles = new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new Store { Profiles = Profiles, ActiveId = ActiveId }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Renames a profile (display name only — the game folder on disk is not moved).</summary>
    public bool Rename(string id, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return false;
        var pr = Profiles.FirstOrDefault(p => p.Id == id);
        if (pr == null || pr.Name == newName) return false;
        pr.Name = newName;
        Save();
        return true;
    }

    class Store
    {
        public List<Profile> Profiles { get; set; }
        public string ActiveId { get; set; }
    }

    public Profile Active => Profiles.FirstOrDefault(p => p.Id == ActiveId) ?? Profiles.FirstOrDefault(p => p.IsPlayable);

    public Profile FindExisting(string bsmId, string path, string name, string version)
    {
        if (!string.IsNullOrEmpty(bsmId))
        {
            var byId = Profiles.FirstOrDefault(p => p.BsmId == bsmId);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrEmpty(path))
        {
            var byPath = Profiles.FirstOrDefault(p => string.Equals(System.IO.Path.GetFullPath(p.Path ?? " "), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            if (byPath != null) return byPath;
        }
        return Profiles.FirstOrDefault(p => p.Name == name && p.Version == version);
    }

    /// <summary>Inserts or updates a profile. Matching existing entries are updated in place, never duplicated.</summary>
    public Profile Upsert(Profile p)
    {
        var existing = FindExisting(p.BsmId, p.Path, p.Name, p.Version);
        if (existing != null)
        {
            existing.Name = p.Name; existing.Version = p.Version; existing.Color = p.Color;
            existing.Path = string.IsNullOrEmpty(existing.Path) || !Directory.Exists(existing.Path) ? p.Path : existing.Path;
            existing.BsmId = string.IsNullOrEmpty(existing.BsmId) ? p.BsmId : existing.BsmId;
            existing.Store = p.Store;
            Save();
            return existing;
        }
        Profiles.Add(p);
        if (string.IsNullOrEmpty(ActiveId)) ActiveId = p.Id;
        Save();
        return p;
    }

    public void Activate(string id)
    {
        if (Profiles.Any(p => p.Id == id)) { ActiveId = id; Save(); }
    }

    public void Remove(string id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        Profiles.Remove(p);
        if (ActiveId == id) ActiveId = Profiles.FirstOrDefault()?.Id ?? "";
        Save();
    }

    /// <summary>Creates a new profile folder (BSManager-compatible instance) for the given version and returns it.
    /// Multiple profiles per version are allowed — names and folders get a numeric suffix.</summary>
    public Profile CreateInstance(string name, string version, string manifest)
    {
        var baseName = name;
        int n = 2;
        while (Profiles.Any(p => p.Name == name)) name = $"{baseName} {n++}";
        var safe = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
        var dir = System.IO.Path.Combine(_settings.EffectiveInstancesDir(), safe);
        int d = 2;
        while (Directory.Exists(dir)) dir = System.IO.Path.Combine(_settings.EffectiveInstancesDir(), $"{safe} {d++}");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch { dir = System.IO.Path.Combine(Dir, "Instances", safe); Directory.CreateDirectory(dir); }
        }
        var p = new Profile { Name = name, Version = version, Manifest = manifest ?? "", Path = dir };
        return Upsert(p);
    }
}
