using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.GameServer
{
    class VTS
    {
        private readonly ServerConfig _serverData;

        public string Error;
        public string Notice { get; set; }

        public const string FullName = "Vintage Story Dedicated Server";
        public string StartPath = "VintageStoryServer.exe";
        public bool AllowsEmbedConsole = true;
        public int PortIncrements = 1;
        public dynamic QueryMethod = null;

        public string Port = "42420";
        public string QueryPort = "42420";
        public string Defaultmap = "default";
        public string Maxplayers = "16";
        public string Additional = "--dataPath ./data";

        public VTS(ServerConfig serverData)
        {
            _serverData = serverData;
        }

        public async Task CreateServerCFG()
        {
            //Download serverconfig.json
            var replaceValues = new List<(string, string)>()
            {
                ("{{ServerName}}", _serverData.ServerName),
                ("{{Port}}", _serverData.ServerPort),
                ("{{MaxClients}}", _serverData.ServerMaxPlayer)
            };

            await Github.DownloadGameServerConfig(ServerPath.GetServersServerFiles(_serverData.ServerID, "data", "serverconfig.json"), FullName, replaceValues);
        }

        public async Task<Process> Start()
        {
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exePath))
            {
                Error = $"{Path.GetFileName(exePath)} not found ({exePath})";
                return null;
            }

            Process p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = Directory.GetParent(exePath).FullName,
                    FileName = exePath,
                    Arguments = _serverData.ServerParam,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            // NEXT: Vintage Story needs a .NET runtime (1.22: .NET 10) that a fresh Windows doesn't have, and WindowsGSM
            // brings its own .NET only for itself. When it's missing, a copy goes into WindowsGSM's folder for this.
            var notes = new List<string>();
            try { await Functions.Runtimes.PrepareDotnetAsync(p.StartInfo, exePath, notes.Add); }
            catch (Exception ex)
            {
                Error = $"Couldn't get the .NET runtime Vintage Story needs: {ex.Message}";
                return null;
            }
            if (notes.Count > 0) { Notice = string.Join(" ", notes); }

            if (AllowsEmbedConsole)
            {
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                var serverConsole = new ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
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
            await Task.Run(() =>
            {
                if (p.StartInfo.RedirectStandardInput)
                {
                    p.StandardInput.WriteLine("/stop");
                }
                else
                {
                    ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "/stop");
                }
            });
        }

        public async Task<Process> Install()
        {
            string version = await GetRemoteBuild();
            if (string.IsNullOrWhiteSpace(version)) { Error ??= "Couldn't find out the newest Vintage Story version."; return null; }
            string zipName = $"vs_server_win-x64_{version}.zip";
            string address = _downloadUrl ?? $"https://cdn.vintagestory.at/gamefiles/stable/{zipName}";
            string zipPath = ServerPath.GetServersServerFiles(_serverData.ServerID, zipName);

            // Download vs_server_win-x64_{version}.zip from https://cdn.vintagestory.at/gamefiles/stable/
            try { await WindowsGSM.Functions.Http.DownloadFileAsync(address, zipPath); }
            catch
            {
                Error = $"Fail to download {zipName}";
                return null;
            }

            // Extract vs_server_win-x64_{version}.zip
            if (!await FileManagement.ExtractZip(zipPath, Directory.GetParent(zipPath).FullName))
            {
                Error = $"Fail to extract {zipName}";
                return null;
            }

            // Delete vs_server_win-x64_{version}.zip, leave it if fail to delete
            await FileManagement.DeleteAsync(zipPath);

            return null;
        }

        public async Task<Process> Update()
        {
            // Backup the data folder
            string dataPath = ServerPath.GetServersServerFiles(_serverData.ServerID, "data");
            string tempPath = ServerPath.GetServers(_serverData.ServerID, "__temp");
            bool needBackup = Directory.Exists(dataPath);
            if (needBackup)
            {
                if (Directory.Exists(tempPath))
                {
                    if (!await DirectoryManagement.DeleteAsync(tempPath, true))
                    {
                        Error = "Fail to delete the temp folder";
                        return null;
                    }
                }

                if (!await Task.Run(() =>
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(dataPath, tempPath);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Error = e.Message;
                        return false;
                    }
                }))
                {
                    return null;
                }
            }

            // Delete the serverfiles folder
            if (!await DirectoryManagement.DeleteAsync(ServerPath.GetServersServerFiles(_serverData.ServerID), true))
            {
                Error = "Fail to delete the serverfiles";
                return null;
            }

            // Recreate the serverfiles folder
            Directory.CreateDirectory(ServerPath.GetServersServerFiles(_serverData.ServerID));

            if (needBackup)
            {
                // Restore the data folder
                if (!await Task.Run(() =>
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(tempPath, dataPath);
                        return true;
                    }
                    catch (Exception e)
                    {
                        Error = e.Message;
                        return false;
                    }
                }))
                {
                    return null;
                }

                await DirectoryManagement.DeleteAsync(tempPath, true);
            }

            // Update the server by install again
            await Install();

            // Return is valid
            if (IsInstallValid())
            {
                return null;
            }

            Error = "Update fail";
            return null;
        }

        public bool IsInstallValid()
        {
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            return File.Exists(exePath);
        }

        public bool IsImportValid(string path)
        {
            string exePath = Path.Combine(path, StartPath);
            Error = $"Invalid Path! Fail to find {StartPath}";
            return File.Exists(exePath);
        }

        public string GetLocalBuild()
        {
            // Get local version in VintageStoryServer.exe
            string exePath = ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(exePath))
            {
                Error = $"{StartPath} is missing.";
                return string.Empty;
            }

            // "1.22.7", or "1.22.7+3f2a…" from newer builds: only the version, or it never matches the feed.
            return (FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? string.Empty).Split('+')[0].Trim();
        }

        private string _downloadUrl;

        public async Task<string> GetRemoteBuild()
        {
            // NEXT: Vintage Story's own version feed (legacy read the newest version off an Arch Linux package page).
            try
            {
                var feed = Newtonsoft.Json.Linq.JObject.Parse(await WindowsGSM.Functions.Http.DownloadStringAsync("https://api.vintagestory.at/stable.json"));
                var newest = feed.Properties()
                    .Select(x => (Name: x.Name, Version: Version.TryParse(x.Name, out var v) ? v : null, Url: (string)x.Value["windowsserver"]?["urls"]?["cdn"]))
                    .Where(x => x.Version != null && x.Url != null)
                    .OrderByDescending(x => x.Version)
                    .FirstOrDefault();
                if (newest.Name == null) { Error = "Vintage Story's version feed listed no Windows server."; return string.Empty; }
                _downloadUrl = newest.Url;
                return newest.Name;
            }
            catch
            {
                Error = "Fail to get remote build";
                return string.Empty;
            }
        }
    }
}
