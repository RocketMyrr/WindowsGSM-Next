using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WindowsGSM.Desktop;

/// <summary>
/// The panel in a native window. Links to other sites open in the default browser; the page's notifications
/// arrive through the WebView2 message bridge and become Windows notifications while the window is hidden.
/// Closing the window only hides it — the tray icon (and the agent) stay.
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x0c, 0x0f, 0x15) };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(0xa8, 0xb0, 0xc0),
        BackColor = Color.FromArgb(0x0c, 0x0f, 0x15), Font = new Font("Segoe UI", 11f), Text = "Starting WindowsGSM…",
    };
    private readonly DesktopSettings _settings;
    private Uri? _base;
    private bool _ready;

    /// <summary>The sign-in page asks the app to sign back in on its own; true once a session cookie is in place.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Task<bool>>? AutoSignIn { get; set; }

    /// <summary>Puts the agent's session cookie into the panel (from the app's own sign-in on this computer).</summary>
    public async Task SetSessionCookieAsync(System.Net.Cookie cookie)
    {
        if (!_ready || _base == null) { return; }
        var mgr = _web.CoreWebView2.CookieManager;
        var c = mgr.CreateCookie(cookie.Name, cookie.Value, _base.Host, "/");
        c.IsHttpOnly = true;
        c.IsSecure = _base.Scheme == Uri.UriSchemeHttps;
        c.SameSite = CoreWebView2CookieSameSiteKind.Strict;
        mgr.AddOrUpdateCookie(c);
        await Task.CompletedTask;
    }

    /// <summary>The page asked for a notification: (title, text, path to open when clicked).</summary>
    public event Action<string, string, string?>? NotificationRequested;

    /// <summary>Set when quitting from the tray: closing then really closes instead of hiding.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Exiting { get; set; }

    public MainWindow(DesktopSettings settings)
    {
        _settings = settings;
        Text = "WindowsGSM";
        Icon = AppIcon.Create(64);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1360, 860);
        BackColor = _status.BackColor;
        if (settings.Bounds is [int x, int y, int w, int h] && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, w, h))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, w, h);
        }
        if (settings.Maximized) { WindowState = FormWindowState.Maximized; }
        Controls.Add(_web);
        Controls.Add(_status);
        _status.BringToFront();
    }

    // ── Title bar: the panel's colours (Windows 11; Windows 10 gets the plain dark/light bar) ──

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmUseDarkMode = 20, DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;
    private (Color Caption, Color Text, Color Border, bool Dark) _chrome =
        (Color.FromArgb(0x0c, 0x0f, 0x15), Color.FromArgb(0xe8, 0xeb, 0xf2), Color.FromArgb(0x2b, 0x31, 0x42), true);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyChrome();
    }

    private void ApplyChrome()
    {
        if (!IsHandleCreated) { return; }
        static int Ref(Color c) => c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
        try
        {
            int dark = _chrome.Dark ? 1 : 0, caption = Ref(_chrome.Caption), text = Ref(_chrome.Text), border = Ref(_chrome.Border);
            DwmSetWindowAttribute(Handle, DwmUseDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(Handle, DwmCaptionColor, ref caption, sizeof(int)); // these three are ignored before Windows 11
            DwmSetWindowAttribute(Handle, DwmTextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(Handle, DwmBorderColor, ref border, sizeof(int));
        }
        catch { /* older Windows: default title bar */ }
    }

    private static Color? CssColor(string? css)
    {
        css = css?.Trim();
        if (css == null || !css.StartsWith('#') || (css.Length != 7 && css.Length != 4)) { return null; }
        try { return ColorTranslator.FromHtml(css); } catch { return null; }
    }

    public void ShowStatus(string text) { _status.Text = text; _status.Visible = true; _status.BringToFront(); }

    public async Task OpenAsync(Uri baseUrl)
    {
        _base = baseUrl;
        if (!_ready)
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DesktopSettings.Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            // Tells the agent "the desktop app on this computer" (it remembers who signs in here — see the agent's DesktopSignIn).
            core.Settings.UserAgent = core.Settings.UserAgent + " WindowsGSM-Desktop/2";
            // Our own agent may use a self-signed certificate; only for this machine.
            core.ServerCertificateErrorDetected += (_, e) =>
            {
                if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u) && AgentLocator.IsLocal(u)) { e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow; }
            };
            // Anything that isn't the panel opens in the default browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (_base != null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && !SameOrigin(u, _base))
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "WindowsGSM" : core.DocumentTitle.Replace(" · WindowsGSM", " — WindowsGSM");
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, e) => { if (e.IsSuccess) { _status.Visible = false; } };
            _ready = true;
        }
        _web.CoreWebView2.Navigate(baseUrl.ToString());
    }

    /// <summary>Goes to a page of the panel ("/machines/…/servers/3").</summary>
    public void Go(string? path)
    {
        if (!_ready || _base == null || string.IsNullOrEmpty(path) || !path.StartsWith('/')) { return; }
        // In-app navigation keeps the page (and its live connection) instead of reloading it.
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.dispatchEvent(new CustomEvent('wgsm-navigate', {{ detail: {JsonSerializer.Serialize(path)} }}))");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            if (m.GetProperty("type").GetString() == "auto-signin")
            {
                _ = Task.Run(async () =>
                {
                    bool ok = false;
                    try { ok = AutoSignIn != null && await AutoSignIn(); } catch { ok = false; }
                    BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "signin-result", ok })));
                });
                return;
            }
            // The page's theme changed (or it just loaded): the title bar follows it.
            if (m.GetProperty("type").GetString() == "chrome")
            {
                if (CssColor(Str(m, "bg")) is Color bg && CssColor(Str(m, "text")) is Color fg)
                {
                    bool dark = Str(m, "theme") != "light";
                    _chrome = (bg, fg, CssColor(Str(m, "line")) ?? bg, dark);
                    _status.BackColor = BackColor = bg;
                    ApplyChrome();
                }
                return;
            }
            if (m.GetProperty("type").GetString() == "notify")
            {
                bool looking = Visible && WindowState != FormWindowState.Minimized && ContainsFocus;
                if (!looking) { NotificationRequested?.Invoke(Str(m, "title") ?? "WindowsGSM", Str(m, "body") ?? "", Str(m, "path")); }
            }
        }
        catch { /* not ours */ }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
        (string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) || (AgentLocator.IsLocal(a) && AgentLocator.IsLocal(b)));

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) { return; }
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    public void ShowAndActivate()
    {
        if (!Visible) { Show(); }
        if (WindowState == FormWindowState.Minimized) { WindowState = _settings.Maximized ? FormWindowState.Maximized : FormWindowState.Normal; }
        Activate();
        BringToFront();
    }

    /// <summary>The first time the window is closed to the tray, say so.</summary>
    public event Action? HiddenToTray;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberBounds();
        if (!Exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }

    private void RememberBounds()
    {
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Bounds = new[] { b.X, b.Y, b.Width, b.Height };
        _settings.Save();
    }
}
