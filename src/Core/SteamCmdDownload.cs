using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace BSVRManager.Core;

/// <summary>
/// Background version downloads via steamcmd (works on Windows and SteamOS/Linux).
/// One-time login with the Steam account caches the session token inside the steamcmd
/// folder (the password is never stored). After that every download runs silently.
/// </summary>
public class SteamCmdDownload
{
    /// <summary>Directory of the running exe (not the .NET BaseDirectory, which differs in exports).</summary>
    public static string ExeDir
    {
        get
        {
            var p = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(p) ? "" : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(dir) ? AppDomain.CurrentDomain.BaseDirectory : dir;
        }
    }

    public static string SteamCmdDir => Path.Combine(ExeDir, "tools", "steamcmd");

    public static bool IsAvailable => File.Exists(Path.Combine(SteamCmdDir, "steamcmd.exe")) ||
                                      File.Exists(Path.Combine(SteamCmdDir, "steamcmd.sh"));

    /// <summary>Downloads steamcmd (Valve's redistributable, ~2MB) when missing, then reports.</summary>
    public static void EnsureInstalled(Action<bool> done)
    {
        if (IsAvailable) { done(true); return; }
        new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(SteamCmdDir);
                var zip = Path.Combine(SteamCmdDir, "steamcmd.zip");
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("BSVRManager/1.0");
                    File.WriteAllBytes(zip, http.GetByteArrayAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip").GetAwaiter().GetResult());
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, SteamCmdDir, overwriteFiles: true);
                File.Delete(zip);
                DepotDownload.DepotLog("steamcmd auto-installed");
                done(File.Exists(Path.Combine(SteamCmdDir, "steamcmd.exe")) || File.Exists(Path.Combine(SteamCmdDir, "steamcmd.sh")));
            }
            catch (Exception e)
            {
                DepotDownload.DepotLog("steamcmd auto-install failed: " + e.Message);
                done(false);
            }
        }) { IsBackground = true }.Start();
    }

    readonly Settings _settings;
    public Action<string> OnStatus;
    public Action<string> OnLoginInputNeeded; // steamcmd asks for something we don't have
    Process _process;

    public SteamCmdDownload(Settings settings) { _settings = settings; }

    static string Exe => File.Exists(Path.Combine(SteamCmdDir, "steamcmd.exe"))
        ? Path.Combine(SteamCmdDir, "steamcmd.exe")
        : Path.Combine(SteamCmdDir, "steamcmd.sh");

    /// <summary>Runs steamcmd with the given command sequence, piping optional stdin lines
    /// (2FA codes), returning (exitCode, fullOutput). Blocks — call from a worker thread.</summary>
    (int code, string output) Run(string commands, string[] stdinLines, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(Exe, commands)
        {
            WorkingDirectory = SteamCmdDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        var p = Process.Start(psi);
        _process = p;
        var output = "";
        var stdinIdx = 0;

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            output += e.Data + "\n";
            onLine?.Invoke(e.Data);
            // steamcmd prompts for a Steam Guard / 2FA code when stdin is open
            if (stdinLines != null && stdinIdx < stdinLines.Length &&
                (e.Data.Contains("Steam Guard") || e.Data.Contains("two-factor") ||
                 e.Data.Contains("set_steam_guard_code") || e.Data.Contains("password:")))
            {
                if (stdinLines[stdinIdx] != null)
                    p.StandardInput.WriteLine(stdinLines[stdinIdx]);
                stdinIdx++;
            }
        };
        p.BeginOutputReadLine();

        p.WaitForExit();
        try { _process = null; } catch { }
        return (p.ExitCode, output);
    }

    /// <summary>One-time login. The password is used once; steamcmd caches the session token.
    /// guardCode: Steam Guard / 2FA code when enabled on the account (may be empty).</summary>
    public bool Login(string user, string password, string guardCode)
    {
        OnStatus?.Invoke("Logging into Steam (steamcmd)…");
        string stdin = null;
        var loginCmd = $"+login {user} {password}";
        if (!string.IsNullOrEmpty(guardCode))
        {
            loginCmd = $"+set_steam_guard_code {guardCode} " + loginCmd;
        }
        else
        {
            stdin = guardCode; // piped when the prompt appears
        }

        var (code, output) = Run(loginCmd, stdin != null ? new[] { stdin, stdin } : new[] { (string)null, null },
            line => { if (line.Contains("Steam Guard") || line.Contains("two-factor")) OnStatus?.Invoke("Waiting for Steam Guard code…"); });

        var ok = output.Contains("Logged in OK") || (code == 0 && !output.Contains("FAILED") && !output.Contains("Invalid Password"));
        if (ok)
        {
            _settings.SteamCmdUser = user;
            _settings.Save();
            OnStatus?.Invoke($"Logged in as {user}. Background downloads enabled.");
        }
        else
        {
            OnStatus?.Invoke("Steam login failed — check username/password/2FA in Settings.");
        }
        return ok;
    }

    public bool IsLoggedIn
    {
        get
        {
            var user = _settings.SteamCmdUser;
            if (string.IsNullOrEmpty(user)) return false;
            var token = Path.Combine(SteamCmdDir, "config", "steamcmd.vdf");
            if (!File.Exists(token)) token = Path.Combine(SteamCmdDir, "config", "loginusers.vdf");
            return File.Exists(token); // a cached steamcmd session exists for this setup
        }
    }

    static string DepotContentDir => Path.Combine(SteamCmdDir, "steamapps", "content", $"app_{SteamConsoleDownload.AppId}", $"depot_{SteamConsoleDownload.DepotId}");

    /// <summary>Silent background download of an exact depot manifest, then import into the profile.</summary>
    public void Download(Profile profile, string manifest, Action<bool> onDone)
    {
        OnStatus?.Invoke($"Background download of Beat Saber {profile.Version} started…");
        var thread = new Thread(() =>
        {
            try
            {
                var (code, output) = Run(
                    $"+login {_settings.SteamCmdUser} +download_depot {SteamConsoleDownload.AppId} {SteamConsoleDownload.DepotId} {manifest} +quit",
                    null,
                    line =>
                    {
                        var idx = line.IndexOf("Depot download progress", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0) OnStatus?.Invoke(Truncate(line.Substring(idx)));
                    });

                var depotDir = DepotContentDir;
                var exe = Path.Combine(depotDir, "Beat Saber.exe");
                if (code == 0 && File.Exists(exe))
                {
                    var actual = SteamConsoleDownload.ImportDepot(depotDir, profile, profile.Version, OnStatus, out _);
                    if (!actual) OnStatus?.Invoke("The downloaded content did not match the requested version — it was kept but renamed.");
                    onDone?.Invoke(true);
                }
                else
                {
                    OnStatus?.Invoke("Background download failed (steamcmd exit " + code + "). Are you logged in?");
                    onDone?.Invoke(false);
                }
            }
            catch (Exception e)
            {
                OnStatus?.Invoke("Background download failed: " + e.Message);
                onDone?.Invoke(false);
            }
        }) { IsBackground = true };
        thread.Start();
    }

    static string Truncate(string s) => s.Length > 100 ? s[..100] : s;
}
