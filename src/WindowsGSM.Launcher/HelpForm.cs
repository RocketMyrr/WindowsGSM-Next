using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WindowsGSM.Launcher
{
    /// <summary>Setup's help: short answers, opened at the topic that fits the step you're on.</summary>
    internal sealed class HelpForm : Form
    {
        /// <summary>(id, title, paragraphs) — a paragraph starting with "• " is a list item.</summary>
        public static readonly Tuple<string, string, string[]>[] Topics =
        {
            T("what", "What gets installed where", new[]
            {
                "WindowsGSM keeps two things apart:",
                "• The app — WindowsGSM itself. It goes in your user profile (AppData\\Local\\Programs\\WindowsGSM) and needs no administrator rights. Updates replace only this.",
                "• Your game servers folder — servers, backups, settings and accounts. It's yours: updating or uninstalling WindowsGSM never deletes it.",
                "Keep them in different folders. Game servers can be big, so put that folder on a drive with plenty of space.",
            }),
            T("remote", "Controlling another PC", new[]
            {
                "\"Control game servers on another PC\" installs just the app. It connects to a PC that runs WindowsGSM — or to your hub, to see every machine in one place — and shows its panel in a window here. Nothing runs in the background on this PC.",
                "• On the PC with the servers: Agent settings → Network → turn on \"Reachable from other computers\", restart its agent, and allow its port (8971) through that PC's firewall.",
                "• In the app here: enter that PC's address, e.g. 192.168.1.20, and sign in with your account from it. Add more PCs and switch between them from the tray icon → PC.",
                "• Over the internet: turn on HTTPS on that PC first (Agent settings → HTTPS), and forward its port on the router. With a self-signed certificate the app shows its fingerprint once — check it matches the one in that PC's Agent settings → HTTPS.",
                "A full install (\"Run game servers on this PC\") can control other PCs too, from the same tray menu. To start running servers on an app-only PC later, run setup again: Start menu → WindowsGSM → WindowsGSM setup.",
            }),
            T("update", "Updating", new[]
            {
                "Your game servers keep running while WindowsGSM updates. It stops only itself (the agent and the app), puts the new version in place, and starts again — then picks your running servers back up.",
                "• From the app: Agent settings → Updates → Install. It checks for new versions on its own and tells you when one is out.",
                "• From a download: run the new WindowsGSM.exe — setup sees your installed copy and offers to update it. Nothing to choose.",
                "The version before stays installed: Agent settings → Updates → Go back, if an update misbehaves.",
                "One thing to know: a server whose console is shown in the panel keeps running, but its new output can't be shown again until that server's next restart (RCON still works).",
            }),
            T("legacy", "Coming from WindowsGSM 1.x", new[]
            {
                "Point setup at your current WindowsGSM folder (the one with servers, configs and backups). It checks the folder first and changes nothing until you install.",
                "• Close the old WindowsGSM first — two managers must never run the same servers. Setup tells you if it's still open.",
                "• Your servers, their settings, backups, schedules and web-dashboard accounts carry over.",
                "• Want to try it without touching the old setup? Copy the folder and point setup at the copy.",
            }),
            T("startup", "Starting with Windows", new[]
            {
                "• Start the agent when I sign in: the agent runs your servers and the web panel. With this on, servers with auto-start come back after a reboot, and the agent is restarted if it ever stops.",
                "• Show the tray icon when I sign in: the WindowsGSM window and notifications, tucked into the tray.",
                "On a dedicated server PC, also turn on Windows' automatic sign-in, so everything comes back after a power cut without anyone signing in.",
                "Both can be changed later in Agent settings, or by running this setup again (Start menu → WindowsGSM → WindowsGSM setup).",
            }),
            T("smartscreen", "\"Windows protected your PC\" / antivirus", new[]
            {
                "WindowsGSM isn't signed with a paid certificate, so Windows SmartScreen may warn the first time you run the download. Click More info → Run anyway.",
                "Setup removes the download mark from the files it installs, so the installed app doesn't warn again.",
                "If your antivirus quarantines a file, restore it and add the WindowsGSM folders as an exception — game server managers start and watch lots of programs, which some scanners dislike.",
            }),
            T("zip", "\"This copy is incomplete\"", new[]
            {
                "WindowsGSM.exe needs the versions folder next to it. Opening it straight from inside the zip doesn't work.",
                "Right-click the zip → Extract All, then run WindowsGSM.exe from the extracted folder.",
            }),
            T("firewall", "After installing: ports and firewall", new[]
            {
                "Open a server's Overview and press \"Can players reach it?\" — it checks Windows Firewall, your router and Steam's server list, and says what to fix.",
                "\"Allow through firewall\" adds the Windows Firewall rule with one prompt. \"Forward ports automatically\" asks your router (UPnP) to forward the game's ports.",
            }),
            T("signin", "First sign-in and lost passwords", new[]
            {
                "The first time WindowsGSM opens, you create the owner account. Accounts from WindowsGSM 1.x's web dashboard carry over with the same passwords.",
                "Locked out? In the WindowsGSM folder (AppData\\Local\\Programs\\WindowsGSM), run: WindowsGSM.exe --reset-password <username>",
            }),
            T("uninstall", "Uninstalling", new[]
            {
                "Windows Settings → Apps → WindowsGSM → Uninstall, or Start menu → WindowsGSM → Uninstall WindowsGSM.",
                "It removes the app, its shortcuts and start-up entries — only the files setup put there. Your game servers folder stays, so reinstalling picks everything up again.",
                "Game servers that are running keep running; stop them first if you're removing them for good.",
            }),
            T("more", "More help", new[]
            {
                "Inside WindowsGSM: the Help page in the sidebar answers the common questions (ports, updates, roll back, Steam Guard, Minecraft, ARK…).",
                "The install guide (docs\\INSTALL.md in the project) covers everything in detail.",
            }),
        };

        private static Tuple<string, string, string[]> T(string id, string title, string[] text) => Tuple.Create(id, title, text);

        private readonly ListBox _list = new ListBox();
        private readonly FlowLayoutPanel _body = new FlowLayoutPanel();

        public HelpForm(string topic)
        {
            Text = "WindowsGSM setup — help";
            Icon = Program.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text1;
            Font = new Font("Segoe UI", 9.75f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 560);
            Chrome.Frameless(this); // matches setup: our own title bar
            AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { Close(); } };

            _list.Dock = DockStyle.Left;
            _list.Width = 290;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Surface;
            _list.ForeColor = Theme.Text1;
            _list.IntegralHeight = false;
            _list.ItemHeight = 30;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) { return; }
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var bg = new SolidBrush(selected ? Theme.AccentSoft : Theme.Surface)) { e.Graphics.FillRectangle(bg, e.Bounds); }
                TextRenderer.DrawText(e.Graphics, Topics[e.Index].Item2, Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height),
                    selected ? Color.White : Theme.Text1, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            foreach (var t in Topics) { _list.Items.Add(t.Item2); }
            _list.SelectedIndexChanged += (s, e) => Show(_list.SelectedIndex);

            _body.Dock = DockStyle.Fill;
            _body.FlowDirection = FlowDirection.TopDown;
            _body.WrapContents = false;
            _body.AutoScroll = true;
            _body.Padding = new Padding(22, 16, 18, 16);
            _body.Resize += (s, e) => Show(_list.SelectedIndex);

            var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Surface };
            var title = new Label { Text = "Setup help", AutoSize = true, ForeColor = Theme.Text1, Font = new Font("Segoe UI Semibold", 10f), Location = new Point(16, 11) };
            bar.Controls.Add(title);
            Chrome.DragBy(this, bar, title);
            Chrome.Corner(bar, Chrome.Button(this, "close", Theme.Surface));
            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
            _body.BackColor = Theme.Bg;

            Controls.Add(_body);
            Controls.Add(_list);
            Controls.Add(line);
            Controls.Add(bar);
            int index = Array.FindIndex(Topics, t => t.Item1 == topic);
            _list.SelectedIndex = index >= 0 ? index : 0;
        }

        protected override CreateParams CreateParams => Chrome.Params(base.CreateParams, minimise: false);

        private void Show(int index)
        {
            if (index < 0) { return; }
            var t = Topics[index];
            int width = Math.Max(300, _body.ClientSize.Width - _body.Padding.Horizontal - 24);
            _body.SuspendLayout();
            _body.Controls.Clear();
            _body.Controls.Add(new Label { Text = t.Item2, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Text1, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 12) });
            foreach (string p in t.Item3)
            {
                bool bullet = p.StartsWith("• ");
                _body.Controls.Add(new Label
                {
                    Text = p, AutoSize = true, MaximumSize = new Size(bullet ? width - 14 : width, 0), ForeColor = bullet ? Theme.Text1 : Theme.Text2,
                    Margin = new Padding(bullet ? 14 : 0, 0, 0, 10),
                });
            }
            _body.ResumeLayout();
        }

        /// <summary>Opens help at <paramref name="topic"/> over <paramref name="owner"/>.</summary>
        public static void Open(IWin32Window owner, string topic)
        {
            using (var f = new HelpForm(topic)) { f.ShowDialog(owner); }
        }
    }

    /// <summary>The panel's colours, for setup's windows.</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x0c, 0x0f, 0x15), Surface = Color.FromArgb(0x13, 0x17, 0x20), Raised = Color.FromArgb(0x18, 0x1c, 0x26),
            Line = Color.FromArgb(0x2b, 0x31, 0x42), Text1 = Color.FromArgb(0xe8, 0xeb, 0xf2), Text2 = Color.FromArgb(0xa8, 0xb0, 0xc0),
            Accent = Color.FromArgb(0x3b, 0x82, 0xf6), AccentSoft = Color.FromArgb(0x1d, 0x3a, 0x6b),
            Good = Color.FromArgb(0x5a, 0xd6, 0x90), Warn = Color.FromArgb(0xf5, 0xa5, 0x24), Bad = Color.FromArgb(0xf0, 0x50, 0x6e);
    }
}
