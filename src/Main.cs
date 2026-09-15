using System.Diagnostics;
using Godot;

namespace BSVRManager;

/// <summary>Entry point (full-rect root control). When a VR runtime is already running, the app
/// restarts itself once with XR enabled and comes up in VR; otherwise it opens as a normal
/// desktop window and SteamVR is never spawned in the background. OpenXR in Godot can only
/// be initialized at boot, which is why the one-shot relaunch exists — "--vr-launched" in
/// the child's arguments makes it strictly one-shot.</summary>
public partial class Main : Control
{
    static readonly string[] VrRuntimeProcesses =
        { "vrmonitor", "vrserver", "vrcompositor", "OVRServer_x64", "OculusClient", "OculusDash", "VirtualDesktop.Streamer" };

    public override void _Ready()
    {
        var args = OsCmdArgs();
        bool desktop = args.Contains("--desktop");
        bool relaunchedForVr = args.Contains("--vr-launched");

        if (!desktop && !relaunchedForVr && VrRuntimeRunning() && RelaunchWithXr(args))
            return;

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
            psi.ArgumentList.Add("--xr-mode");    // consumed by the engine → OpenXR boots enabled
            psi.ArgumentList.Add("on");
            psi.ArgumentList.Add("--vr-launched"); // survives into GetCmdlineArgs → one-shot
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
