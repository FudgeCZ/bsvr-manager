using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace BSVRManager.UiKit;

/// <summary>
/// Resolves ui/ and data/ files in three environments:
/// dev project (res:// mapped to the project folder), exported app (embedded pck),
/// exported editor (real folder next to the exe so the user can edit layouts).
/// </summary>
public static class ResourceFS
{
    public static string ExeDir => Path.GetDirectoryName(OS.GetExecutablePath());

    /// <summary>True when running from the Godot editor / source project (ui\ exists as real files).</summary>
    public static bool IsDevProject => Directory.Exists(ProjectSettings.GlobalizePath("res://ui"));

    /// <summary>Reads every ui/*.json. Exported app: embedded pck, unless exeDir\ui overrides.
    /// Dev project / editor: real files.</summary>
    public static Dictionary<string, string> ReadScreens()
    {
        var result = new Dictionary<string, string>();
        var local = Path.Combine(ExeDir, "ui");
        if (Directory.Exists(local))
        {
            foreach (var f in Directory.GetFiles(local, "*.json"))
                result[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f);
            return result;
        }
        if (IsDevProject)
        {
            var dir = ProjectSettings.GlobalizePath("res://ui");
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                result[Path.GetFileNameWithoutExtension(f)] = File.ReadAllText(f);
            return result;
        }
        // exported: read from pck
        var d = DirAccess.Open("res://ui");
        if (d != null)
        {
            d.ListDirBegin();
            var name = d.GetNext();
            while (!string.IsNullOrEmpty(name))
            {
                if (name.EndsWith(".json"))
                {
                    using var f = Godot.FileAccess.Open("res://ui/" + name, Godot.FileAccess.ModeFlags.Read);
                    if (f != null) result[Path.GetFileNameWithoutExtension(name)] = f.GetAsText();
                }
                name = d.GetNext();
            }
            d.ListDirEnd();
        }
        return result;
    }

    /// <summary>Extracts embedded screens next to the exe so the exported editor can edit them.</summary>
    public static int SeedLocalUi()
    {
        var local = Path.Combine(ExeDir, "ui");
        Directory.CreateDirectory(local);
        int n = 0;
        foreach (var kv in ReadScreensFromPckOnly())
        {
            var target = Path.Combine(local, kv.Key + ".json");
            if (!File.Exists(target))
            {
                File.WriteAllText(target, kv.Value);
                n++;
            }
        }
        return n;
    }

    static Dictionary<string, string> ReadScreensFromPckOnly()
    {
        var result = new Dictionary<string, string>();
        var d = DirAccess.Open("res://ui");
        if (d == null) return result;
        d.ListDirBegin();
        var name = d.GetNext();
        while (!string.IsNullOrEmpty(name))
        {
            if (name.EndsWith(".json"))
            {
                using var f = Godot.FileAccess.Open("res://ui/" + name, Godot.FileAccess.ModeFlags.Read);
                if (f != null) result[Path.GetFileNameWithoutExtension(name)] = f.GetAsText();
            }
            name = d.GetNext();
        }
        d.ListDirEnd();
        return result;
    }

    /// <summary>Writes a screen file (editor mode): project folder in dev, exeDir\ui when exported.</summary>
    public static string UiWriteDir()
    {
        if (IsDevProject) return ProjectSettings.GlobalizePath("res://ui");
        var local = Path.Combine(ExeDir, "ui");
        Directory.CreateDirectory(local);
        return local;
    }

    /// <summary>Reads a bundled data file (e.g. bs-versions-snapshot.json).</summary>
    public static string ReadDataFile(string name)
    {
        var p = Path.Combine(ExeDir, "data", name);
        if (File.Exists(p)) return File.ReadAllText(p);
        using var f = Godot.FileAccess.Open("res://data/" + name, Godot.FileAccess.ModeFlags.Read);
        return f == null ? null : f.GetAsText();
    }
}
