using System.Net.Http.Json;
using WindowsGSM.Desktop.Shared;

namespace WindowsGSM.Desktop;

/// <summary>
/// The tray icon and its menu. Owns the window; starts this PC's agent if it isn't running. Quitting closes this
/// app only — the agent keeps serving the panel and running (and watching) your servers. "Stop everything and
/// quit" stops the game servers and the agent too (shutting the PC down, maintenance).
/// The window shows this PC's agent or another PC's (or a hub's) — switched from the menu. Installed as "control
/// another PC" only, there's no agent here at all (<see cref="_dataRoot"/> is null).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly DesktopSettings _settings;
    private readonly string? _dataRoot;
    private readonly string _exe;
    private string? _balloonPath;

    private bool HasLocal => _dataRoot != null;

    public TrayApp(string? dataRoot, bool startMinimized)
    {
        _dataRoot = dataRoot;
        _exe = Environment.ProcessPath ?? Application.ExecutablePath;
        _settings = DesktopSettings.Load();
        if (_settings.CurrentPc != null && _settings.Current == null) { _settings.CurrentPc = null; } // forgotten meanwhile
        if (!HasLocal && _settings.Current == null && _settings.Pcs.Count > 0) { _settings.CurrentPc = _settings.Pcs[^1].Url; }
        _window = new MainWindow(_settings);
        _window.NotificationRequested += Notify;
        _window.CertificateRejected += CertificateChanged;
        _window.LoadFailed += error => { if (_window.Remote is { } pc) { Unreachable(pc, null); } };
        _window.HiddenToTray += () =>
        {
            if (_settings.TrayHintShown) { return; }
            _settings.TrayHintShown = true;
            _settings.Save();
            _balloonPath = null;
            _tray!.ShowBalloonTip(6000, "WindowsGSM is still running", HasLocal ? "Your servers keep going. Open the panel again from this icon; quit from its menu." : "Open the panel again from this icon; quit from its menu.", ToolTipIcon.Info);
        };
        _window.AutoSignIn = AutoSignInAsync;
        _window.PageLoaded += () => _ = RememberSignInAsync();

        var menu = new ContextMenuStrip();
        menu.Opening += async (_, _) => { BuildMenu(menu); await RefreshAgentMenuAsync(); };
        BuildMenu(menu);
        _tray = new NotifyIcon { Icon = AppIcon.Create(32), Text = "WindowsGSM", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { _window.ShowAndActivate(); } };
        _tray.BalloonTipClicked += (_, _) => { _window.ShowAndActivate(); _window.Go(_balloonPath); };

        if (!startMinimized) { _window.Show(); }
        _ = OpenCurrentAsync();
    }

    // ───────────────────────────── Menu ─────────────────────────────

    private ToolStripMenuItem? _agentStatus, _agentStart, _agentStop, _agentRestart, _updateItem;

    private void BuildMenu(ContextMenuStrip menu)
    {
        var bold = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold);
        var items = new List<ToolStripItem>();
        if (_updateItem != null) { items.Add(_updateItem); }
        items.Add(new ToolStripMenuItem("Open WindowsGSM", null, (_, _) => _window.ShowAndActivate()) { Font = bold });
        items.Add(new ToolStripMenuItem("Activity and jobs", null, (_, _) => { _window.ShowAndActivate(); _window.Go("/?activity=1"); }));
        items.Add(new ToolStripMenuItem("Notifications page", null, (_, _) => { _window.ShowAndActivate(); _window.Go("/notifications"); }));
        items.Add(new ToolStripSeparator());

        // Which PC the window shows.
        var current = _settings.Current;
        var pcs = new ToolStripMenuItem(current == null ? (HasLocal ? "PC: this PC" : "PC: none yet") : $"PC: {current.Name}");
        if (HasLocal) { pcs.DropDownItems.Add(new ToolStripMenuItem("This PC", null, (_, _) => SwitchTo(null)) { Checked = current == null }); }
        foreach (var pc in _settings.Pcs)
        {
            var p = pc;
            pcs.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (_, _) => SwitchTo(p)) { Checked = current == p, ToolTipText = p.Url });
        }
        if (pcs.DropDownItems.Count > 0) { pcs.DropDownItems.Add(new ToolStripSeparator()); }
        pcs.DropDownItems.Add(new ToolStripMenuItem("Connect to another PC…", null, (_, _) => AddPc()));
        if (current != null)
        {
            var stay = new ToolStripMenuItem($"Stay signed in to {current.Name}") { Checked = current.StaySignedIn, CheckOnClick = true, ToolTipText = "Signs this app back in on its own when its session ends." };
            stay.CheckedChanged += async (_, _) => await SetStaySignedInAsync(current, stay.Checked);
            pcs.DropDownItems.Add(stay);
            pcs.DropDownItems.Add(new ToolStripMenuItem($"Forget {current.Name}", null, (_, _) => Forget(current)));
        }
        items.Add(pcs);
        items.Add(new ToolStripSeparator());

        var notifications = new ToolStripMenuItem("Notifications") { Checked = _settings.Notifications, CheckOnClick = true };
        notifications.CheckedChanged += (_, _) => { _settings.Notifications = notifications.Checked; _settings.Save(); };
        items.Add(notifications);
        if (HasLocal)
        {
            var staySignedIn = new ToolStripMenuItem("Stay signed in on this computer") { Checked = _settings.StaySignedIn, CheckOnClick = true, ToolTipText = "Signs back in on its own as whoever last signed in here, when the session ends." };
            staySignedIn.CheckedChanged += (_, _) => { _settings.StaySignedIn = staySignedIn.Checked; _settings.Save(); };
            items.Add(staySignedIn);
        }
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartWithWindows.IsOn(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) =>
        {
            try { StartWithWindows.Set(startup.Checked, _exe, _dataRoot); }
            catch (Exception ex) { MessageBox.Show($"Couldn't change that: {ex.Message}", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        items.Add(startup);

        if (HasLocal)
        {
            // This PC's agent (it runs the servers and the panel): its state, and start / stop / restart. Stopping it
            // leaves game servers running.
            items.Add(new ToolStripSeparator());
            _agentStatus = new ToolStripMenuItem("Agent: checking…") { Enabled = false };
            _agentStart = new ToolStripMenuItem("Start the agent", null, async (_, _) => await StartAgentAsync());
            _agentStop = new ToolStripMenuItem("Stop the agent (servers keep running)", null, async (_, _) => await StopAgentAsync(thenStart: false));
            _agentRestart = new ToolStripMenuItem("Restart the agent", null, async (_, _) => await StopAgentAsync(thenStart: true));
            items.AddRange(new ToolStripItem[] { _agentStatus, _agentStart, _agentStop, _agentRestart });
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(HasLocal ? "Quit (servers keep running)" : "Quit", null, (_, _) => Quit()));
        if (HasLocal) { items.Add(new ToolStripMenuItem("Stop everything and quit…", null, async (_, _) => await StopEverythingAsync())); }

        menu.Items.Clear();
        menu.Items.AddRange(items.ToArray());
    }

    // ───────────────────────────── Which PC ─────────────────────────────

    private async Task OpenCurrentAsync()
    {
        if (_settings.Current is { } pc) { await OpenRemoteAsync(pc); return; }
        if (HasLocal) { await ConnectLocalAsync(); return; }
        // Installed to control other PCs, and none added yet.
        _window.Remote = null;
        _window.ShowStatus("Connect to the PC that runs your game servers — or to your hub.", ("Connect to a PC…", AddPc));
        if (_window.Visible) { _window.BeginInvoke(AddPc); }
    }

    private void SwitchTo(SavedPc? pc)
    {
        _settings.CurrentPc = pc?.Url;
        _settings.Save();
        _window.ShowAndActivate();
        _ = OpenCurrentAsync();
    }

    private void AddPc() => AskForPc(null, null);

    /// <summary>The connect window; on OK the PC is saved and opened.</summary>
    private void AskForPc(SavedPc? again, string? reason)
    {
        using var form = new ConnectForm(again, reason);
        if (form.ShowDialog(_window.Visible ? _window : null) != DialogResult.OK || form.Chosen is not { } chosen) { return; }
        if (again != null && !string.Equals(again.Url, chosen.Url, StringComparison.OrdinalIgnoreCase)) { _settings.Pcs.Remove(again); }
        _settings.Remember(chosen);
        _window.ShowAndActivate();
        _ = OpenRemoteAsync(chosen);
    }

    private void Forget(SavedPc pc)
    {
        var answer = MessageBox.Show($"Remove {pc.Name} ({pc.Url}) from this app?\n\nNothing changes on that PC — connect again any time.",
            "WindowsGSM", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) { return; }
        _ = ForgetKeyAsync(pc); // nothing stays signed in for a PC that's gone from the list
        _settings.Pcs.Remove(pc);
        if (_settings.Current == null || _settings.CurrentPc == pc.Url) { _settings.CurrentPc = !HasLocal && _settings.Pcs.Count > 0 ? _settings.Pcs[^1].Url : null; }
        _settings.Save();
        _ = OpenCurrentAsync();
    }

    private async Task OpenRemoteAsync(SavedPc pc)
    {
        _window.Remote = pc;
        _window.ShowStatus($"Connecting to {pc.Name}…");
        ProbeResult found;
        try { found = await PcProbe.ProbeAsync(pc.Url); }
        // A PC that worked before is most likely just off; the full checklist is for one that never answered.
        catch (Exception ex) { Unreachable(pc, ex.Message.StartsWith("Nothing answered", StringComparison.Ordinal) ? null : ex.Message); return; }
        if (_window.Remote != pc) { return; } // switched meanwhile
        // A certificate Windows doesn't trust: only the one accepted for this PC.
        if (found.Fingerprint != null && !CertificateFingerprint.Same(found.Fingerprint, pc.Fingerprint)) { CertificateChanged(found.Fingerprint); return; }
        if (!string.Equals(pc.Name, found.MachineName, StringComparison.Ordinal) && pc.Name == new Uri(pc.Url).Host) { pc.Name = found.MachineName; _settings.Save(); }
        _window.ShowStatus($"Opening {pc.Name}…");
        _ = WatchRemoteVersionAsync();
        try { await _window.OpenAsync(pc.BaseUri); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            _window.ShowStatus("This app needs the Microsoft Edge WebView2 Runtime (built into Windows 11). Install it from microsoft.com/edge/webview2, or open the panel in your browser: " + pc.Url);
        }
    }

    private void Unreachable(SavedPc pc, string? why)
    {
        var actions = new List<(string, Action)> { ("Try again", () => _ = OpenRemoteAsync(pc)), ("Switch PC…", () => ShowPcMenu()) };
        if (HasLocal) { actions.Add(("This PC", () => SwitchTo(null))); }
        _window.ShowStatus($"Couldn't reach {pc.Name} ({new Uri(pc.Url).Authority}).\n\n{why ?? "Is it switched on, with WindowsGSM running? If its address or port changed, connect to it again from Switch PC."}", actions.ToArray());
    }

    private void CertificateChanged(string seen)
    {
        if (_window.Remote is not { } pc) { return; }
        _window.ShowStatus($"{pc.Name}'s certificate isn't the one this app trusts for it, so the connection was stopped.\n\n" +
            "If you changed HTTPS on that PC (a new or renewed certificate), check the new fingerprint and trust it. If you didn't, something may be pretending to be that PC — don't continue.",
            ("Check the new certificate…", () => AskForPc(pc, $"{pc.Name}'s certificate changed. Compare the fingerprint below with Agent settings → HTTPS on that PC before trusting it.")),
            ("Switch PC…", () => ShowPcMenu()));
    }

    /// <summary>Opens the tray menu's PC list where the cursor is (from a button on the window).</summary>
    private void ShowPcMenu()
    {
        var menu = new ContextMenuStrip();
        if (HasLocal) { menu.Items.Add(new ToolStripMenuItem("This PC", null, (_, _) => SwitchTo(null))); }
        foreach (var pc in _settings.Pcs) { var p = pc; menu.Items.Add(new ToolStripMenuItem(p.Name, null, (_, _) => SwitchTo(p)) { Checked = _settings.Current == p }); }
        if (menu.Items.Count > 0) { menu.Items.Add(new ToolStripSeparator()); }
        menu.Items.Add(new ToolStripMenuItem("Connect to another PC…", null, (_, _) => AddPc()));
        menu.Show(Cursor.Position);
    }

    private async Task ConnectLocalAsync()
    {
        _window.Remote = null;
        Uri url = AgentLocator.UrlFor(_dataRoot!);
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
            try { AgentLocator.StartAgent(agent, _dataRoot!); }
            catch (Exception ex) { _window.ShowStatus($"Couldn't start the agent: {ex.Message}"); return; }
            // First run can take a while (plugins compile, certificates load).
            if (!await AgentLocator.WaitAsync(http, url, TimeSpan.FromSeconds(60)))
            {
                _window.ShowStatus($"The agent didn't answer at {url}. Check its log in {Path.Combine(_dataRoot!, "logs")}.");
                return;
            }
        }
        if (_window.Remote != null) { return; } // switched to another PC meanwhile
        _window.ShowStatus("Opening the panel…");
        if (!_watchingUpdates) { _watchingUpdates = true; _ = WatchForUpdateAsync(url); }
        try { await _window.OpenAsync(url); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            _window.ShowStatus("This app needs the Microsoft Edge WebView2 Runtime (built into Windows 11). Install it from microsoft.com/edge/webview2, or open the panel in your browser: " + url);
        }
    }

    // ───────────────────────────── Updates ─────────────────────────────

    private static string MyVersion => System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0].TrimStart('v') ?? "";

    /// <summary>
    /// After the agent updates itself it runs a newer version than this window. Say so once, and offer a
    /// restart through the launcher (which starts the new version's app).
    /// </summary>
    private async Task WatchForUpdateAsync(Uri url)
    {
        string mine = MyVersion;
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
                if (agent.Length == 0 || agent == mine) { continue; }
                _updateItem = new ToolStripMenuItem($"Restart to finish updating ({agent})", null, (_, _) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher) { UseShellExecute = false });
                    Quit();
                }) { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
                _balloonPath = null;
                _tray.ShowBalloonTip(10000, $"WindowsGSM updated to {agent}", "Your servers kept running. Restart the app from this icon to finish.", ToolTipIcon.Info);
                return;
            }
            catch { /* agent restarting */ }
        }
    }

    private bool _watchingRemote;

    /// <summary>
    /// Installed only to control other PCs, nothing here updates this app — so when the PC it shows runs a newer
    /// WindowsGSM, offer to update to that version (the window itself comes from that PC, so it's only the app).
    /// </summary>
    private async Task WatchRemoteVersionAsync()
    {
        if (HasLocal || _watchingRemote) { return; } // with an agent here, the agent's own updates cover the app
        string? launcher = Environment.GetEnvironmentVariable("WGSM_LAUNCHER"), root = Environment.GetEnvironmentVariable("WGSM_INSTALL_ROOT");
        if (string.IsNullOrEmpty(launcher) || !File.Exists(launcher) || string.IsNullOrEmpty(root) || !Directory.Exists(root)) { return; }
        _watchingRemote = true;
        string mine = MyVersion;
        while (_updateItem == null)
        {
            try
            {
                if (_window.Remote is { } pc)
                {
                    var found = await PcProbe.ProbeAsync(pc.Url);
                    string theirs = found.Version.TrimStart('v');
                    if (theirs.Length > 0 && AppReleases.Compare(theirs, mine) > 0)
                    {
                        _updateItem = new ToolStripMenuItem($"Update this app to {theirs}", null, async (_, _) => await UpdateAppAsync(theirs, root, launcher))
                        { Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
                        _balloonPath = null;
                        _tray.ShowBalloonTip(10000, $"WindowsGSM {theirs} is available", $"{pc.Name} runs {theirs}; this app is {mine}. Update it from this icon's menu.", ToolTipIcon.Info);
                        return;
                    }
                }
            }
            catch { /* unreachable right now; next time */ }
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    private bool _updating;

    private async Task UpdateAppAsync(string version, string root, string launcher)
    {
        if (_updating) { return; }
        _updating = true;
        _tray.Text = "WindowsGSM — updating…";
        _tray.ShowBalloonTip(5000, $"Updating to {version}", "Downloading — the app restarts on its own when it's ready.", ToolTipIcon.Info);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM/2");
            var releases = await AppReleases.FetchAsync(http, AppReleases.DefaultRepo);
            // The version the other PC runs; failing that, the newest that isn't newer than it.
            var release = releases.FirstOrDefault(r => AppReleases.Compare(r.Version, version) == 0)
                ?? releases.Where(r => AppReleases.Compare(r.Version, version) <= 0 && AppReleases.Compare(r.Version, MyVersion) > 0)
                    .OrderByDescending(r => r.Version, Comparer<string>.Create(AppReleases.Compare)).FirstOrDefault()
                ?? throw new InvalidOperationException($"WindowsGSM {version} isn't on the releases page ({AppReleases.DefaultRepo}).");
            string zip = await AppReleases.DownloadAsync(http, release, Path.Combine(root, "downloads"));
            await Task.Run(() => AppReleases.Unpack(root, launcher, zip, release.Version));
            try { File.Delete(zip); } catch { /* next time */ }
            // The launcher waits for this app to close, switches to the new version and opens it again.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher, $"--switch \"{release.Version}\" --wait-pid {Environment.ProcessId} --start-app")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root });
            Quit();
        }
        catch (Exception ex)
        {
            _updating = false;
            _tray.Text = "WindowsGSM";
            MessageBox.Show($"Couldn't update the app: {ex.Message}", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Notify(string title, string text, string? path)
    {
        if (!_settings.Notifications) { return; }
        _balloonPath = path;
        _tray.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(text) ? " " : text, ToolTipIcon.None);
    }

    private bool _watchingUpdates;

    // ───────────────────────────── This PC's agent ─────────────────────────────

    /// <summary>
    /// Signs the panel back in through the agent's local-only endpoint (needs the key this Windows account's apps
    /// can read), as whoever last signed in through this app. Only for this PC's own agent.
    /// </summary>
    private async Task<bool> AutoSignInAsync()
    {
        if (_window.Remote is { } remote) { return await RemoteSignInAsync(remote); }
        if (!_settings.StaySignedIn || _dataRoot == null) { return false; }
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

    // ───────────────────────────── Staying signed in to another PC ─────────────────────────────

    /// <summary>Signs the panel back in with the key that PC gave this app. A key it refuses is dropped.</summary>
    private async Task<bool> RemoteSignInAsync(SavedPc pc)
    {
        if (!pc.StaySignedIn || pc.Key is not { } key) { return false; }
        var jar = new System.Net.CookieContainer();
        try
        {
            using var http = pc.Client(jar);
            using var res = await http.PostAsJsonAsync("api/v2/app-login", new { key });
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Removed over there (signed out in this app, "sign out everywhere", password changed…): sign in again.
                pc.Key = null;
                _settings.Save();
                return false;
            }
            if (!res.IsSuccessStatusCode) { return false; }
        }
        catch { return false; }
        var cookie = jar.GetCookies(pc.BaseUri).Cast<System.Net.Cookie>().FirstOrDefault(c => c.Name == "wgsm_session");
        if (cookie == null) { return false; }
        var done = new TaskCompletionSource();
        _window.BeginInvoke(async () => { await _window.SetSessionCookieAsync(cookie); done.TrySetResult(); });
        await done.Task;
        return true;
    }

    private bool _askingForKey;

    /// <summary>Signed in to another PC that should keep this app signed in, without a key yet: ask it for one.</summary>
    private async Task RememberSignInAsync()
    {
        if (_window.Remote is not { StaySignedIn: true } pc || pc.Key != null || _askingForKey) { return; }
        _askingForKey = true;
        try
        {
            string? session = await _window.SessionCookieAsync();
            if (session == null) { return; } // not signed in yet
            var jar = new System.Net.CookieContainer();
            jar.Add(pc.BaseUri, new System.Net.Cookie("wgsm_session", session));
            using var http = pc.Client(jar);
            using var res = await http.PostAsJsonAsync("api/v2/auth/app-key", new { device = Environment.MachineName });
            if (!res.IsSuccessStatusCode) { return; } // signed out meanwhile, or an older WindowsGSM over there
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("key", out var k) && k.GetString() is { Length: > 0 } key && _window.Remote == pc)
            {
                pc.Key = key;
                _settings.Save();
            }
        }
        catch { /* next page load tries again */ }
        finally { _askingForKey = false; }
    }

    private async Task SetStaySignedInAsync(SavedPc pc, bool on)
    {
        pc.StaySignedIn = on;
        _settings.Save();
        if (on) { await RememberSignInAsync(); } else { await ForgetKeyAsync(pc); }
    }

    /// <summary>Drops this app's key, over there too (best effort — it also lapses on its own after 90 days unused).</summary>
    private async Task ForgetKeyAsync(SavedPc pc)
    {
        if (pc.Key is not { } key) { return; }
        pc.Key = null;
        _settings.Save();
        try
        {
            using var http = pc.Client();
            using var _ = await http.PostAsJsonAsync("api/v2/app-key/forget", new { key });
        }
        catch { /* unreachable: it lapses there on its own */ }
    }

    private async Task RefreshAgentMenuAsync()
    {
        if (_dataRoot == null || _agentStatus == null) { return; }
        using var http = AgentLocator.LocalClient();
        bool running = await AgentLocator.IsRunningAsync(http, AgentLocator.UrlFor(_dataRoot));
        _agentStatus.Text = running ? "Agent: running" : "Agent: stopped";
        _agentStart!.Visible = !running;
        _agentStop!.Visible = _agentRestart!.Visible = running;
    }

    private async Task StartAgentAsync()
    {
        _window.ShowAndActivate();
        SwitchTo(null); // starts it when it isn't running, then opens this PC's panel
        await Task.CompletedTask;
    }

    /// <summary>Stops the agent through its local-only endpoint (game servers keep running); optionally starts it again.</summary>
    private async Task StopAgentAsync(bool thenStart)
    {
        if (_dataRoot == null) { return; }
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

        bool showingLocal = _window.Remote == null;
        if (showingLocal) { _window.ShowStatus(thenStart ? "Restarting the agent… (game servers keep running)" : "Stopping the agent… (game servers keep running)"); }
        var until = DateTime.UtcNow.AddMinutes(1);
        while (DateTime.UtcNow < until && await AgentLocator.IsRunningAsync(http, url)) { await Task.Delay(500); }
        if (thenStart) { if (showingLocal) { await ConnectLocalAsync(); } else { AgentLocator.StartAgent(AgentLocator.AgentExe(AppContext.BaseDirectory)!, _dataRoot); } return; }
        _balloonPath = null;
        _tray.ShowBalloonTip(5000, "WindowsGSM agent stopped", "Your game servers keep running. Start it again from this icon.", ToolTipIcon.Info);
        if (showingLocal) { _window.ShowStatus("The agent is stopped — your game servers keep running. Start it from the WindowsGSM icon in the taskbar (or the Start menu)."); }
    }

    /// <summary>Stops every game server on this machine and the agent (via its local-only endpoint), then quits.</summary>
    private async Task StopEverythingAsync()
    {
        if (_dataRoot == null) { return; }
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
