#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Engine.Services
{
    /// <summary>Result of an update check. Builds are null when unknown; <see cref="Error"/> is set if the check failed.</summary>
    public sealed record UpdateCheck(string? LocalBuild, string? RemoteBuild, bool BranchChangePending, bool UpdateAvailable, string? Error);

    /// <summary>
    /// Updating a server's game files (port of legacy GameServer_Update / Server_BeginUpdate).
    ///
    /// The plugin's own Update() does the work — for Steam games that ends in SteamCMD.UpdateEx, which
    /// <see cref="SteamContentPolicy"/> routes to DepotDownloader. Around that call the engine adds what
    /// legacy lacked: a job with live download progress, a shared limit on simultaneous downloads (instead
    /// of blocking every other server), and the operation gate.
    /// </summary>
    public sealed class UpdateService
    {
        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly PluginCatalog _plugins;
        private readonly ServerLog _log;

        public UpdateService(ServerRegistry servers, OperationGate gate, JobManager jobs, PluginCatalog plugins, ServerLog log)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _plugins = plugins;
            _log = log;
        }

        /// <summary>
        /// Is a newer build waiting? Compares the installed build with the latest one (Steam games ask Steam
        /// directly; others ask their plugin). A pending Steam branch change also counts as an update.
        /// </summary>
        public async Task<UpdateCheck> CheckAsync(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return new UpdateCheck(null, null, false, false, "No such server."); }
            dynamic? game = _plugins.Create(s.Game, s.Config);
            if (game == null) { return new UpdateCheck(null, null, false, false, $"Game '{s.Game}' isn't available on this machine."); }

            string appId = Dyn.Get((object)game, "AppId")?.ToString() ?? string.Empty;
            string local, remote;
            try
            {
                local = game.GetLocalBuild() ?? string.Empty;
                remote = string.IsNullOrWhiteSpace(appId)
                    ? (await game.GetRemoteBuild() ?? string.Empty)
                    : await new SteamCMD().GetRemoteBuild(appId, ServerConfig.GetSetting(s.Id, ServerConfig.SettingName.SteamBranch)).ConfigureAwait(false);
            }
            catch (Exception ex) { return new UpdateCheck(null, null, false, false, ex.Message); }

            bool branchChange = !string.IsNullOrWhiteSpace(appId) && SteamCMD.IsSteamBranchChangePending(s.Id);
            local = string.IsNullOrWhiteSpace(local) ? null! : local;
            remote = string.IsNullOrWhiteSpace(remote) ? null! : remote;
            bool available = branchChange || (local != null && remote != null && local != remote);
            return new UpdateCheck(local, remote, branchChange, available, null);
        }

        /// <summary>Setting (WindowsGSM.cfg): "1" = updates are on hold (after a roll back) — no auto-update or update on start.</summary>
        public const string HoldKey = "updatehold";

        public static bool IsHeld(ServerInstance s) => s.Config.GetCustomSetting(HoldKey, "") == "1";

        public void SetHold(string id, bool held)
        {
            var s = _servers.Get(id);
            if (s == null) { return; }
            ServerConfig.SetSetting(id, HoldKey, held ? "1" : "");
            s.ReloadConfig();
            _log.Write(id, held ? "Updates put on hold." : "Updates resumed.");
        }

        /// <summary>
        /// Puts an earlier build back (one of <see cref="DepotHistory.Builds"/>, by key) with DepotDownloader, then
        /// holds updates so auto-update doesn't undo it.
        /// </summary>
        public OperationRequest Rollback(string id, string buildKey)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} before rolling it back."); }
            var build = DepotHistory.Builds(id).FirstOrDefault(b => b.Key == buildKey);
            if (build == null) { return OperationRequest.Rejected("That build isn't in this server's history any more."); }
            if (build.Current) { return OperationRequest.Rejected("That build is already installed."); }
            if (!_gate.TryBegin(id, OperationKind.Update, "Roll back", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            string label = build.BuildId != null ? $"build {build.BuildId}" : $"the build from {build.InstalledAt.ToLocalTime():d MMM yyyy}";
            return OperationRequest.Running(_jobs.Start("rollback", id, $"Roll back {s.Name} to {label}", async ctx =>
            {
                using (lease) { return await RollbackCoreAsync(s, build, label, ctx, validate: false).ConfigureAwait(false); }
            }));
        }

        /// <summary>Tests: replaces DepotDownloader for roll back.</summary>
        public Func<string, string, IReadOnlyDictionary<uint, ulong>, bool, Task<(Process?, string?)>>? RollbackRunner { get; set; }

        private async Task<string?> RollbackCoreAsync(ServerInstance s, DepotHistory.Build build, string label, JobContext ctx, bool validate)
        {
            string id = s.Id;
            dynamic? game = _plugins.Create(s.Game, s.Config);
            if (game == null) { return $"Unknown game \"{s.Game}\"."; }
            string appId = Dyn.Get((object)game, "AppId")?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(appId)) { return "Only Steam games can be rolled back."; }
            object? anon = Dyn.Get((object)game, "loginAnonymous");
            bool anonymous = anon == null || (bool.TryParse(anon.ToString(), out bool a) && a);

            if (!validate) { DepotHistory.Record(id, BuildCache.Read(id)); } // remember what we're leaving, by build number
            s.SetState(ServerState.Updating);
            _log.Write(id, validate ? $"Action: Verify files of {label} (updates are on hold)" : $"Action: Roll back to {label} ({build.Depots.Count} depot(s))");
            try
            {
                ctx.Report(0, "Waiting for a download slot");
                await _gate.Downloads.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
                try
                {
                    ctx.Report(0, "Downloading the earlier build");
                    Process? p;
                    string? error;
                    using (DownloadContext.Use(ctx.Log, pct => ctx.Report(pct, "Downloading the earlier build")))
                    {
                        var run = RollbackRunner ?? (async (sid, app, depots, anonymousLogin) => await DepotDownloader.RollbackEx(sid, app, depots, anonymousLogin, validate).ConfigureAwait(false));
                        (p, error) = await run(id, appId, build.Depots, anonymous).ConfigureAwait(false);
                        if (p != null) { await p.WaitForExitAsync(ctx.Cancellation).ConfigureAwait(false); }
                    }
                    if (error == null && p != null && p.ExitCode != 0) { error = $"DepotDownloader exited with code {p.ExitCode}"; }
                    var now = DepotHistory.Installed(id).Where(d => build.Depots.ContainsKey(d.Key)).ToDictionary(d => d.Key, d => d.Value);
                    if (error == null && DepotHistory.Key(now) != build.Key)
                    {
                        error = "DepotDownloader finished, but the installed files aren't that build — Steam may no longer offer it.";
                    }
                    if (error != null)
                    {
                        _log.Write(id, (validate ? "[ERROR] Verify failed: " : "[ERROR] Roll back failed: ") + error);
                        return error;
                    }
                    if (build.BuildId != null) { BuildCache.Write(id, build.BuildId); }
                    ServerConfig.SetSetting(id, HoldKey, "1");
                    s.ReloadConfig();
                    _log.Write(id, validate ? $"Server: Verified {label}." : $"Server: Rolled back to {label}. Updates are on hold until you update by hand or resume them.");
                    return null;
                }
                finally { _gate.Downloads.Release(); }
            }
            finally { s.SetState(ServerState.Stopped); }
        }

        /// <summary>Updates (or with <paramref name="validate"/>, verifies and repairs) a stopped server.</summary>
        public OperationRequest Update(string id, bool validate = false)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} before updating it."); }
            if (!_gate.TryBegin(id, OperationKind.Update, validate ? "Validate" : "Update", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            // Rolled back: verifying must check the held build — a plain validate would install the newest one.
            var installed = validate && IsHeld(s) ? DepotHistory.Installed(id) : null;
            if (installed is { Count: > 0 })
            {
                var held = new DepotHistory.Build(DepotHistory.Key(installed), DateTimeOffset.UtcNow, installed, BuildCache.Read(id), true);
                string label = string.IsNullOrEmpty(held.BuildId) ? "the held build" : $"build {held.BuildId}";
                return OperationRequest.Running(_jobs.Start("validate", id, $"Validate {s.Name}", async ctx =>
                {
                    using (lease) { return await RollbackCoreAsync(s, held, label, ctx, validate: true).ConfigureAwait(false); }
                }));
            }

            return OperationRequest.Running(_jobs.Start(validate ? "validate" : "update", id, $"{(validate ? "Validate" : "Update")} {s.Name}", async ctx =>
            {
                using (lease)
                {
                    string? error = await UpdateCoreAsync(s, validate, ctx, notes: string.Empty).ConfigureAwait(false);
                    // Updating by hand ends a roll back's hold (scheduled and automatic updates never run while held).
                    if (error == null && !validate && IsHeld(s)) { ServerConfig.SetSetting(s.Id, HoldKey, ""); s.ReloadConfig(); _log.Write(s.Id, "Updates resumed."); }
                    return error;
                }
            }));
        }

        /// <summary>
        /// The update itself. The caller must already hold the server's operation gate (a user Update job,
        /// or a Start job running "update on start"). Returns an error message, or null on success.
        /// </summary>
        internal async Task<string?> UpdateCoreAsync(ServerInstance s, bool validate, JobContext job, string notes)
        {
            s.ReloadConfig();
            dynamic? game = _plugins.Create(s.Game, s.Config);
            if (game == null) { return $"Unknown game \"{s.Game}\" — is its plugin installed and loading?"; }

            string appId = Dyn.Get((object)game, "AppId")?.ToString() ?? string.Empty;
            bool steam = !string.IsNullOrWhiteSpace(appId);
            string branch = steam ? BranchName(s.Id) : string.Empty;

            if (steam) { DepotHistory.Record(s.Id, BuildCache.Read(s.Id)); } // name the build we're leaving, for roll back
            s.SetState(ServerState.Updating);
            _log.Write(s.Id, "Action: Update" + (steam ? $" | Steam branch {branch}" : string.Empty) + notes);

            try
            {
                // One tool decision per operation; SteamCMD additionally never runs twice at once.
                var tool = steam ? SteamContentPolicy.Choose(s.Id) : SteamContentTool.DepotDownloader;
                job.Report(0, steam ? $"Waiting for a download slot ({tool})" : "Waiting for a download slot");
                await _gate.Downloads.WaitAsync(job.Cancellation).ConfigureAwait(false);
                bool steamCmdHeld = false;
                try
                {
                    if (steam && tool == SteamContentTool.SteamCMD)
                    {
                        await _gate.SteamCmd.WaitAsync(job.Cancellation).ConfigureAwait(false);
                        steamCmdHeld = true;
                    }

                    job.Report(0, steam ? $"Downloading with {tool}" : "Updating");
                    Process? p;
                    using (DownloadContext.Use(job.Log, pct => job.Report(pct, steam ? $"Downloading with {tool}" : "Updating")))
                    {
                        try { p = await game.Update(validate, null); }
                        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                        {
                            // Older plugins only have Update() without (validate, custom).
                            p = await game.Update();
                        }
                        if (p != null) { await p.WaitForExitAsync(job.Cancellation).ConfigureAwait(false); }
                    }

                    // Same success rules as legacy: no process and no error = a non-Steam plugin that updated
                    // itself; otherwise the updater process must exit cleanly.
                    string pluginError = Dyn.Get((object)game, "Error") as string ?? string.Empty;
                    string? error = p == null
                        ? (string.IsNullOrEmpty(pluginError) ? null : pluginError)
                        : (p.ExitCode == 0 ? null : $"{(steam ? tool.ToString() : "The updater")} exited with code {p.ExitCode}");

                    if (error != null)
                    {
                        _log.Write(s.Id, "Server: Fail to update");
                        _log.Write(s.Id, "[ERROR] " + error);
                        return error;
                    }

                    if (steam) { SteamCMD.MarkSteamBranchInstalled(s.Id); }
                    if (steam && tool == SteamContentTool.DepotDownloader)
                    {
                        // The build number is also recorded when the process exits, but in the background; wait for it here.
                        try { await SteamContentPolicy.RecordBuildAsync(s.Id, appId).ConfigureAwait(false); } catch { /* a nicety */ }
                    }
                    string build = steam ? BuildCache.Read(s.Id) : string.Empty;
                    if (steam) { DepotHistory.Record(s.Id, build); }
                    _log.Write(s.Id, $"Server: Updated {(validate ? "Validate " : string.Empty)}{(steam ? $"Steam branch {branch} " : string.Empty)}{(string.IsNullOrEmpty(build) ? string.Empty : $"to build {build}")}".TrimEnd());
                    return null;
                }
                finally
                {
                    if (steamCmdHeld) { _gate.SteamCmd.Release(); }
                    _gate.Downloads.Release();
                }
            }
            finally
            {
                s.SetState(ServerState.Stopped);
            }
        }

        private static string BranchName(string serverId)
        {
            string branch = ServerConfig.GetSetting(serverId, ServerConfig.SettingName.SteamBranch);
            return string.IsNullOrWhiteSpace(branch) ? "public" : branch;
        }
    }

    /// <summary>"Update on start": runs before every start/restart/auto-restart when the server has it enabled.</summary>
    public sealed class UpdateOnStartStep : IPreStartStep
    {
        private readonly UpdateService _updates;
        public UpdateOnStartStep(UpdateService updates) => _updates = updates;

        public string Name => "Update on start";

        public async Task RunAsync(ServerInstance server, StartReason reason, JobContext job)
        {
            if (!server.Config.UpdateOnStart) { return; }
            if (UpdateService.IsHeld(server)) { job.Log("Update on start skipped: updates are on hold (rolled back)."); return; }
            string? error = await _updates.UpdateCoreAsync(server, validate: false, job, notes: " | Update on Start").ConfigureAwait(false);
            if (error != null) { job.Log("Update on start failed: " + error + " — starting anyway."); }
        }
    }
}
