#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace WindowsGSM.Installer
{
    /// <summary>One branch of a Steam app, as Steam reports it to an anonymous user.</summary>
    public sealed record SteamBranch(string Name, string? BuildId, bool PasswordRequired, DateTimeOffset? UpdatedAt, string? Description);

    /// <summary>
    /// Asks Steam directly (anonymous login, product-info query) about an app: the current build id on a
    /// branch, and the list of branches.
    ///
    /// This replaces launching steamcmd.exe for every "is there an update?" check and every branch list — the
    /// legacy lookup ran steamcmd with a workaround (delete appinfo.vdf, print the app info four times) because
    /// steamcmd's cache often returned stale data. SteamKit2 is the library DepotDownloader itself is built on,
    /// so with this, SteamCMD is off the normal install/update/check path entirely.
    /// </summary>
    public static class SteamAppInfo
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
        private static readonly ConcurrentDictionary<uint, (IReadOnlyList<SteamBranch> branches, DateTimeOffset at)> _cache =
            new ConcurrentDictionary<uint, (IReadOnlyList<SteamBranch>, DateTimeOffset)>();

        /// <summary>
        /// The build id of <paramref name="appId"/> on <paramref name="branch"/> ("public" if blank), or null if
        /// Steam didn't say (unknown app, hidden/password branch). Throws on connection failure or timeout so
        /// callers can fall back.
        /// </summary>
        public static async Task<string?> GetBuildIdAsync(string appId, string? branch = null, TimeSpan? timeout = null, CancellationToken cancellation = default)
        {
            string branchName = string.IsNullOrWhiteSpace(branch) ? "public" : branch.Trim();
            var branches = await GetBranchesAsync(appId, timeout, cancellation).ConfigureAwait(false);
            return branches.FirstOrDefault(b => string.Equals(b.Name, branchName, StringComparison.OrdinalIgnoreCase))?.BuildId;
        }

        /// <summary>
        /// Every branch Steam lists for the app ("public" first, then newest first). Password-protected branches
        /// are listed but have no build id. Throws on connection failure or timeout.
        /// </summary>
        public static async Task<IReadOnlyList<SteamBranch>> GetBranchesAsync(string appId, TimeSpan? timeout = null, CancellationToken cancellation = default)
        {
            if (!uint.TryParse(appId, out uint app)) { throw new ArgumentException($"Not a Steam app id: {appId}", nameof(appId)); }
            if (_cache.TryGetValue(app, out var hit) && DateTimeOffset.UtcNow - hit.at < CacheFor) { return hit.branches; }

            var depots = await GetDepotsSectionAsync(app, timeout, cancellation).ConfigureAwait(false);
            var list = new List<SteamBranch>();
            if (depots != null)
            {
                foreach (var b in depots["branches"].Children)
                {
                    if (string.IsNullOrWhiteSpace(b.Name)) { continue; }
                    string? build = b["buildid"] == KeyValue.Invalid ? null : b["buildid"].Value;
                    bool password = b["pwdrequired"] != KeyValue.Invalid && b["pwdrequired"].Value == "1";
                    DateTimeOffset? updated = long.TryParse(b["timeupdated"].Value, out long unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
                    string? description = b["description"] == KeyValue.Invalid ? null : b["description"].Value;
                    list.Add(new SteamBranch(b.Name!, string.IsNullOrWhiteSpace(build) ? null : build, password, updated, description));
                }
            }
            var ordered = list
                .OrderByDescending(b => string.Equals(b.Name, "public", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(b => b.UpdatedAt ?? DateTimeOffset.MinValue)
                .ToList();
            if (depots != null) { _cache[app] = (ordered, DateTimeOffset.UtcNow); }
            return ordered;
        }

        private static async Task<KeyValue?> GetDepotsSectionAsync(uint app, TimeSpan? timeout, CancellationToken cancellation)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

            var client = new SteamClient();
            var manager = new CallbackManager(client);
            var user = client.GetHandler<SteamUser>()!;
            var apps = client.GetHandler<SteamApps>()!;

            var loggedOn = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.Subscribe<SteamClient.ConnectedCallback>(_ => user.LogOnAnonymous());
            manager.Subscribe<SteamClient.DisconnectedCallback>(_ => loggedOn.TrySetException(new IOException("Disconnected from Steam before logging on.")));
            manager.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb.Result));

            // SteamKit2 delivers callbacks only while someone pumps them.
            var pumping = new CancellationTokenSource();
            var pump = Task.Run(() =>
            {
                while (!pumping.IsCancellationRequested) { manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            });

            try
            {
                client.Connect();
                EResult result = await loggedOn.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                if (result != EResult.OK) { throw new IOException($"Steam anonymous logon failed: {result}"); }

                // Some apps only reveal full info with an access token (free for anonymous users).
                var tokens = await apps.PICSGetAccessTokens(app, null).ToTask().WaitAsync(cts.Token).ConfigureAwait(false);
                ulong token = tokens.AppTokens.TryGetValue(app, out ulong t) ? t : 0;

                var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(app, token), null).ToTask().WaitAsync(cts.Token).ConfigureAwait(false);
                if (info.Results == null) { return null; }

                foreach (var callback in info.Results)
                {
                    if (callback.Apps.TryGetValue(app, out var appInfo)) { return appInfo.KeyValues["depots"]; }
                }
                return null;
            }
            finally
            {
                try { user.LogOff(); } catch { /* not logged on */ }
                try { client.Disconnect(); } catch { /* not connected */ }
                pumping.Cancel();
                try { await pump.ConfigureAwait(false); } catch { /* pump ends either way */ }
            }
        }
    }
}
