using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Godot;
using BSVRManager.Core;
using BSVRManager.UiKit;

namespace BSVRManager.App;

/// <summary>
/// App mode. Three UI panels: left = Profiles (+Add), center = dashboard/mods/maps (+versions/settings),
/// right = Launch + Settings. In VR the panels float in 3D around the player; on desktop they
/// appear as three columns. Exposes a debug HTTP API with --debug-api.
/// </summary>
public partial class AppMain : Control
{
    public readonly Dictionary<string, UiScreen> Screens = new();
    public PanelHost LeftHost, CenterHost, RightHost;
    public string CurrentCenterScreen = "";
    public string LastAction = "";
    public string LastParam = "";
    public bool VrActive;

    readonly bool _allowVr;
    readonly bool _debugApi;
    HttpListener _listener;
    Thread _listenerThread;

    public AppMain(bool allowVr)
    {
        Name = "AppMain";
        _allowVr = allowVr;
        var args = new List<string>(OS.GetCmdlineArgs());
        args.AddRange(OS.GetCmdlineUserArgs());
        _debugApi = args.Contains("--debug-api");
    }

    string UiDirAbs => ProjectSettings.GlobalizePath("res://ui");

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        LoadScreens();

        // VR hosts are built in a deferred call — show initial screens after they exist.
        if (_allowVr) { CallDeferred(nameof(SetupVr)); CallDeferred(nameof(ShowInitial)); }
        else { StartDesktop(); ShowInitial(); }

        if (_debugApi) StartDebugApi();
        OnAppReady();
    }

    protected virtual void OnAppReady() { }

    void ShowInitial()
    {
        if (CenterHost != null) CenterShow(Screens.ContainsKey("dashboard") ? "dashboard" : FirstScreen());
        LeftHost?.Show("profilesPanel");
        RightHost?.Show("launchPanel");
    }

    void SetupVr()
    {
        try
        {
            var xr = XRServer.FindInterface("OpenXR");
            if (xr == null || !xr.IsInitialized())
            {
                GD.Print("[vr] OpenXR not initialized at boot — desktop columns mode");
                StartDesktop();
                return;
            }
            GetViewport().UseXR = true;
            VrActive = true;
            GD.Print("[vr] OpenXR active");

            // three panels, each its own viewport: left (profiles), center (main), right (launch)
            var made = new Dictionary<string, (SubViewport vp, PanelHost host)>();
            foreach (var (name, w, h) in new[] { ("left", 560f, 900f), ("center", 1600f, 900f), ("right", 460f, 900f) })
            {
                var vp = new SubViewport
                {
                    Size = new Vector2I((int)w, (int)h),
                    RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                    TransparentBg = false,
                };
                GetTree().Root.AddChild(vp);
                var host = new PanelHost(name, w, h, PanelScreens(name), this);
                vp.AddChild(host);
                made[name] = (vp, host);
            }
            LeftHost = made["left"].host; CenterHost = made["center"].host; RightHost = made["right"].host;

            var vrHost = new VrHost(this,
                made["left"].vp, made["center"].vp, made["right"].vp,
                new[] { 0.55f, 1.42f, 0.46f },   // panel widths (m)
                new[] { 0.895f, 0.798f, 0.9f }); // panel heights (m)
            AddChild(vrHost);
        }
        catch (Exception e)
        {
            GD.PushWarning("[vr] init failed: " + e.Message);
            VrActive = false;
            StartDesktop();
        }
    }

    static readonly string[] CenterScreens = { "dashboard", "profiles", "mods", "maps", "versions", "settings" };
    static string[] PanelScreens(string panel) => panel == "left"
        ? new[] { "profilesPanel" }
        : panel == "right" ? new[] { "launchPanel" }
        : CenterScreens;

    public virtual void OnPanelScreenShown(PanelHost host, UiScreen screen) { }

    public virtual void HandleActionFromHost(PanelHost host, string action, string param, UiWidget widget)
    {
        LastAction = action; LastParam = param;
        GD.Print($"[action] {action} {param}");
    }

    public virtual IEnumerable<Dictionary<string, string>> ProvideData(string key) => null;

    public virtual Texture2D LoadImageFor(PanelHost host, string src) => LoadTexture(src);

    public void OpenModalOnAny(string id)
    {
        foreach (var h in new[] { LeftHost, CenterHost, RightHost })
        {
            if (h?.Ctx != null && h.Ctx.ById.TryGetValue(id, out var c) && c is UiKit.UiModal m) { m.Open(); return; }
        }
    }

    public void CloseModalOnAny(string id)
    {
        foreach (var h in new[] { LeftHost, CenterHost, RightHost })
        {
            if (h?.Ctx != null && h.Ctx.ById.TryGetValue(id, out var c) && c is UiKit.UiModal m) { m.Close(); return; }
        }
    }

    void StartDesktop()
    {
        // desktop (non-VR): just the main panel, filling the window
        try
        {
            var xr = XRServer.FindInterface("OpenXR");
            if (xr != null && xr.IsInitialized()) GetViewport().UseXR = true;
        }
        catch { }

        float totalW = Size.X > 0 ? Size.X : 1584;
        CenterHost = MakeDesktopHost("center", 1600, 900, 0, totalW);
    }

    PanelHost MakeDesktopHost(string name, float designW, float designH, float x, float w)
    {
        var host = new PanelHost(name, designW, designH, PanelScreens(name), this);
        host.SetAnchorsPreset(LayoutPreset.FullRect);
        host.OffsetLeft = x;
        host.OffsetTop = 0;
        host.OffsetRight = x + w;
        host.OffsetBottom = Size.Y > 0 ? Size.Y : 980;
        AddChild(host);
        return host;
    }

    void LoadScreens()
    {
        foreach (var kv in UiKit.ResourceFS.ReadScreens())
        {
            try
            {
                var s = UiKit.UiSchemaIO.Parse(kv.Value, kv.Key);
                Screens[s.Name] = s;
            }
            catch (Exception e) { GD.PushWarning($"bad screen {kv.Key}: {e.Message}"); }
        }
    }

    string FirstScreen()
    {
        foreach (var kv in Screens) return kv.Key;
        return null;
    }

    public void ShowScreen(string name) => CenterShow(name);

    /// <summary>Navigation targets the center panel.</summary>
    public void CenterShow(string name)
    {
        if (CenterHost == null) return;
        CenterHost.Show(name);
        CurrentCenterScreen = name;
    }

    // ---------------- debug API ----------------

    void StartDebugApi()
    {
        try
        {
            int port = 8790;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _listenerThread = new Thread(() =>
            {
                while (_listener != null && _listener.IsListening)
                {
                    try { HandleDebug(_listener.GetContext()); }
                    catch { break; }
                }
            }) { IsBackground = true };
            _listenerThread.Start();
            GD.Print($"[debug-api] listening on http://127.0.0.1:{port}/state");
        }
        catch (Exception e) { GD.PushWarning($"debug-api failed: {e.Message}"); }
    }

    /// <summary>Extra diagnostic fields; overridden by the real app to expose service state.</summary>
    protected virtual string DebugDiag() => "";

    void HandleDebug(HttpListenerContext httpCtx)
    {
        var path = httpCtx.Request.Url.AbsolutePath;
        string body;
        if (path == "/state")
        {
            var diag = DebugDiag();
            body = $"{{\"screen\":\"{CurrentCenterScreen}\",\"lastAction\":\"{LastAction}\",\"lastParam\":\"{LastParam}\",\"vr\":{VrActive.ToString().ToLower()}{diag}}}";
        }
        else if (path == "/click" && httpCtx.Request.HttpMethod == "POST")
        {
            using var rd = new StreamReader(httpCtx.Request.InputStream);
            var json = rd.ReadToEnd();
            var x = ExtractNum(json, "x");
            var y = ExtractNum(json, "y");
            CallDeferred(nameof(DeferredClick), x, y);
            body = "{\"ok\":true}";
        }
        else if (path == "/invoke" && httpCtx.Request.HttpMethod == "POST")
        {
            using var rd = new StreamReader(httpCtx.Request.InputStream);
            var json = rd.ReadToEnd();
            var action = ExtractStr(json, "action");
            var param = ExtractStr(json, "param");
            CallDeferred(nameof(DeferredInvoke), action, param);
            body = "{\"ok\":true}";
        }
        else body = "{\"help\":[/state,/click x,y,/invoke action,param]}";

        var buf = Encoding.UTF8.GetBytes(body);
        httpCtx.Response.ContentType = "application/json";
        httpCtx.Response.ContentLength64 = buf.Length;
        httpCtx.Response.OutputStream.Write(buf);
        httpCtx.Response.OutputStream.Close();
    }

    static float ExtractNum(string json, string key)
    {
        var idx = json.IndexOf(key, System.StringComparison.Ordinal);
        if (idx < 0) return 0;
        var span = json[(idx + key.Length + 1)..];
        var sb = new StringBuilder();
        foreach (var c in span)
        {
            if (char.IsDigit(c) || c == '.' || c == '-') sb.Append(c);
            else if (sb.Length > 0) break;
        }
        return float.TryParse(sb.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    static string ExtractStr(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        catch { }
        return "";
    }

    void DeferredInvoke(string action, string param) =>
        HandleActionFromHost(CenterHost ?? LeftHost ?? RightHost, action, param, null);

    void DeferredClick(float x, float y)
    {
        // desktop columns: hit the host column containing the point
        foreach (var host in new[] { LeftHost, CenterHost, RightHost })
        {
            if (host == null) continue;
            var gx = host.GetGlobalTransformWithCanvas();
            var rect = new Rect2(gx.Origin, host.Size * host.Scale);
            if (rect.HasPoint(new Vector2(x, y)))
            {
                var local = (new Vector2(x, y) - gx.Origin) / host.Scale;
                PushClick(GetViewport(), local);
                return;
            }
        }
    }

    static void PushClick(Viewport vp, Vector2 pos)
    {
        var press = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos };
        Input.ParseInputEvent(press);
        var release = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos };
        Input.ParseInputEvent(release);
    }

    protected override void Dispose(bool disposing)
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        base.Dispose(disposing);
    }

    Texture2D LoadTexture(string src)
    {
        try
        {
            if (string.IsNullOrEmpty(src)) return null;
            if (src.StartsWith("res://"))
                return GD.Load<Texture2D>(src);
            var path = src.StartsWith("http") ? HttpCache.Fetch(src) : src;
            if (path == null || !File.Exists(path)) return null;
            using var img = Image.LoadFromFile(path);
            return img == null ? null : ImageTexture.CreateFromImage(img);
        }
        catch { return null; }
    }
}
