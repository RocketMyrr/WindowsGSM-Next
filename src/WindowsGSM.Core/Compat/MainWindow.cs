using System;
using WindowsGSM.Hosting;

namespace WindowsGSM
{
    /// <summary>
    /// Compatibility shim. In the legacy app these statics lived on the WPF MainWindow, and the imported
    /// engine files (ServerPath, ServerConfig, BackupConfig, …) — and possibly third-party plugins — still
    /// read them as <c>MainWindow.WGSM_PATH</c> etc. Keeping the names lets those files stay identical to
    /// the legacy copies, so fixes port across unchanged. New code should use <see cref="WgsmEnvironment"/>.
    /// There is no window here; this is only the handful of statics.
    /// </summary>
    public static class MainWindow
    {
        public static string WGSM_VERSION => WgsmEnvironment.Version;
        public static string WGSM_PATH => WgsmEnvironment.DataRoot;
        public static int MAX_SERVER => WgsmEnvironment.MaxServers;

        /// <summary>Same values as the legacy enum (persisted/compared by number in places).</summary>
        public enum ServerStatus
        {
            Started = 0,
            Starting = 1,
            Stopped = 2,
            Stopping = 3,
            Restarted = 4,
            Restarting = 5,
            Updated = 6,
            Updating = 7,
            Backuped = 8,
            Backup = 9,
            Restored = 10,
            Restoring = 11,
            Deleting = 12,
            Crashed = 13,
            UpdatingAddons = 14
        }
    }
}
