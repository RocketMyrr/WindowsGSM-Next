#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WindowsGSM.Engine.Events;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Servers
{
    /// <summary>
    /// Every configured server, loaded from the data folder (servers/{id}/configs/WindowsGSM.cfg) — the same
    /// layout the legacy app uses, so an existing data folder is adopted as-is.
    /// </summary>
    public sealed class ServerRegistry
    {
        private readonly ConcurrentDictionary<string, ServerInstance> _servers =
            new ConcurrentDictionary<string, ServerInstance>(StringComparer.OrdinalIgnoreCase);
        private readonly EventBus _events;

        public ServerRegistry(EventBus events) => _events = events;

        /// <summary>(Re)scans the data folder. Servers already known keep their run-time state.</summary>
        public void LoadAll()
        {
            string serversDir = Path.Combine(WgsmEnvironment.DataRoot, "servers");
            if (!Directory.Exists(serversDir)) { return; }

            foreach (string dir in Directory.EnumerateDirectories(serversDir))
            {
                string id = Path.GetFileName(dir);
                if (!int.TryParse(id, out int n) || n < 1 || n > WgsmEnvironment.MaxServers) { continue; }
                if (!File.Exists(Path.Combine(dir, "configs", "WindowsGSM.cfg"))) { continue; }
                _servers.GetOrAdd(id, key => new ServerInstance(key, _events));
            }
        }

        public ServerInstance? Get(string id) => id != null && _servers.TryGetValue(id, out var s) ? s : null;

        /// <summary>All servers, ordered by numeric id.</summary>
        public IReadOnlyList<ServerInstance> All =>
            _servers.Values.OrderBy(s => int.TryParse(s.Id, out int n) ? n : int.MaxValue).ToList();

        /// <summary>Registers a server whose config was just written (e.g. after an install or import).</summary>
        public ServerInstance Add(string id)
        {
            var added = false;
            var instance = _servers.GetOrAdd(id, key => { added = true; return new ServerInstance(key, _events); });
            if (added) { _events.Publish(new ServerListChanged(id, Removed: false)); }
            return instance;
        }

        public bool Remove(string id)
        {
            if (!_servers.TryRemove(id, out _)) { return false; }
            _events.Publish(new ServerListChanged(id, Removed: true));
            return true;
        }
    }
}
