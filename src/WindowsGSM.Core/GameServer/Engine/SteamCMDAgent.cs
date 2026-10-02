using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;

namespace WindowsGSM.GameServer.Engine
{
    public class SteamCMDAgent
    {
        public SteamCMDAgent(Functions.ServerConfig serverData) => this.serverData = serverData;

        // Standard variables
        public Functions.ServerConfig serverData;
        public string Error { get; set; }
        public string Notice { get; set; }

        public virtual bool loginAnonymous { get; set; }
        public virtual string AppId { get; set; }
        public virtual string StartPath { get; set; }

        public async Task<Process> Install()
        {
            Error = null;
            Notice = null;
            var steamCMD = new Installer.SteamCMD();
            var (p, didRetry) = await steamCMD.InstallWithRetry(serverData.ServerID, string.Empty, AppId, true, loginAnonymous);
            Error = steamCMD.Error;
            if (didRetry)
            {
                Notice = "Recovered from corrupt install state by wiping steamapps and retrying.";
            }
            return p;
        }

        public async Task<Process> Update(bool validate = false, string custom = null)
        {
            Error = null;
            Notice = null;
            var (p, error, didRetry) = await Installer.SteamCMD.UpdateExWithRetry(serverData.ServerID, AppId, validate, custom: custom, loginAnonymous: loginAnonymous);
            Error = error;
            if (didRetry)
            {
                Notice = "Recovered from corrupt update state by wiping steamapps and re-downloading.";
            }
            if (p != null)
            {
                await Task.Run(() => p.WaitForExit());
            }
            return p;
        }

        public string GetLocalBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return steamCMD.GetLocalBuild(serverData.ServerID, AppId);
        }

        public async Task<string> GetRemoteBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return await steamCMD.GetRemoteBuild(AppId);
        }

        public bool IsInstallValid()
        {
            string installPath = Functions.ServerPath.GetServersServerFiles(serverData.ServerID, StartPath);
            if (File.Exists(installPath)) { return true; }
            Error = $"Fail to find {installPath}";
            return false;
        }

        public bool IsImportValid(string path)
        {
            string importPath = Path.Combine(path, StartPath);
            if (File.Exists(importPath)) { return true; }
            Error = $"Invalid Path! Fail to find {Path.GetFileName(StartPath)}";
            return false;
        }
    }
}
