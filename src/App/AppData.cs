using System.Collections.Generic;
using Godot;

namespace BSVRManager.App;

/// <summary>Placeholder data bindings; Core services replace these in Phase 3.</summary>
public static class AppData
{
    public static IEnumerable<Dictionary<string, string>> For(string key, AppMain app) => Sample.For(key);
}

/// <summary>Shared sample rows (same shape as editor sample data).</summary>
public static class Sample
{
    public static IEnumerable<Dictionary<string, string>> For(string key) => new List<Dictionary<string, string>>
    {
        new() { ["title"] = "Coming online…", ["subtitle"] = key, ["id"] = "stub" },
    };
}
