using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using WindowsGSM.Functions;

namespace WindowsGSM.GameServer
{
    class MC
    {
        private readonly Functions.ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Minecraft: Java Edition Server";
        public string StartPath = string.Empty;
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public dynamic QueryMethod = new Query.UT3();

        public string Port = "25565";
        public string QueryPort = "25565";
        public string Defaultmap = "world";
        public string Maxplayers = "20";
        public string Additional = "-Xms2G -Xmx4G"; // NEXT: 1 GB (legacy) is tight for current Minecraft

        public MC(Functions.ServerConfig serverData)
        {
            _serverData = serverData;
        }

        public async Task CreateServerCFG()
        {
            //Create server.properties
            string configPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.properties");
            if (await Functions.Github.DownloadGameServerConfig(configPath, FullName))
            {
                string configText = File.ReadAllText(configPath);
                configText = configText.Replace("{{serverPort}}", _serverData.ServerPort);
                configText = configText.Replace("{{maxplayers}}", Maxplayers);
                configText = configText.Replace("{{rconPort}}", (int.Parse(_serverData.ServerPort) + 10).ToString());
                configText = configText.Replace("{{serverIP}}", _serverData.ServerIP);
                configText = configText.Replace("{{defaultmap}}", Defaultmap);
                string rconPassword = _serverData.GetRCONPassword();
                configText = configText.Replace("{{rcon_password}}", rconPassword);
                configText = configText.Replace("{{serverName}}", _serverData.ServerName);
                // NEXT: RCON on, and WindowsGSM's RCON settings to match — Stop after an agent restart, the TPS reading
                // and whitelist changes on a running server go through it (the template left it off and unknown).
                configText = System.Text.RegularExpressions.Regex.Replace(configText, @"(?m)^enable-rcon=.*$", "enable-rcon=true");
                File.WriteAllText(configPath, configText);
                string rconPort = (int.Parse(_serverData.ServerPort) + 10).ToString();
                Functions.ServerConfig.SetSetting(_serverData.ServerID, Functions.ServerConfig.SettingName.RconPort, rconPort);
                Functions.ServerConfig.SetSetting(_serverData.ServerID, Functions.ServerConfig.SettingName.RconPassword, rconPassword);
            }
        }

        public async Task<Process> Start()
        {
            string workingDir = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID);

            // NEXT: NeoForge starts from its arguments file, not server.jar; Minecraft's own jar sits in its libraries.
            var software = Installer.MinecraftSoftware.Read(_serverData.ServerID);
            bool neoforge = software?.Flavor == "neoforge" && software.Build != null;
            string launch = "-jar server.jar nogui";
            string serverJarPath = Path.Combine(workingDir, "server.jar");
            if (neoforge)
            {
                string args = Installer.MinecraftSoftware.NeoForgeArgs(software!.Build!);
                if (!File.Exists(Path.Combine(workingDir, args)))
                {
                    Error = $"NeoForge {software.Build} isn't fully installed ({args} is missing) — reinstall it from the Minecraft tab.";
                    return null;
                }
                launch = $"@user_jvm_args.txt @{args.Replace('\\', '/')} nogui";
                serverJarPath = Path.Combine(workingDir, Installer.MinecraftSoftware.NeoForgeMinecraftJar(software.Version));
            }
            if (!File.Exists(serverJarPath))
            {
                Error = $"{Path.GetFileName(serverJarPath)} not found ({serverJarPath})";
                return null;
            }

            // NEXT: the Java this jar needs (downloaded for WindowsGSM's own use when it isn't installed).
            var notes = new System.Collections.Generic.List<string>();
            string javaPath;
            try { javaPath = await Functions.JavaRuntimes.ForJarAsync(serverJarPath, notes.Add); }
            catch (System.Exception ex)
            {
                Error = $"Couldn't get the Java this server needs: {ex.Message}";
                return null;
            }

            string configPath = Path.Combine(workingDir, "server.properties");
            if (!File.Exists(configPath))
            {
                notes.Add($"server.properties not found ({configPath}). Generated a new one.");
            }
            if (Installer.MinecraftSoftware.AcceptEula(workingDir)) { notes.Add("eula.txt was missing; wrote it again (the EULA was accepted at install)."); }
            if (notes.Count > 0) { Notice = string.Join(" ", notes); }

            WindowsFirewall firewall = new WindowsFirewall("java.exe", javaPath);
            if (!await firewall.IsRuleExist())
            {
                await firewall.AddRule();
            }

            Process p;
            if (!AllowsEmbedConsole)
            {
                p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = workingDir,
                        FileName = javaPath,
                        Arguments = $"{_serverData.ServerParam} {launch}",
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
                        WorkingDirectory = workingDir,
                        FileName = javaPath,
                        // NEXT: UTF-8 both ways — redirected, Java otherwise writes Windows' old code page and names with
                        // accents or other scripts came out garbled in the Console tab.
                        Arguments = $"-Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 {_serverData.ServerParam} {launch}",
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
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
            // NEXT: a server picked up again after the agent restarted has no console input here (StartInfo throws);
            // legacy's Stop failed and the server was ended without saving. RCON does it when it's set up.
            bool typed = await Task.Run(() =>
            {
                try
                {
                    if (p.StartInfo.RedirectStandardInput) { p.StandardInput.WriteLine("stop"); return true; }
                    if (p.MainWindowHandle != System.IntPtr.Zero) { Functions.ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "stop"); return true; }
                }
                catch (System.InvalidOperationException) { /* not started by this agent */ }
                return false;
            });
            if (!typed && WindowsGSM.Engine.Services.ConsoleService.RconConfigured(_serverData, out string ip, out int port))
            {
                try { await Functions.RconClient.SendCommandAsync(ip, port, _serverData.RconPassword ?? string.Empty, "stop"); }
                catch { /* not answering: the stop timeout ends it */ }
            }
        }

        public async Task<Process> Install()
        {
            //EULA — NEXT: answered up front by whoever starts the install (see Functions.UserPrompt)
            if (!await Functions.UserPrompt.ConfirmAsync(Functions.UserPrompt.Keys.Eula, "Agreement to the EULA",
                    "By continuing you are indicating your agreement to the EULA.\n(https://account.mojang.com/documents/minecraft_eula)"))
            {
                Error = "The Minecraft EULA must be accepted to install this server.";
                return null;
            }

            // NEXT: no Java installer here any more — the first start gets the Java the downloaded jar needs.

            try
            {
                const string manifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
                string versionJson = await WindowsGSM.Functions.Http.DownloadStringAsync(manifestUrl);
                string latesetVersion = JObject.Parse(versionJson)["latest"]["release"].ToString();
                var versionObject = JObject.Parse(versionJson)["versions"];
                string packageUrl = null;

                foreach (var obj in versionObject)
                {
                    if (obj["id"].ToString() == latesetVersion)
                    {
                        packageUrl = obj["url"].ToString();
                        break;
                    }
                }

                if (packageUrl == null)
                {
                    Error = $"Fail to fetch packageUrl from {manifestUrl}";
                    return null;
                }

                //packageUrl example: https://launchermeta.mojang.com/v1/packages/6876d19c096de56d1aa2cf434ec6b0e66e0aba00/1.15.json
                var packageJson = await WindowsGSM.Functions.Http.DownloadStringAsync(packageUrl);

                //serverJarUrl example: https://launcher.mojang.com/v1/objects/e9f105b3c5c7e85c7b445249a93362a22f62442d/server.jar
                string serverJarUrl = JObject.Parse(packageJson)["downloads"]["server"]["url"].ToString();
                await WindowsGSM.Functions.Http.DownloadFileAsync(serverJarUrl, Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.jar"));

                //Create eula.txt
                string eulaPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "eula.txt");
                File.Create(eulaPath).Dispose();

                using (TextWriter textwriter = new StreamWriter(eulaPath))
                {
                    textwriter.WriteLine("#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://account.mojang.com/documents/minecraft_eula).");
                    textwriter.WriteLine("#Generated by WindowsGSM.exe");
                    textwriter.WriteLine("eula=true");
                }
            }
            catch
            {
                Error = $"Fail to install {FullName}";
                return null;
            }

            return null;
        }

        public async Task<Process> Update()
        {
            // NEXT: a newer server.jar may need a newer Java; the next start gets it.

            // NEXT: a server set up with the Minecraft tab (Paper, Purpur, Fabric, or a pinned Vanilla version) updates
            // that software, instead of being replaced with the newest Vanilla.
            var chosen = Installer.MinecraftSoftware.Read(_serverData.ServerID);
            if (chosen != null)
            {
                try { await Installer.MinecraftSoftware.UpdateAsync(_serverData.ServerID, chosen, line => Installer.DownloadContext.Current?.OnLine(line)); }
                catch (System.Exception ex) { Error = $"Couldn't update {chosen.Flavor} {chosen.Version}: {ex.Message}"; }
                return null;
            }

            string serverJarPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.jar");
            if (File.Exists(serverJarPath))
            {
                try
                {
                    File.Delete(serverJarPath);
                }
                catch
                {
                    Error = "Fail to delete server.jar";
                    return null;
                }
            }

            try
            {
                const string manifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
                string versionJson = WindowsGSM.Functions.Http.DownloadString(manifestUrl);
                string latesetVersion = JObject.Parse(versionJson)["latest"]["release"].ToString();
                var versionObject = JObject.Parse(versionJson)["versions"];
                string packageUrl = null;

                foreach (var obj in versionObject)
                {
                    if (obj["id"].ToString() == latesetVersion)
                    {
                        packageUrl = obj["url"].ToString();
                        break;
                    }
                }

                if (packageUrl == null)
                {
                    Error = $"Fail to fetch packageUrl from {manifestUrl}";
                    return null;
                }

                //packageUrl example: https://launchermeta.mojang.com/v1/packages/6876d19c096de56d1aa2cf434ec6b0e66e0aba00/1.15.json
                var packageJson = WindowsGSM.Functions.Http.DownloadString(packageUrl);

                //serverJarUrl example: https://launcher.mojang.com/v1/objects/e9f105b3c5c7e85c7b445249a93362a22f62442d/server.jar
                string serverJarUrl = JObject.Parse(packageJson)["downloads"]["server"]["url"].ToString();
                await WindowsGSM.Functions.Http.DownloadFileAsync(serverJarUrl, Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "server.jar"));

                //Create eula.txt
                string eulaPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "eula.txt");
                File.Create(eulaPath).Dispose();

                using (TextWriter textwriter = new StreamWriter(eulaPath))
                {
                    textwriter.WriteLine("#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://account.mojang.com/documents/minecraft_eula).");
                    textwriter.WriteLine("#Generated by WindowsGSM.exe");
                    textwriter.WriteLine("eula=true");
                }
            }
            catch
            {
                Error = $"Fail to install {FullName}";
                return null;
            }

            return null;
        }

        public bool IsInstallValid()
        {
            string jarFile = "server.jar";
            string jarPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, jarFile);

            return File.Exists(jarPath);
        }

        public bool IsImportValid(string path)
        {
            string jarFile = "server.jar";
            string jarPath = Path.Combine(path, jarFile);

            Error = $"Invalid Path! Fail to find {jarFile}";
            return File.Exists(jarPath);
        }

        public string GetLocalBuild()
        {
            var chosen = Installer.MinecraftSoftware.Read(_serverData.ServerID);
            if (chosen != null) { return Installer.MinecraftSoftware.Label(chosen); }

            string logFile = "latest.log";
            string logPath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, "logs", logFile);

            if (!File.Exists(logPath))
            {
                Error = $"{logFile} is missing.";
                return string.Empty;
            }

            // NEXT: the reader is always closed, and two-part versions ("26.3", Minecraft's numbering since 2026) count too.
            using (var fileStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var streamReader = new StreamReader(fileStream))
            {
                while (!streamReader.EndOfStream)
                {
                    string line = streamReader.ReadLine();
                    if (line != null && line.Contains("]: Starting minecraft server version"))
                    {
                        var match = Regex.Match(line, @"version\s+(\d+\.\d+(?:\.\d+)?)");
                        if (match.Success) { return match.Groups[1].Value; }
                    }
                }
            }

            Error = $"Fail to get local build";
            return string.Empty;
        }

        public async Task<string> GetRemoteBuild()
        {
            var chosen = Installer.MinecraftSoftware.Read(_serverData.ServerID);
            if (chosen != null)
            {
                try { return await Installer.MinecraftSoftware.RemoteLabelAsync(chosen); }
                catch { Error = "Fail to get remote build"; return string.Empty; }
            }

            try
            {
                string remoteUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
                string html = await WindowsGSM.Functions.Http.DownloadStringAsync(remoteUrl);

                Regex regex = new Regex("\"latest\":.{\"release\":.\"(.*?)\"");
                var matches = regex.Matches(html);

                if (matches.Count == 1 && matches[0].Groups.Count == 2)
                {
                    return matches[0].Groups[1].Value;
                }
            }
            catch
            {
                //ignore
            }

            Error = $"Fail to get remote build";
            return string.Empty;
        }
    }
}
