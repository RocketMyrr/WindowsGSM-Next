#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Watchdog;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Services
{
    /// <summary>Why a server is being started — pre-start steps use it (e.g. backup-on-start skips plain restarts).</summary>
    public enum StartReason { Manual, AutoStart, Restart, AutoRestart }

    /// <summary>Work that runs just before a server starts: backup/update/add-ons-on-start, etc.</summary>
    public interface IPreStartStep
    {
        string Name { get; }
        Task RunAsync(ServerInstance server, StartReason reason, JobContext job);
    }

    /// <summary>The answer to "please start/stop/… this server": either a job doing it, or why not.</summary>
    public sealed record OperationRequest(bool Accepted, string? Error, Job? Job)
    {
        public static OperationRequest Rejected(string error) => new OperationRequest(false, error, null);
        public static OperationRequest Running(Job job) => new OperationRequest(true, null, job);
    }

    /// <summary>
    /// Starting, stopping, restarting and killing game servers, plus crash handling and auto-restart.
    /// Ported from the legacy MainWindow (GameServer_Start/Stop/Kill/Restart, Server_BeginStart/Stop,
    /// OnGameServerExited) with the behaviour kept and the differences marked NEXT.
    ///
    /// Every operation runs as a job (visible progress, stage and outcome) behind the per-server
    /// <see cref="OperationGate"/>, so conflicting operations can't overlap.
    /// </summary>
    public sealed class LifecycleService
    {
        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly PluginCatalog _plugins;
        private readonly ServerLog _log;
        private readonly EventBus _events;

        public LifecycleService(ServerRegistry servers, OperationGate gate, JobManager jobs, PluginCatalog plugins,
                                ServerLog log, EventBus events, CrashLoopOptions? crashLoop = null)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _plugins = plugins;
            _log = log;
            _events = events;
            CrashLoop = crashLoop ?? new CrashLoopOptions();
            // NEXT: "Show the console window" takes effect straight away on a running server, not at its next start.
            _events.Subscribe<ServerConfigChanged>(c =>
            {
                if (!c.Keys.Any(k => string.Equals(k, ServerConfig.SettingName.ShowConsole, StringComparison.OrdinalIgnoreCase))) { return; }
                var s = _servers.Get(c.ServerId);
                if (s?.Process != null && s.State == ServerState.Running && s.ConsoleWindow != IntPtr.Zero)
                {
                    SetConsoleWindowVisible(s.Id, s.Config.ShowConsole);
                }
            });
        }

        public CrashLoopOptions CrashLoop { get; }

        /// <summary>Run in order before every start (backup/update/add-ons-on-start register here).</summary>
        public IList<IPreStartStep> PreStartSteps { get; } = new List<IPreStartStep>();

        /// <summary>Pause between stopping and starting again during a restart (legacy: 500 ms twice).</summary>
        public TimeSpan RestartPause { get; set; } = TimeSpan.FromMilliseconds(500);

        // ─────────────────────────────── Requests ───────────────────────────────

        public OperationRequest Start(string id, StartReason reason = StartReason.Manual)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"{s.Name} is {Describe(s.State)}."); }
            if (!_gate.TryBegin(id, OperationKind.Start, "Start", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("start", id, $"Start {s.Name}", async ctx =>
            {
                using (lease) { return await StartCoreAsync(s, reason, ctx).ConfigureAwait(false); }
            }));
        }

        public OperationRequest Stop(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Running) { return OperationRequest.Rejected($"{s.Name} isn't running."); }
            if (!_gate.TryBegin(id, OperationKind.Stop, "Stop", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("stop", id, $"Stop {s.Name}", async ctx =>
            {
                using (lease) { return await StopCoreAsync(s, ctx).ConfigureAwait(false); }
            }));
        }

        public OperationRequest Restart(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Running) { return OperationRequest.Rejected($"{s.Name} isn't running."); }
            if (!_gate.TryBegin(id, OperationKind.Restart, "Restart", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("restart", id, $"Restart {s.Name}", async ctx =>
            {
                using (lease) { return await RestartCoreAsync(s, ctx).ConfigureAwait(false); }
            }));
        }

        /// <summary>
        /// Force-terminates the server. Always allowed — it interrupts whatever else is running on the server,
        /// and it's the way out of a server stuck in Starting/Stopping.
        /// </summary>
        public OperationRequest Kill(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State == ServerState.Stopped && s.Process == null) { return OperationRequest.Rejected($"{s.Name} isn't running."); }
            _gate.TryBegin(id, OperationKind.Kill, "Kill", out var lease, out _); // Kill always gets the gate

            return OperationRequest.Running(_jobs.Start("kill", id, $"Kill {s.Name}", ctx =>
            {
                using (lease) { return Task.FromResult(KillCore(s)); }
            }));
        }

        /// <summary>
        /// Adopts game servers that are still running from before this process started (the agent restarted,
        /// or took over from a previous instance), using the PID cache every start writes.
        /// </summary>
        public void ReattachRunningServers()
        {
            foreach (var s in _servers.All)
            {
                int pid = ServerCache.GetPID(s.Id);
                if (pid <= 0 || s.Process != null) { continue; }

                Process p;
                try { p = Process.GetProcessById(pid); } catch { continue; }

                string cachedName;
                try { cachedName = ServerCache.GetProcessName(s.Id); } catch { continue; }
                if (string.IsNullOrWhiteSpace(cachedName) || !string.Equals(p.ProcessName, cachedName, StringComparison.OrdinalIgnoreCase)) { continue; }

                // NEXT: legacy re-adopted the process but never enabled Exited events on it (they're off for
                // processes obtained by PID), so after a WindowsGSM restart crash detection and auto-restart
                // silently stopped working for every server that was already running.
                if (!Watch(s, p)) { continue; }
                // Its console window: asked of the console itself when we can (a cached handle can be stale, and
                // after a reboot could even name some other window).
                IntPtr window = ConsoleHost.Active ? ConsoleHost.WindowOf(pid) : ServerCache.GetWindowsIntPtr(s.Id);
                s.ConsoleWindow = ConsoleHost.IsConsoleWindow(window) ? window : IntPtr.Zero;
                s.ConsoleWindowVisible = ConsoleWindows.IsVisible(s.ConsoleWindow);
                ConsoleWindows.Adopt(p, s.ConsoleWindow);
                ConsoleWindows.Register(s.ConsoleWindow, p);
                if (s.ConsoleWindow != IntPtr.Zero) { ConsoleWindows.AdoptStartInfo(p); } // a window: it wasn't captured
                try { s.StartedAt = p.StartTime; } catch { s.StartedAt = DateTimeOffset.Now; }
                s.Reattached = true;
                s.SetState(ServerState.Running);
                _log.Write(s.Id, $"Server: Reattached (PID {pid})");
            }
        }

        // ─────────────────────────────── Operations ───────────────────────────────

        internal async Task<string?> StartCoreAsync(ServerInstance s, StartReason reason, JobContext job)
        {
            // An explicit start gets a clean slate (crash-loop and memory-guard history).
            if (reason == StartReason.Manual || reason == StartReason.AutoStart) { s.ResetWatchdogState(); }
            s.ReloadConfig();

            string? invalid = ValidateAddress(s.Config);
            if (invalid != null)
            {
                _log.Write(s.Id, "Server: Fail to start");
                _log.Write(s.Id, "[ERROR]" + invalid);
                return invalid.Trim();
            }

            await RunPreStartStepsAsync(s, reason, job).ConfigureAwait(false);

            s.SetState(ServerState.Starting);
            _log.Write(s.Id, "Action: Start" + Notes(reason));
            job.Report(stage: "Starting server");

            string? error = await BeginStartAsync(s, job).ConfigureAwait(false);
            if (error != null)
            {
                s.SetState(ServerState.Stopped);
                _log.Write(s.Id, "Server: Fail to start");
                _log.Write(s.Id, "[ERROR] " + error);
                return error;
            }

            s.SetState(ServerState.Running);
            _log.Write(s.Id, "Server: Started" + Notes(reason));
            if (reason == StartReason.AutoStart)
            {
                _events.Publish(new ServerAlert(s.Id, AlertKind.AutoStarted, $"{s.Name} started", "Auto start brought the server up."));
            }
            return null;
        }

        internal async Task<string?> StopCoreAsync(ServerInstance s, JobContext job)
        {
            var p = s.Process;
            if (p == null) { s.SetState(ServerState.Stopped); return null; }

            s.SetState(ServerState.Stopping);
            _log.Write(s.Id, "Action: Stop");
            job.Report(stage: "Stopping server");

            bool graceful = await BeginStopAsync(s, p).ConfigureAwait(false);
            _log.Write(s.Id, "Server: Stopped");
            if (!graceful) { _log.Write(s.Id, "[NOTICE] Server fail to stop gracefully"); }
            await RunAfterStopScriptAsync(s, job).ConfigureAwait(false);
            s.SetState(ServerState.Stopped);
            return null;
        }

        private async Task<string?> RestartCoreAsync(ServerInstance s, JobContext job)
        {
            var p = s.Process;
            if (p == null) { s.SetState(ServerState.Stopped); return $"{s.Name} isn't running."; }

            s.SetState(ServerState.Restarting);
            _log.Write(s.Id, "Action: Restart");
            job.Report(stage: "Stopping server");
            await BeginStopAsync(s, p).ConfigureAwait(false);
            await RunAfterStopScriptAsync(s, job).ConfigureAwait(false);
            await Task.Delay(RestartPause).ConfigureAwait(false);

            s.ReloadConfig();
            await RunPreStartStepsAsync(s, StartReason.Restart, job).ConfigureAwait(false);
            await Task.Delay(RestartPause).ConfigureAwait(false);

            job.Report(stage: "Starting server");
            string? error = await BeginStartAsync(s, job).ConfigureAwait(false);
            if (error != null)
            {
                s.SetState(ServerState.Stopped);
                _log.Write(s.Id, "Server: Fail to start");
                _log.Write(s.Id, "[ERROR] " + error);
                return error;
            }

            s.SetState(ServerState.Running);
            _log.Write(s.Id, "Server: Restarted");
            return null;
        }

        /// <summary>
        /// Shows or hides a running server's own console window on this machine's screen (servers whose output
        /// isn't captured into the panel). Returns a problem, or null.
        /// </summary>
        public string? SetConsoleWindowVisible(string id, bool visible)
        {
            var s = _servers.Get(id);
            if (s == null) { return "No such server."; }
            if (s.Process == null || s.State != ServerState.Running) { return $"{s.Name} isn't running."; }
            if (!ConsoleWindows.Exists(s.ConsoleWindow))
            {
                bool captured;
                try { captured = s.Process.StartInfo.RedirectStandardOutput; } catch { captured = s.Config.EmbedConsole; } // re-adopted: not ours to ask
                return captured
                    ? "This server's console is captured into the panel, so it has no window. Turn off \"Capture the console here\" and restart the server to get one."
                    : "This server has no console window it can show. Restart the server to give it one.";
            }
            ConsoleWindows.SetVisible(s.ConsoleWindow, visible);
            s.ConsoleWindowVisible = visible;
            _log.Write(id, visible ? "Console window shown" : "Console window hidden");
            return null;
        }

        private string? KillCore(ServerInstance s)
        {
            var p = s.Process;
            _log.Write(s.Id, "Actions: Kill");
            if (p != null)
            {
                s.ClearProcessIf(p); // so the exit isn't treated as a crash
                // NEXT: kill the whole process tree. Legacy killed only the top process, so a server launched
                // through a batch file or wrapper left the actual game running.
                try { if (!p.HasExited) { p.Kill(entireProcessTree: true); } } catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] Kill: {ex.Message}"); }
            }
            ClearPidCache(s.Id);
            s.StartedAt = null;
            s.SetState(ServerState.Stopped);
            _log.Write(s.Id, "Server: Killed");
            return null;
        }

        // ─────────────────────────────── Start / stop mechanics ───────────────────────────────

        /// <summary>Launches the game process. Returns an error message, or null once it's up.</summary>
        private async Task<string?> BeginStartAsync(ServerInstance s, JobContext job)
        {
            // NEXT: game files on a drive that isn't connected: say so, rather than let the game fail oddly.
            if (ServerLocation.Problem(s.Id) is string missing) { return missing; }
            // The plugin gets the settings without the before-start script: the engine runs it (below), for every
            // game. Legacy left it to the plugin, and only Rust's ran it — it would run twice.
            var startConfig = new ServerConfig(s.Id) { BatchFile = string.Empty };
            dynamic? game = _plugins.Create(s.Game, startConfig);
            if (game == null) { return $"Unknown game \"{s.Game}\" — is its plugin installed and loading?"; }

            // Anything left over from a previous run of this server (a crashed wrapper, an orphaned child).
            await EndLeftoverProcessesAsync(s.Id).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);

            // NEXT: a taken port stops the start with a clear reason, instead of the game failing vaguely.
            var ports = PortCheck.Check(s, _servers);
            foreach (string warning in ports.Warnings) { _log.Write(s.Id, "[NOTICE] " + warning); }
            if (ports.Error != null) { return ports.Error; }

            // NEXT: Windows Firewall. Legacy added a rule on every start, which only works as administrator (it
            // always ran elevated) and failed silently otherwise. Now: added when the agent is elevated; if not,
            // the log and the server's overview say so, with a button that asks Windows once (GameFirewall).
            try
            {
                var firewall = GameFirewall.Status(s, _plugins);
                if (firewall.Program != null && firewall.State is "missing" or "blocked")
                {
                    if (GameFirewall.TryAdd(s, firewall.Program) == null && firewall.State == "missing") { _log.Write(s.Id, $"Firewall: allowed {Path.GetFileName(firewall.Program)}"); }
                    else { _log.Write(s.Id, "[NOTICE] " + firewall.Message.Replace("Allow it through the firewall", "Use \"Allow through firewall\" on the server's overview")); }
                }
            }
            catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] Couldn't check Windows Firewall: {ex.Message}"); }

            // NEXT: the server's before-start script (rotate logs, clean up…), for every game.
            string beforeStart = ServerScripts.BeforeStart(s.Config);
            if (beforeStart.Length > 0)
            {
                job.Report(stage: "Running the before-start script");
                bool ran = await ServerScripts.RunAsync(s, beforeStart, "before start", _log).ConfigureAwait(false);
                if (!ran && ServerScripts.BlocksStart(s.Config))
                {
                    return "The before-start script didn't finish successfully, so the server wasn't started (its log says why; Settings → Scripts).";
                }
                job.Report(stage: "Starting server");
            }

            // NEXT: like legacy, only games whose plugin allows it are captured. Asking one that can't (Rust) to
            // capture left it with neither its output in the panel nor a window of its own.
            bool capture = s.Config.EmbedConsole && GameCatalog.CanCapture((object)game);
            Dyn.TrySet((object)game, "AllowsEmbedConsole", capture);

            // NEXT: fresh console per run. Legacy cleared it on stop, which threw away the last output —
            // exactly what you need to see after a server stops unexpectedly. Clients resync via the
            // console generation, so they know a new run began.
            s.Console.Clear();
            // Last run's window is gone; the new one (if any) is set once it's found, below.
            s.ConsoleWindow = IntPtr.Zero;
            s.ConsoleWindowVisible = false;

            // NEXT: a game whose console isn't captured gets a console window of its own (ConsoleHost) — before, it
            // shared the agent's invisible one, so "Show the console window" had nothing to show.
            Process? p;
            IntPtr ownWindow = IntPtr.Zero;
            try
            {
                if (capture) { p = await game.Start(); }
                else { (p, ownWindow) = await ConsoleHost.StartInOwnConsoleAsync<Process?>(async () => (Process?)await game.Start()).ConfigureAwait(false); }
            }
            catch (Exception ex) { return "The game plugin failed to start the server: " + ex.Message; }
            if (p == null)
            {
                string err = Dyn.Get((object)game, "Error") as string ?? string.Empty;
                return string.IsNullOrWhiteSpace(err) ? "The game plugin didn't start a process." : err;
            }

            if (!Watch(s, p))
            {
                s.ClearProcessIf(p);
                return $"The server exited immediately (exit code {SafeExitCode(p)}).";
            }
            s.Reattached = false; // a fresh run: its console is ours again

            // Settle the console window in the background, like legacy.
            bool showConsole = s.Config.ShowConsole;
            _ = Task.Run(async () =>
            {
                IntPtr hWnd = ConsoleHost.Confirm(ownWindow, p);
                if (hWnd != IntPtr.Zero) { ConsoleWindows.Prime(hWnd, showConsole); }
                else if (ownWindow != IntPtr.Zero && ConsoleHost.TakeOverHome(p.Id, out _) is var moved && moved != IntPtr.Zero)
                {
                    hWnd = moved; // it already left its own console for the agent's (see below)
                    ConsoleWindows.Prime(hWnd, showConsole);
                }
                else { hWnd = ConsoleWindows.Settle(p, showConsole); }
                UseWindow(hWnd, showConsole);

                // NEXT: some games leave the console they were started in and join their parent's — the agent's
                // hidden one. Rust does, a few seconds in (Facepunch's console: FreeConsole, then AttachConsole to
                // the parent): its real console never showed, and the window WindowsGSM had was left empty. When that
                // happens the agent hands its console to the game and moves to a new one; the game's window then
                // shows, hides and takes commands like any other.
                // Checked often at first (that's when games do it), so another server doing the same moments later
                // finds a fresh console rather than sharing this one.
                if (ownWindow == IntPtr.Zero) { return; }
                var started = DateTime.UtcNow;
                while (DateTime.UtcNow - started < TimeSpan.FromMinutes(2) && s.Process == p && !SafeHasExited(p))
                {
                    await Task.Delay(DateTime.UtcNow - started < TimeSpan.FromSeconds(30) ? 50 : 1000).ConfigureAwait(false);
                    IntPtr taken = ConsoleHost.TakeOverHome(p.Id, out int others);
                    if (taken == IntPtr.Zero) { continue; }
                    ConsoleWindows.Prime(taken, s.Config.ShowConsole);
                    UseWindow(taken, s.Config.ShowConsole);
                    _log.Write(s.Id, "Console window: the game moved to the agent's console; it's the game's own now");
                    if (others > 0) { _log.Write(s.Id, $"[NOTICE] {others} other program(s) share this server's console (another server that did the same at the same moment?). Restart one of them to separate them."); }
                    return;
                }
            });

            void UseWindow(IntPtr hWnd, bool shown)
            {
                if (hWnd != IntPtr.Zero && s.Process == p)
                {
                    ConsoleWindows.Adopt(p, hWnd);
                    ConsoleWindows.Register(hWnd, p);
                    ConsoleWindows.SetTitle(hWnd, s.Name);
                    s.ConsoleWindow = hWnd;
                    // The setting may have changed while the window was being set up: the latest one wins.
                    s.ConsoleWindowVisible = s.Config.ShowConsole;
                    if (s.ConsoleWindowVisible != shown) { ConsoleWindows.SetVisible(hWnd, s.ConsoleWindowVisible); }
                }
                try { ServerCache.SaveWindowsIntPtr(s.Id, hWnd); } catch { /* cache is best effort */ }
            }

            if (p.HasExited)
            {
                s.ClearProcessIf(p);
                return $"The server exited immediately (exit code {SafeExitCode(p)}).";
            }

            try { Functions.CPU.Priority.SetProcessWithPriority(p, Functions.CPU.Priority.GetPriorityInteger(s.Config.CPUPriority)); }
            catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] Fail to set priority. ({ex.Message})"); }
            try { p.ProcessorAffinity = Functions.CPU.Affinity.GetAffinityIntPtr(s.Config.CPUAffinity); }
            catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] Fail to set affinity. ({ex.Message})"); }

            try
            {
                ServerCache.SavePID(s.Id, p.Id);
                ServerCache.SaveProcessName(s.Id, p.ProcessName);
            }
            catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] Couldn't save the process cache: {ex.Message}"); }

            s.StartedAt = DateTimeOffset.Now;
            string notice = Dyn.Get((object)game, "Notice") as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(notice)) { _log.Write(s.Id, "[Notice] " + notice); }
            return null;
        }

        /// <summary>The server's after-stop script, if it has one (after a stop or a restart's stop — not Force stop).</summary>
        private async Task RunAfterStopScriptAsync(ServerInstance s, JobContext job)
        {
            string afterStop = ServerScripts.AfterStop(s.Config);
            if (afterStop.Length == 0) { return; }
            job.Report(stage: "Running the after-stop script");
            await ServerScripts.RunAsync(s, afterStop, "after stop", _log).ConfigureAwait(false);
        }

        /// <summary>Runs before a normal stop (not Kill), while the game still listens: the engine saves the world here.</summary>
        public Func<ServerInstance, Task>? BeforeStop { get; set; }

        /// <summary>
        /// Asks the game to stop (save first → plugin Stop → Ctrl+C → kill). Returns false if it had to be killed.
        /// NEXT: waits the server's stop timeout (default 30 s) for a clean exit — legacy gave 10 s, which can cut
        /// a big world's shutdown save short.
        /// </summary>
        private async Task<bool> BeginStopAsync(ServerInstance s, Process p)
        {
            if (BeforeStop != null && !p.HasExited)
            {
                try { await BeforeStop(s).WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false); } catch { /* never block a stop */ }
            }
            s.ClearProcessIf(p); // this exit is expected, not a crash

            // Plugins type their stop command into p.MainWindowHandle; make sure it's the game's window.
            if (ConsoleWindows.Exists(s.ConsoleWindow)) { ConsoleWindows.Adopt(p, s.ConsoleWindow); }

            dynamic? game = _plugins.Create(s.Game, s.Config);
            int timeout = WorldSave.StopTimeout(s);
            bool sentCtrlC = false;

            // NEXT: "Send Ctrl+C before the game's own stop" — for games whose plugin just ends the process (no save):
            // most servers shut down properly on Ctrl+C. If it worked, the plugin's stop isn't needed.
            if (StopWithCtrlCFirst(s.Config))
            {
                sentCtrlC = true;
                bool sent = false;
                try { sent = ProcessManagement.SendCtrlC(p.Id); } catch { /* no console to reach */ }
                if (sent)
                {
                    _log.Write(s.Id, "Stopping with Ctrl+C first");
                    for (int i = 0; i < timeout && !p.HasExited; i++) { await Task.Delay(1000).ConfigureAwait(false); }
                }
                else { _log.Write(s.Id, "[NOTICE] Couldn't send Ctrl+C (no console to reach); using the game's own stop"); }
            }

            if (!p.HasExited)
            {
                ServerConsole.Stopping.Value = p; // keys a plugin "presses" with no window reach this server's console
                try
                {
                    if (game == null) { throw new InvalidOperationException("no plugin"); }
                    await game.Stop(p);
                }
                catch
                {
                    // No usable Stop() — try a console Ctrl+C, then kill.
                    // NEXT: and wait for it like a plugin stop; checking straight away killed the game mid-shutdown.
                    sentCtrlC = true;
                    try { ProcessManagement.StopProcess(p); } catch { /* fall through to kill */ }
                }
                finally { ServerConsole.Stopping.Value = null; }
                for (int i = 0; i < timeout && !p.HasExited; i++) { await Task.Delay(1000).ConfigureAwait(false); }
            }

            // NEXT: the plugin's own way didn't work (e.g. it types "quit" into a window the game doesn't have): most
            // console games also shut down cleanly on Ctrl+C — one more chance to save before the kill.
            if (!sentCtrlC && !p.HasExited)
            {
                bool sent = false;
                try { sent = ProcessManagement.SendCtrlC(p.Id); } catch { /* no console to reach */ }
                if (sent)
                {
                    _log.Write(s.Id, "[NOTICE] The game didn't stop in time; sent it Ctrl+C");
                    for (int i = 0; i < 10 && !p.HasExited; i++) { await Task.Delay(1000).ConfigureAwait(false); }
                }
            }

            ClearPidCache(s.Id);
            s.StartedAt = null;

            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return true;
        }

        /// <summary>Hooks exit detection and makes <paramref name="p"/> the server's process. False if it already exited.</summary>
        private bool Watch(ServerInstance s, Process p)
        {
            try { p.EnableRaisingEvents = true; } catch { /* access denied on a foreign process — poll fallback below */ }
            p.Exited += (_, __) => OnProcessExited(s, p);
            s.SetProcess(p);

            // The process can exit before the handler is attached; then Exited never fires.
            if (p.HasExited) { s.ClearProcessIf(p); return false; }
            return true;
        }

        private async Task RunPreStartStepsAsync(ServerInstance s, StartReason reason, JobContext job)
        {
            foreach (var step in PreStartSteps)
            {
                job.Report(stage: step.Name);
                try { await step.RunAsync(s, reason, job).ConfigureAwait(false); }
                catch (Exception ex) { _log.Write(s.Id, $"[ERROR] {step.Name} failed: {ex.Message}"); }
            }
        }

        // ─────────────────────────────── Crashes & auto-restart ───────────────────────────────

        private void OnProcessExited(ServerInstance s, Process p)
        {
            // Only the server's current process matters: stop/kill/restart clear it first, so their exits
            // (and exits of processes from previous runs) are ignored here.
            if (!ReferenceEquals(s.Process, p) || s.State != ServerState.Running) { return; }
            _ = Task.Run(() => HandleCrashAsync(s, p));
        }

        private async Task HandleCrashAsync(ServerInstance s, Process p)
        {
            if (!s.ClearProcessIf(p)) { return; } // someone else already dealt with it

            var (report, exitCode) = CrashReport.Write(s, p);
            ClearPidCache(s.Id);
            s.StartedAt = null;
            s.ReloadConfig();
            bool autoRestart = s.Config.AutoRestart;

            s.SetState(autoRestart ? ServerState.Restarting : ServerState.Stopped);
            _log.Write(s.Id, string.IsNullOrWhiteSpace(exitCode) ? "Server: Crashed" : $"Server: Crashed (Exit Code: {exitCode})");
            _log.Write(s.Id, $"[ERROR] Crash details: {report}");
            _events.Publish(new ServerAlert(s.Id, AlertKind.Crashed,
                autoRestart ? $"{s.Name} crashed — auto-restarting" : $"{s.Name} crashed",
                autoRestart ? "The watchdog is bringing the server back up." : "The server stopped unexpectedly."));

            if (!autoRestart) { return; }

            // NEXT: auto-restart goes through the gate like every other operation (legacy didn't, so it
            // could collide with a user action), and runs as a job so the backoff wait is visible.
            if (!_gate.TryBegin(s.Id, OperationKind.Restart, "Auto-restart", out var lease, out var blockedBy))
            {
                s.SetState(ServerState.Stopped);
                _log.Write(s.Id, $"[NOTICE] Auto-restart skipped: {blockedBy} is running.");
                return;
            }

            var job = _jobs.Start("auto-restart", s.Id, $"Auto-restart {s.Name}", async ctx =>
            {
                using (lease) { return await AutoRestartAsync(s, ctx).ConfigureAwait(false); }
            });
            await job.Completion.ConfigureAwait(false);
        }

        private async Task<string?> AutoRestartAsync(ServerInstance s, JobContext job)
        {
            var decision = CrashLoopPolicy.OnCrash(s.RecentCrashes, DateTimeOffset.Now, CrashLoop);
            int minutes = (int)CrashLoop.Window.TotalMinutes;

            if (decision.Response == CrashResponse.Suspend)
            {
                s.CrashLoopSuspended = true;
                s.SetState(ServerState.Stopped);
                _log.Write(s.Id, $"[Crash Loop] {decision.CrashesInWindow} crashes in {minutes} min — auto-restart suspended. Start the server manually after resolving the issue.");
                _events.Publish(new ServerAlert(s.Id, AlertKind.CrashLoopSuspended, $"{s.Name} — auto-restart suspended",
                    $"Crashed {decision.CrashesInWindow} times in {minutes} min. Start it manually after fixing the issue."));
                return "Auto-restart suspended: crashing repeatedly.";
            }

            if (decision.Response == CrashResponse.RestartAfterDelay)
            {
                int seconds = (int)Math.Round(decision.Delay.TotalSeconds);
                _log.Write(s.Id, $"[Crash Loop] {decision.CrashesInWindow} crashes in {minutes} min — delaying auto-restart {seconds}s (backoff).");
                job.Report(stage: $"Waiting {seconds}s before restarting (crash backoff)");
                await Task.Delay(decision.Delay, job.Cancellation).ConfigureAwait(false);

                // Bail if the user intervened (kill, or auto-restart turned off) during the wait.
                s.ReloadConfig();
                if (s.State != ServerState.Restarting || !s.Config.AutoRestart) { return null; }
            }

            await RunPreStartStepsAsync(s, StartReason.AutoRestart, job).ConfigureAwait(false);

            job.Report(stage: "Starting server");
            string? error = await BeginStartAsync(s, job).ConfigureAwait(false);
            if (error != null)
            {
                s.SetState(ServerState.Stopped);
                _log.Write(s.Id, "Server: Fail to start");
                _log.Write(s.Id, "[ERROR] " + error);
                return error;
            }

            s.SetState(ServerState.Running);
            _log.Write(s.Id, "Server: Started | Auto Restart");
            _events.Publish(new ServerAlert(s.Id, AlertKind.AutoRestarted, $"{s.Name} restarted", "Auto-restart brought the server back up."));
            return null;
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        /// <summary>Kills processes running from this server's folder (servers\{id}\…), like legacy did before every start.</summary>
        internal static Task EndLeftoverProcessesAsync(string id) => Task.Run(() =>
        {
            string prefix = Path.Combine(WgsmEnvironment.DataRoot, "servers", id) + Path.DirectorySeparatorChar;
            string elsewhere = ServerLocation.ProcessFolder(id); // game files on another drive run from there
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string? file = p.MainModule?.FileName;
                    if (file != null && (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || file.StartsWith(elsewhere, StringComparison.OrdinalIgnoreCase))) { p.Kill(entireProcessTree: true); }
                }
                catch { /* no access / already gone */ }
                finally { p.Dispose(); }
            }
        });

        private static void ClearPidCache(string id)
        {
            try
            {
                ServerCache.SavePID(id, -1);
                ServerCache.SaveProcessName(id, string.Empty);
                ServerCache.SaveWindowsIntPtr(id, IntPtr.Zero);
            }
            catch { /* cache is best effort */ }
        }

        private static string? ValidateAddress(ServerConfig cfg)
        {
            string error = string.Empty;
            if (!string.IsNullOrWhiteSpace(cfg.ServerIP) && !IsValidIPv4(cfg.ServerIP)) { error += " IP address is not valid."; }
            if (!string.IsNullOrWhiteSpace(cfg.ServerPort) && !(int.TryParse(cfg.ServerPort, out int port) && port > 1 && port < 65535))
            {
                error += " Port number is not valid.";
            }
            return error.Length == 0 ? null : error;
        }

        private static bool IsValidIPv4(string ip)
        {
            string[] parts = ip.Split('.');
            return parts.Length == 4 && parts.All(r => byte.TryParse(r, out _));
        }

        private static string Notes(StartReason reason) => reason switch
        {
            StartReason.AutoStart => " | Auto Start",
            StartReason.AutoRestart => " | Auto Restart",
            _ => string.Empty,
        };

        /// <summary>The "Send Ctrl+C before the game's own stop" setting.</summary>
        public const string CtrlCFirstKey = "stopctrlcfirst";

        private static bool StopWithCtrlCFirst(ServerConfig cfg) => cfg.GetCustomSetting(CtrlCFirstKey, string.Empty) == "1";

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(); } catch { return "unknown"; }
        }

        private static string Describe(ServerState state) => state switch
        {
            ServerState.Running => "already running",
            ServerState.BackingUp => "backing up",
            ServerState.UpdatingAddons => "updating add-ons",
            _ => state.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Reads/writes members on plugin objects without `dynamic` blowing up when a plugin lacks one.</summary>
    internal static class Dyn
    {
        public static object? Get(object target, string name)
        {
            var type = target.GetType();
            var field = type.GetField(name);
            if (field != null) { return field.GetValue(target); }
            var prop = type.GetProperty(name);
            return prop?.CanRead == true ? prop.GetValue(target) : null;
        }

        public static bool TrySet(object target, string name, object value)
        {
            try
            {
                var type = target.GetType();
                var field = type.GetField(name);
                if (field != null && !field.IsInitOnly && !field.IsLiteral) { field.SetValue(target, value); return true; }
                var prop = type.GetProperty(name);
                if (prop?.CanWrite == true) { prop.SetValue(target, value); return true; }
            }
            catch { /* plugin chose a type we can't assign — leave it */ }
            return false;
        }
    }
}
