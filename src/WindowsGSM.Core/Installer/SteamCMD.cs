using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using WindowsGSM.Functions;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// This script is very old, so it doesn't written in the best practice, but at least it works
    /// </summary>
    public class SteamCMD
    {
        private static readonly string _exeFile = "steamcmd.exe";
        private static readonly string _installPath = ServerPath.GetBin("steamcmd");
        private static readonly string _userDataPath = Path.Combine(_installPath, "userData.txt");
        private static readonly Dictionary<string, SteamBranchOptions> _pendingSteamBranchOptions = new Dictionary<string, SteamBranchOptions>();
        private static readonly object _steamBranchLock = new object();
        private static string _lastDownloadError;
        private string _param;
        public string Error;

        private class SteamBranchOptions
        {
            public string Branch { get; set; }
            public string Password { get; set; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Corrupt-install detection & recovery
        //
        // SteamCMD occasionally fails with "state is 0x486 after update job"
        // (and a few similar variants). When this happens the local steamapps
        // folder is in a corrupt state that can ONLY be fixed by deleting it
        // and re-running the install/update. The helpers below detect the
        // signature in SteamCMD's output stream, then optionally trigger an
        // auto-recovery via InstallWithRetry / UpdateExWithRetry.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Known SteamCMD failure signatures that indicate the local steamapps
        /// folder is corrupt and must be wiped before another install/update
        /// attempt will succeed.
        /// </summary>
        private static readonly string[] _corruptInstallSignatures = new[]
        {
            "state is 0x486 after update job",
        };

        /// <summary>
        /// True if the most recent install/update process emitted a corrupt-state
        /// signature. Reset to false at the start of each Install call.
        /// </summary>
        public bool CorruptInstallDetected { get; private set; }

        private static bool LineIndicatesCorruptInstall(string line)
        {
            if (string.IsNullOrEmpty(line)) { return false; }
            foreach (var sig in _corruptInstallSignatures)
            {
                if (line.IndexOf(sig, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Subscribes to a process's stdout/stderr and invokes onDetected the
        /// first time a corrupt-state signature appears. The handler must be
        /// attached BEFORE BeginOutputReadLine/BeginErrorReadLine is called.
        /// </summary>
        private static void AttachCorruptInstallWatcher(Process p, Action onDetected)
        {
            void Handler(object sender, DataReceivedEventArgs e)
            {
                if (e.Data != null && LineIndicatesCorruptInstall(e.Data))
                {
                    onDetected();
                }
            }
            p.OutputDataReceived += Handler;
            p.ErrorDataReceived += Handler;
        }

        /// <summary>
        /// Deletes the steamapps folder for a server and recreates it empty.
        /// Used as the recovery step when SteamCMD reports a corrupt state.
        /// Returns true if the wipe succeeded.
        /// </summary>
        private static bool WipeSteamappsFolder(string serverId)
        {
            try
            {
                string steamappsPath = Path.Combine(ServerPath.GetServersServerFiles(serverId), "steamapps");
                if (Directory.Exists(steamappsPath))
                {
                    Directory.Delete(steamappsPath, recursive: true);
                }
                // Recreate so the subsequent SteamCMD run has the folder it expects.
                Directory.CreateDirectory(steamappsPath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WipeSteamappsFolder failed for server {serverId}: {ex}");
                return false;
            }
        }


        public SteamCMD()
        {
            Directory.CreateDirectory(_installPath);
        }

        private static async Task<bool> Download()
        {
            Directory.CreateDirectory(_installPath);
            var exePath = Path.Combine(_installPath, _exeFile);
            if (File.Exists(exePath)) { return true; }

            try
            {
                var zipPath = Path.Combine(_installPath, "steamcmd.zip");
                await WindowsGSM.Functions.Http.DownloadFileAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", zipPath);

                //Extract steamcmd.zip and delete the zip
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, _installPath, true));
                await Task.Run(() =>
                {
                    if (File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                    }
                });

                await Bootstrap();

                _lastDownloadError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastDownloadError = ex.Message;
                return false;
            }
        }

        private static async Task Bootstrap()
        {
            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath)) { return; }

            using (var p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = _installPath,
                    FileName = exePath,
                    Arguments = "+quit",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            })
            {
                p.Start();
                await p.WaitForExitAsync();
            }
        }

        // Old parameter script
        public void SetParameter(string installDir, string modName, string appId, bool validate, bool loginAnonymous = true, string serverId = null)
        {
            _param = $"+force_install_dir \"{installDir}\"";

            if (loginAnonymous)
            {
                _param += " +login anonymous";
            }
            else
            {
                var (steamUser, steamPass) = GetSteamUsernamePassword();

                if (string.IsNullOrWhiteSpace(steamUser) || string.IsNullOrWhiteSpace(steamPass))
                {
                    _param = null;
                    return;
                }

                _param += $" +login \"{steamUser}\" \"{steamPass}\"";
            }

            string steamBranchArguments = GetSteamBranchArguments(serverId);
            _param += (string.IsNullOrWhiteSpace(modName) ? string.Empty : $" +app_set_config 90 mod {modName}") + $" +app_update {appId}" + steamBranchArguments + (validate ? " validate" : "");

            if (appId == "90")
            {
                //Install 4 more times if hlds.exe
                for (int i = 0; i < 4; i++)
                {
                    _param += $" +app_update {appId}" + steamBranchArguments + (validate ? " validate" : string.Empty);
                }
            }

            _param += " +quit";
        }

        // New parameter script
        public static string GetParameter(string forceInstallDir, string appId, bool validate = true, bool loginAnonymous = true, string modName = null, string custom = null, string serverId = null)
        {
            var sb = new StringBuilder();

            // Set up force_install_dir parameter
            sb.Append($"+force_install_dir \"{forceInstallDir}\"");

            // Set up login parameter
            if (loginAnonymous)
            {
                sb.Append(" +login anonymous");
            }
            else
            {
                var (username, password) = GetSteamUsernamePassword();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) { return null; }
                sb.Append($" +login \"{username}\" \"{password}\"");
            }

            // Set up app_set_config parameter
            sb.Append(!string.IsNullOrWhiteSpace(modName) ? $" +app_set_config {appId} mod \"{modName}\"" : string.Empty);

            // Install 4 more times if hlds.exe (appId = 90)
            for (var i = 0; i < 4; i++)
            {
                // Set up app_update parameter
                sb.Append($" +app_update {appId}");

                // Set up branch/password parameters from WindowsGSM.cfg unless a game-specific custom argument already chose a beta branch.
                if (string.IsNullOrWhiteSpace(custom) || !custom.Contains("-beta", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(GetSteamBranchArguments(serverId));
                }

                // Set up app_update extra parameter
                sb.Append(!string.IsNullOrWhiteSpace(custom) ? $" {custom}" : string.Empty); // custom parameter like -beta latest_experimental

                // Set up app_update validate parameter
                sb.Append(validate ? " validate" : string.Empty);

                if (appId != "90") { break; }
            }

            // Set up quit parameter
            sb.Append(" +quit");

            return sb.ToString();
        }

        public static void SetPendingSteamBranch(string serverId, string branch, string password)
        {
            if (string.IsNullOrWhiteSpace(serverId)) { return; }

            lock (_steamBranchLock)
            {
                if (string.IsNullOrWhiteSpace(branch) && string.IsNullOrWhiteSpace(password))
                {
                    _pendingSteamBranchOptions.Remove(serverId);
                    return;
                }

                _pendingSteamBranchOptions[serverId] = new SteamBranchOptions
                {
                    Branch = branch?.Trim() ?? string.Empty,
                    Password = password?.Trim() ?? string.Empty
                };
            }
        }

        public static string GetConfiguredSteamBranch(string serverId)
        {
            if (!string.IsNullOrWhiteSpace(serverId))
            {
                lock (_steamBranchLock)
                {
                    if (_pendingSteamBranchOptions.TryGetValue(serverId, out SteamBranchOptions pending))
                    {
                        return pending.Branch ?? string.Empty;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(serverId) ? string.Empty : ServerConfig.GetSetting(serverId, ServerConfig.SettingName.SteamBranch);
        }

        public static string GetConfiguredSteamBranchPassword(string serverId)
        {
            if (!string.IsNullOrWhiteSpace(serverId))
            {
                lock (_steamBranchLock)
                {
                    if (_pendingSteamBranchOptions.TryGetValue(serverId, out SteamBranchOptions pending))
                    {
                        return pending.Password ?? string.Empty;
                    }
                }
            }

            return string.IsNullOrWhiteSpace(serverId) ? string.Empty : ServerConfig.GetSetting(serverId, ServerConfig.SettingName.SteamBranchPassword);
        }

        public static bool IsSteamBranchChangePending(string serverId)
        {
            string branch = GetConfiguredSteamBranch(serverId);
            string lastInstalled = ServerConfig.GetSetting(serverId, ServerConfig.SettingName.SteamBranchLastInstalled);
            return !string.Equals(branch ?? string.Empty, lastInstalled ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        public static void MarkSteamBranchInstalled(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) { return; }

            ServerConfig.SetSetting(serverId, ServerConfig.SettingName.SteamBranchLastInstalled, GetConfiguredSteamBranch(serverId));
            lock (_steamBranchLock)
            {
                _pendingSteamBranchOptions.Remove(serverId);
            }
        }

        private static string GetSteamBranchArguments(string serverId)
        {
            string branch = GetConfiguredSteamBranch(serverId);
            if (string.IsNullOrWhiteSpace(branch)) { return string.Empty; }

            var sb = new StringBuilder();
            sb.Append($" -beta \"{EscapeSteamCmdArgument(branch)}\"");

            string password = GetConfiguredSteamBranchPassword(serverId);
            if (!string.IsNullOrWhiteSpace(password))
            {
                sb.Append($" -betapassword \"{EscapeSteamCmdArgument(password)}\"");
            }

            return sb.ToString();
        }

        private static string EscapeSteamCmdArgument(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static bool TryReadSteamCredentialLine(string line, out string key, out string value)
        {
            key = null;
            value = null;

            if (string.IsNullOrWhiteSpace(line)) { return false; }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("//")) { return false; }

            string[] keyValue = trimmed.Split(new[] { '=' }, 2);
            if (keyValue.Length != 2 || string.IsNullOrWhiteSpace(keyValue[0])) { return false; }

            key = keyValue[0].Trim();
            value = keyValue[1].Trim().Trim('"');
            return true;
        }

        private static string EnsureOverrideMinOs(string custom)
        {
            if (string.IsNullOrWhiteSpace(custom)) { return "-overrideminos"; }
            return custom.Contains("-overrideminos", StringComparison.OrdinalIgnoreCase) ? custom.Trim() : $"{custom.Trim()} -overrideminos";
        }

        // New parameter script
        // NEXT: the account saved in the panel (password encrypted), falling back to the legacy userData.txt.
        private static (string, string) GetSteamUsernamePassword() => SteamAccount.Get();

        public async Task<Process> Run()
        {
            string exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath))
            {
                //If steamcmd.exe not exists, download steamcmd.exe
                if (!await Download())
                {
                    Error = string.IsNullOrWhiteSpace(_lastDownloadError) ? $"Fail to download {_exeFile}" : $"Fail to download {_exeFile}: {_lastDownloadError}";
                    return null;
                }
            }

            if (_param == null)
            {
                Error = "Steam account is not set";
                return null;
            }

            //Console.WriteLine($"SteamCMD Param: {_param}");

            var firewall = new WindowsFirewall(_exeFile, exePath);
            if (!await firewall.IsRuleExist())
            {
                await firewall.AddRule();
            }

            _param = EnsureOverrideMinOs(_param);
            Process p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = _installPath,
                    FileName = exePath,
                    Arguments = _param,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            p.Start();

            return p;
        }

        public async Task<Process> Install(string serverId, string modName, string appId, bool validate = true, bool loginAnonymous = true)
        {
            // NEXT: DepotDownloader is the standard — see SteamContentPolicy for the rules.
            if (SteamContentPolicy.Choose(serverId, modName) == SteamContentTool.DepotDownloader)
            {
                var depot = new Installer.DepotDownloader();
                // CHANGED: embedConsole = false for installations
                var pr = await depot.Install(serverId, appId, validate, loginAnonymous, embedConsole: false);
                Error = depot.Error;

                // Record the installed build once it finishes, so update checks work from day one.
                SteamContentPolicy.RecordBuildWhenFinished(pr, serverId, appId);
                return pr;
            }

            if (WindowsGSM.Functions.AppSettings.GetBool("AutoRecoverSteamCorruption", false))
            {
                var (p, _) = await InstallWithRetry(serverId, modName, appId, validate, loginAnonymous, retryOnCorrupt: true);
                return p;
            }
            return await InstallInternal(serverId, modName, appId, validate, loginAnonymous);
        }

        private async Task<Process> InstallInternal(string serverId, string modName, string appId, bool validate, bool loginAnonymous)
        {

            Directory.CreateDirectory(Path.Combine(ServerPath.GetServersServerFiles(serverId), "steamapps"));

            SetParameter(ServerPath.GetServersServerFiles(serverId), modName, appId, validate, loginAnonymous, serverId);
            Process p = await Run();
            if (p == null) { return null; }


            CorruptInstallDetected = false;
            AttachCorruptInstallWatcher(p, () => CorruptInstallDetected = true);

            SendEnterPreventFreeze(p);
            return p;
        }

        public async Task<(Process process, bool didRetry)> InstallWithRetry(
            string serverId, string modName, string appId,
            bool validate = true, bool loginAnonymous = true,
            bool retryOnCorrupt = true)
        {
            Process p = await InstallInternal(serverId, modName, appId, validate, loginAnonymous);
            if (p == null) { return (null, false); }

            if (!retryOnCorrupt) { return (p, false); }

            await p.WaitForExitAsync();

            if (!CorruptInstallDetected) { return (p, false); }

            Debug.WriteLine($"SteamCMD reported corrupt install state for server {serverId}. Wiping steamapps and retrying.");
            if (!WipeSteamappsFolder(serverId))
            {
                Error = "Install failed (corrupt state) and steamapps wipe also failed.";
                return (p, false);
            }

            CorruptInstallDetected = false;
            Process retryP = await InstallInternal(serverId, modName, appId, validate, loginAnonymous);
            return (retryP, true);
        }

        public static async Task<(Process, string)> UpdateEx(string serverId, string appId, bool validate = true, bool loginAnonymous = true, string modName = null, string custom = null, bool embedConsole = true, bool useDepotDownloader = true)
        {
            // NEXT: DepotDownloader for every server unless it opted out — the legacy per-server flag
            // defaulted to off for older servers, so they kept updating through SteamCMD.
            if (SteamContentPolicy.Choose(serverId, modName) == SteamContentTool.DepotDownloader)
            {
                var (pr, err) = await Installer.DepotDownloader.UpdateEx(serverId, appId, validate, loginAnonymous, custom: custom, embedConsole: embedConsole);
                if (pr != null) await pr.WaitForExitAsync();

                // Cache remote build on success. NEXT: also require a clean exit — a failed download with
                // no error text used to be recorded as "up to date".
                if (string.IsNullOrWhiteSpace(err) && pr != null && pr.ExitCode == 0)
                {
                    await SteamContentPolicy.RecordBuildAsync(serverId, appId);
                }

                return (pr, err);
            }

            if (WindowsGSM.Functions.AppSettings.GetBool("AutoRecoverSteamCorruption", false))
            {
                var (p, err, _) = await UpdateExWithRetry(serverId, appId, validate, loginAnonymous, modName, custom, embedConsole, useDepotDownloader, retryOnCorrupt: true);
                return (p, err);
            }
            return await UpdateExInternal(serverId, appId, validate, loginAnonymous, modName, custom, embedConsole, useDepotDownloader);
        }

        private static async Task<(Process, string)> UpdateExInternal(string serverId, string appId, bool validate = true, bool loginAnonymous = true, string modName = null, string custom = null, bool embedConsole = true, bool useDepotDownloader = true)
        {
            custom = EnsureOverrideMinOs(custom);
            string param = GetParameter(ServerPath.GetServersServerFiles(serverId), appId, validate, loginAnonymous, modName, custom, serverId);
            if (param == null)
            {
                return (null, "Steam account not set up");
            }

            string exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download())
            {
                return (null, string.IsNullOrWhiteSpace(_lastDownloadError) ? "Unable to download steamcmd" : $"Unable to download steamcmd: {_lastDownloadError}");
            }

            // Fix the SteamCMD issue
            Directory.CreateDirectory(Path.Combine(ServerPath.GetServersServerFiles(serverId), "steamapps"));

            var p = new Process
            {
                StartInfo =
                {
                    WorkingDirectory = _installPath,
                    FileName = exePath,
                    Arguments = param,
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            // Always redirect so the UI can read output if it wants
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            if (embedConsole)
            {
                // ONLY do this if you want UpdateEx to own the console reading.
                var serverConsole = new ServerConsole(serverId);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return (p, null);
            }

            p.Start();
            return (p, null);
        }

        /// <summary>
        /// Runs UpdateEx() and, if SteamCMD reports the corrupt-state error,
        /// automatically wipes steamapps/ and retries the update once.
        /// Returns the final process, any error message, and a flag indicating
        /// whether recovery ran. Set retryOnCorrupt=false to disable auto-retry.
        ///
        /// Note: this wrapper waits for the process to exit before returning,
        /// so callers can check exit code immediately. Don't use this if you
        /// need the process handle while it's still running.
        /// </summary>
        public static async Task<(Process process, string error, bool didRetry)> UpdateExWithRetry(
            string serverId, string appId,
            bool validate = true, bool loginAnonymous = true,
            string modName = null, string custom = null,
            bool embedConsole = true, bool useDepotDownloader = true,
            bool retryOnCorrupt = true)
        {
            // Call the internal worker directly. Going through UpdateEx() would
            // re-enter this method if the AutoRecoverSteamCorruption setting is
            // enabled, causing infinite recursion.
            var (p, err) = await UpdateExInternal(serverId, appId, validate, loginAnonymous, modName, custom, embedConsole, useDepotDownloader);
            if (p == null) { return (null, err, false); }

            if (!retryOnCorrupt)
            {
                await p.WaitForExitAsync();
                return (p, err, false);
            }

            // Attach the watcher and start reading. If embedConsole was true,
            // BeginOutputReadLine was already called and our attempt below
            // will throw — we catch and continue, since the watcher's handlers
            // are already wired to fire on the existing read pump.
            bool corruptDetected = false;
            AttachCorruptInstallWatcher(p, () => corruptDetected = true);
            try { p.BeginOutputReadLine(); } catch { /* already started by embedConsole path */ }
            try { p.BeginErrorReadLine(); } catch { /* already started by embedConsole path */ }

            await p.WaitForExitAsync();

            if (!corruptDetected) { return (p, err, false); }

            // Recovery: wipe steamapps and retry once.
            Debug.WriteLine($"SteamCMD reported corrupt install state for server {serverId} during update. Wiping steamapps and retrying.");
            if (!WipeSteamappsFolder(serverId))
            {
                return (p, "Update failed (corrupt state) and steamapps wipe also failed.", false);
            }

            var (retryP, retryErr) = await UpdateExInternal(serverId, appId, validate, loginAnonymous, modName, custom, embedConsole, useDepotDownloader);
            if (retryP == null) { return (null, retryErr, true); }

            // Wait for the retry to complete so callers see a fully-terminated process.
            try { retryP.BeginOutputReadLine(); } catch { /* already started */ }
            try { retryP.BeginErrorReadLine(); } catch { /* already started */ }
            await retryP.WaitForExitAsync();

            return (retryP, retryErr, true);
        }

        // Old
        public async Task<bool> Update(string serverId, string modName, string appId, bool validate, bool loginAnonymous = true)
        {
            SetParameter(Functions.ServerPath.GetServersServerFiles(serverId), modName, appId, validate, loginAnonymous, serverId);

            Process p = await Run();
            if (p == null)
            {
                return false;
            }

            SendEnterPreventFreeze(p);

            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
            {
                Error = $"Exit code: {p.ExitCode.ToString()}";
                return false;
            }

            return true;
        }

        private async void SendEnterPreventFreeze(Process p)
        {
            try
            {
                await Task.Delay(300000);

                // Send enter 3 times per 3 seconds
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(3000);

                    if (p == null || p.HasExited) { break; }
                    p.StandardInput.WriteLine(string.Empty);
                }

                // Wait 5 minutes
                await Task.Delay(300000);

                // Send enter 3 times per 3 seconds
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(3000);

                    if (p == null || p.HasExited) { break; }
                    p.StandardInput.WriteLine(string.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendEnterPreventFreeze failed: {ex}");
            }
        }

        public string GetLocalBuild(string serverId, string appId)
        {
            // DepotDownloader doesn't write Steam's appmanifest_*.acf, so its local build comes from
            // BuildCache instead. NEXT: prefer the record from the tool that manages this server, but fall
            // back to the other — a server installed by SteamCMD and now updated by DepotDownloader has only
            // an appmanifest until its first DepotDownloader update (legacy reported "not found" there).
            string cached = WindowsGSM.Functions.BuildCache.Read(serverId);
            if (SteamContentPolicy.Choose(serverId) == SteamContentTool.DepotDownloader && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }

            string manifestFile = $"appmanifest_{appId}.acf";
            string manifestPath = Path.Combine(ServerPath.GetServersServerFiles(serverId), "steamapps", manifestFile);

            if (!File.Exists(manifestPath))
            {
                if (!string.IsNullOrWhiteSpace(cached)) { return cached; } // NEXT: DepotDownloader-installed, no appmanifest
                Error = $"Local build not found ({manifestFile} is missing and no DepotDownloader build is recorded).";
                return string.Empty;
            }

            string text;
            try
            {
                text = File.ReadAllText(manifestPath);
            }
            catch (Exception e)
            {
                Error = $"Fail to get local build {e.Message}";
                return string.Empty;
            }

            Regex regex = new Regex("\"buildid\".{1,}\"(.*?)\"");
            var matches = regex.Matches(text);

            if (matches.Count != 1 || matches[0].Groups.Count != 2)
            {
                Error = $"Fail to get local build";
                return string.Empty;
            }

            return matches[0].Groups[1].Value;
        }

        public async Task<string> GetRemoteBuild(string appId, string branch = null)
        {
            // NEXT: ask Steam directly first (SteamKit2, anonymous). Launching steamcmd.exe for every update
            // check was slow and flaky (hence the appinfo.vdf workaround below). SteamCMD stays as a fallback
            // for when that fails — e.g. a password-protected branch Steam won't describe anonymously.
            try
            {
                string build = await SteamAppInfo.GetBuildIdAsync(appId, branch);
                if (!string.IsNullOrWhiteSpace(build)) { return build; }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamAppInfo] {appId}: {ex.Message} — falling back to steamcmd");
            }

            string exePath = Path.Combine(_installPath, "steamcmd.exe");
            if (!File.Exists(exePath))
            {
                //If steamcmd.exe not exists, download steamcmd.exe
                if (!await Download())
                {
                    Error = string.IsNullOrWhiteSpace(_lastDownloadError) ? "Fail to download steamcmd.exe" : $"Fail to download steamcmd.exe: {_lastDownloadError}";
                    return string.Empty;
                }
            }

            WindowsFirewall firewall = new WindowsFirewall("steamcmd.exe", exePath);
            if (!await firewall.IsRuleExist())
            {
                await firewall.AddRule();
            }

            // Removes appinfo.vdf as a fix for not always getting up to date version info from SteamCMD.
            await Task.Run(() =>
            {
                string vdfPath = Path.Combine(_installPath, "appcache", "appinfo.vdf");
                try
                {
                    if (File.Exists(vdfPath))
                    {
                        File.Delete(vdfPath);
                        Debug.WriteLine($"Deleted appinfo.vdf ({vdfPath})");
                    }
                }
                catch
                {
                    Debug.WriteLine($"File to delete appinfo.vdf ({vdfPath})");
                }
            });

            Process p = new Process
            {
                StartInfo =
                {
                    FileName = exePath,
                    //Sometimes it fails to get if appID < 90
                    Arguments = $"+login anonymous -overrideminos +app_info_update 1 +app_info_print {appId} +app_info_print {appId} +app_info_print {appId} +app_info_print {appId} +logoff +quit",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            p.Start();

            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            string targetBranch = string.IsNullOrWhiteSpace(branch) ? "public" : branch;
            Regex regex = new Regex($"\"{Regex.Escape(targetBranch)}\"\\s*\\r?\\n\\s*{{.*?\"buildid\"\\s*\"(.*?)\"", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var matches = regex.Matches(output);

            if (matches.Count < 1 || matches[0].Groups.Count < 2)
            {
                Error = $"Fail to get remote build";
                return string.Empty;
            }

            return matches[0].Groups[1].Value;
        }


        public async Task<List<string>> GetBranches(string appId, bool loginAnonymous = true)
        {
            string exePath = Path.Combine(_installPath, "steamcmd.exe");
            if (!File.Exists(exePath))
            {
                if (!await Download())
                {
                    Error = string.IsNullOrWhiteSpace(_lastDownloadError) ? "Fail to download steamcmd.exe" : $"Fail to download steamcmd.exe: {_lastDownloadError}";
                    return new List<string>();
                }
            }

            string login = "+login anonymous";
            if (!loginAnonymous)
            {
                var (username, password) = GetSteamUsernamePassword();
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    Error = "Steam account is not set";
                    return new List<string>();
                }

                login = $"+login \"{EscapeSteamCmdArgument(username)}\" \"{EscapeSteamCmdArgument(password)}\"";
            }

            var firewall = new WindowsFirewall("steamcmd.exe", exePath);
            if (!await firewall.IsRuleExist())
            {
                await firewall.AddRule();
            }

            Process p = new Process
            {
                StartInfo =
                {
                    FileName = exePath,
                    Arguments = $"{login} -overrideminos +app_info_update 1 +app_info_print {appId} +logoff +quit",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            p.Start();

            // Drain stdout and stderr concurrently. Reading one to the end before the
            // other risks a deadlock if the child fills the unread pipe's buffer.
            Task<string> outputTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(outputTask, errorTask);
            string output = outputTask.Result;
            string error = errorTask.Result;
            await p.WaitForExitAsync();

            if (p.ExitCode != 0)
            {
                Error = string.IsNullOrWhiteSpace(error) ? $"SteamCMD exited with code {p.ExitCode}" : error.Trim();
                return new List<string>();
            }

            int branchesIndex = output.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
            if (branchesIndex < 0)
            {
                Error = "Steam branch list was not found in app info.";
                return new List<string>();
            }

            string branchOutput = output.Substring(branchesIndex);
            var branchNames = Regex.Matches(branchOutput, "\"([^\"]+)\"\\s*\\r?\\n\\s*\\{\\s*\\r?\\n\\s*\"buildid\"", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Where(branch => !string.IsNullOrWhiteSpace(branch))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(branch => string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(branch => branch)
                .ToList();

            if (branchNames.Count == 0)
            {
                Error = "No Steam branches were returned. You can still enter a branch manually.";
            }

            return branchNames;
        }

        public void CreateUserDataTxtIfNotExist()
        {
            if (!File.Exists(_userDataPath))
            {
                File.Create(_userDataPath).Dispose();

                using (TextWriter textwriter = new StreamWriter(_userDataPath))
                {
                    textwriter.WriteLine("// For security and compatibility reasons, WindowsGSM suggests you to create a new steam account.");
                    textwriter.WriteLine("// More info: (https://docs.windowsgsm.com/installer/steamcmd)");
                    textwriter.WriteLine("// ");
                    textwriter.WriteLine("// Username and password - No Steam Guard             (Supported + Auto update supported) (Recommended)");
                    textwriter.WriteLine("// Username and password - Steam Guard via Email      (Supported + Auto update supported)");
                    textwriter.WriteLine("// Username and password - Steam Guard via Smartphone (Supported + Auto update NOT supported)");
                    textwriter.WriteLine("// ");
                    textwriter.WriteLine("steamUser=\"\"");
                    textwriter.WriteLine("steamPass=\"\"");
                }
            }
        }
    }
}