#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// Yes/no questions a plugin needs answered mid-operation (e.g. "accept the EULA?", "install Java?").
    /// The legacy app popped a dialog on the server's desktop, which can't work for an engine with no UI
    /// that's controlled from a browser or another machine.
    ///
    /// Instead, the person starting the operation answers up front: the install wizard shows the consents a
    /// game needs and the engine runs the operation inside <see cref="WithConsents"/>. A question whose key
    /// wasn't granted goes to <see cref="AsyncHandler"/> — the hook the engine uses to put the question in
    /// front of a user live — and failing that is answered "no", so an unattended install can never
    /// silently accept a licence on someone's behalf.
    /// </summary>
    public static class UserPrompt
    {
        /// <summary>Well-known consent keys. The UI lists these so it can ask before starting.</summary>
        public static class Keys
        {
            /// <summary>Any game EULA / licence agreement (Minecraft and friends, built-in or plugin).</summary>
            public const string Eula = "eula";
            public const string InstallJava = "install-java";
        }

        private static readonly AsyncLocal<HashSet<string>?> _granted = new AsyncLocal<HashSet<string>?>();

        /// <summary>
        /// Asked when a key wasn't granted up front: (key, title, message) → answer. Null (the default) means
        /// "no". The engine sets this to surface the question to whoever is watching the operation.
        /// </summary>
        public static Func<string, string, string, Task<bool>>? AsyncHandler { get; set; }

        public static bool Confirm(string key, string title, string message) =>
            ConfirmAsync(key, title, message).GetAwaiter().GetResult();

        public static async Task<bool> ConfirmAsync(string key, string title, string message)
        {
            var granted = _granted.Value;
            if (granted != null && granted.Contains(key)) { return true; }
            var handler = AsyncHandler;
            if (handler == null) { return false; }
            try { return await handler(key, title, message).ConfigureAwait(false); }
            catch { return false; }
        }

        /// <summary>Consent key for a free-form plugin question: EULA-style titles share the EULA consent.</summary>
        public static string KeyForTitle(string title) =>
            title != null && (title.IndexOf("EULA", StringComparison.OrdinalIgnoreCase) >= 0
                           || title.IndexOf("licen", StringComparison.OrdinalIgnoreCase) >= 0)
                ? Keys.Eula
                : "prompt:" + (title ?? string.Empty).Trim().ToLowerInvariant();

        /// <summary>
        /// Grants consents for everything that runs (and awaits) inside the returned scope — it flows through
        /// async calls on this logical operation only, never to concurrent operations for other servers.
        /// </summary>
        public static IDisposable WithConsents(IEnumerable<string>? keys)
        {
            var previous = _granted.Value;
            _granted.Value = keys == null ? null : new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            return new Scope(() => _granted.Value = previous);
        }

        private sealed class Scope : IDisposable
        {
            private Action? _onDispose;
            public Scope(Action onDispose) => _onDispose = onDispose;
            public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
        }
    }
}
