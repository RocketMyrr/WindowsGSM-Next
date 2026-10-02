#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WindowsGSM.Hosting
{
    /// <summary>
    /// Process-wide facts every part of the engine needs: where the data lives, what version this is,
    /// and hard limits. Replaces the statics that used to hang off the WPF MainWindow.
    ///
    /// DataRoot is the WindowsGSM data folder (servers/, backups/, configs/, logs/, plugins/, bin/). It is
    /// deliberately separate from where the program is installed: the new build installs as a normal
    /// folder and can point at an existing WindowsGSM data folder, so no migration step is needed.
    /// </summary>
    public static class WgsmEnvironment
    {
        public const int MaxServers = 256;

        private static string? _dataRoot;

        /// <summary>
        /// The data folder. Defaults to the executable's folder (legacy behaviour); hosts set it explicitly
        /// at startup via <see cref="Initialize"/>, before touching any server.
        /// </summary>
        public static string DataRoot => _dataRoot ??= DefaultDataRoot();

        /// <summary>Display version, e.g. "v2.0.0-alpha.1".</summary>
        public static string Version { get; } = "v" + (
            typeof(WgsmEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(WgsmEnvironment).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");

        /// <summary>Sets the data folder. Call once at host startup; later calls with a different path throw.</summary>
        public static void Initialize(string dataRoot)
        {
            string full = Path.GetFullPath(dataRoot);
            if (_dataRoot != null && !string.Equals(_dataRoot, full, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"DataRoot is already set to '{_dataRoot}'.");
            }
            Directory.CreateDirectory(full);
            _dataRoot = full;
        }

        private static string DefaultDataRoot()
        {
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            string? exeDir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
            return string.IsNullOrWhiteSpace(exeDir) ? AppContext.BaseDirectory : exeDir;
        }
    }
}
