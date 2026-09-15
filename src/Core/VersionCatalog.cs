using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace BSVRManager.Core;

/// <summary>
/// Grow-only catalog of every Beat Saber version, merged from BeatMods (fast, strings
/// only) + BSManager's mirror (full history with Steam manifest IDs) + a bundled offline
/// snapshot. Downloadable = has a manifest ID, or is the newest entry.
/// </summary>
public class VersionCatalog
{
    public class Entry
    {
        public string Version = "";
        public string Manifest = "";
        public long ReleaseDate;
        public string Year = "";
        public bool Recommended;
        public string Status = "";
    }

    const string BeatModsUrl = "https://versions.beatmods.com/versions.json";
    const string BsManagerUrl = "https://raw.githubusercontent.com/Zagrios/bs-manager/master/assets/jsons/bs-versions.json";
    static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    static string CachePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSVRManager", "versions-cache.json");

    public List<Entry> Entries = new();
    public string LastRefreshAttempt = "";
    DateTime _lastRefresh = DateTime.MinValue;
    bool _refreshing;
    readonly HashSet<string> _blacklisted = new();

    static readonly System.Text.RegularExpressions.Regex VersionRe =
        new(@"^\d+\.\d+\.\d+(p\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex ManifestRe =
        new(@"^[0-9a-zA-Z]{5,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    readonly string _snapshotPath;

    public VersionCatalog(string snapshotPath)
    {
        _snapshotPath = snapshotPath;
        LoadCacheOrSnapshot();
        if (_snapshotPath == null)
        {
            // engine-provided bundled snapshot (works inside exported pck too)
            var json = UiKit.ResourceFS.ReadDataFile("bs-versions-snapshot.json");
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    MergeJson(doc.RootElement);
                }
                catch { }
            }
        }
        RefreshIfNeeded();
    }

    void LoadCacheOrSnapshot()
    {
        foreach (var path in new[] { CachePath, _snapshotPath })
        {
            if (path != null && File.Exists(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    MergeJson(doc.RootElement);
                    if (path == CachePath && doc.RootElement.TryGetProperty("_lastRefresh", out var lr)
                        && long.TryParse(lr.GetString(), out var unix))
                        _lastRefresh = DateTimeOffset.FromUnixTimeSeconds(unix).DateTime;
                    if (path == CachePath && doc.RootElement.TryGetProperty("_blacklisted", out var bl) && bl.ValueKind == JsonValueKind.Array)
                        foreach (var b in bl.EnumerateArray())
                            if (b.ValueKind == JsonValueKind.String) _blacklisted.Add(b.GetString());
                    if (Entries.Count > 0) return;
                }
                catch { }
            }
        }
    }

    public void RefreshIfNeeded()
    {
        if (_refreshing || DateTime.UtcNow - _lastRefresh < RefreshInterval) return;
        _ = RefreshAsync();
    }

    public async void ForceRefreshAsync() => await RefreshAsync();

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        LastRefreshAttempt = DateTime.UtcNow.ToString("o");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BSVRManager/1.0");
            var t1 = http.GetStringAsync(BeatModsUrl);
            var t2 = http.GetStringAsync(BsManagerUrl);
            try { MergeBeatMods(await t1); } catch { }
            try { MergeBsManager(await t2); } catch { }
            _lastRefresh = DateTime.UtcNow;
            SaveCache();
        }
        catch { }
        finally { _refreshing = false; }
    }

    void MergeBeatMods(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            var s = v.GetString();
            if (VersionRe.IsMatch(s ?? ""))
                AddOrUpdate(new Entry { Version = s });
        }
    }

    void MergeBsManager(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var version = e.TryGetProperty("BSVersion", out var bv) ? bv.GetString() : null;
            if (version == null || !VersionRe.IsMatch(version)) continue;
            var entry = new Entry
            {
                Version = version,
                Manifest = e.TryGetProperty("BSManifest", out var bm) && ManifestRe.IsMatch(bm.GetString() ?? "") ? bm.GetString() : "",
                Year = e.TryGetProperty("year", out var y) ? y.GetString() : "",
                Recommended = e.TryGetProperty("recommended", out var r) && r.ValueKind == JsonValueKind.True,
                ReleaseDate = e.TryGetProperty("ReleaseDate", out var rd) && long.TryParse(rd.GetString(), out var l) ? l : 0,
            };
            if (entry.Manifest != "" && _blacklisted.Contains(entry.Manifest)) entry.Manifest = "";
            AddOrUpdate(entry);
        }
    }

    void MergeJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var arr)) return;
        foreach (var e in arr.EnumerateArray())
        {
            var entry = new Entry
            {
                Version = e.TryGetProperty("version", out var v) ? v.GetString() : "",
                Manifest = e.TryGetProperty("manifest", out var m) ? m.GetString() : "",
                Year = e.TryGetProperty("year", out var y) ? y.GetString() : "",
                Status = e.TryGetProperty("status", out var s) ? s.GetString() : "",
            };
            if (e.TryGetProperty("releaseDate", out var rd) && long.TryParse(rd.GetString(), out var l)) entry.ReleaseDate = l;
            if (!VersionRe.IsMatch(entry.Version)) continue;
            if (!string.IsNullOrEmpty(entry.Manifest) && !ManifestRe.IsMatch(entry.Manifest)) entry.Manifest = "";
            AddOrUpdate(entry);
        }
    }

    void AddOrUpdate(Entry entry)
    {
        var existing = Entries.FirstOrDefault(e => e.Version == entry.Version);
        if (existing == null) Entries.Add(entry);
        else
        {
            // enrich; never downgrade a manifest to empty; never resurrect a blacklisted manifest
            if (entry.Manifest != "" && _blacklisted.Contains(entry.Manifest)) entry.Manifest = "";
            if (existing.Manifest == "" && entry.Manifest != "" && !_blacklisted.Contains(entry.Manifest)) existing.Manifest = entry.Manifest;
            if (entry.ReleaseDate > 0) existing.ReleaseDate = entry.ReleaseDate;
            if (!string.IsNullOrEmpty(entry.Year)) existing.Year = entry.Year;
            if (entry.Recommended) existing.Recommended = true;
        }
    }

    void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath));
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("_lastRefresh", new DateTimeOffset(_lastRefresh).ToUnixTimeSeconds().ToString());
                w.WritePropertyName("_blacklisted");
                w.WriteStartArray();
                foreach (var b in _blacklisted) w.WriteStringValue(b);
                w.WriteEndArray();
                w.WritePropertyName("entries");
                w.WriteStartArray();
                foreach (var e in Entries)
                {
                    w.WriteStartObject();
                    w.WriteString("version", e.Version);
                    w.WriteString("manifest", e.Manifest);
                    w.WriteString("year", e.Year);
                    w.WriteString("releaseDate", e.ReleaseDate.ToString());
                    w.WriteString("status", e.Status);
                    if (e.Recommended) w.WriteBoolean("recommended", true);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllText(CachePath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { }
    }

    /// <summary>Newest-first list (by version number) annotated with a "latest" status.</summary>
    public List<Entry> Catalog()
    {
        var sorted = Entries
            .OrderByDescending(e => e.Version, new VersionComparer())
            .ToList();
        if (sorted.Count > 0)
            sorted[0].Status = "latest";
        return sorted;
    }

    public Entry Find(string version) => Entries.FirstOrDefault(e => e.Version == version);

    /// <summary>Disables a manifest that delivered the wrong content (persisted), so the version
    /// shows as not downloadable until a corrected ID shows up.</summary>
    public void BlacklistManifest(string manifest)
    {
        _blacklisted.Add(manifest);
        var e = Entries.FirstOrDefault(x => x.Manifest == manifest);
        if (e != null)
        {
            e.Manifest = "";
            SaveCache();
        }
    }

    public bool IsDownloadable(Entry e)
    {
        if (e == null) return false;
        if (!string.IsNullOrEmpty(e.Manifest)) return true;
        // newest entry downloads without a manifest (depot's current public manifest)
        var newest = Entries.OrderByDescending(x => x.Version, new VersionComparer()).First();
        return e.Version == newest.Version;
    }

    class VersionComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            var pa = Parse(a); var pb = Parse(b);
            for (int i = 0; i < 3; i++)
                if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            return string.CompareOrdinal(a, b);
        }
        static int[] Parse(string v)
        {
            var parts = v.Split('p')[0].Split('.');
            var r = new int[3];
            for (int i = 0; i < 3; i++) int.TryParse(parts[i], out r[i]);
            return r;
        }
    }
}
