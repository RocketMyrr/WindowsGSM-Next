#nullable enable
using System;
using System.Threading;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// Where download output and progress should go for the operation currently running on this logical
    /// call chain. Plugins call <c>SteamCMD.UpdateEx(...)</c> without any progress callback, so the engine
    /// sets a context around the plugin call (see <see cref="Use"/>) and the downloaders report into it —
    /// live progress bars and logs without changing the plugin API.
    ///
    /// It flows through awaits (AsyncLocal) and is captured when a download process starts, because output
    /// arrives later on the process's reader threads.
    /// </summary>
    public static class DownloadContext
    {
        public sealed class Sink
        {
            public Sink(Action<string> onLine, Action<int> onProgress) { OnLine = onLine; OnProgress = onProgress; }
            public Action<string> OnLine { get; }
            public Action<int> OnProgress { get; }
        }

        private static readonly AsyncLocal<Sink?> _current = new AsyncLocal<Sink?>();

        public static Sink? Current => _current.Value;

        /// <summary>Routes download output/progress for everything started inside the returned scope.</summary>
        public static IDisposable Use(Action<string> onLine, Action<int> onProgress)
        {
            var previous = _current.Value;
            _current.Value = new Sink(onLine, onProgress);
            return new Scope(() => _current.Value = previous);
        }

        private sealed class Scope : IDisposable
        {
            private Action? _restore;
            public Scope(Action restore) => _restore = restore;
            public void Dispose() { Interlocked.Exchange(ref _restore, null)?.Invoke(); }
        }
    }
}
