using Godot;

namespace BSVRManager;

/// <summary>Entry point (full-rect root control). VR is attempted first; when no VR runtime /
/// headset answers at boot, AppMain relaunches once into a clean desktop window
/// (marked with --desktop-launched so it never loops).</summary>
public partial class Main : Control
{
    public override void _Ready()
    {
        var args = OsCmdArgs();
        bool vr = !args.Contains("--desktop-launched") && !args.Contains("--desktop");
        DisplayServer.WindowSetTitle("BS VR Manager v" + App.AppInfo.Version);
        var app = new App.ManagerApp(allowVr: vr);
        AddChild(app);
    }

    static System.Collections.Generic.List<string> OsCmdArgs()
    {
        var all = new System.Collections.Generic.List<string>(OS.GetCmdlineArgs());
        all.AddRange(OS.GetCmdlineUserArgs());
        return all;
    }
}
