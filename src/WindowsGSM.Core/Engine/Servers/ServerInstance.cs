#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using WindowsGSM.Engine.Events;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Servers
{
    /// <summary>
    /// Where a server is in its life. Anything other than <see cref="Stopped"/> and <see cref="Running"/> is a
    /// transition an operation is currently driving.
    /// </summary>
    public enum ServerState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Restarting,
        Installing,
        Updating,
        UpdatingAddons,
        BackingUp,
        Restoring,
        Deleting,
        /// <summary>NEXT: its game files are being moved to another folder or drive.</summary>
        Moving,
    }

    /// <summary>
    /// One game server: its saved config plus everything the engine knows about it at run time. This is the
    /// single source of truth that the legacy app kept spread across the WPF grid row and ServerMetadata.
    /// UIs never touch it directly — they read snapshots and listen to events.
    /// </summary>
    public sealed class ServerInstance
    {
        private readonly object _gate = new object();
        private readonly EventBus _events;

        internal ServerInstance(string id, EventBus events)
        {
            Id = id;
            _events = events;
            Config = new ServerConfig(id);
        }

        public string Id { get; }

        /// <summary>The saved configuration (WindowsGSM.cfg). Call <see cref="ReloadConfig"/> after changing it.</summary>
        public ServerConfig Config { get; private set; }

        public string Name => Config.ServerName ?? $"Server #{Id}";
        public string Game => Config.ServerGame ?? string.Empty;

        /// <summary>The embedded console buffer (game output), shared with plugins.</summary>
        public ServerConsole Console => ServerConsole.For(Id);

        public ServerState State { get { lock (_gate) { return _state; } } }
        private ServerState _state = ServerState.Stopped;

        /// <summary>The running game process, or null. Replaced/cleared by the lifecycle service only.</summary>
        public Process? Process { get { lock (_gate) { return _process; } } }
        private Process? _process;

        /// <summary>The game's own console window (for servers driven by typing into it). Zero if none.</summary>
        public IntPtr ConsoleWindow { get; internal set; }

        /// <summary>Whether the game's own console window is showing on this machine's screen.</summary>
        public bool ConsoleWindowVisible { get; internal set; }

        /// <summary>
        /// Picked up again after the agent restarted (an update, or Stop/Start agent): the game kept running, but a
        /// captured console's pipe can't be reconnected — no output or typed commands until the server restarts.
        /// </summary>
        public bool Reattached { get; internal set; }

        public DateTimeOffset? StartedAt { get; internal set; }

        // ── Watchdog state ──
        internal List<DateTimeOffset> RecentCrashes { get; } = new List<DateTimeOffset>();

        /// <summary>True after too many crashes in a short time; auto-restart stays off until a manual start.</summary>
        public bool CrashLoopSuspended { get; internal set; }

        /// <summary>When the process first went over the memory-guard threshold (null = currently under).</summary>
        public DateTimeOffset? MemoryOverThresholdSince { get; internal set; }

        public void ReloadConfig() => Config = new ServerConfig(Id);

        /// <summary>Moves to <paramref name="to"/> and publishes the change (no-op if already there).</summary>
        internal void SetState(ServerState to)
        {
            ServerState from;
            lock (_gate)
            {
                from = _state;
                if (from == to) { return; }
                _state = to;
            }
            _events.Publish(new ServerStateChanged(Id, from, to));
        }

        internal void SetProcess(Process? process)
        {
            lock (_gate) { _process = process; }
        }

        /// <summary>
        /// Clears the process only if it's still <paramref name="expected"/> — used when stopping, so an
        /// exit handler can tell "this process went away because we stopped it" from a crash.
        /// </summary>
        internal bool ClearProcessIf(Process expected)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_process, expected)) { return false; }
                _process = null;
                return true;
            }
        }

        /// <summary>Resets crash-loop and memory-guard state (an explicit start gets a clean slate).</summary>
        internal void ResetWatchdogState()
        {
            lock (_gate)
            {
                RecentCrashes.Clear();
                CrashLoopSuspended = false;
                MemoryOverThresholdSince = null;
            }
        }
    }
}
