using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace WindowsGSM.Functions
{
    public class ServerConfig
    {
        public static class SettingName
        {
            public const string ServerGame = "servergame";
            public const string ServerName = "servername";
            public const string ServerIP = "serverip";
            public const string ServerPort = "serverport";
            public const string ServerQueryPort = "serverqueryport";
            public const string ServerMap = "servermap";
            public const string ServerMaxPlayer = "servermaxplayer";
            public const string ServerGSLT = "servergslt";
            public const string ServerParam = "serverparam";
            public const string BatchFile = "batchfile";
            public const string AutoRestart = "autorestart";
            public const string AutoStart = "autostart";
            public const string AutoUpdate = "autoupdate";
            public const string UpdateOnStart = "updateonstart";
            public const string UpdateAddonsOnStart = "updateaddonsonstart";
            public const string BackupOnStart = "backuponstart";
            public const string DiscordAlert = "discordalert";
            public const string DiscordMessage = "discordmessage";
            public const string DiscordWebhook = "discordwebhook";
            public const string RestartCrontab = "restartcrontab";
            public const string CrontabFormat = "crontabformat";
            public const string EmbedConsole = "embedconsole";
            public const string ShowConsole = "showconsole";
            public const string AutoStartAlert = "autostartalert";
            public const string AutoRestartAlert = "autorestartalert";
            public const string AutoUpdateAlert = "autoupdatealert";
            public const string AutoIpUpdateAlert = "autoipupdatealert";
            public const string SkipUserSetup = "skipusersetup";
            public const string RconIp = "rconip";
            public const string RconPort = "rconport";
            public const string RconPassword = "rconpassword";
            public const string SteamBranch = "steambranch";
            public const string SteamBranchPassword = "steambeta_password";
            public const string SteamBranchLastInstalled = "steambranch_lastinstalled";
            public const string DepotDownloader = "depotdownloader";

            public const string RestartCrontabAlert = "restartcrontabalert";
            public const string CrashAlert = "crashalert";
            public const string CPUPriority = "cpupriority";
            public const string CPUAffinity = "cpuaffinity";
            public const string AutoScroll = "autoscroll";
            public const string MemoryGuard = "memoryguard";
            public const string MemoryGuardThresholdMb = "memoryguardthresholdmb";
            public const string MemoryGuardSustainMinutes = "memoryguardsustainminutes";
        }

        public string ServerID;
        public string ServerGame;
        public string ServerName;
        public string ServerIP;
        public string ServerPort;
        public string ServerQueryPort;
        public string ServerMap;
        public string ServerMaxPlayer;
        public string ServerGSLT;
        public string ServerParam;
        public string BatchFile;
        public bool AutoRestart;
        public bool AutoStart;
        public bool AutoUpdate;
        public bool UpdateOnStart;
        public bool UpdateAddonsOnStart;
        public bool BackupOnStart;
        public bool DiscordAlert;
        public string DiscordMessage;
        public string DiscordWebhook;
        public bool RestartCrontab;
        public string CrontabFormat;
        public bool EmbedConsole;
        public bool ShowConsole;
        public bool AutoStartAlert;
        public bool AutoRestartAlert;
        public bool AutoUpdateAlert;
        public bool RestartCrontabAlert;
        public bool CrashAlert;
        public bool AutoIpUpdate;
        public bool SkipUserSetup;
        public string CPUPriority;
        public string CPUAffinity;
        public bool AutoScroll;
        public bool MemoryGuard;
        public int MemoryGuardThresholdMb = 4096;
        public int MemoryGuardSustainMinutes = 10;
        public string RconPort;
        public string RconIp;
        public string RconPassword;
        public string SteamBranch;
        public string SteamBranchPassword;
        public string SteamBranchLastInstalled;
        public bool DepotDownloader;
        public System.Collections.Generic.Dictionary<string, string> CustomSettings = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ServerConfig(string serverid)
        {
            //Get next available ServerID
            if (string.IsNullOrEmpty(serverid))
            {
                for (int id = 1; id <= MainWindow.MAX_SERVER; id++)
                {
                    string serverid_dir = MainWindow.WGSM_PATH + @"\servers\" + id;
                    if (Directory.Exists(serverid_dir))
                    {
                        string config = MainWindow.WGSM_PATH + @"\servers\" + id + @"\configs\WindowsGSM.cfg";
                        if (!File.Exists(config))
                        {
                            ServerID = id.ToString();
                            break;
                        }
                    }
                    else
                    {
                        ServerID = id.ToString();
                        break;
                    }
                }

                return;
            }

            ServerID = serverid;

            //Get values from configpath
            string configpath = ServerPath.GetServersConfigs(serverid, "WindowsGSM.cfg");
            if (File.Exists(configpath))
            {
                foreach (string line in File.ReadLines(configpath))
                {
                    if (line.StartsWith("//"))
                    {
                        continue;
                    }
                    string[] keyvalue = line.Split(new[] { '=' }, 2);
                    if (keyvalue.Length == 2)
                    {
                        // NEXT: tolerate hand-edited values. Legacy stripped the first and last character
                        // unconditionally, so `autostart=1` or `key=` threw and made the WHOLE config
                        // unreadable; an unquoted `abc` silently became `b`.
                        keyvalue[1] = Unquote(keyvalue[1]);

                        switch (keyvalue[0])
                        {
                            case SettingName.ServerGame: ServerGame = keyvalue[1]; break;
                            case SettingName.ServerName: ServerName = keyvalue[1]; break;
                            case SettingName.ServerIP: ServerIP = keyvalue[1]; break;
                            case SettingName.ServerPort: ServerPort = keyvalue[1]; break;
                            case SettingName.ServerQueryPort: ServerQueryPort = keyvalue[1]; break;
                            case SettingName.ServerMap: ServerMap = keyvalue[1]; break;
                            case SettingName.ServerMaxPlayer: ServerMaxPlayer = keyvalue[1]; break;
                            case SettingName.ServerGSLT: ServerGSLT = keyvalue[1]; break;
                            case SettingName.ServerParam: ServerParam = keyvalue[1]; break;
                            case SettingName.BatchFile: BatchFile = keyvalue[1]; break;
                            case SettingName.AutoRestart: AutoRestart = keyvalue[1] == "1"; break;
                            case SettingName.AutoStart: AutoStart = keyvalue[1] == "1"; break;
                            case SettingName.AutoUpdate: AutoUpdate = keyvalue[1] == "1"; break;
                            case SettingName.UpdateOnStart: UpdateOnStart = keyvalue[1] == "1"; break;
                            case SettingName.UpdateAddonsOnStart: UpdateAddonsOnStart = keyvalue[1] == "1"; break;
                            case SettingName.BackupOnStart: BackupOnStart = keyvalue[1] == "1"; break;
                            case SettingName.DiscordAlert: DiscordAlert = keyvalue[1] == "1"; break;
                            case SettingName.DiscordMessage: DiscordMessage = keyvalue[1]; break;
                            case SettingName.DiscordWebhook: DiscordWebhook = keyvalue[1]; break;
                            case SettingName.RestartCrontab: RestartCrontab = keyvalue[1] == "1"; break;
                            case SettingName.CrontabFormat: CrontabFormat = keyvalue[1]; break;
                            case SettingName.EmbedConsole: EmbedConsole = keyvalue[1] == "1"; break;
                            case SettingName.ShowConsole: ShowConsole = keyvalue[1] == "1"; break;
                            case SettingName.AutoStartAlert: AutoStartAlert = keyvalue[1] == "1"; break;
                            case SettingName.AutoRestartAlert: AutoRestartAlert = keyvalue[1] == "1"; break;
                            case SettingName.AutoUpdateAlert: AutoUpdateAlert = keyvalue[1] == "1"; break;
                            case SettingName.RestartCrontabAlert: RestartCrontabAlert = keyvalue[1] == "1"; break;
                            case SettingName.CrashAlert: CrashAlert = keyvalue[1] == "1"; break;
                            case SettingName.AutoIpUpdateAlert: AutoIpUpdate = keyvalue[1] == "1"; break;
                            case SettingName.SkipUserSetup: SkipUserSetup = keyvalue[1] == "1"; break;
                            case SettingName.CPUPriority: CPUPriority = keyvalue[1]; break;
                            case SettingName.CPUAffinity: CPUAffinity = keyvalue[1]; break;
                            case SettingName.AutoScroll: AutoScroll = keyvalue[1] == "1"; break;
                            case SettingName.MemoryGuard: MemoryGuard = keyvalue[1] == "1"; break;
                            case SettingName.MemoryGuardThresholdMb: if (int.TryParse(keyvalue[1], out int mgThreshold)) { MemoryGuardThresholdMb = mgThreshold; } break;
                            case SettingName.MemoryGuardSustainMinutes: if (int.TryParse(keyvalue[1], out int mgSustain)) { MemoryGuardSustainMinutes = mgSustain; } break;
                            case SettingName.RconIp: RconIp = keyvalue[1]; break;
                            case SettingName.RconPort: RconPort = keyvalue[1]; break;
                            case SettingName.RconPassword: RconPassword = keyvalue[1]; break;
                            case SettingName.SteamBranch: SteamBranch = keyvalue[1]; break;
                            case SettingName.SteamBranchPassword: SteamBranchPassword = keyvalue[1]; break;
                            case SettingName.SteamBranchLastInstalled: SteamBranchLastInstalled = keyvalue[1]; break;
                            case SettingName.DepotDownloader: DepotDownloader = keyvalue[1] == "1"; break;
                            default: CustomSettings[keyvalue[0]] = keyvalue[1]; break;
                        }
                    }
                }
            }
        }

        public void SetData(string serverGame, string serverName, dynamic gameServer)
        {
            bool usesCustomServerSettingSchema = UsesCustomServerSettingSchema(gameServer);
            string defaultPort = GetCustomServerSettingDefault(gameServer, SettingName.ServerPort, gameServer.Port);

            ServerGame = serverGame;
            ServerName = serverName;
            ServerIP = GetCustomServerSettingDefault(gameServer, SettingName.ServerIP, GetIPAddress());
            ServerPort = GetAvailablePort(defaultPort, gameServer.PortIncrements);
            ServerQueryPort = GetCustomServerSettingDefault(
                gameServer,
                SettingName.ServerQueryPort,
                (int.Parse(ServerPort) - int.Parse(gameServer.Port) + int.Parse(gameServer.QueryPort)).ToString()
            ); // Magic
            ServerMap = GetCustomServerSettingDefault(gameServer, SettingName.ServerMap, gameServer.Defaultmap);
            ServerMaxPlayer = GetCustomServerSettingDefault(gameServer, SettingName.ServerMaxPlayer, gameServer.Maxplayers);
            ServerGSLT = string.Empty;
            ServerParam = gameServer.Additional;
            BatchFile = string.Empty;
            EmbedConsole = false;
            ShowConsole = false;

            AutoRestart = false;
            AutoStart = false;
            AutoUpdate = false;
            UpdateOnStart = false;
            UpdateAddonsOnStart = false;
            BackupOnStart = false;
            DiscordAlert = false;
            DiscordMessage = string.Empty;
            DiscordWebhook = string.Empty;
            RestartCrontab = false;
            CrontabFormat = "0 6 * * *";
            AutoStartAlert = true;
            AutoRestartAlert = true;
            AutoUpdateAlert = true;
            RestartCrontabAlert = true;
            DepotDownloader = false;
            CrashAlert = true;
            AutoIpUpdate = true;
            SkipUserSetup = false;
            CPUPriority = "2";
            CPUAffinity = string.Concat(System.Linq.Enumerable.Repeat("1", Environment.ProcessorCount));
            AutoScroll = true;
            MemoryGuard = false;
            MemoryGuardThresholdMb = 4096;
            MemoryGuardSustainMinutes = 10;
            RconIp = GetIPAddress();
            RconPort = "0";
            RconPassword = "";
            SteamBranch = "";
            SteamBranchPassword = "";
            SteamBranchLastInstalled = "";

            if (usesCustomServerSettingSchema)
            {
                ApplyCustomServerSettingDefaults(gameServer);
            }
        }

        private void ApplyCustomServerSettingDefaults(dynamic gameServer)
        {
            object rawSettings = GetMemberValue(gameServer, "CustomSettings");
            if (rawSettings == null || rawSettings is string) { return; }

            if (rawSettings is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    string key = GetMemberValue(item, "Key")?.ToString()
                        ?? GetMemberValue(item, "Name")?.ToString()
                        ?? GetMemberValue(item, "SettingName")?.ToString();

                    if (string.IsNullOrWhiteSpace(key) || IsBuiltInSetting(key)) { continue; }

                    CustomSettings[key] = GetMemberValue(item, "DefaultValue")?.ToString()
                        ?? GetMemberValue(item, "Default")?.ToString()
                        ?? string.Empty;
                }
            }
        }

        private static string GetCustomServerSettingDefault(dynamic gameServer, string settingName, string fallback)
        {
            object rawSettings = GetMemberValue(gameServer, "CustomSettings");
            if (rawSettings == null || rawSettings is string) { return fallback; }

            if (rawSettings is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    string key = GetMemberValue(item, "Key")?.ToString()
                        ?? GetMemberValue(item, "Name")?.ToString()
                        ?? GetMemberValue(item, "SettingName")?.ToString();

                    if (!string.Equals(key, settingName, StringComparison.OrdinalIgnoreCase)) { continue; }

                    string value = GetMemberValue(item, "DefaultValue")?.ToString()
                        ?? GetMemberValue(item, "Default")?.ToString();

                    return string.IsNullOrWhiteSpace(value) ? fallback : value;
                }
            }

            return fallback;
        }

        private static bool UsesCustomServerSettingSchema(dynamic gameServer)
        {
            object rawSettings = GetMemberValue(gameServer, "CustomSettings");
            if (rawSettings == null || rawSettings is string) { return false; }
            if (rawSettings is CustomServerSetting) { return true; }

            if (rawSettings is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    if (item is CustomServerSetting) { return true; }
                }
            }

            return false;
        }

        private static object GetMemberValue(object source, string memberName)
        {
            if (source == null) { return null; }

            var type = source.GetType();
            var property = type.GetProperty(memberName);
            if (property != null) { return property.GetValue(source); }

            var field = type.GetField(memberName);
            return field?.GetValue(source);
        }

        public bool CreateWindowsGSMConfig()
        {
            CreateServerDirectory();

            string configpath = ServerPath.GetServersConfigs(ServerID, "WindowsGSM.cfg");
            if (!File.Exists(configpath))
            {
                File.Create(configpath).Dispose();

                using (TextWriter textwriter = new StreamWriter(configpath))
                {
                    textwriter.WriteLine($"{SettingName.ServerGame}=\"{ServerGame}\"");
                    textwriter.WriteLine($"{SettingName.ServerName}=\"{ServerName}\"");
                    textwriter.WriteLine($"{SettingName.ServerIP}=\"{ServerIP}\"");
                    textwriter.WriteLine($"{SettingName.ServerPort}=\"{ServerPort}\"");
                    textwriter.WriteLine($"{SettingName.ServerQueryPort}=\"{ServerQueryPort}\"");
                    textwriter.WriteLine($"{SettingName.ServerMap}=\"{ServerMap}\"");
                    textwriter.WriteLine($"{SettingName.ServerMaxPlayer}=\"{ServerMaxPlayer}\"");
                    textwriter.WriteLine($"{SettingName.ServerGSLT}=\"{ServerGSLT}\"");
                    textwriter.WriteLine($"{SettingName.ServerParam}=\"{ServerParam}\"");
                    textwriter.WriteLine($"{SettingName.BatchFile}=\"{BatchFile}\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.CPUPriority}=\"{CPUPriority}\"");
                    textwriter.WriteLine($"{SettingName.CPUAffinity}=\"{CPUAffinity}\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.AutoRestart}=\"0\"");
                    textwriter.WriteLine($"{SettingName.AutoStart}=\"0\"");
                    textwriter.WriteLine($"{SettingName.AutoUpdate}=\"0\"");
                    textwriter.WriteLine($"{SettingName.UpdateOnStart}=\"0\"");
                    textwriter.WriteLine($"{SettingName.UpdateAddonsOnStart}=\"0\"");
                    textwriter.WriteLine($"{SettingName.BackupOnStart}=\"0\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.DiscordAlert}=\"0\"");
                    textwriter.WriteLine($"{SettingName.DiscordMessage}=\"{DiscordMessage}\"");
                    textwriter.WriteLine($"{SettingName.DiscordWebhook}=\"{DiscordWebhook}\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.RestartCrontab}=\"0\"");
                    textwriter.WriteLine($"{SettingName.CrontabFormat}=\"{CrontabFormat}\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.EmbedConsole}=\"{(EmbedConsole ? "1" : "0")}\"");
                    textwriter.WriteLine($"{SettingName.ShowConsole}=\"{(ShowConsole ? "1" : "0")}\"");
                    textwriter.WriteLine($"{SettingName.AutoScroll}=\"{(AutoScroll ? "1" : "0")}\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.MemoryGuard}=\"0\"");
                    textwriter.WriteLine($"{SettingName.MemoryGuardThresholdMb}=\"4096\"");
                    textwriter.WriteLine($"{SettingName.MemoryGuardSustainMinutes}=\"10\"");
                    textwriter.WriteLine(string.Empty);
                    textwriter.WriteLine($"{SettingName.AutoStartAlert}=\"1\"");
                    textwriter.WriteLine($"{SettingName.AutoRestartAlert}=\"1\"");
                    textwriter.WriteLine($"{SettingName.AutoUpdateAlert}=\"1\"");
                    textwriter.WriteLine($"{SettingName.RestartCrontabAlert}=\"1\"");
                    textwriter.WriteLine($"{SettingName.CrashAlert}=\"1\"");
                    textwriter.WriteLine($"{SettingName.AutoIpUpdateAlert}=\"0\"");
                    textwriter.WriteLine($"{SettingName.SkipUserSetup}=\"0\"");
                    textwriter.WriteLine($"{SettingName.RconIp}=\"{ServerIP}\"");
                    textwriter.WriteLine($"{SettingName.RconPort}=\"0\"");
                    textwriter.WriteLine($"{SettingName.RconPassword}=\"\"");
                    textwriter.WriteLine($"{SettingName.DepotDownloader}=\"1\"");
                    textwriter.WriteLine($"{SettingName.SteamBranch}=\"{SteamBranch}\"");
                    textwriter.WriteLine($"{SettingName.SteamBranchPassword}=\"{SteamBranchPassword}\"");
                    textwriter.WriteLine($"{SettingName.SteamBranchLastInstalled}=\"{SteamBranchLastInstalled}\"");

                    if (CustomSettings.Count > 0)
                    {
                        textwriter.WriteLine(string.Empty);
                        foreach (var customSetting in CustomSettings)
                        {
                            textwriter.WriteLine($"{customSetting.Key}=\"{customSetting.Value}\"");
                        }
                    }
                }

                return true;
            }

            return false;
        }

        public void CreateServerDirectory()
        {
            Directory.CreateDirectory(ServerPath.GetServers(ServerID));
            Directory.CreateDirectory(ServerPath.GetServersConfigs(ServerID));
            Directory.CreateDirectory(ServerPath.GetServersServerFiles(ServerID));
        }

        public bool DeleteServerDirectory()
        {
            string serverid_dir = ServerPath.GetServers(ServerID);
            if (Directory.Exists(serverid_dir) && ServerID != null && ServerID != string.Empty)
            {
                try
                {
                    Directory.Delete(serverid_dir, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        public string GetIPAddress()
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint.Address.ToString();
            }
        }

        /// <summary>
        /// First port at or above <paramref name="defaultport"/> (stepping by <paramref name="increment"/>)
        /// whose block isn't used by another configured server.
        ///
        /// NEXT: the legacy version read ports off the WPF server grid, and compared against the sorted list
        /// in a single pass — so it could return a port still in use. This reads every server's saved
        /// config (game port and query port) and keeps stepping until the whole block is free.
        /// </summary>
        public string GetAvailablePort(string defaultport, int increment)
        {
            if (!int.TryParse(defaultport, out int port)) { return defaultport; }
            if (increment < 1) { increment = 1; }

            var used = new System.Collections.Generic.HashSet<int> { 27020 }; // SourceTV
            string serversDir = Path.Combine(MainWindow.WGSM_PATH, "servers");
            if (Directory.Exists(serversDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(serversDir))
                {
                    string id = Path.GetFileName(dir);
                    if (id == ServerID || !File.Exists(Path.Combine(dir, "configs", "WindowsGSM.cfg"))) { continue; }
                    var other = new ServerConfig(id);
                    if (int.TryParse(other.ServerPort, out int p)) { used.Add(p); }
                    if (int.TryParse(other.ServerQueryPort, out int q)) { used.Add(q); }
                }
            }

            bool BlockFree(int start)
            {
                for (int i = 0; i < increment; i++) { if (used.Contains(start + i)) { return false; } }
                return true;
            }

            while (port < 65535 && !BlockFree(port)) { port += increment; }
            return port.ToString();
        }

        public string GetRCONPassword()
        {
            string allowedChars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNOPQRSTUVWXYZ0123456789!@$?_-";
            char[] chars = new char[12];
            // NEXT: a password, so the cryptographic generator (System.Random is predictable).
            for (int i = 0; i < 12; i++)
            {
                chars[i] = allowedChars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(allowedChars.Length)];
            }
            RconPassword = new string(chars);
            SetSetting(ServerID, SettingName.RconPassword, RconPassword);
            return RconPassword;
        }

        public static string GetSetting(string serverId, string settingName)
        {
            string configFile = ServerPath.GetServersConfigs(serverId, "WindowsGSM.cfg");

            if (File.Exists(configFile))
            {
                //Read the config lines
                string[] lines = File.ReadAllLines(configFile);

                //Read all lines
                foreach (string line in lines)
                {
                    string[] keyvalue = line.Split(new char[] { '=' }, 2);
                    if (keyvalue.Length == 2)
                    {
                        if (settingName == keyvalue[0])
                        {
                            // NEXT: tolerant, like the constructor — a hand-edited unquoted value threw here.
                            return Unquote(keyvalue[1]);
                        }
                    }
                }
            }

            return string.Empty;
        }

        public string GetCustomSetting(string settingName, string defaultValue = "")
        {
            return CustomSettings.TryGetValue(settingName, out string value) ? value : defaultValue;
        }

        public static string GetCustomSetting(string serverId, string settingName, string defaultValue = "")
        {
            string value = GetSetting(serverId, settingName);
            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        public static bool IsBuiltInSetting(string settingName)
        {
            foreach (var field in typeof(SettingName).GetFields())
            {
                if (field.IsLiteral && !field.IsInitOnly && string.Equals(field.GetRawConstantValue()?.ToString(), settingName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Unquote(string value)
        {
            string raw = value.TrimEnd('\r');
            return raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"' ? raw.Substring(1, raw.Length - 2) : raw;
        }

        // NEXT: serialises config writes. The web API, scheduler and watchdog can all save settings at
        // once; unsynchronised read-modify-write lost updates.
        private static readonly object _writeLock = new object();

        public static void SetSetting(string serverId, string settingName, string data)
        {
            string configFile = ServerPath.GetServersConfigs(serverId, "WindowsGSM.cfg");

            lock (_writeLock)
            {
                if (!File.Exists(configFile)) { return; }

                bool saved = false;
                var output = new System.Collections.Generic.List<string>();
                foreach (string line in File.ReadAllLines(configFile))
                {
                    string[] keyvalue = line.Split(new[] { '=' }, 2);
                    if (keyvalue.Length == 2 && settingName == keyvalue[0])
                    {
                        output.Add($"{settingName}=\"{data}\"");
                        saved = true;
                    }
                    else
                    {
                        output.Add(line);
                    }
                }

                if (!saved)
                {
                    output.Add($"{settingName}=\"{data}\"");
                }

                // NEXT: write a temp file and swap it in. Legacy truncated the config first and then
                // rewrote it, so a crash or a locked file mid-write left the server with an empty config.
                string temp = configFile + ".tmp";
                File.WriteAllLines(temp, output);
                File.Move(temp, configFile, overwrite: true);
            }
        }
    }
}
