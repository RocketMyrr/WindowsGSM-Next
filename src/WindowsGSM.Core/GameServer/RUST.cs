using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;

namespace WindowsGSM.GameServer
{
    /// <summary>
    ///
    /// Notes:
    /// Both RedirectStandardInput or RedirectStandardOutput cannot use on WindowsGSM.
    /// RedirectStandardOutput is possible but it will break the input, if used both, the server can run successfully but the input become useless again.
    ///
    /// The solution for this is don't use neither RedirectStandardInput nor RedirectStandardOutput.
    /// Just use the traditional method to handle the server.
    ///
    /// </summary>
    class RUST
    {
        private readonly Functions.ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Rust Dedicated Server";
        public string StartPath = "RustDedicated.exe";
        public bool AllowsEmbedConsole = false;
        public int PortIncrements = 2;
        public dynamic QueryMethod = new Query.A2S();

        public string Port = "28015";
        public string QueryPort = "28016";
        public string Defaultmap = "Procedural Map";
        public string Maxplayers = "50";
        public string Additional = string.Empty;

        public string AppId = "258550";

        public RUST(Functions.ServerConfig serverData)
        {
            _serverData = serverData;
        }

        public async Task CreateServerCFG()
        {
            // NEXT: no WindowsGSM server.cfg on the start line any more (legacy downloaded one and added every line).
            // RCON is on, as before, from the server's RCON settings; everything else is Rust's default until you set
            // it — in Rust's own server\<identity>\cfg\server.cfg, or in Extra start parameters.
            string id = _serverData.ServerID;
            Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconPort, _serverData.ServerPort);
            Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconPassword, _serverData.GetRCONPassword());
            Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconIp, string.Empty);
            await Task.CompletedTask;
        }

        public async Task<Process> Start()
        {
            // An older install's WindowsGSM server.cfg: its settings move to where they belong, once.
            string moved = MoveOldDefaults(_serverData);
            if (moved != null) { Notice = moved; }

            string workingDir = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);
            if (_serverData.BatchFile != string.Empty)
            {
                if (File.Exists(_serverData.BatchFile))
                {
                    Process proc = new Process();
                    proc.StartInfo.FileName = _serverData.BatchFile;
                    proc.StartInfo.WorkingDirectory = workingDir;
                    proc.Start();
                    if (!proc.WaitForExit(20000))
                        proc.Kill();
                }
            }

            string srcdsPath = Path.Combine(workingDir, "RustDedicated.exe");

            string param = Arguments(_serverData);

            Process p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = workingDir,
                    FileName = srcdsPath,
                    Arguments = param.TrimEnd()
                },
                EnableRaisingEvents = true
            };
            p.Start();

            return p;
        }

        /// <summary>The start line: Rust's usual switches, the form's settings, RCON (when set) and Extra start parameters.</summary>
        internal static string Arguments(Functions.ServerConfig cfg)
        {
            string param = "-nographics -batchmode -silent-crashes";
            param += string.IsNullOrWhiteSpace(cfg.ServerName) ? string.Empty : $" +server.hostname \"{cfg.ServerName}\"";
            param += string.IsNullOrWhiteSpace(cfg.ServerIP) ? string.Empty : $" +server.ip {cfg.ServerIP}";
            param += string.IsNullOrWhiteSpace(cfg.ServerPort) ? string.Empty : $" +server.port {cfg.ServerPort}";
            param += string.IsNullOrWhiteSpace(cfg.ServerQueryPort) ? string.Empty : $" +server.queryport {cfg.ServerQueryPort}";
            //param += string.IsNullOrWhiteSpace(cfg.ServerMap) ? string.Empty : $" +server.level \"{cfg.ServerMap}\"";
            param += string.IsNullOrWhiteSpace(cfg.ServerMaxPlayer) ? string.Empty : $" +server.maxplayers {cfg.ServerMaxPlayer}";

            // RCON from the server's RCON settings (WindowsGSM's console uses the same), when a port is set.
            if (int.TryParse(cfg.RconPort, out int rconPort) && rconPort > 0)
            {
                param += string.IsNullOrWhiteSpace(cfg.RconIp) ? string.Empty : $" +rcon.ip {cfg.RconIp}";
                param += $" +rcon.port {rconPort} +rcon.password \"{cfg.RconPassword}\" +rcon.web 1";
            }

            // Then only what you set yourself.
            param += string.IsNullOrWhiteSpace(cfg.ServerParam) ? string.Empty : $" {cfg.ServerParam.Trim()}";
            return param.Trim();
        }

        /// <summary>Settings the form sets (Server name, IP address, Game port, Query port, Max players).</summary>
        private static readonly string[] FormSettings = { "server.hostname", "server.ip", "server.port", "server.queryport", "server.maxplayers" };

        /// <summary>
        /// Older installs have WindowsGSM's own server.cfg in the server files, every line of which went on the start
        /// line. Moves it once, keeping the server exactly as it ran: RCON into the RCON settings; the save folder
        /// (+server.identity) and -logfile into Extra start parameters, as they only work there; everything else into
        /// Rust's own server\&lt;identity&gt;\cfg\server.cfg (lines already there win). The file becomes server.cfg.old.
        /// Returns a note for the log, or null when there was nothing to move.
        /// </summary>
        internal static string MoveOldDefaults(Functions.ServerConfig cfg)
        {
            string id = cfg.ServerID;
            string old = Functions.ServerPath.GetServersServerFiles(id, "server.cfg");
            if (!File.Exists(old)) { return null; }

            var lines = File.ReadAllLines(old).Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => { var m = Regex.Match(l, @"^([+-])([A-Za-z][\w.]*)\s*(.*)$"); return (Ok: m.Success, Sign: m.Groups[1].Value, Name: m.Groups[2].Value, Value: m.Groups[3].Value.Trim()); })
                .Where(x => x.Ok && !FormSettings.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            string Value(string name) => lines.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value.Trim('"')).LastOrDefault();

            // RCON: what the server actually ran with, if WindowsGSM's own RCON settings weren't in use.
            if (!(int.TryParse(cfg.RconPort, out int p) && p > 0) && int.TryParse(Value("rcon.port"), out int rconPort) && rconPort > 0)
            {
                Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconPort, rconPort.ToString());
                Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconPassword, Value("rcon.password") ?? string.Empty);
                Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.RconIp, Value("rcon.ip") ?? string.Empty);
                cfg.RconPort = rconPort.ToString();
                cfg.RconPassword = Value("rcon.password") ?? string.Empty;
                cfg.RconIp = Value("rcon.ip") ?? string.Empty;
            }

            // Command line only: the save folder and the log file. Kept unless Extra start parameters already has them.
            string extras = (cfg.ServerParam ?? string.Empty).Trim();
            foreach (var x in lines.Where(x => x.Name.Equals("server.identity", StringComparison.OrdinalIgnoreCase) || x.Sign == "-"))
            {
                if (Regex.IsMatch(extras, $@"(^|\s)[+-]{Regex.Escape(x.Name)}(\s|$)", RegexOptions.IgnoreCase)) { continue; }
                extras = ($"{x.Sign}{x.Name} {x.Value}".Trim() + " " + extras).Trim();
            }
            if (extras != (cfg.ServerParam ?? string.Empty).Trim())
            {
                Functions.ServerConfig.SetSetting(id, Functions.ServerConfig.SettingName.ServerParam, extras);
                cfg.ServerParam = extras;
            }

            // The rest: Rust's own config for this save, which Rust reads itself on start.
            string identity = Value("server.identity");
            if (string.IsNullOrWhiteSpace(identity)) { identity = "my_server_identity"; } // Rust's default folder
            string rustCfg = Functions.ServerPath.GetServersServerFiles(id, Path.Combine("server", identity, "cfg", "server.cfg"));
            var existing = File.Exists(rustCfg) ? File.ReadAllLines(rustCfg) : Array.Empty<string>();
            var have = new System.Collections.Generic.HashSet<string>(existing.Select(l => l.Trim().Split(' ', 2)[0]).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
            var add = lines.Where(x => x.Sign == "+" && !x.Name.StartsWith("rcon.", StringComparison.OrdinalIgnoreCase)
                    && !x.Name.Equals("server.identity", StringComparison.OrdinalIgnoreCase) && !have.Contains(x.Name))
                .Select(x => $"{x.Name} {x.Value}".Trim()).ToList();
            if (add.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rustCfg));
                var text = existing.Length > 0 && existing[^1].Length > 0 ? Environment.NewLine : string.Empty;
                File.AppendAllText(rustCfg, text + string.Join(Environment.NewLine, add) + Environment.NewLine);
            }

            File.Move(old, old + ".old", overwrite: true);
            return $"Moved WindowsGSM's server.cfg (now server.cfg.old): {(add.Count > 0 ? $"{add.Count} setting(s) to server\\{identity}\\cfg\\server.cfg, " : "")}"
                + "the save folder and log file to Extra start parameters, RCON to the RCON settings. The start line now only has what you set.";
        }

        public async Task Stop(Process p)
        {
            // NEXT: over RCON when it's set up — a server started with -noconsole has no console to type "quit" into, so
            // legacy waited out the stop timeout and then ended the process. The console window is the fallback.
            if (WindowsGSM.Engine.Services.ConsoleService.RconConfigured(_serverData, out string ip, out int port))
            {
                try
                {
                    var quit = WindowsGSM.Engine.Services.ConsoleService.RustOneShotAsync(ip, port, _serverData.RconPassword ?? string.Empty, "quit");
                    if (await Task.WhenAny(quit, Task.Delay(System.TimeSpan.FromSeconds(8))) == quit && !quit.IsFaulted) { return; }
                }
                catch { /* RCON not answering: the console window below */ }
            }
            await Task.Run(() =>
            {
                Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "quit");
            });
        }

        public async Task<Process> Install()
        {
            var steamCMD = new Installer.SteamCMD();
            Process p = await steamCMD.Install(_serverData.ServerID, string.Empty, AppId);
            Error = steamCMD.Error;

            return p;
        }

        public async Task<Process> Update(bool validate = false, string custom = null)
        {
            var (p, error) = await Installer.SteamCMD.UpdateEx(_serverData.ServerID, AppId, validate, custom: custom);
            Error = error;
            return p;
        }

        public bool IsInstallValid()
        {
            return File.Exists(Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath));
        }

        public bool IsImportValid(string path)
        {
            Error = $"Invalid Path! Fail to find {StartPath}";
            return File.Exists(Path.Combine(path, StartPath));
        }

        public string GetLocalBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return steamCMD.GetLocalBuild(_serverData.ServerID, AppId);
        }

        public async Task<string> GetRemoteBuild()
        {
            var steamCMD = new Installer.SteamCMD();
            return await steamCMD.GetRemoteBuild(AppId);
        }
    }
}
