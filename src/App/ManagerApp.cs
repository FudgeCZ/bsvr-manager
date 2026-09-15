using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Godot;
using BSVRManager.Core;
using BSVRManager.UiKit;

namespace BSVRManager.App;

/// <summary>The real app: wires ui/*.json screens to the Core services.</summary>
public partial class ManagerApp : AppMain
{
    readonly Settings _settings = Settings.Load();
    SteamCmdDownload _steamcmd;
    ProfileManager _profiles;
    VersionCatalog _catalog;
    ModInstaller _mods;

    string _status = "";
    string _pendingVersion; // version chosen while not logged in — auto-starts after login
    VersionCatalog.Entry _pendingEntry;
    Profile _pendingProfile; // created at popup time so QR/console downloads land in it
    DepotDownload _qrLogin;

    // mods tab desired state: seeded from installed mods; checkboxes flip membership;
    // "Install or update" applies the difference (installs + dependencies, uninstalls).
    readonly HashSet<string> _modsDesired = new(StringComparer.OrdinalIgnoreCase);
    string _desiredProfile; // profile id the desired set was seeded for
    string _lastModName = ""; // last checkbox touched — "More info" opens its link
    readonly Dictionary<string, long> _modSizes = new(StringComparer.OrdinalIgnoreCase);

    public ManagerApp(bool allowVr) : base(allowVr) { }

    public override void _Ready()
    {
        _profiles = new ProfileManager(_settings);
        _catalog = new VersionCatalog(null);
        _mods = new ModInstaller(_settings);
        _mods.OnStatus += s => Ui(() => { _status = s; RefreshStatusLabels(); });
        _steamcmd = new SteamCmdDownload(_settings);
        _steamcmd.OnStatus += s => Ui(() => { _status = s; RefreshStatusLabels(); });

        DetectSteamBs();

        base._Ready();

        // pre-scan all playable profiles in the background so counts are ready
        LoadBegin("Scanning profiles…");
        Task.Run(() =>
        {
            foreach (var pr in _profiles.Profiles.Where(x => x.IsPlayable))
                RescanMods(pr);
            Ui(() => { RefreshCurrent(); LoadEnd(); });
        });
    }

    readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> _uiQueue = new();
    readonly Dictionary<string, List<ModInstaller.InstalledMod>> _modsScanCache = new();
    readonly HashSet<string> _beatModsUnavailable = new(); // versions BeatMods answers 400 for

    /// <summary>Marshals an action onto the UI thread (safe from any background thread).</summary>
    void Ui(System.Action a)
    {
        _uiQueue.Enqueue(a);
        CallDeferred(nameof(RunUiQueue));
    }

    void RunUiQueue()
    {
        while (_uiQueue.TryDequeue(out var a))
        {
            try { a(); } catch (Exception e) { GD.PushWarning($"[uiqueue] {e.Message}"); }
        }
    }

    // ---------------- loading overlay ----------------

    int _loads;
    void LoadBegin(string text) => Ui(() =>
    {
        _loads++;
        foreach (var h in Hosts) h.Loading.ShowLoading(text);
    });

    void LoadEnd() => Ui(() =>
    {
        _loads = Math.Max(0, _loads - 1);
        if (_loads == 0)
            foreach (var h in Hosts) h.Loading.Visible = false;
    });

    // ---------------- host helpers ----------------

    IEnumerable<PanelHost> Hosts
    {
        get
        {
            if (LeftHost != null) yield return LeftHost;
            if (CenterHost != null) yield return CenterHost;
            if (RightHost != null) yield return RightHost;
        }
    }

    Control FindCtl(string id)
    {
        foreach (var h in Hosts)
            if (h.Ctx.ById.TryGetValue(id, out var c)) return c;
        return null;
    }

    void SetTextSafe(string id, string text)
    {
        var c = FindCtl(id);
        if (c != null) UiRuntime.SetControlText(c, text);
    }

    string FieldText(string id) => FindCtl(id) is LineEdit le ? le.Text : "";

    void OpenModal(string id) => OpenModalOnAny(id);
    void CloseModal(string id) => CloseModalOnAny(id);

    // ---------------- screen lifecycle + data ----------------

    public override void OnPanelScreenShown(PanelHost host, UiScreen screen)
    {
        RefreshStatusLabels();

        // BSManager import offer — once, when the dashboard first appears (modal lives there)
        if (!_importOffered && screen.Name == "dashboard")
        {
            _importOffered = true;
            MaybeOfferImport();
        }

        if (host != CenterHost) return;

        // background fetches + scans the screen needs (never on the UI thread — VR freezes)
        var p = _profiles.Active;
        if (p is { IsPlayable: true })
        {
            if (screen.Name == "mods" &&
                !_modsCache.ContainsKey(p.GameVersion()) && !_beatModsUnavailable.Contains(p.GameVersion()))
                Task.Run(() => { GetAvail(p); Ui(RefreshCurrent); });
            if (!_modsScanCache.ContainsKey(p.Id))
                Task.Run(() => { RescanMods(p); Ui(RefreshCurrent); });
        }
        if (screen.Name == "settings")
        {
            var userCtl = FindCtl("acct_user");
            if (userCtl is LineEdit ue && ue.Text.Length == 0 && !string.IsNullOrEmpty(_settings.SteamCmdUser))
                ue.Text = _settings.SteamCmdUser;
            if (!string.IsNullOrEmpty(_settings.SteamCmdUser))
                SetTextSafe("acct_status", _steamcmd != null && _steamcmd.IsLoggedIn
                    ? "Logged in as " + _settings.SteamCmdUser + " — background downloads enabled."
                    : "Not logged in. Enter your Steam account once — the session is cached (password never stored).");
        }
    }

    bool _importOffered;

    void RescanMods(Profile p)
    {
        List<ModInstaller.InstalledMod> scan = null;
        string scanError = null;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            scan = _mods.ScanInstalled(p);
            if (_profiles.ActiveId == p.Id)
            {
                // installed sizes for the mods table — from the mod's own BSIPA manifest file list
                foreach (var im in scan)
                {
                    try
                    {
                        if (im.Files == null || im.Files.Count == 0) continue;
                        long sum = 0;
                        foreach (var f in im.Files)
                        {
                            var fp = System.IO.Path.Combine(p.Path, f.Replace('/', System.IO.Path.DirectorySeparatorChar));
                            if (File.Exists(fp)) sum += new FileInfo(fp).Length;
                        }
                        if (sum > 0) sizes[im.Name] = sum;
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { scanError = e.Message; }

        bool isActive = _profiles.ActiveId == p.Id;
        Ui(() =>
        {
            if (scan != null) _modsScanCache[p.Id] = scan;
            if (isActive)
            {
                _modSizes.Clear();
                foreach (var kv in sizes) _modSizes[kv.Key] = kv.Value;
                _desiredProfile = null; // desired state reseeds from the fresh scan
            }
            if (scanError != null) { _status = "Mod scan failed: " + scanError; RefreshStatusLabels(); }
        });
    }

    public override IEnumerable<Dictionary<string, string>> ProvideData(string key)
    {
        var p = _profiles.Active;
        switch (key)
        {
            case "profiles.list":
                return _profiles.Profiles.Select(pr => new Dictionary<string, string>
                {
                    ["id"] = pr.Id,
                    ["title"] = pr.Name,
                    ["subtitle"] = $"Beat Saber {Strip(pr.Version)}" + (pr.IsPlayable ? $" · {CountMods(pr)} mods" : " · not downloaded yet"),
                    ["active"] = pr.Id == _profiles.ActiveId ? "●" : "",
                    ["color"] = pr.Color,
                });

            case "mods.table":
            {
                if (p == null) return Stub("No active profile");
                if (!p.IsPlayable) return Stub("Active profile has no game files");
                if (!_modsScanCache.TryGetValue(p.Id, out var scan)) return Stub("Scanning installed mods…");
                if (!_modsCache.TryGetValue(p.GameVersion(), out var avail))
                    return _beatModsUnavailable.Contains(p.GameVersion()) ? Stub("BeatMods has no mods for this game version.") : Stub("Loading BeatMods…");

                if (_desiredProfile != p.Id)
                {
                    _modsDesired.Clear();
                    foreach (var im in scan) _modsDesired.Add(im.Name);
                    _desiredProfile = p.Id;
                }

                var installed = scan.ToDictionary(m => m.Name, m => m, StringComparer.OrdinalIgnoreCase);
                var rows = new List<Dictionary<string, string>>();
                foreach (var (category, modsInCat) in GroupByCategory(avail))
                {
                    rows.Add(new Dictionary<string, string> { ["_header"] = "1", ["title"] = category });
                    foreach (var m in modsInCat)
                    {
                        bool inst = installed.TryGetValue(m.Name, out var im);
                        bool update = inst && !string.IsNullOrEmpty(im.Version) && CompareVersions(m.Version, im.Version) > 0;
                        rows.Add(new Dictionary<string, string>
                        {
                            ["id"] = m.Name,
                            ["name"] = m.Name,
                            ["installed"] = inst ? (string.IsNullOrEmpty(im.Version) ? "✓" : im.Version) : "-",
                            ["installedColor"] = !inst ? "#6f7889" : update ? "#f2ad42" : "#3f9d63",
                            ["latest"] = m.Version,
                            ["size"] = inst && _modSizes.TryGetValue(m.Name, out var sz) ? FormatSize(sz) : "-",
                            ["description"] = m.Description ?? "",
                            ["sel"] = _modsDesired.Contains(m.Name) ? "1" : "0",
                            ["canUninstall"] = inst ? "1" : "0",
                        });
                    }
                }
                return rows;
            }

            case "versions.catalog":
            {
                var newest = _catalog.Catalog().FirstOrDefault();
                return _catalog.Catalog().Select(e =>
                {
                    bool dl = _catalog.IsDownloadable(e);
                    var owned = OwnedBy(e.Version);
                    string label, color;
                    if (owned != null) { label = "In profiles"; color = "#3f9d63"; }
                    else if (!dl) { label = "-"; color = "#2a3140"; }
                    else { label = "Download"; color = "#2f6fce"; }
                    string status = owned != null
                        ? $"already owned · profile '{owned.Name}' · has BS {Strip(owned.GameVersion())}"
                        : e == newest ? "latest"
                        : e.Recommended ? "recommended"
                        : "";
                    return new Dictionary<string, string>
                    {
                        ["id"] = e.Version,
                        ["version"] = "BS " + e.Version,
                        ["versionStatus"] = status,
                        ["releaseDate"] = ReleaseText(e),
                        ["downloadLabel"] = label,
                        ["downloadColor"] = color,
                    };
                });
            }

            default:
                return null;
        }
    }

    static readonly string[] CategoryOrder =
        { "Core", "Essential", "Library", "Cosmetic", "Gameplay", "Practice", "Multiplayer", "Streaming", "UI", "Utility", "Other" };

    static IEnumerable<(string, List<BeatModsClient.ModInfo>)> GroupByCategory(List<BeatModsClient.ModInfo> mods)
    {
        var groups = mods.GroupBy(m => string.IsNullOrWhiteSpace(m.Category) ? "Other" : m.Category.Trim())
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var cat in CategoryOrder)
            if (cat != "Other" && groups.Remove(cat, out var list))
                yield return (cat, list);
        foreach (var g in groups.Where(g => g.Key != "Other").OrderBy(g => g.Key).ToList())
            if (groups.Remove(g.Key, out var list))
                yield return (g.Key, list);
        if (groups.TryGetValue("Other", out var other)) yield return ("Other", other);
    }

    static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.##}MB" : $"{bytes / 1024.0:0.##}KB";

    static int CompareVersions(string a, string b)
    {
        var pa = a.Split('p')[0].Split('.');
        var pb = b.Split('p')[0].Split('.');
        for (int i = 0; i < 3; i++)
        {
            int.TryParse(pa.Length > i ? pa[i] : "0", out var xa);
            int.TryParse(pb.Length > i ? pb[i] : "0", out var xb);
            if (xa != xb) return xa.CompareTo(xb);
        }
        return string.CompareOrdinal(a, b);
    }

    static string Strip(string version) => (version ?? "").Split('_')[0];

    static string ReleaseText(VersionCatalog.Entry e)
    {
        if (e.ReleaseDate > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(e.ReleaseDate).ToString("yyyy-MM-dd"); }
            catch { }
        }
        if (e.Year.Length == 4 && e.Year[0] == '2' && int.TryParse(e.Year, out _)) return e.Year;
        return "-";
    }

    IEnumerable<Dictionary<string, string>> Stub(string msg) => new[] { new Dictionary<string, string> { ["id"] = "", ["title"] = msg, ["subtitle"] = "", ["state"] = "", ["stateColor"] = "#00000000", ["coverUrl"] = "", ["stats"] = "" } };

    int CountMods(Profile p)
    {
        if (!p.IsPlayable) return 0;
        return _modsScanCache.TryGetValue(p.Id, out var list) ? list.Count : 0;
    }

    /// <summary>A playable profile running exactly this game version.</summary>
    Profile OwnedBy(string version)
    {
        var target = VersionTuple(version);
        return _profiles.Profiles.FirstOrDefault(pr =>
        {
            if (!pr.IsPlayable) return false;
            return VersionTuple(pr.GameVersion()) == target;
        });
    }

    static (int, int, int) VersionTuple(string v)
    {
        var parts = v.Split('p')[0].Split('.');
        var r = (0, 0, 0);
        int.TryParse(parts[0], out r.Item1);
        if (parts.Length > 1) int.TryParse(parts[1], out r.Item2);
        if (parts.Length > 2) int.TryParse(parts[2], out r.Item3);
        return r;
    }

    static bool IsModdable(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var maj) || !int.TryParse(parts[1], out var min)) return true;
        if (maj > 1) return false;
        if (min < 40) return true;
        if (min > 40) return false;
        var patch = parts.Length > 2 ? parts[2] : "0";
        var pnum = new string(patch.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(pnum, out var pv) && pv <= 8;
    }

    // ---------------- actions ----------------

    public override void HandleActionFromHost(PanelHost host, string action, string param, UiWidget widget)
    {
        base.HandleActionFromHost(host, action, param, widget);

        if (action.StartsWith("nav."))
        {
            _status = "";
            var target = action["nav.".Length..];
            if (CenterHost != null && CenterHost.HasScreen(target))
                CenterShow(target);
            RefreshStatusLabels();
            return;
        }

        var p = _profiles.Active;
        switch (action)
        {
            case "profiles.import": DoImport(); break;

            case "profiles.reset.ask":
                OpenModal("resetModal");
                break;

            case "settings.steamcmd.login":
            {
                string user = FieldText("acct_user"), pass = FieldText("acct_pass"), code = FieldText("acct_code");
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                { _status = "Enter your Steam username and password first."; RefreshStatusLabels(); break; }
                _status = "Logging into Steam…";
                RefreshStatusLabels();
                LoadBegin("Logging into Steam…");
                Task.Run(() =>
                {
                    bool ok = _steamcmd.Login(user, pass, code);
                    Ui(() =>
                    {
                        _status = ok ? $"Logged in as {user} — background downloads enabled." : "Steam login failed — check username, password and 2FA code.";
                        if (ok) SetTextSafe("acct_status", "Logged in as " + user + ". You can clear these fields — the session stays cached in the tools folder.");
                        RefreshStatusLabels();
                        RefreshCurrent();
                        LoadEnd();
                        if (ok && _pendingVersion != null)
                        {
                            var v = _pendingVersion;
                            _pendingVersion = null;
                            StartVersionDownload(v); // continue what the user originally asked for
                        }
                    });
                });
                break;
            }

            case "login.choice": // loginModal on the versions screen
                CloseModal("loginModal");
                if (param == "log in with password")
                {
                    CancelQrSession();
                    CenterShow("settings"); // pending download resumes after a successful login
                }
                else if (param == "use steam console" && _pendingEntry != null)
                {
                    CancelQrSession();
                    var profile = PendingOrCreate(_pendingVersion, _pendingEntry);
                    _pendingVersion = null;
                    StartConsoleDownload(profile, _pendingEntry);
                    _pendingEntry = null;
                }
                else CancelPending("Login cancelled.");
                break;

            case "import.choice":
                if (param == "import") DoImport();
                else CloseModal("importModal");
                break;

            case "profiles.activate":
                _profiles.Activate(param);
                RefreshCurrent();
                _status = "Active profile switched.";
                RefreshStatusLabels();
                break;

            case "launch.active":
                if (p != null) LaunchProfile(p);
                break;

            case "launch.profile":
                var lp = _profiles.Profiles.FirstOrDefault(x => x.Id == param);
                if (lp != null) LaunchProfile(lp);
                break;

            case "profiles.menu":
                _removeTargetId = param;
                OpenModal("removeModal");
                break;

            case "profiles.rename.ask":
                _renameTargetId = param;
                var current = _profiles.Profiles.FirstOrDefault(x => x.Id == param);
                if (FindCtl("renameModal") is UiModal m)
                    m.SetInput("Profile name", current?.Name ?? "");
                OpenModal("renameModal");
                break;

            case "profiles.rename": // via renameModal
                if (param == "rename" && _renameTargetId != null)
                {
                    var newName = (FindCtl("renameModal") as UiModal)?.InputValue;
                    if (_profiles.Rename(_renameTargetId, newName))
                        _status = "Profile renamed.";
                    else
                        _status = "Rename failed — the name was empty or unchanged.";
                    _renameTargetId = null;
                    RefreshCurrent();
                    RefreshStatusLabels();
                }
                CloseModal("renameModal");
                break;

            case "profiles.remove": // via removeModal — Delete = remove from list AND wipe the folder
                if (_removeTargetId != null && param == "delete")
                {
                    var rp = _profiles.Profiles.FirstOrDefault(x => x.Id == _removeTargetId);
                    if (rp != null)
                    {
                        var instances = System.IO.Path.GetFullPath(_settings.EffectiveInstancesDir());
                        var target = System.IO.Path.GetFullPath(rp.Path);
                        if (target.StartsWith(instances, System.StringComparison.OrdinalIgnoreCase))
                        {
                            try { Directory.Delete(target, true); _status = $"Profile '{rp.Name}' deleted."; }
                            catch (Exception e) { _status = $"Profile '{rp.Name}' removed from the list — folder delete failed: {e.Message}"; }
                        }
                        else _status = $"Profile '{rp.Name}' removed — its folder is outside the Instances folder and was kept.";
                        _profiles.Remove(rp.Id);
                    }
                    _removeTargetId = null;
                    RefreshCurrent();
                    RefreshStatusLabels();
                }
                CloseModal("removeModal");
                break;

            case "profiles.reset": // via resetModal in settings
                if (param == "reset")
                {
                    _status = "Resetting BSManager profiles…";
                    RefreshStatusLabels();
                    LoadBegin("Resetting BSManager profiles…");
                    Task.Run(() =>
                    {
                        var res = BsManagerImport.Reset(_settings, _profiles);
                        var import = BsManagerImport.Import(_settings, _profiles);
                        Ui(() =>
                        {
                            _status = $"Reset done: {res.Updated} removed, then {import.Summary}.";
                            RefreshCurrent();
                            RefreshStatusLabels();
                            LoadEnd();
                        });
                    });
                }
                CloseModal("resetModal");
                break;

            case "mods.select": // checkbox flip: param "name|on/off"
            {
                var parts = (param ?? "").Split('|');
                if (parts.Length == 2)
                {
                    _lastModName = parts[0];
                    if (parts[1] == "on") _modsDesired.Add(parts[0]);
                    else _modsDesired.Remove(parts[0]);
                }
                break;
            }

            case "mods.apply":
            {
                if (p == null || !p.IsPlayable) { _status = "Active profile has no game files."; RefreshStatusLabels(); break; }
                var scanNow = _modsScanCache.TryGetValue(p.Id, out var sl) ? sl : new List<ModInstaller.InstalledMod>();
                var installedNames = scanNow.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var toInstall = _modsDesired.Where(n => !installedNames.Contains(n)).ToList();
                var toRemove = installedNames.Where(n => !_modsDesired.Contains(n)).ToList();
                if (toInstall.Count == 0 && toRemove.Count == 0)
                { _status = "Nothing to change — every mod already matches."; RefreshStatusLabels(); break; }
                LoadBegin($"Applying mods — {toInstall.Count} installs, {toRemove.Count} removals…");
                Task.Run(() =>
                {
                    var avail = GetAvail(p);
                    foreach (var name in toInstall)
                    {
                        var mod = avail.FirstOrDefault(m => m.Name == name);
                        if (mod == null) { OnCoreStatus($"'{name}' is not on BeatMods for this game version."); continue; }
                        try { _mods.Install(p, mod); } catch (Exception e) { OnCoreStatus($"Install failed — {name}: " + e.Message); }
                    }
                    foreach (var name in toRemove)
                    {
                        try { _mods.Uninstall(p, name); } catch (Exception e) { OnCoreStatus($"Uninstall failed — {name}: " + e.Message); }
                    }
                    RescanMods(p);
                    Ui(() => { RefreshCurrent(); LoadEnd(); });
                });
                break;
            }

            case "mods.moreinfo":
            {
                var availList = p != null && _modsCache.TryGetValue(p.GameVersion(), out var av) ? av : null;
                var mod = availList?.FirstOrDefault(m => m.Name.Equals(_lastModName, StringComparison.OrdinalIgnoreCase));
                var url = mod?.Link;
                if (string.IsNullOrEmpty(url))
                { _status = "Tick a mod's checkbox first — More info opens its page."; RefreshStatusLabels(); break; }
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception e) { _status = "Could not open the link: " + e.Message; RefreshStatusLabels(); }
                break;
            }

            case "mods.uninstall":
                if (p != null)
                {
                    LoadBegin("Uninstalling " + param + "…");
                    Task.Run(() =>
                    {
                        _mods.Uninstall(p, param);
                        RescanMods(p);
                        Ui(() => { RefreshCurrent(); LoadEnd(); });
                    });
                }
                break;

            case "mods.installbsipa":
                if (p == null || !p.IsPlayable) { _status = "Active profile has no game files."; RefreshStatusLabels(); break; }
                LoadBegin("Installing BSIPA…");
                Task.Run(() =>
                {
                    try { _mods.InstallBsipa(p); RescanMods(p); }
                    catch (Exception e) { OnCoreStatus("BSIPA install failed: " + e.Message); }
                    Ui(() => { RefreshCurrent(); LoadEnd(); });
                });
                break;

            case "mods.refresh":
                if (p != null)
                {
                    LoadBegin("Loading BeatMods…");
                    Task.Run(() =>
                    {
                        _modsCache.Remove(p.GameVersion());
                        _beatModsUnavailable.Remove(p.GameVersion());
                        GetAvail(p);
                        Ui(() => { RefreshCurrent(); LoadEnd(); });
                    });
                }
                else RefreshCurrent();
                break;

            case "versions.download":
                StartVersionDownload(param);
                break;

            case "versions.refresh":
                _status = "Refreshing version catalog…";
                RefreshStatusLabels();
                LoadBegin("Refreshing version catalog…");
                Task.Run(() => { _catalog.RefreshAsync().Wait(); Ui(() => { RefreshCurrent(); LoadEnd(); }); });
                break;

            case "settings.detect":
                DetectSteamBs();
                _settings.Save();
                RefreshCurrent();
                _status = "Steam Beat Saber: " + (string.IsNullOrEmpty(_settings.SteamBsPath) ? "not found" : _settings.SteamBsPath);
                RefreshStatusLabels();
                break;

            case "modal.button":
                break;
        }
    }

    string _removeTargetId;
    string _renameTargetId;
    readonly Dictionary<string, List<BeatModsClient.ModInfo>> _modsCache = new();

    List<BeatModsClient.ModInfo> GetAvail(Profile p)
    {
        var v = p.GameVersion();
        if (!_modsCache.TryGetValue(v, out var list) || list.Count == 0)
        {
            try
            {
                list = new BeatModsClient().GetMods(v);
                Ui(() => OnCoreStatusStatic($"BeatMods: {list.Count} mods for {v}"));
            }
            catch (System.Net.Http.HttpRequestException e) when (e.Message.Contains("400"))
            {
                list = new();
                _beatModsUnavailable.Add(v); // BeatMods has nothing for this version — stop retrying
                Ui(() => OnCoreStatusStatic($"BeatMods lists no mods for {v}."));
            }
            catch (Exception e)
            {
                list = new();
                Ui(() => OnCoreStatusStatic("BeatMods fetch failed: " + e.Message));
            }
            _modsCache[v] = list; // empty lists are retried on next Refresh/Mods screen visit
        }
        return list;
    }

    void OnCoreStatusStatic(string s) { _status = s; RefreshStatusLabels(); }

    void OnCoreStatus(string s) => Ui(() => { _status = s; RefreshStatusLabels(); });

    void LaunchProfile(Profile p)
    {
        var exe = System.IO.Path.Combine(p.Path, "Beat Saber.exe");
        if (!File.Exists(exe)) { _status = $"Beat Saber.exe not found in '{p.Name}'."; RefreshStatusLabels(); return; }

        // Beat Saber restarts itself through Steam when launched directly, which makes Steam
        // close this copy and open the one from its own library. A steam_appid.txt next to
        // the exe tells Steamworks the game is already running and stops that restart.
        // Skipped for the actual Steam install — Steam restarting it is harmless there.
        bool isSteamInstall = false;
        if (!string.IsNullOrEmpty(_settings.SteamBsPath))
            isSteamInstall = System.IO.Path.GetFullPath(p.Path).Equals(
                System.IO.Path.GetFullPath(_settings.SteamBsPath), System.StringComparison.OrdinalIgnoreCase);
        if (!isSteamInstall && !File.Exists(System.IO.Path.Combine(p.Path, "steam_appid.txt")))
        {
            try { File.WriteAllText(System.IO.Path.Combine(p.Path, "steam_appid.txt"), SteamConsoleDownload.AppId.ToString()); }
            catch { }
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = p.Path });
            _status = $"Launching '{p.Name}' — Beat Saber {p.GameVersion()}…";
        }
        catch (Exception e) { _status = "Launch failed: " + e.Message; }
        RefreshStatusLabels();
    }

    // ---------------- BSManager import + popup ----------------

    void MaybeOfferImport()
    {
        if (BsManagerImport.IsAvailable(_settings) && _profiles.Profiles.Count == 0)
        {
            OpenModal("importModal");
        }
    }

    void DoImport()
    {
        _status = "Importing BSManager profiles…";
        RefreshStatusLabels();
        LoadBegin("Importing BSManager profiles…");
        Task.Run(() =>
        {
            var res = BsManagerImport.Import(_settings, _profiles);
            Ui(() =>
            {
                _status = $"BSManager import: {res.Summary}." + (res.Notes.Count > 0 ? " " + string.Join(" ", res.Notes) : "");
                RefreshCurrent();
                RefreshStatusLabels();
                CloseModal("importModal");
                LoadEnd();
            });
        });
    }

    // ---------------- version download ----------------

    void StartVersionDownload(string version)
    {
        var entry = _catalog.Find(version);
        if (entry == null) { _status = $"Unknown version '{version}'."; RefreshStatusLabels(); return; }
        if (!_catalog.IsDownloadable(entry))
        {
            _status = $"Version {version} has no manifest ID yet and is not the newest — cannot download. Refresh the catalog later.";
            RefreshStatusLabels();
            return;
        }

        if (SteamCmdDownload.IsAvailable && _steamcmd.IsLoggedIn)
        {
            // fully background: no Steam console, works from SteamOS/Steam Frame too
            var profile = PendingOrCreate(version, entry);
            LoadBegin($"Downloading Beat Saber {version}…");
            _status = $"Downloading Beat Saber {version} → profile '{profile.Name}'…";
            RefreshStatusLabels();
            _steamcmd.Download(profile, entry.Manifest, ok => Ui(() =>
            {
                LoadEnd();
                AfterDownload(profile, ok);
            }));
            return;
        }

        if (DepotDownload.HasCachedLogin())
        {
            // DepotDownloader already has a cached login (from a previous QR approval)
            var profile = PendingOrCreate(version, entry);
            StartDepotDownload(profile, entry);
            return;
        }

        // not logged in at all: fetch the helpers if needed, then show the QR login popup;
        // the download resumes automatically once the QR is approved (or after a password
        // login in Settings)
        _pendingVersion = version;
        _pendingEntry = entry;
        if (_pendingProfile == null)
            _pendingProfile = _profiles.CreateInstance("BS " + version, version, entry.Manifest);

        _status = "Preparing the one-time login…";
        RefreshStatusLabels();
        LoadBegin("Preparing download helpers…");
        var myVersion = version;
        Task.Run(() =>
        {
            DepotDownload.EnsureInstalled(ddOk =>
            {
                SteamCmdDownload.EnsureInstalled(scOk =>
                {
                    Ui(() =>
                    {
                        LoadEnd();
                        if (_pendingVersion != myVersion) return; // cancelled meanwhile
                        _status = ddOk
                            ? "Scan the QR code with the Steam mobile app to approve the login."
                            : scOk
                                ? "Helpers ready — log in once with your Steam account."
                                : "Could not download the login helpers — check your internet connection.";
                        RefreshStatusLabels();
                        if (ddOk) StartQrSession();
                        OpenModal("loginModal");
                    });
                });
            });
        });
    }

    void StartDepotDownload(Profile profile, VersionCatalog.Entry entry)
    {
        LoadBegin($"Downloading Beat Saber {entry.Version}…");
        _status = $"Downloading Beat Saber {entry.Version} → profile '{profile.Name}'…";
        RefreshStatusLabels();
        var session = new DepotDownload(_settings);
        session.OnStatus = s => OnCoreStatus(s);
        session.OnDone += ok => Ui(() =>
        {
            LoadEnd();
            AfterDownload(profile, ok);
        });
        session.Start(profile, entry.Manifest);
    }

    Profile PendingOrCreate(string version, VersionCatalog.Entry entry)
    {
        if (_pendingProfile != null) { var p = _pendingProfile; _pendingProfile = null; return p; }
        return _profiles.CreateInstance("BS " + version, version, entry.Manifest);
    }

    /// <summary>Shared tail of every download path: activate the profile, then auto-install
    /// BSIPA + SongCore (with their dependencies) when BeatMods supports the version.</summary>
    void AfterDownload(Profile profile, bool ok)
    {
        if (ok) _profiles.Activate(profile.Id);
        RefreshCurrent();
        if (!ok) return;
        LoadBegin("Installing BSIPA and SongCore…");
        Task.Run(() =>
        {
            AutoInstallCore(profile);
            Ui(() => { RefreshCurrent(); LoadEnd(); });
        });
    }

    void AutoInstallCore(Profile p)
    {
        try
        {
            var avail = GetAvail(p);
            if (avail.Count == 0)
            {
                OnCoreStatus("BeatMods has no mods for this game version — BSIPA/SongCore skipped.");
                return;
            }
            var scan = _mods.ScanInstalled(p);
            foreach (var name in new[] { "BSIPA", "SongCore" })
            {
                if (scan.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    OnCoreStatus($"{name} is already installed.");
                    continue;
                }
                var mod = avail.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (mod == null)
                {
                    OnCoreStatus($"{name} is not on BeatMods for this game version.");
                    continue;
                }
                try
                {
                    _mods.Install(p, mod); // dependencies resolve automatically
                    OnCoreStatus($"Auto-installed {name} {mod.Version}.");
                }
                catch (Exception e)
                {
                    OnCoreStatus($"Auto-install failed for {name}: " + e.Message);
                }
            }
            RescanMods(p);
        }
        catch (Exception e)
        {
            OnCoreStatus("Auto-install failed: " + e.Message);
        }
    }

    // ---------------- QR login (DepotDownloader -qr) ----------------

    void StartQrSession()
    {
        CancelQrSession();
        var session = new DepotDownload(_settings);
        _qrLogin = session;
        session.OnQr += url =>
        {
            DepotDownload.DepotLog("app: QR url received: " + url);
            Ui(() =>
            {
                if (_qrLogin == session && FindCtl("loginModal") is UiModal m)
                    m.SetImage(MakeQrTexture(url));
                else DepotDownload.DepotLog($"app: QR dropped (login={_qrLogin == session}, modal={FindCtl("loginModal") != null})");
            });
        };
        session.OnStatus += s => { if (_qrLogin == session) OnCoreStatus(s); };
        session.OnDone += ok => Ui(() =>
        {
            if (_qrLogin != session) return; // cancelled or replaced — already cleaned up
            _qrLogin = null;
            CloseModal("loginModal");
            if (ok)
            {
                _status = "Steam login approved — download complete.";
                var prof = _pendingProfile;
                _pendingProfile = null;
                _pendingVersion = null;
                _pendingEntry = null;
                if (prof != null) AfterDownload(prof, true);
            }
            else
            {
                _status = "QR login failed — try Download again, or log in with your password in Settings.";
            }
            RefreshStatusLabels();
            RefreshCurrent();
        });
        session.Start(_pendingProfile, _pendingEntry.Manifest, qr: true);
    }

    void CancelQrSession()
    {
        if (_qrLogin == null) return;
        _qrLogin.Cancel();
        _qrLogin = null;
    }

    void CancelPending(string message)
    {
        CancelQrSession();
        if (_pendingProfile != null)
        {
            _profiles.Remove(_pendingProfile.Id);
            try { Directory.Delete(_pendingProfile.Path, true); } catch { }
            _pendingProfile = null;
        }
        _pendingVersion = null;
        _pendingEntry = null;
        _status = message;
        RefreshStatusLabels();
    }

    static Texture2D MakeQrTexture(string url)
    {
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(4);
            var img = new Image();
            img.LoadPngFromBuffer(png);
            var tex = ImageTexture.CreateFromImage(img);
            DepotDownload.DepotLog($"app: QR texture created ({png.Length} bytes png)");
            return tex;
        }
        catch (Exception e)
        {
            DepotDownload.DepotLog("app: QR texture FAILED: " + e.Message);
            return null;
        }
    }

    void StartConsoleDownload(Profile profile, VersionCatalog.Entry entry)
    {
        LoadBegin($"Downloading Beat Saber {entry.Version}…");
        _status = $"Downloading Beat Saber {entry.Version} via Steam console → profile '{profile.Name}'…";
        RefreshStatusLabels();
        var console = new SteamConsoleDownload(_settings) { OnStatus = s => OnCoreStatus(s) };
        console.OnProgress = (pct, text) => Ui(() =>
        {
            _status = text;
            foreach (var h in Hosts)
            {
                h.Loading.ShowLoading(text); // live progress on the overlay
                var bar = h.Ctx.ById.TryGetValue("downloadProgress", out var c) ? c : null;
                if (bar != null) UiRuntime.SetProgressValue(bar, pct);
            }
            RefreshStatusLabels();
        });
        console.OnWrongManifest += m =>
        {
            _catalog.BlacklistManifest(m);
            Ui(RefreshCurrent);
        };
        console.Start(profile, entry.Manifest, ok => Ui(() =>
        {
            LoadEnd();
            AfterDownload(profile, ok);
        }));
    }

    // ---------------- settings/detect ----------------

    void DetectSteamBs()
    {
        if (!string.IsNullOrEmpty(_settings.SteamBsPath) && Directory.Exists(_settings.SteamBsPath)) return;
        foreach (var lib in SteamConsoleDownload.SteamLibraries())
        {
            var manifest = System.IO.Path.Combine(lib, "steamapps", "appmanifest_620980.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var text = File.ReadAllText(manifest);
                var idx = text.IndexOf("\"installdir\"");
                if (idx < 0) continue;
                var start = text.IndexOf('"', idx + 12) + 1;
                var end = text.IndexOf('"', start);
                var installdir = text[start..end];
                var path = System.IO.Path.Combine(lib, "steamapps", "common", installdir);
                if (Directory.Exists(path)) { _settings.SteamBsPath = path; _settings.Save(); return; }
            }
            catch { }
        }
    }

    // ---------------- status bar ----------------

    void RefreshStatusLabels()
    {
        var p = _profiles.Active;
        SetTextSafe("statusLine", _status);
        SetTextSafe("statusBar", _status);
        SetTextSafe("downloadStatus", _status);
        SetTextSafe("importStatus", _status);
        SetTextSafe("leftStatus", _status);
        SetTextSafe("rightStatus", _status);
        SetTextSafe("modsStatus", _status);
        if (p != null)
        {
            SetTextSafe("activeProfile", p.Name + (p.IsPlayable ? "" : " (not downloaded)"));
            SetTextSafe("profileVersion", "Beat Saber " + Strip(p.IsPlayable ? p.GameVersion() : p.Version));
            SetTextSafe("profileMods", $"Mods: {CountMods(p)}");
            SetTextSafe("modsProfileName", "Profile: " + p.Name);
        }
        // right (launch) panel
        SetTextSafe("activeName", p != null ? p.Name : "No profile");
        SetTextSafe("activeVersion", p != null
            ? "Beat Saber " + Strip(p.IsPlayable ? p.GameVersion() : p.Version) + (p.IsPlayable ? "" : " · not downloaded")
            : "Create one from the Versions screen");
    }

    protected override string DebugDiag() =>
        $",\"baseDir\":\"{EscapeJson(AppDomain.CurrentDomain.BaseDirectory)}\"" +
        $",\"exeDir\":\"{EscapeJson(SteamCmdDownload.ExeDir)}\"" +
        $",\"steamcmd\":{(SteamCmdDownload.IsAvailable ? "true" : "false")}" +
        $",\"steamLoggedIn\":{(_steamcmd != null && _steamcmd.IsLoggedIn ? "true" : "false")}" +
        $",\"profiles\":{_profiles?.Profiles.Count ?? 0}" +
        $",\"status\":\"{EscapeJson(_status)}\"";

    static string EscapeJson(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    void RefreshCurrent()
    {
        foreach (var h in Hosts)
            if (!string.IsNullOrEmpty(h.Current) && h.HasScreen(h.Current))
                h.Show(h.Current);
    }
}
