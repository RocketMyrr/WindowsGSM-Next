namespace WindowsGSM.Desktop;

/// <summary>
/// "Connect to a PC": type the other PC's address, see what answered (its name and version, whether the connection
/// is encrypted, and — for a self-signed certificate — the fingerprint to check), name it, open it.
/// </summary>
internal sealed class ConnectForm : Form
{
    private static readonly Color Bg = Color.FromArgb(0x0c, 0x0f, 0x15), Surface = Color.FromArgb(0x15, 0x19, 0x22), Line = Color.FromArgb(0x2b, 0x31, 0x42),
        Text1 = Color.FromArgb(0xe8, 0xeb, 0xf2), Text2 = Color.FromArgb(0xa8, 0xb0, 0xc0), Accent = Color.FromArgb(0x5b, 0x8d, 0xef),
        Warn = Color.FromArgb(0xf0, 0xb4, 0x29), Bad = Color.FromArgb(0xef, 0x5b, 0x5b), Good = Color.FromArgb(0x3f, 0xc1, 0x7a);

    private readonly TextBox _address = Box(), _name = Box();
    private readonly Button _check = Btn("Check", primary: false), _open = Btn("Open", primary: true), _cancel = Btn("Cancel", primary: false);
    private readonly Label _status = Para("", Text2);
    private readonly FlowLayoutPanel _found = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Visible = false, Margin = new Padding(0, 8, 0, 0) };
    private readonly CheckBox _trust = new() { AutoSize = true, ForeColor = Text1, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _stay = new() { AutoSize = true, ForeColor = Text1, Margin = new Padding(0, 10, 0, 0), Checked = true, Text = "Stay signed in to this PC" };
    private ProbeResult? _result;
    private CancellationTokenSource? _probe;

    /// <summary>The PC to open, once Open was pressed.</summary>
    public SavedPc? Chosen { get; private set; }

    public ConnectForm(SavedPc? again = null, string? reason = null)
    {
        if (again != null) { _stay.Checked = again.StaySignedIn; }
        Text = again == null ? "Connect to a PC" : $"Connect to {again.Name}";
        Icon = AppIcon.Create(32);
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24, 18, 24, 18);
        ShowInTaskbar = true;

        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        page.Controls.Add(new Label { Text = "Control game servers on another PC", AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Text1, Margin = new Padding(0, 0, 0, 6) });
        if (reason != null) { page.Controls.Add(Para(reason, Warn)); }
        page.Controls.Add(Para("The address of the PC that runs WindowsGSM — or of your hub, to see all your machines in one place.", Text2));

        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        _address.Width = 380;
        _address.PlaceholderText = "192.168.1.20   or   games.example.com";
        _address.Text = again?.Url ?? "";
        row.Controls.Add(_address);
        row.Controls.Add(_check);
        page.Controls.Add(row);
        page.Controls.Add(Para("On that PC: Agent settings → Network → \"Reachable from other computers\", then restart its agent. The port is 8971 unless it was changed.", Text2, small: true));
        page.Controls.Add(_status);

        _found.Controls.Add(new Label { Text = "Name in this app", AutoSize = true, ForeColor = Text2, Margin = new Padding(0, 10, 0, 2) });
        _name.Width = 380;
        _found.Controls.Add(_stay);
        _found.Controls.Add(Para("Signs this app back in on its own after you sign in once. Remove it any time: Account & security on that PC, or untick it in the tray's PC menu.", Text2, small: true));
        page.Controls.Add(_found);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_open);
        page.Controls.Add(buttons);
        page.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(page);

        _open.Enabled = false;
        AcceptButton = _check;
        CancelButton = _cancel;
        _check.Click += async (_, _) => await CheckAsync();
        _address.TextChanged += (_, _) => { _result = null; _found.Visible = false; _open.Enabled = false; AcceptButton = _check; _status.Text = ""; };
        _trust.CheckedChanged += (_, _) => _open.Enabled = _result != null && (_result.Fingerprint == null || _trust.Checked);
        _open.Click += (_, _) => Finish();
        _cancel.Click += (_, _) => { _probe?.Cancel(); DialogResult = DialogResult.Cancel; Close(); };
        Shown += async (_, _) => { _address.Focus(); if (again != null) { await CheckAsync(); } };
    }

    private async Task CheckAsync()
    {
        _probe?.Cancel();
        var cts = _probe = new CancellationTokenSource();
        _result = null;
        _found.Visible = false;
        _open.Enabled = false;
        _check.Enabled = false;
        Say("Looking for WindowsGSM…", Text2);
        try
        {
            var r = await PcProbe.ProbeAsync(_address.Text, cts.Token);
            if (cts.IsCancellationRequested) { return; }
            _result = r;
            Present(r);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) { Say(ex.Message, Bad); } }
        finally { if (!IsDisposed) { _check.Enabled = true; } }
    }

    private void Present(ProbeResult r)
    {
        Say($"✔  Found {r.MachineName} — WindowsGSM {r.Version.TrimStart('v')} at {r.Url.Host}:{r.Url.Port}", Good);
        // Rebuild the details (keeps the name box and its label at the end).
        foreach (var c in _found.Controls.OfType<Control>().Where(c => c.Tag as string == "detail").ToList()) { _found.Controls.Remove(c); if (c != _trust) { c.Dispose(); } }
        var details = new List<Control>();
        if (r.SetupRequired) { details.Add(Detail("That PC hasn't been set up yet. Finish setup on it first (create the owner account), or have its one-time setup code ready.", Warn)); }
        if (r.PlainHttp && !r.OnThisNetwork)
        {
            details.Add(Detail("⚠  Not encrypted, and this address is across the internet: your password would be sent in the clear. On that PC, turn on HTTPS (Agent settings → HTTPS) and connect again.", Bad));
        }
        else if (r.PlainHttp)
        {
            details.Add(Detail("Not encrypted (HTTP). Fine on your own network; turn on HTTPS on that PC before using it over the internet.", Text2));
        }
        if (r.Fingerprint != null)
        {
            details.Add(Detail($"Encrypted, but {r.CertificateProblem}. Check this fingerprint matches the one in Agent settings → HTTPS on that PC:", Warn));
            var fp = new TextBox { Text = r.Fingerprint, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Text1, Font = new Font("Consolas", 9f), Width = 470, Multiline = true, Height = 36, Tag = "detail", Margin = new Padding(0, 4, 0, 0) };
            details.Add(fp);
            _trust.Text = "It matches — trust this PC's certificate";
            _trust.Checked = false;
            _trust.Tag = "detail";
            details.Add(_trust);
        }
        else if (!r.PlainHttp) { details.Add(Detail("🔒  Encrypted with a certificate Windows trusts.", Text2)); }
        for (int i = 0; i < details.Count; i++) { _found.Controls.Add(details[i]); _found.Controls.SetChildIndex(details[i], i); }
        if (!_found.Controls.Contains(_name)) { _found.Controls.Add(_name); _found.Controls.SetChildIndex(_name, _found.Controls.IndexOf(_stay)); }
        if (_name.Text.Length == 0 || _name.Tag as string != "typed") { _name.Text = r.MachineName; }
        _name.TextChanged -= NameTyped;
        _name.TextChanged += NameTyped;
        _found.Visible = true;
        bool blocked = r.PlainHttp && !r.OnThisNetwork;
        _open.Enabled = !blocked && r.Fingerprint == null;
        if (!blocked) { AcceptButton = _open; }
        if (blocked) { _open.Enabled = false; }
    }

    private void NameTyped(object? sender, EventArgs e) => _name.Tag = "typed";

    private void Finish()
    {
        if (_result == null) { return; }
        Chosen = new SavedPc
        {
            Name = _name.Text.Trim() is { Length: > 0 } n ? n : _result.MachineName,
            Url = _result.Url.ToString(),
            Fingerprint = _result.Fingerprint,
            StaySignedIn = _stay.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Say(string text, Color colour) { _status.Text = text; _status.ForeColor = colour; }

    private static Label Detail(string text, Color colour) { var l = Para(text, colour); l.Tag = "detail"; return l; }

    private static Label Para(string text, Color colour, bool small = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = colour, Margin = new Padding(0, 6, 0, 0),
        Font = small ? new Font("Segoe UI", 9f) : new Font("Segoe UI", 10f),
    };

    private static TextBox Box() => new() { BackColor = Surface, ForeColor = Text1, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 0, 8, 0) };

    private static Button Btn(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(96, 32), FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0),
            BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.White : Text1, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        // Flat buttons look the same when off: dim it so "Open" clearly waits for the trust box.
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? (primary ? Accent : Surface) : Surface;
            b.FlatAppearance.BorderColor = b.Enabled && primary ? Accent : Line;
        };
        return b;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { /* older Windows */ }
    }
}
