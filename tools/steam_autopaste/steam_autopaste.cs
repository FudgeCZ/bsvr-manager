// steam_autopaste.exe v3 — triggers `download_depot` in the logged-in Steam client
// WITHOUT showing anything to the player: the Steam window is moved off-screen
// during the ~2 s of automated typing, then restored.
// Usage: steam_autopaste.exe "download_depot 620980 620981 <manifestId>"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class Program
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCB cb, IntPtr lp);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inp, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll")] static extern IntPtr ShellExecute(IntPtr hwnd, string op, string file, string args, string dir, int show);

    delegate bool EnumCB(IntPtr h, IntPtr lp);
    struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT k; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 2;
    const uint KEYEVENTF_UNICODE = 4;
    const ushort VK_CONTROL = 0x11, VK_A = 0x41, VK_DELETE = 0x2E, VK_RETURN = 0x0D;

    static IntPtr SteamHwnd = IntPtr.Zero;

    static bool TopCB(IntPtr h, IntPtr lp)
    {
        if (!IsWindowVisible(h)) return true;
        var cn = new StringBuilder(256); GetClassName(h, cn, 256);
        if (cn.ToString() == "SDL_app") { SteamHwnd = h; return false; }
        return true;
    }

    static IntPtr FindSteamWindow()
    {
        SteamHwnd = IntPtr.Zero;
        EnumWindows(TopCB, IntPtr.Zero);
        return SteamHwnd;
    }

    static void TypeChar(char c)
    {
        var d = new INPUT { type = INPUT_KEYBOARD }; d.k.scan = c; d.k.flags = KEYEVENTF_UNICODE;
        var u = new INPUT { type = INPUT_KEYBOARD }; u.k.scan = c; u.k.flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, new[] { d, u }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void TypeText(string s) { foreach (char c in s) TypeChar(c); }

    static void PressKey(ushort vk)
    {
        var d = new INPUT { type = INPUT_KEYBOARD }; d.k.vk = vk;
        var u = new INPUT { type = INPUT_KEYBOARD }; u.k.vk = vk; u.k.flags = KEYEVENTF_KEYUP;
        SendInput(1, new[] { d }, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(20);
        SendInput(1, new[] { u }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void KeyDown(ushort vk)
    {
        var d = new INPUT { type = INPUT_KEYBOARD }; d.k.vk = vk;
        SendInput(1, new[] { d }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void KeyUp(ushort vk)
    {
        var u = new INPUT { type = INPUT_KEYBOARD }; u.k.vk = vk; u.k.flags = KEYEVENTF_KEYUP;
        SendInput(1, new[] { u }, Marshal.SizeOf(typeof(INPUT)));
    }

    static void Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "";
        if (cmd.Length == 0) { Console.WriteLine("no command"); Environment.Exit(2); }

        // 1. make sure the Steam client window exists and is on the console tab
        var hwnd = FindSteamWindow();
        bool wasRunning = hwnd != IntPtr.Zero;
        if (!wasRunning)
        {
            ShellExecute(IntPtr.Zero, "open", "steam://nav/console", null, null, 0);
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(500);
                hwnd = FindSteamWindow();
                if (hwnd != IntPtr.Zero) break;
            }
        }
        else
        {
            ShellExecute(IntPtr.Zero, "open", "steam://nav/console", null, null, 0);
        }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("steam window not found"); Environment.Exit(1); }
        Thread.Sleep(1200); // let the console tab render

        // 2. move the window off-screen so the player never sees the console
        RECT r = new RECT();
        GetWindowRect(hwnd, out r);
        SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, 0x0011); // NOSIZE|NOZORDER|NOACTIVATE

        // 3. take foreground reliably (attach input chains, ALT trick)
        uint fgPidDummy = 0;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPidDummy);
        uint myThread = GetCurrentThreadId();
        bool attached = AttachThreadInput(myThread, fgThread, true);
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); // ALT tap
        SetForegroundWindow(hwnd);
        AttachThreadInput(myThread, fgThread, false);
        Thread.Sleep(400);

        // 4. clear leftovers (ctrl+a, delete), type the command, execute
        KeyDown(VK_CONTROL); KeyDown(VK_A); KeyUp(VK_A); KeyUp(VK_CONTROL);
        Thread.Sleep(100);
        KeyDown(VK_DELETE); KeyUp(VK_DELETE);
        Thread.Sleep(150);
        TypeText(cmd);
        Thread.Sleep(250);
        PressKey(VK_RETURN);
        Thread.Sleep(300);

        // 5. restore the window position
        SetWindowPos(hwnd, IntPtr.Zero, r.L, r.T, r.R - r.L, r.B - r.T, 0x0004); // NOZORDER
        Console.WriteLine("sent: " + cmd);
    }
}
