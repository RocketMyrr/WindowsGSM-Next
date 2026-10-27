using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// If the agent itself crashes: the details go to logs\CRASH_&lt;date&gt;.log (the legacy app's file name), and
/// the next start raises an "agent crashed" notification — which owners can route to their own Discord channel.
/// (The legacy app posted crash logs to the original author's hidden Discord webhook; this never sends anything
/// anywhere you didn't set up.) Background task failures nobody awaited are written too, without stopping.
/// </summary>
public static class CrashLog
{
    private static string? _logs;
    private static string _who = "agent";

    public static void Install(string dataRoot, string who)
    {
        Configure(dataRoot, who);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { if (!Harmless(e.Exception)) { Write(e.Exception, fatal: false); } e.SetObserved(); };
    }

    /// <summary>
    /// Failures that mean nothing: SteamKit2's socket task tripping over its own closed connection after a Steam check
    /// is already done (only ever "object disposed", only ever inside SteamKit2's connection code).
    /// </summary>
    public static bool Harmless(AggregateException e) =>
        e.Flatten().InnerExceptions is { Count: > 0 } inner && inner.All(x => x is ObjectDisposedException && (x.StackTrace?.Contains("SteamKit2.WebSocketConnection") ?? false));

    /// <summary>Where crash logs go, without hooking the process (tests).</summary>
    public static void Configure(string dataRoot, string who)
    {
        _logs = Path.Combine(dataRoot, "logs");
        _who = who;
    }

    public static void Write(Exception? ex, bool fatal)
    {
        if (_logs == null || ex == null) { return; }
        try
        {
            Directory.CreateDirectory(_logs);
            string version = global::WindowsGSM.Hosting.WgsmEnvironment.Version;
            string head = fatal ? $"The {_who} crashed ({version})" : $"A background task in the {_who} failed ({version})";
            File.AppendAllText(Path.Combine(_logs, $"CRASH_{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:MM/dd/yyyy-HH:mm:ss}] {head}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            if (fatal)
            {
                File.WriteAllText(Path.Combine(_logs, "crash-pending.txt"), $"{DateTimeOffset.Now:o}\n{ex.GetType().Name}: {ex.Message}");
            }
        }
        catch { /* nowhere left to report it */ }
    }

    /// <summary>On start: if the last run crashed, say so in the notification centre (and its channels).</summary>
    /// <summary>The launcher went back to the previous version because the new one didn't start: say so once.</summary>
    public static void ReportFallback(string dataRoot, NotificationCentre centre, string machineId)
    {
        string marker = Path.Combine(dataRoot, "logs", "update-fallback.txt");
        if (!File.Exists(marker)) { return; }
        try
        {
            string[] lines = File.ReadAllLines(marker);
            string failed = lines.Length > 1 ? lines[1] : "the new version", back = lines.Length > 2 ? lines[2] : "the previous one";
            centre.Record("appUpdateFailed", $"WindowsGSM {failed} didn't start — back on {back}",
                $"The update to {failed} was installed but its agent didn't come up, so WindowsGSM went back to {back} by itself; game servers kept running. " +
                "Its log is in logs\\agent. Try the update again later, or report it (Help → Report a problem).",
                machineId, null, null, Visibility.Admin);
            File.Delete(marker);
        }
        catch { /* try again next start */ }
    }

    public static void ReportPrevious(string dataRoot, NotificationCentre centre, string machineId)
    {
        string marker = Path.Combine(dataRoot, "logs", "crash-pending.txt");
        if (!File.Exists(marker)) { return; }
        try
        {
            string[] lines = File.ReadAllLines(marker);
            string when = lines.Length > 0 && DateTimeOffset.TryParse(lines[0], out var at) ? at.ToLocalTime().ToString("g") : "recently";
            string what = lines.Length > 1 ? lines[1] : "unknown error";
            centre.Record("appCrash", "The WindowsGSM agent stopped unexpectedly",
                $"It crashed at {when} ({what}) and has restarted; game servers kept running. Details: logs\\CRASH_{DateTime.Now:yyyyMMdd}.log",
                machineId, null, null, Visibility.Admin);
            File.Delete(marker);
        }
        catch { /* try again next start */ }
    }
}
