using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace WindowsGSM.GameServer
{
    class GTA5
    {
        private readonly Functions.ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Grand Theft Auto V Dedicated Server (FiveM)";
        public string StartPath = @"server\FXServer.exe";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public dynamic QueryMethod = new Query.FIVEM();

        public string Port = "30120";
        public string QueryPort = "30120";
        public string Defaultmap = "fivem-map-skater";
        public string Maxplayers = "32";
        public string Additional = "+exec server.cfg";

        public GTA5(Functions.ServerConfig serverData)
        {
            _serverData = serverData;
        }

        public async Task CreateServerCFG()
        {
            //Download server.cfg
            string configPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"cfx-server-data-master\server.cfg");
            if (await Functions.Github.DownloadGameServerConfig(configPath, FullName))
            {
                string configText = File.ReadAllText(configPath);
                configText = configText.Replace("{{hostname}}", _serverData.ServerName);
                configText = configText.Replace("{{rcon_password}}", _serverData.GetRCONPassword());
                configText = configText.Replace("{{ip}}", _serverData.GetIPAddress());
                configText = configText.Replace("{{port}}", _serverData.ServerPort);
                configText = configText.Replace("{{maxplayers}}", Maxplayers);
                File.WriteAllText(configPath, configText);
            }

            //Download sample logo
            string logoPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"cfx-server-data-master\myLogo.png");
            await Functions.Github.DownloadGameServerConfig(logoPath, FullName);
        }

        public async Task<Process> Start()
        {
            string fxServerPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"server\FXServer.exe");
            if (!File.Exists(fxServerPath))
            {
                Error = $"FXServer.exe not found ({fxServerPath})";
                return null;
            }

            string citizenPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"server\citizen");
            if (!Directory.Exists(citizenPath))
            {
                Error = $"Directory citizen not found ({citizenPath})";
                return null;
            }

            string serverDataPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "cfx-server-data-master");
            if (!Directory.Exists(serverDataPath))
            {
                Error = $"Directory cfx-server-data-master not found ({serverDataPath})";
                return null;
            }

            string configPath = Path.Combine(serverDataPath, "server.cfg");
            if (!File.Exists(configPath))
            {
                Notice = $"server.cfg not found ({configPath})";
            }

            Process p;
            if (!AllowsEmbedConsole)
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = serverDataPath,
                        FileName = fxServerPath,
                        Arguments = $"+set citizen_dir \"{citizenPath}\" {_serverData.ServerParam}",
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = false
                    },
                    EnableRaisingEvents = true
                };
                p.Start();
            }
            else
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = serverDataPath,
                        FileName = fxServerPath,
                        Arguments = $"+set citizen_dir \"{citizenPath}\" {_serverData.ServerParam}",
                        WindowStyle = ProcessWindowStyle.Minimized,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    EnableRaisingEvents = true
                };
                var serverConsole = new Functions.ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }

            return p;
        }

        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                if (p.StartInfo.RedirectStandardInput)
                {
                    p.StandardInput.WriteLine("quit");
                }
                else
                {
                    Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "quit");
                }
            });
        }

        // NEXT: FiveM's own version feed. Legacy read the artifacts web page (now empty) with a pattern for 4-digit builds
        // (they're 5 digits now), and swallowed every error — so an install "succeeded" with nothing downloaded.
        private const string VersionsUrl = "https://changelogs-live.fivem.net/api/changelog/versions/win32/server";

        /// <summary>The recommended build ("35245-6efb47df…", as the version file stores it) and its download.</summary>
        private static async Task<(string Build, string Url)> RecommendedAsync()
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(await WindowsGSM.Functions.Http.DownloadStringAsync(VersionsUrl));
            string url = (string)json["recommended_download"];
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://runtime.fivem.net/", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("FiveM's version list has no recommended server download.");
            }
            string build = url.TrimEnd('/').Split('/')[^2]; // …/master/35245-6efb47df…/server.zip
            return (build, url);
        }

        /// <summary>Downloads the server build into server\ (replacing what's there) and records its version.</summary>
        private async Task DownloadServerAsync()
        {
            var (build, url) = await RecommendedAsync();
            string serverPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server");
            string zipPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.zip");
            await WindowsGSM.Functions.Http.DownloadFileAsync(url, zipPath);
            try
            {
                using (var test = ZipFile.OpenRead(zipPath)) { if (test.GetEntry("FXServer.exe") == null) { throw new InvalidDataException("The FiveM download has no FXServer.exe."); } }
                if (Directory.Exists(serverPath)) { Directory.Delete(serverPath, true); }
                Directory.CreateDirectory(serverPath);
                WindowsGSM.Engine.Services.AddonService.ExtractSafely(zipPath, serverPath);
            }
            finally { try { File.Delete(zipPath); } catch { } }
            File.WriteAllText(Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "FiveM-version.txt"), build);
        }

        public async Task<Process> Install()
        {
            try
            {
                await DownloadServerAsync();

                //Download cfx-server-data-master and extract to folder cfx-server-data-master then delete cfx-server-data-master.zip
                string zipPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "cfx-server-data-master.zip");
                await WindowsGSM.Functions.Http.DownloadFileAsync("https://github.com/citizenfx/cfx-server-data/archive/master.zip", zipPath);
                try { WindowsGSM.Engine.Services.AddonService.ExtractSafely(zipPath, Functions.ServerPath.GetServersServerFiles(_serverData.ServerID)); }
                finally { try { File.Delete(zipPath); } catch { } }
            }
            catch (System.Exception ex) { Error = $"Couldn't install the FiveM server: {ex.Message}"; }
            return null;
        }

        public async Task<Process> Update()
        {
            try { await DownloadServerAsync(); }
            catch (System.Exception ex) { Error = $"Couldn't update the FiveM server: {ex.Message}"; }
            return null;
        }

        public bool IsInstallValid()
        {
            string exeFile = @"server\FXServer.exe";
            string exePath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, exeFile);

            return File.Exists(exePath);
        }

        public bool IsImportValid(string path)
        {
            string exeFile = @"server\FXServer.exe";
            string exePath = Path.Combine(path, exeFile);

            Error = $"Invalid Path! Fail to find {exeFile}";
            return File.Exists(exePath);
        }

        public string GetLocalBuild()
        {
            string versionPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "FiveM-version.txt");
            Error = $"Fail to get local build";
            return File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : string.Empty;
        }

        public async Task<string> GetRemoteBuild()
        {
            try { return (await RecommendedAsync()).Build; }
            catch
            {
                Error = $"Fail to get remote build";
                return string.Empty;
            }
        }
    }
}
