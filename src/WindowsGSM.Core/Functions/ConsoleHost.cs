#nullable enable
using System;
using System.Collections.Generic;
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
        [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] list, uint count);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WriteConsoleInputW(IntPtr input, InputRecord[] records, uint count, out uint written);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern IntPtr CreateFileInheritable(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int which, IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public int Inherit;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint MapVirtualKeyW(uint code, uint mapType);

        /// <summary>INPUT_RECORD holding a KEY_EVENT_RECORD.</summary>
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct InputRecord
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public int KeyDown;
            [FieldOffset(8)] public ushort RepeatCount;
            [FieldOffset(10)] public ushort VirtualKeyCode;
            [FieldOffset(12)] public ushort VirtualScanCode;
            [FieldOffset(14)] public char UnicodeChar;
            [FieldOffset(16)] public uint ControlKeyState;
        }
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
        private static Process? _spare; // a hidden console made ahead, so a hand-over (TakeOverHome) is instant
        private static readonly object SpareLock = new object();
        private static IntPtr _job;

        public static bool Active => _enabled;

        /// <summary>For tests: told of every console the agent leaves and joins (what, and how it went).</summary>
        internal static Action<string>? Trace;

        private static bool Free(string why)
        {
            bool ok = FreeConsole();
            Trace?.Invoke($"free ({why}) ok={ok}");
            return ok;
        }

        private static bool Join(uint pid, string why)
        {
            bool ok = AttachConsole(pid);
            Trace?.Invoke($"join {pid} ({why}) ok={ok}{(ok ? "" : " error=" + Marshal.GetLastWin32Error())}");
            return ok;
        }

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
            PrepareSpare();
        }

        private static int _preparing;

        /// <summary>
        /// Makes a spare hidden console in the background, if there isn't one ready. Made outside the lock (it takes a
        /// moment), so taking the spare never waits for the next one.
        /// </summary>
        private static void PrepareSpare()
        {
            if (Interlocked.Exchange(ref _preparing, 1) == 1) { return; }
            Task.Run(() =>
            {
                try
                {
                    lock (SpareLock) { if (_spare != null && !HasExited(_spare)) { return; } }
                    var made = NewHiddenConsole();
                    lock (SpareLock)
                    {
                        if (_spare == null || HasExited(_spare)) { End(_spare); _spare = made; made = null; }
                    }
                    End(made); // one was made meanwhile
                }
                finally { Interlocked.Exchange(ref _preparing, 0); }
            });
        }

        /// <summary>The spare console's placeholder (taken: the next one is made in the background), or a new one now.</summary>
        private static Process? TakeSpare()
        {
            Process? spare;
            lock (SpareLock)
            {
                spare = _spare != null && !HasExited(_spare) ? _spare : null;
                _spare = null;
            }
            PrepareSpare();
            return spare ?? NewHiddenConsole();
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
            (IntPtr input, IntPtr output, IntPtr error, IntPtr conin, IntPtr conout)? std = null;
            try
            {
                waiter = NewHiddenConsole() ?? NewHiddenConsole(); // once more before giving up
                if (waiter != null)
                {
                    Free("start");
                    joined = Join((uint)waiter.Id, "start: the new game's console");
                    if (!joined) { GoHome(); }
                }
                IntPtr window = joined ? GetConsoleWindow() : IntPtr.Zero;
                if (joined) { std = UseConsoleForStandardHandles(); }
                return (await start().ConfigureAwait(false), window);
            }
            finally
            {
                RestoreStandardHandles(std);
                if (joined) { Free("start done"); GoHome(); }
                // The game (if it started) now holds the console; if nothing did, it closes once the placeholder ends.
                End(waiter);
                Gate.Release();
            }
        }

        /// <summary>
        /// Points this process's standard handles at the console it's in, for the game started now to inherit. A game
        /// inherits its parent's standard handles: if the agent's were a file or pipe (started with redirected input,
        /// say), the game would read that instead of its own console, and nothing typed into it would arrive. Returns
        /// what to put back.
        /// </summary>
        private static (IntPtr, IntPtr, IntPtr, IntPtr, IntPtr)? UseConsoleForStandardHandles()
        {
            var inherit = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            IntPtr conin = CreateFileInheritable("CONIN$", 0xC0000000, 3, ref inherit, 3, 0, IntPtr.Zero);
            IntPtr conout = CreateFileInheritable("CONOUT$", 0xC0000000, 3, ref inherit, 3, 0, IntPtr.Zero);
            if (conin == new IntPtr(-1) || conout == new IntPtr(-1))
            {
                if (conin != new IntPtr(-1)) { CloseHandle(conin); }
                if (conout != new IntPtr(-1)) { CloseHandle(conout); }
                return null;
            }
            var saved = (GetStdHandle(-10), GetStdHandle(-11), GetStdHandle(-12), conin, conout);
            SetStdHandle(-10, conin);  // STD_INPUT_HANDLE
            SetStdHandle(-11, conout); // STD_OUTPUT_HANDLE
            SetStdHandle(-12, conout); // STD_ERROR_HANDLE
            return saved;
        }

        private static void RestoreStandardHandles((IntPtr input, IntPtr output, IntPtr error, IntPtr conin, IntPtr conout)? saved)
        {
            if (saved is not { } s) { return; }
            SetStdHandle(-10, s.input);
            SetStdHandle(-11, s.output);
            SetStdHandle(-12, s.error);
            CloseHandle(s.conin); // the game has its own copies
            CloseHandle(s.conout);
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
        /// If <paramref name="pid"/> has joined the agent's own hidden console — a game that leaves the console it was
        /// started in for its parent's (Rust) — that console becomes the game's: the agent moves to a new home and the
        /// old one's window is returned, now the game's to show and hide. Zero if the game isn't there.
        /// </summary>
        /// <param name="others">Other programs left in that console besides the game (and the agent's placeholder).</param>
        public static IntPtr TakeOverHome(int pid, out int others)
        {
            others = 0;
            // Asked every 50 ms after a start: if the console is busy (another start), skip this time rather than wait.
            if (!_enabled || !Gate.Wait(TimeSpan.FromMilliseconds(100))) { return IntPtr.Zero; }
            try
            {
                IntPtr home = GetConsoleWindow();
                if (!IsConsoleWindow(home)) { return IntPtr.Zero; }
                var list = new uint[64];
                int count = (int)Math.Min(GetConsoleProcessList(list, (uint)list.Length), (uint)list.Length);
                var inHome = list.Take(count).ToList();
                if (!inHome.Contains((uint)pid)) { return IntPtr.Zero; }
                int self = Environment.ProcessId, placeholder = _home?.Id ?? 0;
                others = inHome.Count(id => id != pid && id != self && id != placeholder);

                // Straight into a console made ahead: the agent must not linger here (another game making the same
                // move now would share this console) nor be without one (a game finding no console to join opens one
                // WindowsGSM can't manage — on Windows 11, in Windows Terminal).
                var fresh = TakeSpare();
                var old = _home;
                Free("hand over");
                if (fresh != null && Join((uint)fresh.Id, "hand over: the spare")) { _home = fresh; }
                else { End(fresh); _home = null; GoHome(); }
                End(old); // the game keeps that console (and its window) alive
                return home;
            }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Types <paramref name="text"/> into <paramref name="pid"/>'s console ('\r' or '\n' is Enter) by writing key
        /// presses straight into its input, as the window itself does with real ones. NEXT: plugins type by posting
        /// key messages to the window, and a hidden console window can sit on those for a long time — a Rust server's
        /// "quit" never arrived and it had to be killed. False if this isn't active or the console couldn't be reached
        /// (the caller then falls back to posting).
        /// </summary>
        public static bool TypeInto(int pid, string text, IntPtr window = default)
        {
            if (string.IsNullOrEmpty(text)) { return true; }
            var records = new List<InputRecord>(text.Length * 2);
            foreach (char c in text)
            {
                // As a keyboard would send it: the key's code and scan code, with Shift where the character needs it —
                // games that read key by key (Unity's console, Rust's) look at those, not just the character.
                bool enter = c is '\r' or '\n';
                short scan = enter ? (short)0x0D : VkKeyScanW(c);
                ushort vk = scan == -1 ? (ushort)0 : (ushort)(scan & 0xFF);
                uint state = scan != -1 && (scan & 0x100) != 0 ? 0x10u : 0u; // SHIFT_PRESSED
                foreach (int down in new[] { 1, 0 })
                {
                    records.Add(new InputRecord
                    {
                        EventType = 1, // KEY_EVENT
                        KeyDown = down,
                        RepeatCount = 1,
                        VirtualKeyCode = vk,
                        VirtualScanCode = (ushort)(vk == 0 ? 0 : MapVirtualKeyW(vk, 0)), // MAPVK_VK_TO_VSC
                        UnicodeChar = enter ? '\r' : c,
                        ControlKeyState = state,
                    });
                }
            }
            return Visit(pid, theirs =>
            {
                if (window != IntPtr.Zero && theirs != window) { return false; } // not the console behind that window
                IntPtr input = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // read/write, shared, open existing
                if (input == new IntPtr(-1)) { return false; }
                try { return WriteConsoleInputW(input, records.ToArray(), (uint)records.Count, out uint written) && written == records.Count; }
                finally { CloseHandle(input); }
            });
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
                Free("visit");
                bool ok = false;
                // A Ctrl+C in that console (ours, or someone pressing it in the game's window) reaches every process
                // in it, the agent included while it's there: ignore it for now. Only for now — games inherit the
                // setting, and one started meanwhile would never stop on Ctrl+C.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                try
                {
                    if (Join((uint)pid, "visit"))
                    {
                        IntPtr theirs = GetConsoleWindow();
                        if (theirs != home || home == IntPtr.Zero) { ok = action(theirs); }
                        Free("visit done");
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
            if (_home != null && !HasExited(_home) && Join((uint)_home.Id, "home")) { return; }
            Trace?.Invoke("home: making a new one");
            End(_home);
            _home = null;
            Free("home gone");
            var waiter = TakeSpare(); // ready-made when possible: the agent shouldn't be without a console for long
            if (waiter != null && Join((uint)waiter.Id, "new home")) { _home = waiter; }
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
