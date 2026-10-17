using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WindowsGSM.GameServer
{
    /// <summary>
    /// NEXT: Counter-Strike 2. Its dedicated server is the game itself (app 730, downloadable anonymously — about 60 GB)
    /// run with -dedicated; the program and the csgo content folder live under game\. Like srcds, its console takes
    /// keystrokes in its window rather than stdin; output is captured.
    /// </summary>
    class CS2
    {
        private readonly Functions.ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Counter-Strike 2 Dedicated Server";
        public string StartPath = @"game\bin\win64\cs2.exe";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public dynamic QueryMethod = new Query.A2S();

        public string Port = "27015";
        public string QueryPort = "27015";
        public string Defaultmap = "de_dust2";
        public string Maxplayers = "16";
        public string Additional = "-usercon +game_type 0 +game_mode 1";

        public string AppId = "730";

        public CS2(Functions.ServerConfig serverData) => _serverData = serverData;

        private string ConfigPath => Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, @"game\csgo\cfg\server.cfg");

        public async Task CreateServerCFG()
        {
            // Name and an RCON password; everything else is CS2's default until changed under Configs.
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllLines(ConfigPath, new[]
                {
                    "// Written by WindowsGSM. Counter-Strike 2 runs it at every map start.",
                    $"hostname \"{_serverData.ServerName}\"",
                    $"rcon_password \"{_serverData.GetRCONPassword()}\"",
                    "sv_lan 0",
                });
            }
            await Task.CompletedTask;
        }

        public async Task<Process> Start()
        {
            string exe = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exe))
            {
                Error = $"{Path.GetFileName(exe)} not found ({exe})";
                return null;
            }
            if (!File.Exists(ConfigPath)) { Notice = $"server.cfg not found ({ConfigPath})"; }

            var sb = new StringBuilder("-dedicated -console");
            if (!string.IsNullOrWhiteSpace(_serverData.ServerIP)) { sb.Append($" -ip {_serverData.ServerIP}"); }
            if (!string.IsNullOrWhiteSpace(_serverData.ServerPort)) { sb.Append($" -port {_serverData.ServerPort}"); }
            if (!string.IsNullOrWhiteSpace(_serverData.ServerMaxPlayer)) { sb.Append($" -maxplayers {_serverData.ServerMaxPlayer}"); }
            if (!string.IsNullOrWhiteSpace(_serverData.ServerGSLT)) { sb.Append($" +sv_setsteamaccount {_serverData.ServerGSLT}"); }
            if (!string.IsNullOrWhiteSpace(_serverData.ServerParam)) { sb.Append($" {_serverData.ServerParam}"); }
            if (!string.IsNullOrWhiteSpace(_serverData.ServerMap)) { sb.Append($" +map {_serverData.ServerMap}"); }

            var p = new Process
            {
                StartInfo =
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    Arguments = sb.ToString(),
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false,
                },
                EnableRaisingEvents = true,
            };
            if (AllowsEmbedConsole)
            {
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                var console = new Functions.ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += console.AddOutput;
                p.ErrorDataReceived += console.AddOutput;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return p;
            }
            p.Start();
            return p;
        }

        public async Task Stop(Process p)
        {
            await Task.Run(() => Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "quit"));
        }

        public async Task<Process> Install()
        {
            var steamCMD = new Installer.SteamCMD();
            Process p = await steamCMD.Install(_serverData.ServerID, string.Empty, AppId, true);
            Error = steamCMD.Error;
            return p;
        }

        public async Task<Process> Update(bool validate = false, string custom = null)
        {
            var (p, error) = await Installer.SteamCMD.UpdateEx(_serverData.ServerID, AppId, validate, custom: custom);
            Error = error;
            return p;
        }

        public bool IsInstallValid() => File.Exists(Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath));

        public bool IsImportValid(string path)
        {
            Error = $"Invalid Path! Fail to find {StartPath}";
            return File.Exists(Path.Combine(path, StartPath));
        }

        public string GetLocalBuild() => new Installer.SteamCMD().GetLocalBuild(_serverData.ServerID, AppId);

        public async Task<string> GetRemoteBuild() => await new Installer.SteamCMD().GetRemoteBuild(AppId);
    }
}
