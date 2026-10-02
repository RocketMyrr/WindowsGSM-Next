using System;
using System.IO;

namespace WindowsGSM.Functions
{
    internal static class BuildCache
    {
        private const string FileName = "windowsgsm_buildid.txt";

        public static string GetPath(string serverId)
        {
            return Path.Combine(ServerPath.GetServersServerFiles(serverId), FileName);
        }

        public static void Write(string serverId, string buildId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(buildId)) return;
                File.WriteAllText(GetPath(serverId), buildId.Trim());
            }
            catch { }
        }

        public static string Read(string serverId)
        {
            try
            {
                var path = GetPath(serverId);
                if (!File.Exists(path)) return string.Empty;
                return File.ReadAllText(path).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
