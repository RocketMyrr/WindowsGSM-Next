using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// Per-server record of which built-in add-ons (Oxide, Carbon, SourceMod, …) were installed
    /// through WindowsGSM's own installer — as opposed to files that merely happen to be on disk
    /// (e.g. an Oxide provided by a custom add-on). "Update Addons on Start" only touches add-ons
    /// listed here, so it never clobbers a build the user installed themselves via a custom add-on.
    /// Stored at servers/{id}/configs/installedaddons.json.
    /// </summary>
    public static class InstalledAddonStore
    {
        private static string ConfigFile(string serverId) => ServerPath.GetServersConfigs(serverId, "installedaddons.json");

        public static HashSet<string> Load(string serverId)
        {
            var keys = global::WindowsGSM.Hosting.SafeJson.ReadWith(ConfigFile(serverId), text => JsonConvert.DeserializeObject<List<string>>(text, global::WindowsGSM.Hosting.SafeJson.LenientNewtonsoft()));
            return new HashSet<string>((keys ?? new List<string>()).Where(k => k != null), System.StringComparer.OrdinalIgnoreCase);
        }

        private static void Save(string serverId, HashSet<string> keys)
        {
            try
            {
                global::WindowsGSM.Hosting.SafeJson.WriteText(ConfigFile(serverId), JsonConvert.SerializeObject(keys.ToList(), Formatting.Indented));
            }
            catch { /* best effort */ }
        }

        /// <summary>Record that a built-in add-on was installed via WindowsGSM.</summary>
        public static void Mark(string serverId, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return; }
            var set = Load(serverId);
            if (set.Add(key)) { Save(serverId, set); }
        }

        /// <summary>Stop treating a built-in add-on as WindowsGSM-managed (e.g. replaced by a custom add-on).</summary>
        public static void Unmark(string serverId, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { return; }
            var set = Load(serverId);
            if (set.Remove(key)) { Save(serverId, set); }
        }

        public static bool Has(string serverId, string key) => Load(serverId).Contains(key);
    }
}
