namespace WindowsGSM.Desktop;

// WindowsGSM [--data <folder> | --remote] [--minimized]
//   Opens the panel for the agent that uses <folder> (default: this app's folder), starting the agent if
//   needed. --remote: installed only to control other PCs — no agent here; the window shows the PC (or hub) picked
//   in the app. --minimized starts in the tray (used by "Start with Windows"). Only one copy runs per data
//   folder; starting another brings the first one to the front.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool remoteOnly = args.Any(a => string.Equals(a, "--remote", StringComparison.OrdinalIgnoreCase));
        string? dataRoot = remoteOnly ? null : AgentLocator.DataRoot(args, AppContext.BaseDirectory);
        string id = "WindowsGSM.Desktop." + (dataRoot == null ? "remote" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataRoot.ToLowerInvariant())))[..16]);

        using var single = new Mutex(initiallyOwned: true, @"Local\" + id, out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + id + ".Show");
        if (!first)
        {
            showSignal.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();

        // Crashes go to the data folder's logs\CRASH_<date>.log, like the agent's (and the legacy app's).
        void WriteCrash(Exception? ex, string what)
        {
            if (ex == null) { return; }
            try
            {
                string logs = Path.Combine(dataRoot ?? DesktopSettings.Folder, "logs");
                Directory.CreateDirectory(logs);
                File.AppendAllText(Path.Combine(logs, $"CRASH_{DateTime.Now:yyyyMMdd}.log"),
                    $"[{DateTime.Now:MM/dd/yyyy-HH:mm:ss}] The desktop app {what}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* nowhere left to write it */ }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            WriteCrash(e.Exception, "hit an error");
            MessageBox.Show($"Something went wrong in the WindowsGSM app: {e.Exception.Message}\n\nIt was written to logs\\CRASH_{DateTime.Now:yyyyMMdd}.log in {(dataRoot == null ? DesktopSettings.Folder : "your data folder")}. Your game servers aren't affected.",
                "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception, "crashed");
        TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash(e.Exception, "had a background error"); e.SetObserved(); };
        bool minimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        using var app = new TrayApp(dataRoot, minimized);

        // A second launch asks us to come forward.
        var listener = new Thread(() =>
        {
            while (true)
            {
                try { showSignal.WaitOne(); } catch (ObjectDisposedException) { return; }
                app.Invoke(app.BringToFront);
            }
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();

        Application.Run(app);
        return 0;
    }
}
