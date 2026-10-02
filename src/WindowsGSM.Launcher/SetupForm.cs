using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WindowsGSM.Launcher
{
    /// <summary>
    /// Setup, step by step. It works out what you're doing before asking anything:
    ///   • a copy is already installed → Update (one click; your game servers keep running), or repair it;
    ///   • run from the installed folder (Start menu → setup) → change options or repair;
    ///   • WindowsGSM 1.x found → carry its servers over (the folder is checked first, nothing changes until Install);
    ///   • otherwise a fresh install.
    /// Per user — no administrator rights. Help (F1) answers questions at every step.
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private enum Step { Welcome, Data, Options, Working, Done }
        private enum Kind { Fresh, Update, Maintain }

        private readonly string _packageRoot, _version;
        private readonly Install _existing;
        private readonly string _legacy;
        private Kind _kind;
        private Step _step;

        // Header, page, footer
        private readonly Label _title = new Label(), _subtitle = new Label();
        private readonly Panel _page = new Panel();
        private readonly Button _back = new Button(), _next = new Button(), _cancel = new Button();
        private readonly LinkLabel _help = new LinkLabel();
        private readonly Label _steps = new Label();

        // Welcome
        private readonly RadioButton _doUpdate = new RadioButton(), _doSeparate = new RadioButton();
        // Data
        private readonly RadioButton _useExisting = new RadioButton(), _startFresh = new RadioButton();
        private readonly TextBox _existingDir = new TextBox(), _freshDir = new TextBox();
        private readonly ListView _report = new ListView { View = View.Details, HeaderStyle = ColumnHeaderStyle.None, FullRowSelect = true, BorderStyle = BorderStyle.None };
        private readonly Label _reportTitle = new Label(), _space = new Label();
        private bool _checkBlocks = true;
        private int _checkRun;
        // Options
        private readonly TextBox _installDir = new TextBox();
        private readonly Label _installNote = new Label();
        private readonly CheckBox _startMenu = new CheckBox(), _desktop = new CheckBox(), _agentAtSignIn = new CheckBox(), _trayAtSignIn = new CheckBox();
        // Working / done
        private readonly ProgressBar _progress = new ProgressBar { Style = ProgressBarStyle.Continuous, Height = 10 };
        private readonly Label _status = new Label();
        private readonly ListBox _log = new ListBox();
        private readonly CheckBox _openNow = new CheckBox();
        private string _doneMessage, _doneWarning;
        private bool _failed;
        private Install _installed;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override CreateParams CreateParams => Chrome.Params(base.CreateParams, minimise: true);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE: a title bar that matches the window
            try { DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { }
        }

        public SetupForm(string packageRoot, string version, string dataHint = null)
        {
            _packageRoot = Path.GetFullPath(packageRoot).TrimEnd('\\');
            _version = version;
            _existing = Install.Load(_packageRoot) ?? Install.FindInstalled(); // run from an installed folder: that copy
            bool runFromInstall = _existing != null && string.Equals(Path.GetFullPath(_existing.Root).TrimEnd('\\'), _packageRoot, StringComparison.OrdinalIgnoreCase);
            _kind = _existing == null ? Kind.Fresh : runFromInstall ? Kind.Maintain : Kind.Update;
            _legacy = !string.IsNullOrWhiteSpace(dataHint) && Directory.Exists(dataHint) ? dataHint : GuessLegacyFolder(_existing?.Data);

            Text = _kind == Kind.Fresh ? $"Install WindowsGSM {version}" : _kind == Kind.Update ? $"Update WindowsGSM to {version}" : "WindowsGSM setup";
            Icon = Program.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text1;
            Font = new Font("Segoe UI", 9.75f);
            Chrome.Frameless(this); // our own title bar: the header below
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 660);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F1) { OpenHelp(); e.Handled = true; } };

            BuildChrome();
            DefaultChoices();
            Shown += (s, e) => Go(Step.Welcome);
            FormClosing += (s, e) =>
            {
                if (_step == Step.Working) { e.Cancel = true; return; } // never half-installed
            };
        }

        // ─────────────────────────────── Layout ───────────────────────────────

        private int ContentWidth => ClientSize.Width - 64 - SystemInformation.VerticalScrollBarWidth - 4; // room for the page scrollbar

        private void BuildChrome()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Surface, Padding = new Padding(28, 18, 28, 12) };
            var logo = new PictureBox { Image = Program.AppLogo(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Location = new Point(28, 20) };
            _title.Font = new Font("Segoe UI Semibold", 15f);
            _title.AutoSize = true;
            _title.Location = new Point(90, 16);
            _subtitle.ForeColor = Theme.Text2;
            _subtitle.AutoSize = true;
            _subtitle.Location = new Point(92, 50);
            _steps.ForeColor = Theme.Text2;
            _steps.AutoSize = true;
            _steps.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.AddRange(new Control[] { logo, _title, _subtitle, _steps });
            // The header is the title bar: drag it to move the window; minimise and close in its corner.
            Chrome.DragBy(this, header, logo, _title, _subtitle, _steps);
            Chrome.Corner(header, Chrome.Button(this, "close", Theme.Surface), Chrome.Button(this, "min", Theme.Surface));
            header.Resize += (s, e) => PlaceSteps();

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Surface, Padding = new Padding(28, 14, 28, 14) };
            _help.Text = "Help (F1)";
            _help.AutoSize = true;
            _help.LinkColor = Theme.Text2;
            _help.ActiveLinkColor = Theme.Text1;
            _help.LinkBehavior = LinkBehavior.HoverUnderline;
            _help.Location = new Point(28, 22);
            _help.LinkClicked += (s, e) => OpenHelp();
            Style(_next, primary: true, width: 130);
            Style(_back, primary: false, width: 96);
            Style(_cancel, primary: false, width: 96);
            _back.Text = "Back";
            _cancel.Text = "Cancel";
            _next.Anchor = _back.Anchor = _cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Controls.AddRange(new Control[] { _help, _back, _next, _cancel });
            footer.Resize += (s, e) =>
            {
                _cancel.Location = new Point(footer.Width - 28 - _cancel.Width, 14);
                _next.Location = new Point(_cancel.Left - 10 - _next.Width, 14);
                _back.Location = new Point(_next.Left - 10 - _back.Width, 14);
            };
            _back.Click += (s, e) => GoBack();
            _next.Click += async (s, e) => await NextAsync();
            _cancel.Click += (s, e) => Close();
            AcceptButton = _next;

            _page.Dock = DockStyle.Fill;
            _page.BackColor = Theme.Bg; // the form's own colour is the 1px border
            _page.Padding = new Padding(32, 20, 32, 12);
            _page.AutoScroll = true;
            Controls.Add(_page);
            Controls.Add(header);
            Controls.Add(footer);
        }

        private static void Style(Button b, bool primary, int width)
        {
            b.Width = width;
            b.Height = 36;
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            b.BackColor = primary ? Theme.Accent : Theme.Raised;
            b.ForeColor = primary ? Color.White : Theme.Text1;
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Theme.Line;
            // Flat buttons keep their colours when disabled; make "not yet" look it.
            b.EnabledChanged += (s, e) =>
            {
                b.BackColor = b.Enabled ? (primary ? Theme.Accent : Theme.Raised) : Color.FromArgb(0x23, 0x28, 0x38);
                b.ForeColor = b.Enabled ? (primary ? Color.White : Theme.Text1) : Theme.Text2;
            };
        }

        /// <summary>A vertical stack for one page.</summary>
        private FlowLayoutPanel Stack()
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
            return f;
        }

        private Label Heading(string text, int top = 14) => new Label { Text = text, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, ForeColor = Theme.Text1, Margin = new Padding(0, top, 0, 6) };

        private Label Para(string text, bool muted = true, int indent = 0) => new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth - indent, 0), ForeColor = muted ? Theme.Text2 : Theme.Text1,
            Margin = new Padding(indent, 0, 0, 8),
        };

        /// <summary>A coloured note box: info (blue), good, warn.</summary>
        private Control Note(string text, Color colour)
        {
            var p = new Panel { BackColor = Theme.Raised, Margin = new Padding(0, 6, 0, 10) };
            var bar = new Panel { BackColor = colour, Width = 3, Dock = DockStyle.Left };
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth - 40, 0), ForeColor = Theme.Text1, Location = new Point(16, 10) };
            p.Controls.Add(l);
            p.Controls.Add(bar);
            p.Width = ContentWidth;
            p.Height = l.GetPreferredSize(new Size(ContentWidth - 40, 0)).Height + 20;
            l.SizeChanged += (s, e) => p.Height = l.Height + 20;
            return p;
        }

        private Control Choice(RadioButton r, string title, string description)
        {
            r.Text = title;
            r.AutoSize = true;
            r.Font = new Font("Segoe UI Semibold", 10f);
            r.ForeColor = Theme.Text1;
            r.Margin = new Padding(0, 6, 0, 0);
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            f.Controls.Add(r);
            if (description != null) { f.Controls.Add(Para(description, indent: 20)); }
            return f;
        }

        private Control PathRow(TextBox box, string browseTitle, int indent = 0)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, Width = ContentWidth - indent, Height = 34, Margin = new Padding(indent, 2, 0, 6) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 3, 8, 0);
            box.BackColor = Theme.Surface;
            box.ForeColor = Theme.Text1;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Consolas", 10f);
            var browse = new Button { Text = "Browse…", AutoSize = true };
            Style(browse, primary: false, width: 90);
            browse.Height = 28;
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = browseTitle, SelectedPath = Directory.Exists(box.Text) ? box.Text : "", ShowNewFolderButton = true })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) { box.Text = dlg.SelectedPath; }
                }
            };
            row.Controls.Add(box, 0, 0);
            row.Controls.Add(browse, 1, 0);
            return row;
        }

        private CheckBox Option(CheckBox c, string text, string hint, FlowLayoutPanel into)
        {
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text1;
            c.Margin = new Padding(0, 6, 0, 0);
            into.Controls.Add(c);
            if (hint != null) { into.Controls.Add(Para(hint, indent: 18)); }
            return c;
        }

        // ─────────────────────────────── Navigation ───────────────────────────────

        private void DefaultChoices()
        {
            _doUpdate.Checked = true;
            _installDir.Text = Install.DefaultRoot;
            _freshDir.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WindowsGSM");
            _existingDir.Text = _legacy ?? "";
            (_legacy != null ? _useExisting : _startFresh).Checked = true;

            // Options: what's there now when maintaining or updating; sensible defaults otherwise.
            bool known = _existing != null;
            _startMenu.Checked = !known || File.Exists(Shell.StartMenuShortcut);
            _desktop.Checked = !known || File.Exists(Shell.DesktopShortcut);
            _agentAtSignIn.Checked = !known || Shell.AgentAtSignIn();
            _trayAtSignIn.Checked = known && Shell.TrayAtSignIn();

            // Each radio sits in its own panel, so WinForms doesn't group them: pair them by hand.
            Pair(_doUpdate, _doSeparate);
            Pair(_useExisting, _startFresh);
            foreach (var r in new[] { _useExisting, _startFresh }) { r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) { DataModeChanged(); } }; }
            _existingDir.TextChanged += (s, e) => { if (_useExisting.Checked) { _ = CheckAsync(); } };
            _freshDir.TextChanged += (s, e) => { if (_startFresh.Checked) { _ = CheckAsync(); } };
            _installDir.TextChanged += (s, e) => PaintInstallNote();
            _doUpdate.CheckedChanged += (s, e) => PaintWelcomeButton();
        }

        private static void Pair(RadioButton a, RadioButton b)
        {
            a.CheckedChanged += (s, e) => { if (a.Checked) { b.Checked = false; } };
            b.CheckedChanged += (s, e) => { if (b.Checked) { a.Checked = false; } };
        }

        private bool Updating => _kind == Kind.Update && _doUpdate.Checked;
        private bool Maintaining => _kind == Kind.Maintain;

        /// <summary>The steps this run shows (for "Step 2 of 3").</summary>
        private Step[] Flow =>
            Updating ? new[] { Step.Welcome, Step.Working, Step.Done }
            : Maintaining ? new[] { Step.Welcome, Step.Options, Step.Working, Step.Done }
            : new[] { Step.Welcome, Step.Data, Step.Options, Step.Working, Step.Done };

        private void Go(Step step)
        {
            _step = step;
            Text = Updating ? $"Update WindowsGSM to {_version}" : Maintaining ? "WindowsGSM setup" : $"Install WindowsGSM {_version}";
            _page.SuspendLayout();
            _page.Controls.Clear();
            _page.AutoScrollPosition = new Point(0, 0);
            Control content;
            switch (step)
            {
                case Step.Welcome: content = WelcomePage(); break;
                case Step.Data: content = DataPage(); break;
                case Step.Options: content = OptionsPage(); break;
                case Step.Working: content = WorkingPage(); break;
                default: content = DonePage(); break;
            }
            _page.Controls.Add(content);
            _page.ResumeLayout();

            var flow = Flow.Where(s => s != Step.Working && s != Step.Done).ToArray();
            int at = Array.IndexOf(flow, step);
            _steps.Text = at >= 0 && flow.Length > 1 ? $"Step {at + 1} of {flow.Length}" : "";
            PlaceSteps();
            _back.Visible = step == Step.Data || step == Step.Options;
            _cancel.Text = "Cancel";
            _cancel.Visible = step != Step.Working && step != Step.Done;
            _cancel.Enabled = true;
            _next.Enabled = step != Step.Working;
            _next.Visible = step != Step.Working;
            if (step == Step.Welcome) { PaintWelcomeButton(); }
            if (step == Step.Data) { _next.Text = "Next"; DataModeChanged(); }
            if (step == Step.Options) { _next.Text = Maintaining ? "Apply" : "Install"; PaintInstallNote(); }
            if (step == Step.Done) { _next.Text = "Finish"; }
            _next.Focus();
        }

        /// <summary>"Step 2 of 3", under the window buttons.</summary>
        private void PlaceSteps()
        {
            if (_steps.Parent != null) { _steps.Location = new Point(_steps.Parent.ClientSize.Width - _steps.PreferredWidth - 28, 50); }
        }

        private void GoBack()
        {
            var flow = Flow;
            int i = Array.IndexOf(flow, _step);
            if (i > 0) { Go(flow[i - 1]); }
        }

        private async Task NextAsync()
        {
            switch (_step)
            {
                case Step.Welcome:
                    if (_kind == Kind.Update && !Updating && _existing != null
                        && string.Equals(Path.GetFullPath(_installDir.Text.Trim()).TrimEnd('\\'), Path.GetFullPath(_existing.Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        _installDir.Text = _existing.Root.TrimEnd('\\') + " (2)"; // a separate copy: not on top of the installed one
                    }
                    if (Updating) { await RunAsync(); } else { Go(Maintaining ? Step.Options : Step.Data); }
                    break;
                case Step.Data:
                    if (!ValidateData()) { return; }
                    Go(Step.Options);
                    break;
                case Step.Options:
                    if (!Maintaining && !ValidateInstallDir()) { return; }
                    await RunAsync();
                    break;
                case Step.Done:
                    if (_failed) { _failed = false; Go(Step.Welcome); } else { Finish(); }
                    break;
            }
        }

        private void OpenHelp()
        {
            string topic = _step == Step.Data ? (_useExisting.Checked ? "legacy" : "what")
                : _step == Step.Options ? "startup"
                : _step == Step.Done ? "firewall"
                : Updating || Maintaining ? "update" : "what";
            HelpForm.Open(this, topic);
        }

        // ─────────────────────────────── Pages ───────────────────────────────

        private Control WelcomePage()
        {
            var s = Stack();
            if (_kind == Kind.Update)
            {
                _title.Text = "Update WindowsGSM";
                _subtitle.Text = $"{_existing.Current} is installed · this is {_version}";
                int cmp = Install.CompareVersions(_version, _existing.Current);
                if (cmp < 0) { s.Controls.Add(Note($"This download ({_version}) is older than the installed WindowsGSM ({_existing.Current}). To go back after an update, use Agent settings → Updates → Go back instead. You can still install it.", Theme.Warn)); }
                else if (cmp == 0) { s.Controls.Add(Note($"WindowsGSM {_version} is already installed. Continuing repairs it — the same version is copied in again.", Theme.Accent)); }

                s.Controls.Add(Choice(_doUpdate, cmp == 0 ? "Repair my installed WindowsGSM (recommended)" : $"Update my installed WindowsGSM (recommended)",
                    $"Installed in {_existing.Root}\nGame servers: {_existing.Data}"));
                s.Controls.Add(Para("• Your game servers keep running — only WindowsGSM restarts, then picks them back up.", false, 20));
                s.Controls.Add(Para("• Servers, backups, settings, accounts and shortcuts stay as they are.", false, 20));
                s.Controls.Add(Para($"• {_existing.Current} stays installed, so you can go back (Agent settings → Updates).", false, 20));
                s.Controls.Add(Choice(_doSeparate, "Install a separate copy instead", "For testing: a second WindowsGSM with its own game servers folder. Most people don't need this."));
                if (_existing.Data != null && !Directory.Exists(_existing.Data))
                {
                    s.Controls.Add(Note($"The installed copy's game servers folder ({_existing.Data}) isn't there right now — is a drive disconnected? Updating keeps pointing at it.", Theme.Warn));
                }
                return s;
            }
            if (_kind == Kind.Maintain)
            {
                _title.Text = "WindowsGSM setup";
                _subtitle.Text = $"Version {_existing.Current} · installed in {_existing.Root}";
                s.Controls.Add(Heading("Change options or repair", 0));
                s.Controls.Add(Para("Change the shortcuts and what starts with Windows. Applying also repairs the Start menu entries and the uninstall entry in Windows Settings."));
                s.Controls.Add(Para("To update, use Agent settings → Updates in the app, or run a newer download's WindowsGSM.exe.", false));
                s.Controls.Add(Para($"Game servers folder: {_existing.Data}"));
                return s;
            }

            _title.Text = "Install WindowsGSM";
            _subtitle.Text = $"Version {_version} · installs for you only, no administrator needed";
            s.Controls.Add(Heading("Welcome", 0));
            s.Controls.Add(Para("WindowsGSM installs, runs, updates and backs up your game servers — from this PC, your phone or anywhere, in the browser or this app.", false));
            s.Controls.Add(Para("Setup takes about a minute. It asks two things:"));
            s.Controls.Add(Para("1.  Where your game servers live — a new folder, or your WindowsGSM 1.x folder to keep everything.", false, 16));
            s.Controls.Add(Para("2.  Shortcuts and whether WindowsGSM starts with Windows.", false, 16));
            if (_legacy != null)
            {
                s.Controls.Add(Note($"Found WindowsGSM 1.x in {_legacy}. You can bring its servers, backups and accounts over on the next step — it's checked first, and nothing changes until you press Install.", Theme.Good));
            }
            s.Controls.Add(Para("Questions? Press Help (F1) at any step."));
            return s;
        }

        private void PaintWelcomeButton()
        {
            if (_step != Step.Welcome) { return; }
            _next.Text = Updating ? (Install.CompareVersions(_version, _existing.Current) == 0 ? "Repair" : "Update") : "Next";
            _back.Visible = false;
        }

        private Control DataPage()
        {
            _title.Text = "Your game servers";
            _subtitle.Text = "Where servers, backups, settings and accounts are kept";
            var s = Stack();
            s.Controls.Add(Para("This folder is yours: updates and uninstalling never delete it. Game servers can take a lot of space, so pick a drive with room to spare."));
            s.Controls.Add(Choice(_useExisting, _legacy != null ? "Use my WindowsGSM 1.x folder (keep my servers)" : "Use an existing WindowsGSM folder",
                "Servers, their settings, backups, schedules and web-dashboard accounts carry over. Close the old WindowsGSM first."));
            s.Controls.Add(PathRow(_existingDir, "Pick your WindowsGSM folder (the one with servers, configs and backups)", 20));
            s.Controls.Add(Choice(_startFresh, "Start fresh", "A new, empty folder for your game servers."));
            s.Controls.Add(PathRow(_freshDir, "Choose a folder for your game servers", 20));

            _space.AutoSize = true;
            _space.ForeColor = Theme.Text2;
            _space.Margin = new Padding(0, 6, 0, 0);
            s.Controls.Add(_space);
            _reportTitle.AutoSize = true;
            _reportTitle.MaximumSize = new Size(ContentWidth, 0);
            _reportTitle.Margin = new Padding(0, 8, 0, 4);
            s.Controls.Add(_reportTitle);
            _report.BackColor = Theme.Surface;
            _report.ForeColor = Theme.Text1;
            _report.Width = ContentWidth;
            _report.Height = 150;
            _report.ShowItemToolTips = true;
            if (_report.Columns.Count == 0) { _report.Columns.Add("", 70); _report.Columns.Add("", ContentWidth - 90); }
            s.Controls.Add(_report);
            return s;
        }

        private Control OptionsPage()
        {
            _title.Text = Maintaining ? "Options" : "Almost done";
            _subtitle.Text = Maintaining ? "Shortcuts and start-up" : "Where the app goes, shortcuts and start-up";
            var s = Stack();
            if (!Maintaining)
            {
                s.Controls.Add(Heading("Install the app to", 0));
                s.Controls.Add(Para("Just WindowsGSM itself — the suggested folder is right for almost everyone."));
                s.Controls.Add(PathRow(_installDir, "Choose where to install the WindowsGSM app"));
                _installNote.AutoSize = true;
                _installNote.MaximumSize = new Size(ContentWidth, 0);
                _installNote.ForeColor = Theme.Text2;
                _installNote.Margin = new Padding(0, 0, 0, 4);
                s.Controls.Add(_installNote);
            }
            s.Controls.Add(Heading("Shortcuts", Maintaining ? 0 : 14));
            var shortcuts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            Option(_startMenu, "Start menu", "WindowsGSM, plus a WindowsGSM folder: start / stop / restart the agent, setup, uninstall.", shortcuts);
            Option(_desktop, "Desktop", null, shortcuts);
            s.Controls.Add(shortcuts);
            s.Controls.Add(Heading("When I sign in to Windows"));
            var startup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            Option(_agentAtSignIn, "Start the agent (recommended)", "Runs your game servers and the web panel. Servers with auto-start come back after a reboot, and the agent is restarted if it ever stops.", startup);
            Option(_trayAtSignIn, "Show the WindowsGSM tray icon", "The app window and notifications, waiting in the tray.", startup);
            s.Controls.Add(startup);
            s.Controls.Add(Para("Dedicated server PC? Turn on Windows' automatic sign-in too, so everything comes back after a power cut. Help (F1) explains."));
            return s;
        }

        private Control WorkingPage()
        {
            _title.Text = Updating ? "Updating…" : Maintaining ? "Applying…" : "Installing…";
            _subtitle.Text = "This takes a moment — please keep this window open";
            var s = Stack();
            _status.AutoSize = true;
            _status.MaximumSize = new Size(ContentWidth, 0);
            _status.Font = new Font("Segoe UI Semibold", 10f);
            _status.Margin = new Padding(0, 0, 0, 10);
            s.Controls.Add(_status);
            _progress.Width = ContentWidth;
            _progress.Margin = new Padding(0, 0, 0, 14);
            s.Controls.Add(_progress);
            _log.Width = ContentWidth;
            _log.Height = 300;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.Text2;
            _log.IntegralHeight = false;
            s.Controls.Add(_log);
            return s;
        }

        private Control DonePage()
        {
            bool ok = _doneWarning == null;
            _title.Text = Updating ? "Updated" : Maintaining ? "Done" : "WindowsGSM is installed";
            _subtitle.Text = _doneMessage ?? "";
            var s = Stack();
            if (_doneWarning != null) { s.Controls.Add(Note(_doneWarning, Theme.Warn)); }
            if (Updating)
            {
                s.Controls.Add(Note($"WindowsGSM {_version} is in place. Your game servers kept running and have been picked up again.", Theme.Good));
                s.Controls.Add(Para("If something isn't right, Agent settings → Updates → Go back returns to the previous version."));
            }
            else if (!Maintaining)
            {
                s.Controls.Add(Note("Installed. Here's how to get going:", Theme.Good));
                s.Controls.Add(Para("1.  Open WindowsGSM and create your owner account (accounts from WindowsGSM 1.x's web dashboard already work).", false, 16));
                s.Controls.Add(Para("2.  Install a server — pick a game, give it a name. Ports are chosen for you.", false, 16));
                s.Controls.Add(Para("3.  On the server's Overview, press \"Can players reach it?\" — firewall and router in one place.", false, 16));
                s.Controls.Add(Para("4.  Use it from your phone or another PC: Agent settings → Network.", false, 16));
                s.Controls.Add(Para("The Help page inside the app answers the common questions."));
            }
            _openNow.Text = "Open WindowsGSM now";
            _openNow.Checked = !Maintaining;
            _openNow.AutoSize = true;
            _openNow.ForeColor = Theme.Text1;
            _openNow.Margin = new Padding(0, 10, 0, 0);
            if (ok || _installed != null) { s.Controls.Add(_openNow); }
            return s;
        }

        // ─────────────────────────────── Data folder ───────────────────────────────

        private string DataFolder => (_useExisting.Checked ? _existingDir.Text : _freshDir.Text).Trim();

        private void DataModeChanged()
        {
            _existingDir.Enabled = _useExisting.Checked;
            _freshDir.Enabled = _startFresh.Checked;
            _ = CheckAsync();
        }

        private void PaintSpace(string folder)
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
                double gb = d.AvailableFreeSpace / 1073741824.0;
                _space.Text = $"{d.Name.TrimEnd('\\')} has {gb:0} GB free. Most game servers need 5–50 GB each; ARK and similar, 100+ GB.";
                _space.ForeColor = gb < 30 ? Theme.Warn : Theme.Text2;
            }
            catch { _space.Text = ""; }
        }

        /// <summary>Runs the agent's dry run on the chosen folder and shows what switching means (nothing is changed).</summary>
        private async Task CheckAsync()
        {
            if (_step != Step.Data) { return; }
            int run = ++_checkRun;
            string folder = DataFolder;
            _report.Items.Clear();
            _report.Visible = false;
            _checkBlocks = true;
            _reportTitle.ForeColor = Theme.Text2;
            PaintSpace(folder);
            if (folder.Length == 0) { _reportTitle.Text = "Choose a folder."; _next.Enabled = false; return; }
            if (_startFresh.Checked && (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                _reportTitle.Text = Directory.Exists(folder) ? "✔  An empty folder — ready." : "✔  The folder will be created.";
                _reportTitle.ForeColor = Theme.Good;
                _checkBlocks = false;
                _next.Enabled = true;
                return;
            }
            if (_useExisting.Checked && !Directory.Exists(folder)) { _reportTitle.Text = "That folder doesn't exist. Pick the folder that has servers, configs and backups in it."; _reportTitle.ForeColor = Theme.Warn; _next.Enabled = false; return; }

            _reportTitle.Text = "Checking the folder (nothing is changed)…";
            _next.Enabled = false;
            await Task.Delay(400); // let typing settle
            if (run != _checkRun) { return; }
            string agent = Path.Combine(_packageRoot, "versions", _version, "wgsm-agent.exe");
            string json = await Task.Run(() => RunCheck(agent, folder));
            if (run != _checkRun || _step != Step.Data) { return; }
            try
            {
                var report = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                bool ok = report.ContainsKey("canAdopt") && report["canAdopt"] is bool b && b;
                _checkBlocks = !ok;
                _reportTitle.Text = ok ? "Ready — here's what happens with this folder:" : "Not ready yet — sort out the red items (the list updates when you change the folder):";
                _reportTitle.ForeColor = ok ? Theme.Good : Theme.Bad;
                foreach (Dictionary<string, object> f in (ArrayList)report["findings"])
                {
                    string status = f["status"] as string;
                    var item = new ListViewItem(status == "block" ? "✖ Fix" : status == "warn" ? "⚠ Note" : status == "ok" ? "✔" : "ℹ");
                    string text = (f["title"] as string) + (f["detail"] is string d && d.Length > 0 ? " — " + d : "");
                    item.SubItems.Add(text);
                    item.ToolTipText = text;
                    item.ForeColor = status == "block" ? Theme.Bad : status == "warn" ? Theme.Warn : status == "ok" ? Theme.Good : Theme.Text2;
                    _report.Items.Add(item);
                }
                _report.Visible = _report.Items.Count > 0;
            }
            catch
            {
                _checkBlocks = false;
                _reportTitle.Text = "Couldn't check the folder — you can still go on; WindowsGSM tells you if anything's wrong when it starts.";
            }
            _next.Enabled = !_checkBlocks;
        }

        private static string RunCheck(string agent, string folder)
        {
            if (!File.Exists(agent)) { return "{}"; }
            var psi = new ProcessStartInfo(agent, Install.Quote(new[] { "--check-data", folder, "--json" }))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(120000);
                int start = output.IndexOf('{');
                return start >= 0 ? output.Substring(start) : "{}";
            }
        }

        private bool ValidateData()
        {
            if (_checkBlocks) { return false; }
            string problem = Install.CanWrite(DataFolder);
            if (problem != null) { MessageBox.Show(this, problem, "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            return true;
        }

        // ─────────────────────────────── Install folder ───────────────────────────────

        private string ResolvedRoot()
        {
            try { return _installDir.Text.Trim().Length == 0 ? null : Install.ResolveRoot(_installDir.Text); } catch { return null; }
        }

        private void PaintInstallNote()
        {
            if (_step != Step.Options || Maintaining) { return; }
            string root = ResolvedRoot();
            string typed = _installDir.Text.Trim().TrimEnd('\\');
            _installNote.ForeColor = Theme.Text2;
            if (root == null) { _installNote.Text = "Choose a folder."; return; }
            if (!string.Equals(root, Path.GetFullPath(typed).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _installNote.Text = $"That folder has other files in it, so WindowsGSM goes in its own subfolder: {root}";
            }
            else if (Install.Load(root) is Install there)
            {
                bool otherData = there.Data != null && DataFolder.Length > 0
                    && !string.Equals(Path.GetFullPath(there.Data).TrimEnd('\\'), Path.GetFullPath(DataFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                _installNote.Text = $"WindowsGSM {there.Current} is installed there — it's updated in place"
                    + (otherData ? $", and switched from {there.Data} to the game servers folder you picked." : ".");
                _installNote.ForeColor = otherData ? Theme.Warn : Theme.Text2;
            }
            else { _installNote.Text = "Needs about 250 MB."; }
        }

        private bool ValidateInstallDir()
        {
            string root = ResolvedRoot();
            if (root == null) { MessageBox.Show(this, "Choose where to install the app.", "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }
            if (Install.Overlaps(root, DataFolder))
            {
                MessageBox.Show(this, "Keep the app and your game servers in separate folders (neither inside the other), so updating or uninstalling the app can never touch your servers.",
                    "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            string problem = Install.CanWrite(root);
            if (problem != null) { MessageBox.Show(this, problem, "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            return true;
        }

        // ─────────────────────────────── Doing it ───────────────────────────────

        private void Say(string status, string detail = null)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Say(status, detail))); return; }
            if (status != null) { _status.Text = status; }
            _log.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + (detail ?? status));
            _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
        }

        private void Progress(int pct)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Progress(pct))); return; }
            _progress.Value = Math.Max(0, Math.Min(100, pct));
        }

        private async Task RunAsync()
        {
            Go(Step.Working);
            try
            {
                if (Maintaining) { await Task.Run(() => ApplyOptions(_existing, maintaining: true)); _installed = _existing; _doneMessage = "Your changes are applied."; }
                else if (Updating) { await Task.Run(() => DoUpdate()); }
                else { await Task.Run(() => DoInstall()); }
                Go(Step.Done);
            }
            catch (Exception ex)
            {
                Say("Setup couldn't finish.", "✖ " + ex.Message);
                _step = Step.Done; // allow closing
                _failed = true;
                _cancel.Visible = true;
                _cancel.Text = "Close";
                _next.Visible = true;
                _next.Text = "Try again";
                _next.Enabled = true;
                MessageBox.Show(this, "Setup couldn't finish:\n\n" + ex.Message + "\n\nNothing you had is lost — your game servers folder is untouched. Fix the problem (e.g. close a program using the folder) and try again.",
                    "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Update in place: copy the new version next to the current one, restart WindowsGSM on it. Game servers keep running.</summary>
        private void DoUpdate()
        {
            var install = _existing;
            string root = install.Root;
            bool sameVersion = install.Current == _version;
            bool agentWasRunning = install.Data != null && AgentControl.IsAgentRunning(install.Data);
            bool appWasRunning = RunningFrom(root).Any(p => SafeName(p) == "WindowsGSM");

            // A repair copies into the folder the running version uses: everything from it has to stop first.
            if (sameVersion) { StopWindowsGSM(install, agentWasRunning); }

            Say($"Copying WindowsGSM {_version}…");
            CopyApp(root);

            if (!sameVersion)
            {
                StopWindowsGSM(install, agentWasRunning);
                Say("Switching to the new version", $"{install.Current} → {_version} ({install.Current} stays installed so you can go back)");
                install.SwitchTo(_version);
            }
            Shell.RegisterUninstall(install, _version);
            if (File.Exists(Shell.StartMenuShortcut)) { Shell.CreateShortcut(Shell.StartMenuShortcut, Path.Combine(root, "WindowsGSM.exe"), "Game server control"); }
            Shell.EnsureStartMenuTools(install);

            if (agentWasRunning)
            {
                Say("Starting WindowsGSM again…", "Starting the agent — it picks up your running game servers");
                install.Start(install.AgentExe, new string[0], hidden: true);
                WaitForAgent(install);
            }
            _installed = install;
            _openNow.Checked = appWasRunning;
            _doneMessage = $"Now running {_version}";
            Progress(100);
        }

        /// <summary>Stops the app and agent of an install the normal way (game servers keep running).</summary>
        private void StopWindowsGSM(Install install, bool agentRunning)
        {
            if (agentRunning)
            {
                Say("Stopping WindowsGSM (your game servers keep running)…", "Asking the agent to stop — game servers aren't touched");
                string problem = AgentControl.StopAgent(install);
                if (problem != null) { Say(null, "⚠ " + problem + " — ending it instead"); }
            }
            foreach (var p in RunningFrom(install.Root))
            {
                try { Say(null, $"Closing {SafeName(p)} (PID {p.Id})"); p.Kill(); p.WaitForExit(10000); } catch { }
                finally { p.Dispose(); }
            }
            System.Threading.Thread.Sleep(800); // let file handles close
        }

        private void WaitForAgent(Install install)
        {
            for (int i = 0; i < 60; i++)
            {
                if (AgentControl.IsAgentRunning(install.Data)) { Say(null, "✔ The agent is running"); return; }
                System.Threading.Thread.Sleep(1000);
            }
            _doneWarning = "The new version is installed, but the agent didn't answer within a minute. Open WindowsGSM — it starts the agent — or check the log in " + Path.Combine(install.Data ?? "", "logs") + ".";
        }

        /// <summary>A fresh install (or a separate copy).</summary>
        private void DoInstall()
        {
            string root = null, data = null;
            Invoke((Action)(() => { root = ResolvedRoot(); data = Path.GetFullPath(DataFolder); }));

            // Installing into a folder that already has a copy: that copy is updated in place.
            var there = Install.Load(root);
            bool agentWasRunning = there != null && there.Data != null && AgentControl.IsAgentRunning(there.Data);
            if (there != null && there.Current == _version) { StopWindowsGSM(there, agentWasRunning); }

            Say($"Copying WindowsGSM {_version}…", $"Installing to {root}");
            CopyApp(root);
            if (there != null && there.Current != _version) { StopWindowsGSM(there, agentWasRunning); }

            Say("Setting up your game servers folder…", data);
            Directory.CreateDirectory(data);
            var install = there ?? Install.Create(root, _version, data);
            install.Data = data;
            if (install.Current != _version && Directory.Exists(install.VersionDir(install.Current))) { install.SwitchTo(_version); } else { install.Current = _version; install.Save(); }
            ApplyOptions(install, maintaining: false);
            Shell.RegisterUninstall(install, _version);
            _installed = install;
            _doneMessage = $"Version {_version} · game servers in {data}";
            Progress(100);
        }

        private void ApplyOptions(Install install, bool maintaining)
        {
            bool startMenu = false, desktop = false, agentAtSignIn = false, tray = false;
            Invoke((Action)(() => { startMenu = _startMenu.Checked; desktop = _desktop.Checked; agentAtSignIn = _agentAtSignIn.Checked; tray = _trayAtSignIn.Checked; }));
            string launcher = Path.Combine(install.Root, "WindowsGSM.exe");

            Say("Shortcuts…", startMenu || desktop ? "Creating shortcuts" : "No shortcuts");
            if (startMenu) { Shell.CreateShortcut(Shell.StartMenuShortcut, launcher, "Game server control"); Shell.EnsureStartMenuTools(install); }
            else { TryDelete(Shell.StartMenuShortcut); try { if (Directory.Exists(Shell.StartMenuFolder)) { Directory.Delete(Shell.StartMenuFolder, true); } } catch { } }
            if (desktop) { Shell.CreateShortcut(Shell.DesktopShortcut, launcher, "Game server control"); } else { TryDelete(Shell.DesktopShortcut); }

            Say("Start-up…", agentAtSignIn ? "The agent starts when you sign in" : "The agent doesn't start on its own");
            string problem = Shell.SetAgentAtSignIn(install, agentAtSignIn);
            if (problem != null) { _doneWarning = "Couldn't set the agent to start at sign-in: " + problem + " You can turn it on later in Agent settings."; Say(null, "⚠ " + problem); }
            Shell.SetStartWithWindows(tray, launcher);
            if (maintaining) { Shell.RegisterUninstall(install, install.Current); }
        }

        private static void TryDelete(string file) { try { if (File.Exists(file)) { File.Delete(file); } } catch { } }

        /// <summary>Copies the launcher and versions\&lt;version&gt; from the download into the install folder.</summary>
        private void CopyApp(string root)
        {
            Directory.CreateDirectory(root);
            if (string.Equals(_packageRoot, Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { Progress(100); return; }
            string from = Path.Combine(_packageRoot, "versions", _version), to = Path.Combine(root, "versions", _version);
            var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string target = Path.Combine(to, files[i].Substring(from.Length).TrimStart('\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(files[i], target, true);
                Install.Unblock(target);
                if (i % 20 == 0) { Progress(5 + i * 85 / files.Length); }
            }
            Say(null, $"Copied {files.Length} files");
            string launcher = Path.Combine(root, "WindowsGSM.exe");
            if (File.Exists(launcher))
            {
                // It may be running (the sign-in task waits in it): move it aside, which Windows allows.
                string old = launcher + ".old";
                try { if (File.Exists(old)) { File.Delete(old); } } catch { }
                try { File.Move(launcher, old); } catch { }
            }
            File.Copy(Application.ExecutablePath, launcher, true);
            Install.Unblock(launcher);
            string config = Application.ExecutablePath + ".config";
            if (File.Exists(config)) { File.Copy(config, launcher + ".config", true); }
            Progress(92);
        }

        private void Finish()
        {
            if (_openNow.Checked && _installed != null)
            {
                string launcher = Path.Combine(_installed.Root, "WindowsGSM.exe");
                try { Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = _installed.Root }); } catch { }
            }
            Close();
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        private static string SafeName(Process p) { try { return p.ProcessName; } catch { return "?"; } }

        /// <summary>The agent and app processes running from an install folder (other than this setup).</summary>
        private static List<Process> RunningFrom(string root)
        {
            var list = new List<Process>();
            string versions = Path.Combine(root, "versions") + "\\";
            int self = Process.GetCurrentProcess().Id;
            foreach (string name in new[] { "wgsm-agent", "WindowsGSM" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id != self && p.MainModule.FileName.StartsWith(versions, StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; }
                    }
                    catch { }
                    p.Dispose();
                }
            }
            return list;
        }

        /// <summary>Where WindowsGSM 1.x usually lives, if it's running or in a common place (never the installed copy's own data).</summary>
        private static string GuessLegacyFolder(string notThis)
        {
            bool Usable(string dir) => dir != null && Directory.Exists(Path.Combine(dir, "servers"))
                && (notThis == null || !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(notThis).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            foreach (var p in Process.GetProcessesByName("WindowsGSM"))
            {
                try
                {
                    string dir = Path.GetDirectoryName(p.MainModule.FileName);
                    if (Usable(dir)) { return dir; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (string d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Select(x => x.RootDirectory.FullName))
            {
                foreach (string name in new[] { "WindowsGSM", Path.Combine("Games", "WindowsGSM"), Path.Combine("Servers", "WindowsGSM") })
                {
                    string c = Path.Combine(d, name);
                    if (Usable(c) && Directory.Exists(Path.Combine(c, "configs"))) { return c; }
                }
            }
            return null;
        }
    }
}
