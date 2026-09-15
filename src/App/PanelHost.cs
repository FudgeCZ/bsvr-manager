using Godot;
using BSVRManager.UiKit;

namespace BSVRManager.App;

/// <summary>One UI panel: its own context/Byid, current screen, loading overlay.</summary>
public partial class PanelHost : Control
{
    public readonly string HostName;
    public readonly float DesignWidth, DesignHeight;
    public readonly string[] Screens;
    public AppMain App;
    public UiContext Ctx;
    public Control ScreenRoot;
    public string Current = "";
    public LoadingOverlay Loading;

    public PanelHost(string hostName, float designW, float designH, string[] screens, AppMain app)
    {
        HostName = hostName;
        DesignWidth = designW;
        DesignHeight = designH;
        Screens = screens;
        App = app;
        Name = "Host_" + hostName;
        MouseFilter = MouseFilterEnum.Stop;

        Ctx = new UiContext { EditMode = false };
        Ctx.OnAction = (a, p, w) => App.HandleActionFromHost(this, a, p, w);
        Ctx.GetData = key => App.ProvideData(key);
        Ctx.GetImage = src => App.LoadImageFor(this, src);
        Ctx.GlobalTokens["version"] = AppInfo.Version;

        Loading = new LoadingOverlay();
        AddChild(Loading);
    }

    public bool HasScreen(string name) => System.Array.IndexOf(Screens, name) >= 0;

    public void Show(string name)
    {
        if (!App.Screens.TryGetValue(name, out var screen)) return;
        Current = name;
        if (ScreenRoot != null) { ScreenRoot.QueueFree(); ScreenRoot = null; }
        Ctx.ById.Clear();
        ScreenRoot = UiLoader.BuildScreen(screen, Ctx);
        AddChild(ScreenRoot);

        // VR: the SubViewport is exactly the design size (scale 1).
        // Desktop: scale the design to fit the column.
        if (GetParent() is SubViewport)
        {
            ScreenRoot.Scale = Vector2.One;
            ScreenRoot.Position = Vector2.Zero;
        }
        else
        {
            float s = Mathf.Min(Size.X / DesignWidth, Size.Y / DesignHeight);
            ScreenRoot.Scale = new Vector2(s, s);
        }
        ScreenRoot.MoveToFront();
        Loading.MoveToFront();
        App.OnPanelScreenShown(this, screen);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized && ScreenRoot != null && GetParent() is not SubViewport)
        {
            float s = Mathf.Min(Size.X / DesignWidth, Size.Y / DesignHeight);
            ScreenRoot.Scale = new Vector2(s, s);
        }
    }
}
