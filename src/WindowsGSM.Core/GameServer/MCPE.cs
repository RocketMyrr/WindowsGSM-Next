using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;

namespace WindowsGSM.GameServer
{
    class MCPE
    {
        private readonly Functions.ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Minecraft: Pocket Edition Server (PocketMine-MP)";
        public string StartPath = @"bin\php\php.exe";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public dynamic QueryMethod = new Query.UT3();

        public string Port = "19132";
        public string QueryPort = "19132";
        public string Defaultmap = "world";
        public string Maxplayers = "20";
        public string Additional = string.Empty;

        public MCPE(Functions.ServerConfig serverData)
        {
            _serverData = serverData;
        }

        public async Task CreateServerCFG()
        {
            //Download server.properties
            string configPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.properties");
            if (await Functions.Github.DownloadGameServerConfig(configPath, FullName))
            {
                string configText = File.ReadAllText(configPath);
                configText = configText.Replace("{{hostname}}", _serverData.ServerName);
                configText = configText.Replace("{{rcon_password}}", _serverData.GetRCONPassword());
                configText = configText.Replace("{{port}}", _serverData.ServerPort);
                configText = configText.Replace("{{maxplayers}}", Maxplayers);
                File.WriteAllText(configPath, configText);
            }
        }

        public async Task<Process> Start()
        {
            string workingDir = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);

            string phpPath = Path.Combine(workingDir, @"bin\php\php.exe");
            if (!File.Exists(phpPath))
            {
                Error = $"php.exe not found ({phpPath})";
                return null;
            }

            string PMMPPath = Path.Combine(workingDir, "PocketMine-MP.phar");
            if (!File.Exists(PMMPPath))
            {
                Error = $"PocketMine-MP.phar not found ({PMMPPath})";
                return null;
            }

            string serverConfigPath = Path.Combine(workingDir, "server.properties");
            if (!File.Exists(serverConfigPath))
            {
                Error = $"server.properties not found ({serverConfigPath})";
                return null;
            }

            Process p;
            if (!AllowsEmbedConsole)
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = workingDir,
                        FileName = phpPath,
                        Arguments = @"-c bin\php PocketMine-MP.phar",
                        WindowStyle = ProcessWindowStyle.Minimized,
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
                        WorkingDirectory = workingDir,
                        FileName = phpPath,
                        Arguments = @"-c bin\php PocketMine-MP.phar",
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
                    p.StandardInput.WriteLine("stop");
                }
                else
                {
                    Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "stop");
                }
            });
        }

        // NEXT: PocketMine moved from Jenkins (now a redirect to a GitHub page, which legacy saved as the PHP zip and the
        // .phar) to GitHub releases. Its build_info.json names the PHP build it wants, so that's what's installed.
        private const string BuildInfoUrl = "https://github.com/pmmp/PocketMine-MP/releases/latest/download/build_info.json";
        private const string BuildFile = "pocketmine-build.txt";   // the installed build, for update checks
        private const string PhpFile = @"bin\php\wgsm-php-version.txt"; // the PHP version installed with it

        private static async Task<Newtonsoft.Json.Linq.JObject> BuildInfoAsync() =>
            Newtonsoft.Json.Linq.JObject.Parse(await WindowsGSM.Functions.Http.DownloadStringAsync(BuildInfoUrl));

        public async Task<Process> Install()
        {
            try { await InstallOrUpdateAsync(forcePhp: true); }
            catch (System.Exception ex) { Error = $"Couldn't install PocketMine-MP: {ex.Message}"; }
            return null;
        }

        public async Task<Process> Update()
        {
            try { await InstallOrUpdateAsync(forcePhp: false); }
            catch (System.Exception ex) { Error = $"Couldn't update PocketMine-MP: {ex.Message}"; }
            return null;
        }

        /// <summary>The newest PocketMine-MP.phar, and the PHP build it asks for when that isn't the one installed.</summary>
        private async Task InstallOrUpdateAsync(bool forcePhp)
        {
            string files = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);
            Directory.CreateDirectory(files);
            var info = await BuildInfoAsync();
            string phar = (string)info["download_url"];
            string php = (string)info["php_version"];          // "8.2"
            string phpPage = (string)info["php_download_url"];  // …/PHP-Binaries/releases/tag/pm5-php-8.2-latest
            if (string.IsNullOrWhiteSpace(phar) || string.IsNullOrWhiteSpace(php) || string.IsNullOrWhiteSpace(phpPage)) { throw new InvalidDataException("PocketMine's build_info.json is missing download links."); }

            string phpVersionFile = Path.Combine(files, PhpFile);
            bool phpCurrent = File.Exists(Path.Combine(files, @"bin\php\php.exe")) && File.Exists(phpVersionFile) && File.ReadAllText(phpVersionFile).Trim() == php;
            if (forcePhp || !phpCurrent)
            {
                string tag = phpPage.Substring(phpPage.LastIndexOf('/') + 1);
                string zipName = $"PHP-{php}-Windows-x64-PM5.zip";
                string zip = Path.Combine(files, zipName);
                await WindowsGSM.Functions.Http.DownloadFileAsync($"https://github.com/pmmp/PHP-Binaries/releases/download/{tag}/{zipName}", zip);
                try
                {
                    using (var test = ZipFile.OpenRead(zip)) { if (test.GetEntry("bin/php/php.exe") == null) { throw new InvalidDataException($"{zipName} has no bin/php/php.exe."); } }
                    string phpDir = Path.Combine(files, "bin", "php");
                    if (Directory.Exists(phpDir)) { Directory.Delete(phpDir, true); } // a different PHP version's DLLs mustn't linger
                    WindowsGSM.Engine.Services.AddonService.ExtractSafely(zip, files);
                    File.WriteAllText(phpVersionFile, php);
                }
                finally { try { File.Delete(zip); } catch { } }
            }

            string pharPath = Path.Combine(files, "PocketMine-MP.phar");
            string part = pharPath + ".part";
            await WindowsGSM.Functions.Http.DownloadFileAsync(phar, part);
            using (var f = File.OpenRead(part)) // a .phar starts with PHP code, never an HTML page
            {
                var head = new byte[64];
                int n = f.Read(head, 0, head.Length);
                if (System.Text.Encoding.ASCII.GetString(head, 0, n).TrimStart().StartsWith("<!DOCTYPE", System.StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("GitHub returned a web page instead of PocketMine-MP.phar."); }
            }
            File.Move(part, pharPath, overwrite: true);
            File.WriteAllText(Path.Combine(files, BuildFile), ((int?)info["build"])?.ToString() ?? "");
        }

        public bool IsInstallValid()
        {
            string PHPPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"bin\php\php.exe");
            string PMMPPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "PocketMine-MP.phar");
            return File.Exists(PHPPath) && File.Exists(PMMPPath);
        }

        public bool IsImportValid(string path)
        {
            string PMMPFile = "PocketMine-MP.phar";
            string PMMPPath = Path.Combine(path, PMMPFile);

            Error = $"Invalid Path! Fail to find {PMMPFile}";
            return File.Exists(PMMPPath);
        }

        public string GetLocalBuild()
        {
            // NEXT: the build saved at install/update; older installs: the BUILD_NUMBER inside the .phar.
            string saved = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, BuildFile);
            if (File.Exists(saved) && File.ReadAllText(saved).Trim() is string build && build.Length > 0) { return build; }

            string PMMPFile = "PocketMine-MP.phar";
            string PMMPPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, PMMPFile);
            if (!File.Exists(PMMPPath))
            {
                Error = $"{PMMPFile} is missing.";
                return string.Empty;
            }

            foreach (string line in File.ReadLines(PMMPPath))
            {
                if (line.Contains("const BUILD_NUMBER")) { return Regex.Match(line, @"\d+").Value; }
            }

            Error = $"Fail to get local build";
            return string.Empty;
        }

        public async Task<string> GetRemoteBuild()
        {
            try { return ((int?)(await BuildInfoAsync())["build"])?.ToString() ?? string.Empty; }
            catch
            {
                Error = $"Fail to get remote build";
                return string.Empty;
            }
        }
    }
}
