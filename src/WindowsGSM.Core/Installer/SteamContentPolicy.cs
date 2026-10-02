#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WindowsGSM.Installer
{
    public enum SteamContentTool { DepotDownloader, SteamCMD }

    /// <summary>
    /// The one place that decides which tool installs and updates Steam game servers.
    ///
    /// DepotDownloader is the standard for every install AND every update. The legacy app decided this in
    /// two different places with two different defaults: installs followed a global setting (default:
    /// DepotDownloader), but updates followed a per-server flag that defaults to OFF for any server created
    /// before that setting existed — so older servers silently kept updating through SteamCMD.
    ///
    /// SteamCMD is only used when:
    ///   • the operation downloads a Workshop item (DepotDownloader can't, via this path), or
    ///   • a server explicitly opts out with the <see cref="SteamCmdOverrideSetting"/> setting — an escape
    ///     hatch for the odd game that misbehaves under DepotDownloader. The legacy "depotdownloader" flag
    ///     is deliberately ignored, because "0/absent" there never meant "the user chose SteamCMD".
    /// </summary>
    public static class SteamContentPolicy
    {
        /// <summary>Per-server setting (WindowsGSM.cfg): steamcmd_override="1" forces SteamCMD for that server.</summary>
        public const string SteamCmdOverrideSetting = "steamcmd_override";

        public static SteamContentTool Choose(string serverId, string? workshopItem = null)
        {
            if (!string.IsNullOrEmpty(workshopItem)) { return SteamContentTool.SteamCMD; }
            return ForcesSteamCmd(serverId) ? SteamContentTool.SteamCMD : SteamContentTool.DepotDownloader;
        }

        public static bool ForcesSteamCmd(string serverId)
        {
            if (string.IsNullOrEmpty(serverId)) { return false; }
            var cfg = new Functions.ServerConfig(serverId);
            return cfg.CustomSettings.TryGetValue(SteamCmdOverrideSetting, out var v) && v == "1";
        }

        /// <summary>
        /// DepotDownloader doesn't write Steam's appmanifest, so the installed build number has to be recorded
        /// separately (BuildCache) or update checks can't tell what's installed. Legacy only did this after
        /// updates, never after the initial install. Call with the running install/update process.
        /// </summary>
        public static void RecordBuildWhenFinished(Process? process, string serverId, string appId)
        {
            if (process == null) { return; }
            try { process.EnableRaisingEvents = true; } catch { /* already started with events on */ }
            process.Exited += async (_, __) =>
            {
                try
                {
                    if (process.ExitCode != 0) { return; }
                    await RecordBuildAsync(serverId, appId);
                }
                catch (Exception ex) { Debug.WriteLine($"[SteamContentPolicy] couldn't record build for {serverId}: {ex.Message}"); }
            };
        }

        public static async Task RecordBuildAsync(string serverId, string appId)
        {
            // The server's own branch: a beta's build recorded as the public one never matches the update check,
            // so auto-update would reinstall it every 30 minutes.
            string branch = SteamCMD.GetConfiguredSteamBranch(serverId); // includes a branch picked for an install in progress
            string remote = await new SteamCMD().GetRemoteBuild(appId, string.IsNullOrWhiteSpace(branch) ? null : branch.Trim());
            if (!string.IsNullOrWhiteSpace(remote)) { Functions.BuildCache.Write(serverId, remote); }
        }
    }
}
