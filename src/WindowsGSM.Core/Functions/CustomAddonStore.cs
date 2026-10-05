using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace WindowsGSM.Functions
{
    /// <summary>A saved custom add-on (a zip URL the user installs into a server's files).</summary>
    public class CustomAddon
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public string Subfolder { get; set; }
    }

    /// <summary>Per-server list of saved custom add-ons (servers/{id}/configs/customaddons.json).</summary>
    public static class CustomAddonStore
    {
        private static string ConfigFile(string serverId) => ServerPath.GetServersConfigs(serverId, "customaddons.json");

        public static List<CustomAddon> Load(string serverId)
        {
            return global::WindowsGSM.Hosting.SafeJson.ReadWith(ConfigFile(serverId), text => JsonConvert.DeserializeObject<List<CustomAddon>>(text, global::WindowsGSM.Hosting.SafeJson.LenientNewtonsoft()))
                ?? new List<CustomAddon>();
        }

        private static void Save(string serverId, List<CustomAddon> list)
        {
            try
            {
                global::WindowsGSM.Hosting.SafeJson.WriteText(ConfigFile(serverId), JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch { /* best effort */ }
        }

        /// <summary>Adds or updates a saved add-on (deduped by URL+subfolder). Returns it.</summary>
        public static CustomAddon Upsert(string serverId, string name, string url, string subfolder)
        {
            var list = Load(serverId);
            var existing = list.FirstOrDefault(a =>
                string.Equals(a.Url, url, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Subfolder ?? "", subfolder ?? "", StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = name;
                Save(serverId, list);
                return existing;
            }
            var addon = new CustomAddon { Id = Guid.NewGuid().ToString("N"), Name = name, Url = url, Subfolder = subfolder };
            list.Add(addon);
            Save(serverId, list);
            return addon;
        }

        public static CustomAddon Get(string serverId, string addonId) =>
            Load(serverId).FirstOrDefault(a => a.Id == addonId);

        public static bool Remove(string serverId, string addonId)
        {
            var list = Load(serverId);
            if (list.RemoveAll(a => a.Id == addonId) > 0) { Save(serverId, list); return true; }
            return false;
        }
    }
}
