#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WindowsGSM.Engine.Operations
{
    public enum OperationKind { Install, Import, Start, Stop, Restart, Kill, Update, Backup, Restore, Delete, Addon }

    /// <summary>What's currently running on a server (or across all servers, for a global operation).</summary>
    public sealed record OperationInfo(string? ServerId, OperationKind Kind, string Description, DateTimeOffset StartedAt);

    /// <summary>
    /// Stops conflicting operations from overlapping — you can't start a server while it's updating.
    ///
    /// NEXT: the legacy gate was globally serial: while ANY server had an operation running, every other
    /// server was refused too (so one server's long update blocked starting a different one). That mostly
    /// protected SteamCMD, which can't run twice at once. Here the lock is per server; a global operation
    /// (e.g. "start all") still excludes everything; and heavy downloads share <see cref="Downloads"/>, a
    /// small concurrency limit, instead of a lock on the whole app.
    /// </summary>
    public sealed class OperationGate
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, OperationInfo> _running = new Dictionary<string, OperationInfo>(StringComparer.OrdinalIgnoreCase);
        private OperationInfo? _global;

        /// <summary>Limits simultaneous installs/updates (disk and bandwidth). Two by default.</summary>
        public SemaphoreSlim Downloads { get; } = new SemaphoreSlim(2, 2);

        /// <summary>SteamCMD can only ever run once at a time (single install, shared lock files).</summary>
        public SemaphoreSlim SteamCmd { get; } = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Claims <paramref name="serverId"/> for an operation. Kill always wins: it interrupts whatever is
        /// running on that server (the interrupted operation finds out when its own lease no longer matches).
        /// </summary>
        public bool TryBegin(string serverId, OperationKind kind, string description, out IDisposable? lease, out string? blockedBy)
        {
            lease = null;
            blockedBy = null;
            var info = new OperationInfo(serverId, kind, description, DateTimeOffset.UtcNow);

            lock (_lock)
            {
                if (_global != null)
                {
                    blockedBy = _global.Description;
                    return false;
                }
                if (_running.TryGetValue(serverId, out var current) && kind != OperationKind.Kill)
                {
                    blockedBy = current.Description;
                    return false;
                }
                _running[serverId] = info;
            }

            lease = new Lease(() =>
            {
                lock (_lock)
                {
                    // Only release our own claim — a Kill may have replaced it.
                    if (_running.TryGetValue(serverId, out var now) && ReferenceEquals(now, info)) { _running.Remove(serverId); }
                }
            });
            return true;
        }

        /// <summary>Claims every server at once (bulk operations). Fails if anything is running.</summary>
        public bool TryBeginGlobal(OperationKind kind, string description, out IDisposable? lease, out string? blockedBy)
        {
            lease = null;
            blockedBy = null;
            var info = new OperationInfo(null, kind, description, DateTimeOffset.UtcNow);

            lock (_lock)
            {
                if (_global != null) { blockedBy = _global.Description; return false; }
                if (_running.Count > 0) { blockedBy = _running.Values.OrderBy(o => o.StartedAt).First().Description; return false; }
                _global = info;
            }

            lease = new Lease(() => { lock (_lock) { if (ReferenceEquals(_global, info)) { _global = null; } } });
            return true;
        }

        public OperationInfo? Current(string serverId)
        {
            lock (_lock) { return _running.TryGetValue(serverId, out var info) ? info : _global; }
        }

        public bool IsBusy(string serverId) => Current(serverId) != null;

        private sealed class Lease : IDisposable
        {
            private Action? _release;
            public Lease(Action release) => _release = release;
            public void Dispose() { Interlocked.Exchange(ref _release, null)?.Invoke(); }
        }
    }
}
