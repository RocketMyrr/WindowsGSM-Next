#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: gives each game server a console window of its own that WindowsGSM can show and hide.
    ///
    /// The agent runs without a visible console, and a game started from it inherited that invisible console — so
    /// "Show the console window" had no window to show. Letting Windows make a new console doesn't help either: on
    /// Windows 11 it goes to Windows Terminal (when that's the default terminal), whose tabs can't be shown, hidden
    /// or titled per server. So, for each start, the agent starts a hidden classic console (conhost) with a
    /// placeholder in it, joins that console, starts the game (which inherits it), and leaves again. The game keeps a
    /// classic console window of its own; the placeholder is ended.
    ///
    /// Between starts the agent sits in a hidden "home" console of its own, so other programs it starts without a
    /// window of their own (tools, wrappers) still don't pop up anywhere.
    ///
    /// Placeholders belong to a job that Windows ends with the agent, so a crashed or stopped agent never leaves
    /// them behind (the games are not in it: they keep running).
    ///
    /// Only the agent turns this on (<see cref="Enable"/>); a process with a visible console of its own (someone
    /// running the agent in a terminal) keeps the old behaviour, sharing that console.
    /// </summary>
    internal static class ConsoleHost
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobLimits info, int length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int size);

        /// <summary>
        /// Everything that joins or leaves a console goes through here: a process has one console at a time. A
        /// plugin's Start runs inside it, so a stuck one could hold it — others give up after a while rather than hang.
        /// </summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(60);
        private static bool _enabled;
        private static Process? _home; // the placeholder holding the agent's hidden home console
        private static IntPtr _job;

        public static bool Active => _enabled;

        /// <summary>Turns the scheme on when this process has no visible console. Safe to call more than once.</summary>
        public static void Enable()
        {
            if (_enabled || !OperatingSystem.IsWindows()) { return; }
            if (Watched(GetConsoleWindow())) { return; } // someone is watching the agent's console: leave it be
            // Writes to a console the agent has left would fail; nobody can see this output anyway (it goes to the log).
            if (!Console.IsOutputRedirected) { Console.SetOut(System.IO.TextWriter.Null); }
            if (!Console.IsErrorRedirected) { Console.SetError(System.IO.TextWriter.Null); }
            _job = PlaceholderJob();
            _enabled = true;
            Gate.Wait();
            try { GoHome(); }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Starts a process with <paramref name="start"/> inside a new hidden console of its own, and returns that
        /// console's window (Zero if this isn't active or it didn't work — the process is then started as before).
        /// </summary>
        public static async Task<(T result, IntPtr window)> StartInOwnConsoleAsync<T>(Func<Task<T>> start)
        {
            if (!_enabled || !await Gate.WaitAsync(GateWait).ConfigureAwait(false)) { return (await start().ConfigureAwait(false), IntPtr.Zero); }

            Process? waiter = null;
            bool joined = false;
            try
            {
                waiter = NewHiddenConsole() ?? NewHiddenConsole(); // once more before giving up
                if (waiter != null)
                {
                    FreeConsole();
                    joined = AttachConsole((uint)waiter.Id);
                    if (!joined) { GoHome(); }
                }
                IntPtr window = joined ? GetConsoleWindow() : IntPtr.Zero;
                return (await start().ConfigureAwait(false), window);
            }
            finally
            {
                if (joined) { FreeConsole(); GoHome(); }
                // The game (if it started) now holds the console; if nothing did, it closes once the placeholder ends.
                End(waiter);
                Gate.Release();
            }
        }

        /// <summary>The game's console window, once its placeholder has gone: Zero if the game didn't keep it.</summary>
        public static IntPtr Confirm(IntPtr window, Process game)
        {
            if (window == IntPtr.Zero) { return IntPtr.Zero; }
            try
            {
                // A plugin that asked for no window, or let Windows start it, has a console of its own elsewhere.
                if (game.StartInfo.CreateNoWindow || game.StartInfo.UseShellExecute || game.StartInfo.RedirectStandardOutput) { return IntPtr.Zero; }
            }
            catch { /* not started by us */ }
            // A game with a window of its own (not a console program) never joins the console; it closes a moment
            // after the placeholder ends — so ask the game's console rather than trust the window still being there.
            // A game that has only just started may not be connected to its console yet: give it a moment.
            for (int attempt = 0; attempt < 15; attempt++)
            {
                try { if (game.HasExited) { return IntPtr.Zero; } } catch { return IntPtr.Zero; }
                IntPtr theirs = WindowOf(game.Id);
                if (theirs == window) { return window; }
                if (theirs != IntPtr.Zero) { return IntPtr.Zero; } // in some other console
                Thread.Sleep(200);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// The classic console window <paramref name="pid"/> is in (Zero if none, or it's the agent's own). Asks the
        /// console itself, so it's right even when a cached handle is stale — used to find a game's window again after
        /// the agent restarts.
        /// </summary>
        public static IntPtr WindowOf(int pid)
        {
            IntPtr window = IntPtr.Zero;
            Visit(pid, own => { window = own; return true; });
            return IsConsoleWindow(window) ? window : IntPtr.Zero;
        }

        /// <summary>
        /// Runs <paramref name="action"/> while joined to <paramref name="pid"/>'s console (for sending it Ctrl+C).
        /// False if it couldn't join, or the console is the agent's own — Ctrl+C there would reach everything in it.
        /// </summary>
        public static bool InConsoleOf(int pid, Func<bool> action) => Visit(pid, _ => action());

        /// <summary>Joins <paramref name="pid"/>'s console, runs <paramref name="action"/> with its window, and comes home.</summary>
        private static bool Visit(int pid, Func<IntPtr, bool> action)
        {
            if (!_enabled || !Gate.Wait(GateWait)) { return false; }
            try
            {
                IntPtr home = GetConsoleWindow();
                FreeConsole();
                bool ok = false;
                // A Ctrl+C in that console (ours, or someone pressing it in the game's window) reaches every process
                // in it, the agent included while it's there: ignore it for now. Only for now — games inherit the
                // setting, and one started meanwhile would never stop on Ctrl+C.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                try
                {
                    if (AttachConsole((uint)pid))
                    {
                        IntPtr theirs = GetConsoleWindow();
                        if (theirs != home || home == IntPtr.Zero) { ok = action(theirs); }
                        FreeConsole();
                    }
                    GoHome();
                }
                finally { SetConsoleCtrlHandler(IntPtr.Zero, false); }
                return ok;
            }
            finally { Gate.Release(); }
        }

        /// <summary>Joins the agent's hidden home console, making it first if needed. Call with the gate held.</summary>
        private static void GoHome()
        {
            if (_home != null && !HasExited(_home) && AttachConsole((uint)_home.Id)) { return; }
            End(_home);
            _home = null;
            FreeConsole();
            var waiter = NewHiddenConsole();
            if (waiter != null && AttachConsole((uint)waiter.Id)) { _home = waiter; }
            else { End(waiter); }
        }

        /// <summary>A hidden classic console window, and the placeholder in it that waits forever (null if that failed).</summary>
        private static Process? NewHiddenConsole()
        {
            Process? conhost = null;
            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                conhost = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(system, "conhost.exe"), $"\"{System.IO.Path.Combine(system, "cmd.exe")}\" /d /c pause>nul")
                {
                    UseShellExecute = true, // so the window style reaches conhost: hidden from the first moment
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (conhost == null) { return null; }
                // Finding it goes through WMI, whose first query after a while can take a few seconds.
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && !conhost.HasExited)
                {
                    if (FindChild(conhost.Id, "cmd.exe") is int pid)
                    {
                        var waiter = Process.GetProcessById(pid);
                        if (_job != IntPtr.Zero) { AssignProcessToJobObject(_job, waiter.Handle); }
                        conhost.Dispose();
                        return waiter;
                    }
                    Thread.Sleep(50);
                }
            }
            catch { /* fall back to the old behaviour */ }
            try { if (conhost != null && !conhost.HasExited) { conhost.Kill(); } } catch { }
            conhost?.Dispose();
            return null;
        }

        private static int? FindChild(int parentId, string name)
        {
            using var search = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId={parentId} AND Name='{name}'");
            using var results = search.Get();
            foreach (var mo in results.Cast<ManagementObject>())
            {
                using (mo) { return Convert.ToInt32(mo["ProcessId"]); }
            }
            return null;
        }

        /// <summary>A job that ends every placeholder in it when the agent exits (its handle closes with the process).</summary>
        private static IntPtr PlaceholderJob()
        {
            try
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) { return IntPtr.Zero; }
                var limits = new JobLimits { LimitFlags = 0x2000 }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                return SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref limits, Marshal.SizeOf<JobLimits>()) ? job : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION (only LimitFlags is used).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        private static string ClassOf(IntPtr window)
        {
            if (window == IntPtr.Zero) { return string.Empty; }
            var name = new System.Text.StringBuilder(64);
            return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
        }

        /// <summary>A classic console window (not Windows Terminal's stand-in, not some other window reusing a stale handle).</summary>
        public static bool IsConsoleWindow(IntPtr window) => ClassOf(window) == "ConsoleWindowClass";

        /// <summary>A console window on screen, or one hosted by Windows Terminal (whose stand-in window is never "visible").</summary>
        private static bool Watched(IntPtr window) =>
            window != IntPtr.Zero && (IsWindowVisible(window) || ClassOf(window) == "PseudoConsoleWindow");

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        private static void End(Process? p)
        {
            if (p == null) { return; }
            try { if (!p.HasExited) { p.Kill(); } } catch { /* gone */ }
            p.Dispose();
        }
    }
}
