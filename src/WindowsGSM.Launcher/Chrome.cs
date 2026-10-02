using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsGSM.Launcher
{
    /// <summary>
    /// Setup's own window frame: no Windows title bar — the dark header is the title bar (drag it to move the
    /// window), with minimise / close buttons drawn to match. Keeps a drop shadow, Windows 11's rounded corners and
    /// a 1-pixel border, and the window still minimises from the taskbar and answers Alt+Space.
    /// </summary>
    internal static class Chrome
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        public const int WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000, CS_DROPSHADOW = 0x20000;

        /// <summary>Call from the form's constructor: no frame, a 1px border in the theme's line colour.</summary>
        public static void Frameless(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.BackColor = Theme.Line;            // shows as the border…
            form.Padding = new Padding(1);          // …around everything docked inside
            form.HandleCreated += (s, e) =>
            {
                int round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11; ignored before)
                try { DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int)); } catch { }
            };
        }

        /// <summary>CreateParams for a frameless window that still behaves like one (taskbar minimise, system menu, shadow).</summary>
        public static CreateParams Params(CreateParams cp, bool minimise)
        {
            cp.Style |= WS_SYSMENU | (minimise ? WS_MINIMIZEBOX : 0);
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }

        /// <summary>Dragging any of these moves the window (like a title bar).</summary>
        public static void DragBy(Form form, params Control[] handles)
        {
            foreach (var c in handles)
            {
                c.MouseDown += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) { return; }
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                };
            }
        }

        /// <summary>A title-bar button: "min" or "close". Close turns red on hover, like Windows.</summary>
        public static Control Button(Form form, string kind, Color background)
        {
            var b = new Label
            {
                Size = new Size(46, 32), TextAlign = ContentAlignment.MiddleCenter, BackColor = background, ForeColor = Theme.Text2,
                Font = new Font("Segoe MDL2 Assets", 9f), Cursor = Cursors.Default, AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = kind == "close" ? "Close" : "Minimise",
                Text = kind == "close" ? "" : "", // Windows' own close / minimise glyphs
            };
            Color hover = kind == "close" ? Color.FromArgb(0xc4, 0x2b, 0x1c) : Theme.Raised;
            b.MouseEnter += (s, e) => { b.BackColor = hover; b.ForeColor = kind == "close" ? Color.White : Theme.Text1; };
            b.MouseLeave += (s, e) => { b.BackColor = background; b.ForeColor = Theme.Text2; };
            b.Click += (s, e) => { if (kind == "close") { form.Close(); } else { form.WindowState = FormWindowState.Minimized; } };
            return b;
        }

        /// <summary>Places the buttons in the top-right corner of <paramref name="bar"/> (and keeps them there).</summary>
        public static void Corner(Control bar, params Control[] buttons)
        {
            foreach (var b in buttons) { bar.Controls.Add(b); b.BringToFront(); }
            void Place()
            {
                int x = bar.ClientSize.Width;
                foreach (var b in buttons) { x -= b.Width; b.Location = new Point(x, 0); }
            }
            bar.Resize += (s, e) => Place();
            Place();
        }
    }
}
