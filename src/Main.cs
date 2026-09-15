using System.Diagnostics;
using Godot;

namespace BSVRManager;

/// <summary>Entry point (full-rect root control). VR mode starts only when a VR runtime
/// is already running; otherwise the app opens as a normal desktop window and never
/// spawns SteamVR in the background.</summary>
public partial class Main : Control
{
    static readonly string[] VrRuntimeProcesses =
        { "vrmonitor", "vrserver", "vrcompositor", "OVRServer_x64", "OculusClient", "OculusDash", "VirtualDesktop.Streamer" };

    public override void _Ready()
    {
        var args = OsCmdArgs();
        bool desktop = args.Contains("--desktop");
        bool xrForced = args.Exists(a => a.StartsWith("--xr-mode"));

        // no explicit choice and a VR runtime is alive? relaunch with XR enabled so
        // OpenXR initializes at boot (the only supported way)
        if (!desktop && !xrForced && VrRuntimeRunning())
        {
            if (RelaunchWithXr(args)) return;
        }

        var app = new App.ManagerApp(allowVr: !desktop);
        AddChild(app);
    }

    static bool VrRuntimeRunning()
    {
        foreach (var name in VrRuntimeProcesses)
            if (Process.GetProcessesByName(name).Length > 0)
                return true;
        return false;
    }

    bool RelaunchWithXr(System.Collections.Generic.List<string> args)
    {
        try
        {
            var exe = OS.GetExecutablePath();
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--xr-mode on");
            Process.Start(psi);
        }
        catch (System.Exception e)
        {
            GD.PushWarning("VR relaunch failed: " + e.Message + " — staying in desktop mode.");
            return false;
        }
        GD.Print("[vr] VR runtime detected — restarting with XR enabled.");
        GetTree().Quit();
        return true;
    }

    static System.Collections.Generic.List<string> OsCmdArgs()
    {
        var all = new System.Collections.Generic.List<string>(OS.GetCmdlineArgs());
        all.AddRange(OS.GetCmdlineUserArgs());
        return all;
    }
}
