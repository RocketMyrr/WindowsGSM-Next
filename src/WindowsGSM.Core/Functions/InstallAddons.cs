using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using System.Linq;
using System;
using System.Diagnostics;

namespace WindowsGSM.Functions
{
    class InstallAddons
    {
        private const string WindroseFullName = "Windrose Dedicated Server";
        private const string WindrosePlusDownloadUrl = "https://github.com/HumanGenome/WindrosePlus/releases/latest/download/WindrosePlus.zip";

        public static bool? IsAMXModXAndMetaModPExists(Functions.ServerTable server)
        {
            dynamic gameServer = GameServer.Data.Class.Get(server.Game);
            if (!(gameServer is GameServer.Engine.GoldSource))
            {
                // Game Type not supported
                return null;
            }

            // NEXT: legacy looked for a folder called addons\metamod.dll, so it always read "not installed".
            string MMPath = Functions.ServerPath.GetServersServerFiles(server.ID, gameServer.Game, "addons\\metamod\\dlls\\metamod.dll");
            return File.Exists(MMPath);
        }

        public static async Task<bool> AMXModXAndMetaModP(Functions.ServerTable server)
        {
            try
            {
                dynamic gameServer = GameServer.Data.Class.Get(server.Game);
                return await GameServer.Addon.AMXModX.Install(server.ID, modFolder: gameServer.Game);
            }
            catch
            {
                return false;
            }
        }

        public static bool? IsSourceModAndMetaModExists(Functions.ServerTable server)
        {
            dynamic gameServer = GameServer.Data.Class.Get(server.Game);
            if (!(gameServer is GameServer.Engine.Source))
            {
                // Game Type not supported
                return null;
            }

            string SMPath = Functions.ServerPath.GetServersServerFiles(server.ID, gameServer.Game, "addons\\sourcemod");
            return Directory.Exists(SMPath);
        }

        public static async Task<bool> SourceModAndMetaMod(Functions.ServerTable server)
        {
            try
            {
                dynamic gameServer = GameServer.Data.Class.Get(server.Game);
                return await GameServer.Addon.SourceMod.Install(server.ID, modFolder: gameServer.Game);
            }
            catch
            {
                return false;
            }
        }

        public static bool? IsDayZSALModServerExists(Functions.ServerTable server)
        {
            if (server.Game != GameServer.DAYZ.FullName)
            {
                // Game Type not supported
                return null;
            }

            // NEXT: DZSA no longer publishes the server download (the old link 404s), so it's only listed where it's
            // already installed — and reinstalling it then fails with a clear message instead of doing nothing.
            string exePath = Functions.ServerPath.GetServersServerFiles(server.ID, "DZSALModServer.exe");
            return File.Exists(exePath) ? true : null;
        }

        public static async Task<bool> DayZSALModServer(Functions.ServerTable server)
        {
            string zipPath = Functions.ServerPath.GetServersServerFiles(server.ID, "dzsalmodserver.zip");
            try
            {
                // https: it's a program the server then runs, so not over plain http.
                await WindowsGSM.Functions.Http.DownloadFileAsync("https://dayzsalauncher.com/releases/dzsalmodserver.zip", zipPath);
                await Task.Run(() => Engine.Services.AddonService.ExtractSafely(zipPath, Functions.ServerPath.GetServersServerFiles(server.ID)));
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        public static bool? IsOxideModExists(Functions.ServerTable server)
        {
            if (server.Game != GameServer.RUST.FullName)
            {
                // Game Type not supported
                return null;
            }

            return File.Exists(Functions.ServerPath.GetServersServerFiles(server.ID, "RustDedicated_Data", "Managed", "Oxide.Core.dll"));
        }

        public static async Task<bool> OxideMod(Functions.ServerTable server)
        {
            try
            {
                string basePath = Functions.ServerPath.GetServersServerFiles(server.ID);
                string zipPath = Functions.ServerPath.GetServersServerFiles(server.ID, "Oxide.Rust.zip");
                {
                    await WindowsGSM.Functions.Http.DownloadFileAsync("https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust.zip", zipPath);
                }

                bool success = await Task.Run(() =>
                {
                    try
                    {
                        // NEXT: never outside the server folder (an entry like ..\..\x is skipped).
                        Engine.Services.AddonService.ExtractSafely(zipPath, basePath);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });

                await Task.Run(() => { try { File.Delete(zipPath); } catch { } });
                return success;
            }
            catch
            {
                return false;
            }
        }

        public static bool? IsCarbonModExists(Functions.ServerTable server)
        {
            if (server.Game != GameServer.RUST.FullName)
            {
                // Game Type not supported
                return null;
            }

            return File.Exists(Functions.ServerPath.GetServersServerFiles(server.ID, "carbon", "managed", "Carbon.dll"));
        }

        public static async Task<bool> CarbonMod(Functions.ServerTable server)
        {
            try
            {
                string basePath = Functions.ServerPath.GetServersServerFiles(server.ID);
                string zipPath = Functions.ServerPath.GetServersServerFiles(server.ID, "Carbon.Windows.Release.zip");
                {
                    await WindowsGSM.Functions.Http.DownloadFileAsync("https://github.com/CarbonCommunity/Carbon/releases/download/production_build/Carbon.Windows.Release.zip", zipPath);
                }

                bool success = await Task.Run(() =>
                {
                    try
                    {
                        // NEXT: never outside the server folder (an entry like ..\..\x is skipped).
                        Engine.Services.AddonService.ExtractSafely(zipPath, basePath);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });

                await Task.Run(() => { try { File.Delete(zipPath); } catch { } });
                return success;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<string> GetOxideModLatestVersion()
        {
            try
            {
                string json = await Http.DownloadStringAsync("https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest");
                return JObject.Parse(json)["tag_name"].ToString();
            }
            catch
            {
                return null;
            }
        }

        public static bool? IsWindrosePlusExists(Functions.ServerTable server)
        {
            if (!IsWindroseServer(server))
            {
                return null;
            }

            string basePath = Functions.ServerPath.GetServersServerFiles(server.ID);
            return Directory.Exists(Path.Combine(basePath, "windrose_plus")) ||
                   File.Exists(Path.Combine(basePath, "StartWindrosePlusServer.bat")) ||
                   File.Exists(Path.Combine(basePath, "R5", "Binaries", "Win64", "dwmapi.dll"));
        }

        public static async Task<bool> WindrosePlus(Functions.ServerTable server)
        {
            try
            {
                string basePath = Functions.ServerPath.GetServersServerFiles(server.ID);
                string zipPath = Functions.ServerPath.GetServersCache(server.ID, "WindrosePlus.zip");

                await WindowsGSM.Functions.Http.DownloadFileAsync(WindrosePlusDownloadUrl, zipPath);

                bool extracted = await Task.Run(() =>
                {
                    try
                    {
                        ExtractZipOverwrite(zipPath, basePath);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (!extracted)
                {
                    return false;
                }

                await Task.Run(() => { try { File.Delete(zipPath); } catch { } });

                string installScript = Path.Combine(basePath, "install.ps1");
                if (!File.Exists(installScript))
                {
                    return false;
                }

                return await RunPowerShellScript(basePath, installScript);
            }
            catch
            {
                return false;
            }
        }

        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(10);

        private static async Task<bool> RunPowerShellScript(string workingDirectory, string scriptPath)
        {
            using (var process = new Process())
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
                process.StartInfo.FileName = "powershell.exe";
                process.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.Start();
                // NEXT: read both streams — unread, a chatty script fills the pipe and never exits. And don't wait forever.
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var timeout = new System.Threading.CancellationTokenSource(ScriptTimeout);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                await Task.WhenAll(output, errors);
                return process.ExitCode == 0;
            }
        }

        private static void ExtractZipOverwrite(string zipPath, string destinationDirectory)
        {
            string destinationRoot = Path.GetFullPath(destinationDirectory);
            if (!destinationRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                destinationRoot += Path.DirectorySeparatorChar;
            }

            using (var file = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(file))
            {
                foreach (var entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    entry.ExtractToFile(destinationPath, true);
                }
            }
        }

        private static bool IsWindroseServer(Functions.ServerTable server)
        {
            string serverGame = server?.Game ?? string.Empty;
            return string.Equals(serverGame, WindroseFullName, StringComparison.OrdinalIgnoreCase) ||
                   serverGame.StartsWith(WindroseFullName + " [", StringComparison.OrdinalIgnoreCase);
        }
    }
}

