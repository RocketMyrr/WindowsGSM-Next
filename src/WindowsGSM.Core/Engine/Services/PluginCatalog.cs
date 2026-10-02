#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// The games this engine can run: the built-in servers plus community plugins compiled from the data
    /// folder's plugins/ directory, created through the same GameServer.Data.Class.Get path as legacy.
    /// </summary>
    public sealed class PluginCatalog
    {
        private List<PluginMetadata> _plugins = new List<PluginMetadata>();

        public IReadOnlyList<PluginMetadata> Plugins => _plugins;

        /// <summary>Compiles and loads every plugin in plugins/. Failed plugins stay listed with their error.</summary>
        /// <param name="writeLogs">false for a read-only look (the data-folder dry run).</param>
        public async Task LoadAsync(bool writeLogs = true)
        {
            _plugins = await new PluginManagement().LoadPlugins(shouldAwait: true).ConfigureAwait(false);
            if (writeLogs) { WriteErrorLogs(); }
        }

        /// <summary>
        /// Like the legacy app: a plugin that doesn't compile leaves its errors in logs/plugins/&lt;file&gt;.log (and a
        /// fixed one's old log goes away), so they can be sent to its author.
        /// </summary>
        private void WriteErrorLogs()
        {
            try
            {
                string dir = ServerPath.GetLogs(ServerPath.FolderName.Plugins);
                foreach (var p in _plugins.Where(p => p.FileName != "NoValidPlugin"))
                {
                    string file = Path.Combine(dir, p.FileName + ".log");
                    if (p.IsLoaded) { if (File.Exists(file)) { File.Delete(file); } continue; }
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(file, $"[{DateTime.Now:MM/dd/yyyy-HH:mm:ss}] {p.FileName} didn't load:{Environment.NewLine}{p.Error ?? "Unknown error"}{Environment.NewLine}");
                }
            }
            catch { /* the panel still shows the error */ }
        }

        /// <summary>A game-server object (built-in or plugin) for <paramref name="game"/>, or null if unknown.</summary>
        public dynamic? Create(string game, ServerConfig config) =>
            GameServer.Data.Class.Get(game, config, _plugins.Where(p => p.IsLoaded).ToList());
    }
}
