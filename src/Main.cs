using Godot;

namespace BSVRManager;

/// <summary>Entry point (full-rect root control).</summary>
public partial class Main : Control
{
    public override void _Ready()
    {
        var args = OsCmdArgs();
        bool desktop = args.Contains("--desktop");
        var app = new App.ManagerApp(allowVr: !desktop);
        AddChild(app);
    }

    static System.Collections.Generic.List<string> OsCmdArgs()
    {
        var all = new System.Collections.Generic.List<string>(OS.GetCmdlineArgs());
        all.AddRange(OS.GetCmdlineUserArgs());
        return all;
    }
}
