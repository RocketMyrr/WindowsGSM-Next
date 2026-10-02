#nullable enable
using System;
using System.Diagnostics;
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
