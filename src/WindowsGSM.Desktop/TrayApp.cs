namespace WindowsGSM.Desktop;

/// <summary>
/// The tray icon and its menu. Owns the window; starts the agent if it isn't running. Quitting closes this
/// app only — the agent keeps serving the panel and running (and watching) your servers. "Stop everything and
/// quit" stops the game servers and the agent too (shutting the PC down, maintenance).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly DesktopSettings _settings;
    private readonly string _dataRoot;
    private readonly string _exe;
    private string? _balloonPath;

    public TrayApp(string dataRoot, bool startMinimized)
    {
        _dataRoot = dataRoot;
        _exe = Environment.ProcessPath ?? Application.ExecutablePath;
        _settings = DesktopSettings.Load();
        _window = new MainWindow(_settings);
        _window.NotificationRequested += Notify;
        _window.HiddenToTray += () =>
        {
            if (_settings.TrayHintShown) { return; }
            _settings.TrayHintShown = true;
            _settings.Save();
            _balloonPath = null;
            _tray!.ShowBalloonTip(6000, "WindowsGSM is still running", "Your servers keep going. Open the panel again from this icon; quit from its menu.", ToolTipIcon.Info);
        };

        var notifications = new ToolStripMenuItem("Notifications") { Checked = _settings.Notifications, CheckOnClick = true };
        notifications.CheckedChanged += (_, _) => { _settings.Notifications = notifications.Checked; _settings.Save(); };
        var staySignedIn = new ToolStripMenuItem("Stay signed in on this computer") { Checked = _settings.StaySignedIn, CheckOnClick = true, ToolTipText = "Signs back in on its own as whoever last signed in here, when the session ends." };
        staySignedIn.CheckedChanged += (_, _) => { _settings.StaySignedIn = staySignedIn.Checked; _settings.Save(); };
        _window.AutoSignIn = AutoSignInAsync;
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartWithWindows.IsOn(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            try { StartWithWindows.Set(startup.Checked, _exe, _dataRoot); }
            catch (Exception ex) { MessageBox.Show($"Couldn't change that: {ex.Message}", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var open = new ToolStripMenuItem("Open WindowsGSM", null, (_, _) => _window.ShowAndActivate()) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        // The agent (it runs the servers and the panel): its state, and start / stop / restart. Stopping it leaves
        // game servers running.
        _agentStatus = new ToolStripMenuItem("Agent: checking…") { Enabled = false };
        _agentStart = new ToolStripMenuItem("Start the agent", null, async (_, _) => await StartAgentAsync());
        _agentStop = new ToolStripMenuItem("Stop the agent (servers keep running)", null, async (_, _) => await StopAgentAsync(thenStart: false));
        _agentRestart = new ToolStripMenuItem("Restart the agent", null, async (_, _) => await StopAgentAsync(thenStart: true));
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            open,
            new ToolStripMenuItem("Activity and jobs", null, (_, _) => { _window.ShowAndActivate(); _window.Go("/?activity=1"); }),
            new ToolStripMenuItem("Notifications page", null, (_, _) => { _window.ShowAndActivate(); _window.Go("/notifications"); }),
            new ToolStripSeparator(),
            notifications,
            staySignedIn,
            startup,
            new ToolStripSeparator(),
            _agentStatus, _agentStart, _agentStop, _agentRestart,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit (servers keep running)", null, (_, _) => Quit()),
            new ToolStripMenuItem("Stop everything and quit…", null, async (_, _) => await StopEverythingAsync()),
        });

        menu.Opening += async (_, _) => await RefreshAgentMenuAsync();
        _tray = new NotifyIcon { Icon = AppIcon.Create(32), Text = "WindowsGSM", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { _window.ShowAndActivate(); } };
        _tray.BalloonTipClicked += (_, _) => { _window.ShowAndActivate(); _window.Go(_balloonPath); };

        if (!startMinimized) { _window.Show(); }
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        Uri url = AgentLocator.UrlFor(_dataRoot);
        using var http = AgentLocator.LocalClient();
        if (!await AgentLocator.IsRunningAsync(http, url))
        {
            string? agent = AgentLocator.AgentExe(AppContext.BaseDirectory);
            if (agent == null)
            {
                _window.ShowStatus($"Couldn't find the WindowsGSM agent (wgsm-agent.exe) next to this app, and nothing answers at {url}.");
                return;
            }
            _window.ShowStatus("Starting the WindowsGSM agent…");
            try { AgentLocator.StartAgent(agent, _dataRoot); }
            catch (Exception ex) { _window.ShowStatus($"Couldn't start the agent: {ex.Message}"); return; }
            // First run can take a while (plugins compile, certificates load).
            if (!await AgentLocator.WaitAsync(http, url, TimeSpan.FromSeconds(60)))
            {
                _window.ShowStatus($"The agent didn't answer at {url}. Check its log in {Path.Combine(_dataRoot, "logs")}.");
                return;
            }
        }
        _window.ShowStatus("Opening the panel…");
        if (!_watchingUpdates) { _watchingUpdates = true; _ = WatchForUpdateAsync(url); }
        try { await _window.OpenAsync(url); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            _window.ShowStatus("This app needs the Microsoft Edge WebView2 Runtime (built into Windows 11). Install it from microsoft.com/edge/webview2, or open the panel in your browser: " + url);
        }
    }

    /// <summary>
    /// After the agent updates itself it runs a newer version than this window. Say so once, and offer a
    /// restart through the launcher (which starts the new version's app).
    /// </summary>
    private async Task WatchForUpdateAsync(Uri url)
    {
        string mine = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";
        string? launcher = Environment.GetEnvironmentVariable("WGSM_LAUNCHER");
        if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher)) { return; }
        using var http = AgentLocator.LocalClient();
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(url, "api/v2/info")));
                string agent = doc.RootElement.GetProperty("version").GetString()?.TrimStart('v') ?? "";
                if (agent.Length == 0 || agent == mine.TrimStart('v')) { continue; }
                _tray.ContextMenuStrip!.Items.Insert(0, new ToolStripMenuItem($"Restart to finish updating ({agent})", null, (_, _) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher) { UseShellExecute = false });
                    Quit();
                }) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) });
                _balloonPath = null;
                _tray.ShowBalloonTip(10000, $"WindowsGSM updated to {agent}", "Your servers kept running. Restart the app from this icon to finish.", ToolTipIcon.Info);
                return;
            }
            catch { /* agent restarting */ }
        }
    }

    private void Notify(string title, string text, string? path)
    {
        if (!_settings.Notifications) { return; }
        _balloonPath = path;
        _tray.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(text) ? " " : text, ToolTipIcon.None);
    }

    private bool _watchingUpdates;
    /// <summary>
    /// Signs the panel back in through the agent's local-only endpoint (needs the key this Windows account's apps
    /// can read), as whoever last signed in through this app.
    /// </summary>
    private async Task<bool> AutoSignInAsync()
    {
        if (!_settings.StaySignedIn) { return false; }
        string keyFile = Path.Combine(_dataRoot, "configs", "next", "local-control.key");
        if (!File.Exists(keyFile)) { return false; }
        Uri url = AgentLocator.UrlFor(_dataRoot);
        var jar = new System.Net.CookieContainer();
        using var http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = jar, UseCookies = true,
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri is { } u && AgentLocator.IsLocal(u),
        }) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "api/v2/local/desktop-session"));
        request.Headers.Add("X-WGSM-CSRF", "1");
        request.Headers.Add("X-WGSM-Local-Key", File.ReadAllText(keyFile).Trim());
        using var res = await http.SendAsync(request);
        if (!res.IsSuccessStatusCode) { return false; }
        var cookie = jar.GetCookies(url).Cast<System.Net.Cookie>().FirstOrDefault(c => c.Name == "wgsm_session");
        if (cookie == null) { return false; }
        var done = new TaskCompletionSource();
        _window.BeginInvoke(async () => { await _window.SetSessionCookieAsync(cookie); done.TrySetResult(); });
        await done.Task;
        return true;
    }

    private readonly ToolStripMenuItem _agentStatus, _agentStart, _agentStop, _agentRestart;

    private async Task RefreshAgentMenuAsync()
    {
        using var http = AgentLocator.LocalClient();
        bool running = await AgentLocator.IsRunningAsync(http, AgentLocator.UrlFor(_dataRoot));
        _agentStatus.Text = running ? "Agent: running" : "Agent: stopped";
        _agentStart.Visible = !running;
        _agentStop.Visible = _agentRestart.Visible = running;
    }

    private async Task StartAgentAsync()
    {
        _window.ShowAndActivate();
        await ConnectAsync(); // starts it when it isn't running, then opens the panel
    }

    /// <summary>Stops the agent through its local-only endpoint (game servers keep running); optionally starts it again.</summary>
    private async Task StopAgentAsync(bool thenStart)
    {
        Uri url = AgentLocator.UrlFor(_dataRoot);
        using var http = AgentLocator.LocalClient();
        string keyFile = Path.Combine(_dataRoot, "configs", "next", "local-control.key");
        if (!File.Exists(keyFile)) { MessageBox.Show("Couldn't find the agent's key to stop it.", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "api/v2/local/stop-agent"));
            request.Headers.Add("X-WGSM-CSRF", "1");
            request.Headers.Add("X-WGSM-Local-Key", File.ReadAllText(keyFile).Trim());
            using var res = await http.SendAsync(request);
            if (!res.IsSuccessStatusCode) { MessageBox.Show($"The agent refused ({(int)res.StatusCode}).", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        }
        catch (Exception ex) { MessageBox.Show($"Couldn't reach the agent: {ex.Message}", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        _window.ShowStatus(thenStart ? "Restarting the agent… (game servers keep running)" : "Stopping the agent… (game servers keep running)");
        var until = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < until && await AgentLocator.IsRunningAsync(http, url)) { await Task.Delay(500); }
        if (thenStart) { await ConnectAsync(); return; }
        _balloonPath = null;
        _tray.ShowBalloonTip(5000, "WindowsGSM agent stopped", "Your game servers keep running. Start it again from this icon.", ToolTipIcon.Info);
        _window.ShowStatus("The agent is stopped — your game servers keep running. Start it from the WindowsGSM icon in the taskbar (or the Start menu).");
    }

    /// <summary>Stops every game server on this machine and the agent (via its local-only endpoint), then quits.</summary>
    private async Task StopEverythingAsync()
    {
        var answer = MessageBox.Show(
            "This stops every game server on this computer — players are disconnected — and then WindowsGSM itself.\n\n" +
            "Servers set to start automatically start again the next time WindowsGSM starts.",
            "Stop everything and quit?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) { return; }

        Uri url = AgentLocator.UrlFor(_dataRoot);
        using var http = AgentLocator.LocalClient();
        string keyFile = Path.Combine(_dataRoot, "configs", "next", "local-control.key");
        if (!await AgentLocator.IsRunningAsync(http, url) || !File.Exists(keyFile)) { Quit(); return; } // nothing to stop

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "api/v2/local/stop-everything"));
            request.Headers.Add("X-WGSM-CSRF", "1");
            request.Headers.Add("X-WGSM-Local-Key", File.ReadAllText(keyFile).Trim());
            using var res = await http.SendAsync(request);
            if (!res.IsSuccessStatusCode)
            {
                MessageBox.Show($"The agent refused ({(int)res.StatusCode}). Stop the servers from the panel instead.", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            int servers = doc.RootElement.TryGetProperty("servers", out var n) ? n.GetInt32() : 0;
            _balloonPath = null;
            _tray.Text = "WindowsGSM — stopping…";
            _tray.ShowBalloonTip(5000, "Stopping WindowsGSM", servers == 0 ? "No servers were running. Stopping the agent." : $"Stopping {servers} server{(servers == 1 ? "" : "s")}, then the agent. This can take a minute.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't reach the agent: {ex.Message}", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Wait for the agent to go (it stops the servers first — at most a few minutes).
        var until = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < until && await AgentLocator.IsRunningAsync(http, url)) { await Task.Delay(1000); }
        Quit();
    }

    private void Quit()
    {
        _window.Exiting = true;
        _tray.Visible = false;
        _window.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Dispose(); _window.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Called when a second copy of the app is started: show this one instead.</summary>
    public void BringToFront() => _window.ShowAndActivate();

    public void Invoke(Action a)
    {
        if (_window.IsHandleCreated) { _window.BeginInvoke(a); } else { a(); }
    }

}
