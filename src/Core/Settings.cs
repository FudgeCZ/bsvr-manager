using System;
using System.IO;
using System.Text.Json;

namespace BSVRManager.Core;

/// <summary>App-wide settings persisted in %APPDATA%\BSVRManager.</summary>
public class Settings
{
    public string InstancesDir { get; set; } = "";
    public string SteamBsPath { get; set; } = "";
    public string BsManagerPath { get; set; } = "";
    public string DownloadMethod { get; set; } = "depot_downloader"; // depot_downloader (background) | steam_console (visible fallback)
    public bool EnableDepotDownloader { get; set; } = true;
    /// <summary>Manifest the depot folder's current content belongs to (for resume-import).</summary>
    public string LastDepotManifest { get; set; } = "";
    /// <summary>Steam account name used for steamcmd background downloads (password never stored).</summary>
    public string SteamCmdUser { get; set; } = "";
    public string LastRefresh { get; set; } = "";

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSVRManager");
    static string StorePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(StorePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Default instance folder: the app's OWN folder (%APPDATA%\BSVRManager\Instances).
    /// BSManager instances stay referenced in place — the app never mixes its downloads into BSManager's folder.</summary>
    public string EffectiveInstancesDir()
    {
        if (!string.IsNullOrEmpty(InstancesDir) && Directory.Exists(InstancesDir)) return InstancesDir;
        return Path.Combine(Dir, "Instances");
    }

    /// <summary>BSManager data root from %APPDATA%\bs-manager\config.json → installation-folder.</summary>
    public string BsManagerRoot()
    {
        if (!string.IsNullOrEmpty(BsManagerPath) && Directory.Exists(BsManagerPath)) return BsManagerPath;
        try
        {
            var cfg = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "bs-manager", "config.json");
            if (!File.Exists(cfg)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty("installation-folder", out var f))
            {
                var p = f.GetString();
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }
}
