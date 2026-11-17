using System.Diagnostics;

namespace WinFinder.App.Services;

/// <summary>
/// Opt-in startup timing (set WINFINDER_TRACE=1). Writes milliseconds since process start
/// for each mark to startup-trace.log in the data folder. Costs nothing when disabled.
/// </summary>
public static class StartupTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("WINFINDER_TRACE") == "1";
    private static readonly List<string> Lines = new();

    public static void Mark(string what)
    {
        if (!Enabled) return;
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        lock (Lines) Lines.Add($"{ms,7:0} ms  {what}");
    }

    public static void Flush()
    {
        if (!Enabled) return;
        try
        {
            Directory.CreateDirectory(Core.Settings.SettingsStore.DefaultDirectory);
            lock (Lines) File.WriteAllLines(Path.Combine(Core.Settings.SettingsStore.DefaultDirectory, "startup-trace.log"), Lines);
        }
        catch { }
    }
}
