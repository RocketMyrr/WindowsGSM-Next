#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsGSM.Engine.Services
{
    /// <summary>Win32 handling of a game server's own console window (show/hide/title), ported from legacy.</summary>
    internal static class ConsoleWindows
    {
        private enum ShowStyle : uint { Hide = 0, ShowNormal = 1, Show = 5, Minimize = 6, ShowMinNoActivate = 7 }

        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, ShowStyle nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
        [DllImport("user32.dll")] private static extern bool EnableMenuItem(IntPtr menu, uint item, uint flags);
        private const uint SC_CLOSE = 0xF060, MF_BYCOMMAND = 0, MF_GRAYED = 1;

        public static bool Exists(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);
        public static bool IsVisible(IntPtr hWnd) => Exists(hWnd) && IsWindowVisible(hWnd);

        /// <summary>
        /// Sets up a game's own console window (from <see cref="Functions.ConsoleHost"/>): it was started hidden, and
        /// Windows replaces a window's first ShowWindow with the style it was started with — so that one is spent first.
        /// </summary>
        public static void Prime(IntPtr hWnd, bool visible)
        {
            if (hWnd == IntPtr.Zero) { return; }
            ShowWindow(hWnd, ShowStyle.Hide);
            SetVisible(hWnd, visible);
            ProtectClose(hWnd);
        }

        /// <summary>
        /// Greys out the window's close button: closing a console window ends the game at once, without saving.
        /// The panel's Stop saves and stops it properly (and so does typing the game's own stop command in the window).
        /// </summary>
        public static void ProtectClose(IntPtr hWnd)
        {
            IntPtr menu = hWnd == IntPtr.Zero ? IntPtr.Zero : GetSystemMenu(hWnd, false);
            if (menu != IntPtr.Zero) { EnableMenuItem(menu, SC_CLOSE, MF_BYCOMMAND | MF_GRAYED); }
        }

        // Process.MainWindowHandle, as the runtime keeps it.
        private static readonly FieldInfo? MainWindowField = typeof(Process).GetField("_mainWindowHandle", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? HaveMainWindowField = typeof(Process).GetField("_haveMainWindow", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, int> Games = new();

        /// <summary>
        /// Records that <paramref name="hWnd"/> is <paramref name="p"/>'s own console window (see <see cref="GameOf"/>).
        /// Only classic console windows: a game's graphical window (found the legacy way) isn't a console to type into.
        /// </summary>
        public static void Register(IntPtr hWnd, Process p)
        {
            if (hWnd == IntPtr.Zero || !Functions.ConsoleHost.IsConsoleWindow(hWnd)) { return; }
            try { Games[hWnd] = p.Id; } catch { /* gone */ }
        }

        /// <summary>The game whose own console window this is, while it runs (null for any other window).</summary>
        public static int? GameOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !Games.TryGetValue(hWnd, out int pid)) { return null; }
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && Exists(hWnd)) { return pid; }
            }
            catch { /* ended */ }
            Games.TryRemove(hWnd, out _);
            return null;
        }

        /// <summary>True when <see cref="Adopt"/> can work on this runtime (a test keeps an eye on it).</summary>
        internal static bool CanAdopt => MainWindowField?.FieldType == typeof(IntPtr) && HaveMainWindowField?.FieldType == typeof(bool);

        /// <summary>
        /// Makes <paramref name="p"/>.MainWindowHandle return the game's console window. Windows reports a console
        /// window as belonging to the process that made the console — the placeholder WindowsGSM ended (ConsoleHost) —
        /// so the game's own MainWindowHandle is Zero, and plugins type their stop command ("quit", "stop") into it.
        /// </summary>
        public static void Adopt(Process p, IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !CanAdopt) { return; }
            try
            {
                MainWindowField!.SetValue(p, hWnd);
                HaveMainWindowField!.SetValue(p, true);
            }
            catch { /* plugins fall back to Ctrl+C / kill */ }
        }

        public static void SetVisible(IntPtr hWnd, bool visible)
        {
            if (hWnd != IntPtr.Zero) { ShowWindow(hWnd, visible ? ShowStyle.ShowNormal : ShowStyle.Hide); }
        }

        public static void SetTitle(IntPtr hWnd, string title)
        {
            if (hWnd != IntPtr.Zero) { SetWindowText(hWnd, title); }
        }

        /// <summary>
        /// Waits for the game's console window to appear, minimises then hides/shows it, and returns its handle
        /// (Zero if it has none). Runs on a background thread right after start.
        ///
        /// NEXT: legacy polled p.MainWindowHandle without calling Refresh() — the value is cached after the
        /// first read, so a window that appeared a moment later was never seen and the loop spun (a thread
        /// per server) until the server exited. This refreshes each poll and gives up after a minute.
        /// </summary>
        public static IntPtr Settle(Process p, bool showConsole)
        {
            try
            {
                if (p.StartInfo.CreateNoWindow) { return IntPtr.Zero; }

                var deadline = DateTime.UtcNow.AddMinutes(1);
                IntPtr hWnd = IntPtr.Zero;
                while (!p.HasExited && DateTime.UtcNow < deadline)
                {
                    p.Refresh();
                    hWnd = p.MainWindowHandle;
                    if (hWnd != IntPtr.Zero && ShowWindow(hWnd, ShowStyle.Minimize)) { break; }
                    Thread.Sleep(500);
                }
                if (hWnd == IntPtr.Zero || p.HasExited) { return IntPtr.Zero; }

                try { p.WaitForInputIdle(5000); } catch { /* console apps have no message loop */ }
                ShowWindow(hWnd, ShowStyle.Hide);
                Thread.Sleep(500);
                SetVisible(hWnd, showConsole);
                return hWnd;
            }
            catch
            {
                return IntPtr.Zero; // no window to manage
            }
        }
    }
}
