#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// Saving the world before a stop, restart or update. Many games' plugins just end the process, which loses
    /// whatever happened since the game's last autosave (often 10–30 minutes). Before stopping, WindowsGSM sends the
    /// game's save command (known for common games; any server can set its own, or none) and gives it a moment.
    /// Settings (WindowsGSM.cfg): savecommand ("" = the known one, "-" = don't), savewait (seconds, default 10),
    /// stoptimeout (seconds to wait for a clean shutdown before ending it, default 30).
    /// </summary>
    public static class WorldSave
    {
        public const string CommandKey = "savecommand", WaitKey = "savewait", StopTimeoutKey = "stoptimeout";

        /// <summary>Known save commands by dedicated-server Steam app id (and a few by game name).</summary>
        public static readonly IReadOnlyDictionary<string, (string Command, int Wait)> Known = new Dictionary<string, (string, int)>
        {
            ["258550"] = ("server.save", 10),      // Rust
            ["376030"] = ("saveworld", 15),        // ARK: Survival Evolved
            ["2430930"] = ("SaveWorld", 15),       // ARK: Survival Ascended
            ["294420"] = ("saveworld", 10),        // 7 Days to Die
            ["2394010"] = ("Save", 10),            // Palworld
            ["380870"] = ("save", 10),             // Project Zomboid
            ["105600"] = ("save", 10),             // Terraria
            ["1110390"] = ("save", 5),             // Unturned
            ["443030"] = ("", 0),                  // Conan Exiles: saves on shutdown
        };

        /// <summary>The save command for a server and how long to wait after it ("" = none).</summary>
        public static (string Command, int Wait) For(ServerInstance s, string? appId)
        {
            string custom = s.Config.GetCustomSetting(CommandKey, "").Trim();
            int wait = int.TryParse(s.Config.GetCustomSetting(WaitKey, ""), out int w) ? Math.Clamp(w, 0, 300) : -1;
            if (custom == "-") { return ("", 0); }
            if (custom.Length > 0) { return (custom, wait >= 0 ? wait : 10); }
            if (appId != null && Known.TryGetValue(appId, out var k)) { return (k.Command, wait >= 0 ? wait : k.Wait); }
            if (s.Game.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0) { return ("save-all", wait >= 0 ? wait : 5); }
            return ("", 0);
        }

        /// <summary>How long to wait for a clean shutdown before ending the process (seconds).</summary>
        public static int StopTimeout(ServerInstance s) =>
            int.TryParse(s.Config.GetCustomSetting(StopTimeoutKey, ""), out int t) ? Math.Clamp(t, 5, 600) : 30;

        /// <summary>Sends the save command (if any) and waits. Never throws; a failed save doesn't block the stop.</summary>
        public static async Task RunAsync(ServerInstance s, string? appId, ConsoleService console, ServerLog log)
        {
            try
            {
                var (command, wait) = For(s, appId);
                if (command.Length == 0) { return; }
                log.Write(s.Id, $"Saving the world before stopping ({command})…");
                var sent = await console.SendAsync(s.Id, command, "WindowsGSM").ConfigureAwait(false);
                if (!sent.Sent)
                {
                    log.Write(s.Id, $"[NOTICE] Couldn't send the save command: {sent.Error} — stopping without it.");
                    return;
                }
                if (wait > 0) { await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false); }
            }
            catch (Exception ex) { log.Write(s.Id, $"[NOTICE] Save before stop failed: {ex.Message}"); }
        }
    }
}
