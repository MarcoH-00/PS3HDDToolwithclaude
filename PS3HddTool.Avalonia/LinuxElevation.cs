using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PS3HddTool.Avalonia;

/// <summary>
/// On Linux, raw access to /dev/sdX needs root. Instead of asking the user to start the
/// app from a terminal with sudo, the app relaunches itself through pkexec, which shows
/// the desktop's graphical password prompt (polkit). If that is cancelled or not
/// available, the app starts normally and tells the user what is missing.
/// </summary>
internal static class LinuxElevation
{
    private const string TriedVar = "PS3HDDTOOL_ELEVATION_TRIED";
    private const string StatusVar = "PS3HDDTOOL_ELEVATION_STATUS";

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    public static bool IsRoot => OperatingSystem.IsLinux() && geteuid() == 0;

    /// <summary>
    /// A note for the UI when the app is running without root on Linux, or null.
    /// </summary>
    public static string? StartupWarning
    {
        get
        {
            if (!OperatingSystem.IsLinux() || IsRoot) return null;
            string? status = Environment.GetEnvironmentVariable(StatusVar);
            string why = status switch
            {
                "cancelled" => "The administrator password prompt was cancelled.",
                "no-pkexec" => "pkexec (polkit) is not installed, so the password prompt could not be shown.",
                "failed" => "Starting with administrator rights failed.",
                "disabled" => "Administrator rights were skipped (--no-elevate).",
                _ => "The app is not running as administrator.",
            };
            return why + " Physical drives cannot be listed or opened; disk images still work. " +
                   "Restart the app and enter your password to use a drive.";
        }
    }

    /// <summary>
    /// Returns an exit code when the elevated copy ran (the caller should exit with it),
    /// or null to continue starting this unprivileged instance.
    /// </summary>
    public static int? TryRelaunchElevated(string[] args)
    {
        if (!OperatingSystem.IsLinux() || IsRoot) return null;
        if (Environment.GetEnvironmentVariable(TriedVar) == "1") return null;
        if (args.Contains("--no-elevate"))
        {
            Environment.SetEnvironmentVariable(StatusVar, "disabled");
            return null;
        }

        string? pkexec = FindInPath("pkexec");
        if (pkexec == null)
        {
            Environment.SetEnvironmentVariable(StatusVar, "no-pkexec");
            return null;
        }

        // What to run: the app itself (apphost / single-file), or "dotnet App.dll".
        string? processPath = Environment.ProcessPath;
        if (processPath == null) return null;
        var command = new List<string>();
        if (Path.GetFileNameWithoutExtension(processPath) == "dotnet")
        {
            string? dll = typeof(LinuxElevation).Assembly.Location;
            if (string.IsNullOrEmpty(dll)) return null;
            command.Add(processPath);
            command.Add(dll);
        }
        else command.Add(processPath);
        command.AddRange(args);

        // pkexec clears the environment; pass what a GUI needs to reach the user's display.
        var env = new List<string> { $"{TriedVar}=1" };
        void Pass(string name, string? fallback = null)
        {
            string? value = Environment.GetEnvironmentVariable(name) ?? fallback;
            if (!string.IsNullOrEmpty(value)) env.Add($"{name}={value}");
        }
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        Pass("DISPLAY");
        Pass("XAUTHORITY", File.Exists(Path.Combine(home, ".Xauthority")) ? Path.Combine(home, ".Xauthority") : null);
        Pass("LANG");
        Pass("LC_ALL");
        Pass("AVALONIA_SCREEN_SCALE_FACTORS");
        Pass("AVALONIA_GLOBAL_SCALE_FACTOR");
        if (Environment.GetEnvironmentVariable("DISPLAY") == null)
            return null; // no X11/XWayland display to show a root window on; stay unprivileged

        var psi = new ProcessStartInfo(pkexec) { UseShellExecute = false };
        psi.ArgumentList.Add("env");
        foreach (var e in env) psi.ArgumentList.Add(e);
        foreach (var c in command) psi.ArgumentList.Add(c);

        try
        {
            var started = DateTime.UtcNow;
            using var child = Process.Start(psi);
            if (child == null) { Environment.SetEnvironmentVariable(StatusVar, "failed"); return null; }
            child.WaitForExit();
            int code = child.ExitCode;

            // 126/127: the password prompt was dismissed or authentication failed.
            if (code is 126 or 127)
            {
                Environment.SetEnvironmentVariable(StatusVar, "cancelled");
                return null;
            }
            // A root copy that dies within seconds never showed a window (for example the
            // display refused a root client): start normally rather than vanish silently.
            if (code != 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5))
            {
                Environment.SetEnvironmentVariable(StatusVar, "failed");
                return null;
            }
            return code;
        }
        catch
        {
            Environment.SetEnvironmentVariable(StatusVar, "failed");
            return null;
        }
    }

    private static string? FindInPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            if (dir.Length == 0) continue;
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string candidate in new[] { "/usr/bin/pkexec", "/bin/pkexec" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}
