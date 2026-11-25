using System.Diagnostics;
using Microsoft.Win32;

namespace FilipBoard.App.Services;

public static class Units
{
    public static bool Fahrenheit { get; set; }
    public static string Suffix => Fahrenheit ? "°F" : "°C";
    public static double Conv(double c) => Fahrenheit ? c * 9 / 5 + 32 : c;
    public static string Temp(double? c) => c is null ? "--" : $"{Conv(c.Value):0.#}{Suffix}";
}

/// <summary>
/// Start with Windows. Elevated: a highest-privilege scheduled task (no UAC prompt at logon, needed for sensor access).
/// Not elevated: a normal per-user Run entry (app then runs with limited sensors until the user elevates).
/// </summary>
public static class StartupManager
{
    private const string Name = "FilipBoardFanControl";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string Apply(bool enable, bool elevated)
    {
        var exe = Environment.ProcessPath!;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)!;
            if (!enable)
            {
                key.DeleteValue(Name, false);
                if (elevated) RunSchtasks($"/Delete /TN {Name} /F");
                return "Start with Windows disabled.";
            }
            if (elevated)
            {
                var ok = RunSchtasks($"/Create /TN {Name} /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
                if (ok) { key.DeleteValue(Name, false); return "Enabled (elevated scheduled task)."; }
                key.SetValue(Name, $"\"{exe}\"");
                return "Scheduled task failed; enabled as a normal startup entry (no admin sensors at logon).";
            }
            key.SetValue(Name, $"\"{exe}\"");
            return "Enabled as a normal startup entry. Run the app as Administrator once and re-enable this option to start elevated (full sensor access).";
        }
        catch (Exception ex) { return "Could not change startup setting: " + ex.Message; }
    }

    private static bool RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10000);
        return p.HasExited && p.ExitCode == 0;
    }
}
