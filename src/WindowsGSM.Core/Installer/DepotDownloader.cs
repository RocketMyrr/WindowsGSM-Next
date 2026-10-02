using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// DepotDownloader installer/updater (SteamRE/DepotDownloader).
    /// Mirrors SteamCMD.UpdateEx behavior: downloads tool to /bin, runs it, can embed console output.
    /// Honors the same branch/password selection as SteamCMD so the installer choice is transparent.
    /// </summary>
    public class DepotDownloader
    {
        private static readonly string _exeFile = "DepotDownloader.exe";
        private static readonly string _installPath = ServerPath.GetBin("depotdownloader");
        private static readonly string _zipUrl = "https://github.com/SteamRE/DepotDownloader/releases/latest/download/DepotDownloader-windows-x64.zip";

        public string Error;

        public DepotDownloader()
        {
            Directory.CreateDirectory(_installPath);
        }

        private static async Task<bool> Download()
        {
            Directory.CreateDirectory(_installPath);
            var exePath = Path.Combine(_installPath, _exeFile);
            if (File.Exists(exePath)) return true;

            try
            {
                var zipPath = Path.Combine(_installPath, "depotdownloader.zip");
                await WindowsGSM.Functions.Http.DownloadFileAsync(_zipUrl, zipPath);

                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, _installPath, true));
                await Task.Run(() => File.Delete(zipPath));

                return File.Exists(exePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DepotDownloader Download Error: {ex.Message}");
                return false;
            }
        }

        // NEXT: the account saved in the panel (password encrypted), falling back to the legacy userData.txt.
        private static (string user, string pass) GetSteamUsernamePassword() => SteamAccount.Get();

        /// <summary>
        /// Signs in to Steam once with <paramref name="username"/>/<paramref name="password"/> (downloading only the
        /// manifest of a small free app) so DepotDownloader remembers the login. Steam Guard prompts are passed to
        /// <paramref name="onPrompt"/>, whose answer (the code, or null to give up) is typed in.
        /// Returns null when signed in, or what went wrong.
        /// </summary>
        public static async Task<string> SignInAsync(string username, string password, Func<string, Task<string>> onPrompt,
            Action<string> onLine, System.Threading.CancellationToken token)
        {
            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download()) { return $"Couldn't download {_exeFile}."; }
            string dir = Path.Combine(Path.GetTempPath(), "wgsm-steam-signin-" + Guid.NewGuid().ToString("N"));
            var psi = new ProcessStartInfo(exePath,
                $"-app 1007 -manifest-only -dir \"{dir}\" -username \"{EscapeArg(username)}\" -password \"{EscapeArg(password)}\" -remember-password")
            {
                WorkingDirectory = _installPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi };
            var output = new StringBuilder();
            string failure = null;
            try { p.Start(); } catch (Exception ex) { return ex.Message; }
            try
            {
                // Prompts ("Please enter your 2 factor auth code…") end without a newline, so read characters, not lines.
                var stdout = ReadPromptsAsync(p.StandardOutput, output, onLine, async prompt =>
                {
                    string answer = await onPrompt(prompt);
                    if (string.IsNullOrWhiteSpace(answer)) { failure = "Sign-in was cancelled."; try { p.Kill(true); } catch { } return; }
                    await p.StandardInput.WriteLineAsync(answer.Trim());
                    await p.StandardInput.FlushAsync();
                }, token);
                var stderr = ReadPromptsAsync(p.StandardError, output, onLine, null, token);
                await p.WaitForExitAsync(token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return "Sign-in took too long and was stopped."; }
            finally { try { Directory.Delete(dir, true); } catch { } }

            if (failure != null) { return failure; }
            string text = output.ToString();
            if (Regex.IsMatch(text, @"InvalidPassword|Failed to authenticate|AccessDenied|InvalidLoginAuthCode|RateLimitExceeded", RegexOptions.IgnoreCase))
            {
                return Regex.IsMatch(text, "RateLimit", RegexOptions.IgnoreCase) ? "Steam says too many sign-in attempts — wait a while and try again."
                    : Regex.IsMatch(text, "AuthCode|2 factor|TwoFactor", RegexOptions.IgnoreCase) ? "Steam didn't accept the Steam Guard code."
                    : "Steam didn't accept the username or password.";
            }
            return p.ExitCode == 0 ? null : "Steam sign-in didn't finish: " + LastLine(text);
        }

        private static string LastLine(string text)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "no output." : lines[^1].Trim();
        }

        /// <summary>True for a DepotDownloader line that waits for a Steam Guard answer.</summary>
        public static bool IsSteamGuardPrompt(string text) =>
            Regex.IsMatch(text ?? "", @"STEAM GUARD|2 factor auth|two.factor|auth code|sent to the email|Use the Steam Mobile App", RegexOptions.IgnoreCase);

        private static async Task ReadPromptsAsync(StreamReader reader, StringBuilder all, Action<string> onLine, Func<string, Task> onPrompt,
            System.Threading.CancellationToken token)
        {
            var line = new StringBuilder();
            var buffer = new char[512];
            int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    char c = buffer[i];
                    lock (all) { all.Append(c); }
                    if (c == '\n') { Flush(); continue; }
                    if (c != '\r') { line.Append(c); }
                }
                // Text left without a newline that asks for a code: it's waiting on us.
                string pending = line.ToString();
                if (onPrompt != null && pending.TrimEnd().EndsWith(":") && IsSteamGuardPrompt(pending))
                {
                    line.Clear();
                    onLine?.Invoke(pending.Trim());
                    await onPrompt(pending.Trim());
                }
            }
            Flush();

            void Flush()
            {
                string text = line.ToString().Trim();
                line.Clear();
                if (text.Length > 0) { onLine?.Invoke(text); }
            }
        }

        private static void ParseSteamCmdCustom(string custom, out string branch, out string branchPassword)
        {
            branch = null;
            branchPassword = null;

            if (string.IsNullOrWhiteSpace(custom))
                return;

            // token parse for: -beta X -betapassword Y
            var tokens = custom.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];

                if (string.Equals(t, "-beta", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                {
                    branch = tokens[i + 1];
                    i++;
                    continue;
                }

                if (string.Equals(t, "-betapassword", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length)
                {
                    branchPassword = tokens[i + 1];
                    i++;
                    continue;
                }
            }
        }

        /// <summary>
        /// Resolves the branch/password to use for this run.
        /// Priority:
        ///   1. Explicit -beta / -betapassword tokens in <paramref name="custom"/> (game-specific override)
        ///   2. The branch configured via SteamCMD.SetPendingSteamBranch / ServerConfig (shared selection)
        /// "public" is treated as default and emits no -branch flag.
        /// </summary>
        private static (string branch, string password) ResolveBranch(string serverId, string custom)
        {
            // 1. custom takes priority — matches SteamCMD's behavior in GetParameter
            ParseSteamCmdCustom(custom, out var customBranch, out var customPassword);
            if (!string.IsNullOrWhiteSpace(customBranch))
            {
                return (customBranch, customPassword);
            }

            // 2. Fall back to the shared branch selection used by SteamCMD
            if (string.IsNullOrWhiteSpace(serverId))
            {
                return (null, null);
            }

            var configuredBranch = SteamCMD.GetConfiguredSteamBranch(serverId);
            var configuredPassword = SteamCMD.GetConfiguredSteamBranchPassword(serverId);

            return (configuredBranch, configuredPassword);
        }

        private static string BuildArgs(string serverId, string installDir, string appId, bool validate, bool loginAnonymous, string custom)
        {
            // DepotDownloader args:
            // -app <id> -dir "<path>" [-validate] [-branch <n>] [-branchpassword <pw>] -username <u> [-password <p>]
            var sb = new StringBuilder();
            sb.Append($"-app {appId} ");
            sb.Append($"-dir \"{installDir}\" ");

            if (validate)
                sb.Append("-validate ");

            var (branch, branchPassword) = ResolveBranch(serverId, custom);

            // DepotDownloader defaults to "public" implicitly — only emit -branch when non-default
            if (!string.IsNullOrWhiteSpace(branch) &&
                !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append($"-branch \"{EscapeArg(branch)}\" ");

                if (!string.IsNullOrWhiteSpace(branchPassword))
                {
                    sb.Append($"-branchpassword \"{EscapeArg(branchPassword)}\" ");
                }
            }

            if (!loginAnonymous)
            {
                var (u, p) = GetSteamUsernamePassword();
                if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p))
                    return null;

                // -remember-password: reuse the login (and its Steam Guard approval) from the panel's sign-in.
                sb.Append($"-username \"{EscapeArg(u)}\" -password \"{EscapeArg(p)}\" -remember-password ");
            }

            return sb.ToString();
        }

        private static string EscapeArg(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int? ParseProgress(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            // DepotDownloader outputs lines like: "Downloading depot 258550_1... 45.2%"
            if (line.Contains("%"))
            {
                var match = Regex.Match(line, @"(\d+\.?\d*)%");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
                {
                    return (int)percent;
                }
            }

            // SteamCMD outputs: "Update state (0x61) downloading, progress: 45.23 (12345678 / 27000000)"
            if (line.Contains("progress:"))
            {
                var match = Regex.Match(line, @"progress:\s*(\d+\.?\d*)");
                if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
                {
                    return (int)percent;
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads one Steam Workshop item (<paramref name="fileId"/>, a published file of game <paramref name="appId"/>)
        /// into <paramref name="dir"/>. Anonymous unless <paramref name="loginAnonymous"/> is false (then the Steam account
        /// saved for SteamCMD is used — many games only let owners download their Workshop items).
        /// Returns the exit code (null if it couldn't start) and the tail of the output.
        /// </summary>
        public static async Task<(int? ExitCode, string Output)> DownloadWorkshopItemAsync(string appId, string fileId, string dir, bool loginAnonymous,
            Action<string> onLine, Action<int> onProgress, System.Threading.CancellationToken token)
        {
            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download()) { return (null, $"Couldn't download {_exeFile}."); }
            if (!Regex.IsMatch(appId ?? "", @"^\d{1,10}$") || !Regex.IsMatch(fileId ?? "", @"^\d{1,20}$")) { return (null, "Bad Workshop app or item id."); }
            Directory.CreateDirectory(dir);

            var args = new StringBuilder($"-app {appId} -pubfile {fileId} -dir \"{dir}\" ");
            if (!loginAnonymous)
            {
                var (u, pw) = GetSteamUsernamePassword();
                if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(pw)) { return (null, "No Steam account is saved — set one up, or try without the Steam account."); }
                args.Append($"-username \"{EscapeArg(u)}\" -password \"{EscapeArg(pw)}\" -remember-password ");
            }

            var tail = new System.Collections.Generic.Queue<string>();
            var psi = new ProcessStartInfo(exePath, args.ToString())
            {
                WorkingDirectory = _installPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) { return; }
                lock (tail) { tail.Enqueue(line); while (tail.Count > 30) { tail.Dequeue(); } }
                onLine?.Invoke(line);
                if (ParseProgress(line) is int pct) { onProgress?.Invoke(pct); }
            }
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived += (_, e) => Line(e.Data);
            try { p.Start(); } catch (Exception ex) { return (null, ex.Message); }
            p.StandardInput.Close(); // never wait on a Steam Guard prompt nobody can answer
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { await p.WaitForExitAsync(token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            lock (tail) { return (p.ExitCode, string.Join(Environment.NewLine, tail)); }
        }

        public async Task<Process> Install(string serverId, string appId, bool validate = true, bool loginAnonymous = true, bool embedConsole = false, Action<int> progressCallback = null)
        {
            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download())
            {
                Error = $"Fail to download {_exeFile}";
                return null;
            }

            var installDir = ServerPath.GetServersServerFiles(serverId);
            Directory.CreateDirectory(installDir);

            var args = BuildArgs(serverId, installDir, appId, validate, loginAnonymous, custom: null);
            if (args == null)
            {
                Error = "Steam account is not set up";
                return null;
            }

            return StartProcess(serverId, exePath, args, redirectIO: true, pipeToServerConsole: embedConsole, progressCallback);
        }

        public static async Task<(Process, string)> UpdateEx(string serverId, string appId, bool validate = true, bool loginAnonymous = true, string custom = null, bool embedConsole = true, Action<int> progressCallback = null)
        {
            var depot = new DepotDownloader();

            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download())
            {
                return (null, $"Fail to download {_exeFile}");
            }

            var installDir = ServerPath.GetServersServerFiles(serverId);
            Directory.CreateDirectory(installDir);

            var args = BuildArgs(serverId, installDir, appId, validate, loginAnonymous, custom);
            if (args == null)
            {
                return (null, "Steam account is not set up");
            }

            // IMPORTANT: When embedConsole is FALSE, caller must manually read StandardOutput/StandardError using events
            // When embedConsole is TRUE, output is piped via events to ServerConsole and caller should NOT read the streams
            // NEXT: also redirect when an engine job is listening, so it gets output and progress.
            bool redirect = embedConsole || DownloadContext.Current != null;
            var p = depot.StartProcess(serverId, exePath, args, redirectIO: redirect, pipeToServerConsole: embedConsole, progressCallback);
            if (p == null)
            {
                return (null, depot.Error ?? "Failed to start DepotDownloader");
            }

            return (p, null);
        }

        /// <summary>
        /// NEXT: puts back an earlier build — the given manifest of each depot (see <see cref="DepotHistory"/>).
        /// Same process handling as <see cref="UpdateEx"/> (output and progress go to the running job).
        /// </summary>
        public static async Task<(Process, string)> RollbackEx(string serverId, string appId, System.Collections.Generic.IReadOnlyDictionary<uint, ulong> depots, bool loginAnonymous, bool validate = false)
        {
            var exePath = Path.Combine(_installPath, _exeFile);
            if (!File.Exists(exePath) && !await Download()) { return (null, $"Fail to download {_exeFile}"); }
            if (!Regex.IsMatch(appId ?? "", @"^\d{1,10}$") || depots == null || depots.Count == 0) { return (null, "Nothing to roll back to."); }

            var ordered = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, ulong>>(depots);
            ordered.Sort((a, b) => a.Key.CompareTo(b.Key));
            var sb = new StringBuilder($"-app {appId} -dir \"{ServerPath.GetServersServerFiles(serverId)}\" -depot");
            foreach (var d in ordered) { sb.Append(' ').Append(d.Key); }
            sb.Append(" -manifest");
            foreach (var d in ordered) { sb.Append(' ').Append(d.Value); }
            sb.Append(validate ? " -validate " : " ");
            if (!loginAnonymous)
            {
                var (u, p) = GetSteamUsernamePassword();
                if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(p)) { return (null, "Steam account is not set up"); }
                sb.Append($"-username \"{EscapeArg(u)}\" -password \"{EscapeArg(p)}\" -remember-password ");
            }

            var depot = new DepotDownloader();
            var process = depot.StartProcess(serverId, exePath, sb.ToString(), redirectIO: true, pipeToServerConsole: false);
            return process == null ? (null, depot.Error ?? "Failed to start DepotDownloader") : (process, null);
        }

        private Process StartProcess(string serverId, string exePath, string args, bool redirectIO, bool pipeToServerConsole, Action<int> progressCallback = null)
        {
            try
            {
                var firewall = new WindowsFirewall(_exeFile, exePath);
                _ = firewall.IsRuleExist().ContinueWith(async t =>
                {
                    try
                    {
                        if (!t.Result)
                            await firewall.AddRule();
                    }
                    catch { }
                });

                var p = new Process
                {
                    StartInfo =
                    {
                        WorkingDirectory = _installPath,
                        FileName = exePath,
                        Arguments = args,
                        WindowStyle = ProcessWindowStyle.Minimized,
                        UseShellExecute = false
                    },
                    EnableRaisingEvents = true
                };

                if (redirectIO)
                {
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.RedirectStandardInput = true;
                }

                // NEXT: when an engine job is running this download, stream output and progress to it. That
                // also means the output is always drained — Install() redirects output without reading it,
                // so with no reader the pipe fills and DepotDownloader blocks forever.
                var sink = DownloadContext.Current; // capture now: output arrives later on reader threads
                bool eventMode = redirectIO && (pipeToServerConsole || sink != null);

                // CRITICAL: Only use event-based reading in event mode.
                // Otherwise the caller MUST set up their own event handlers before starting.
                if (eventMode)
                {
                    var serverConsole = pipeToServerConsole ? new ServerConsole(serverId) : null;

                    p.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        serverConsole?.AddOutput(_, e);
                        sink?.OnLine(e.Data);

                        var progress = ParseProgress(e.Data);
                        if (progress.HasValue)
                        {
                            progressCallback?.Invoke(progress.Value);
                            sink?.OnProgress(progress.Value);
                        }
                    };

                    p.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        serverConsole?.AddOutput(_, e);
                        sink?.OnLine(e.Data);
                    };
                }

                p.Start();

                // CRITICAL: Start async reads ONLY when using event mode
                if (eventMode)
                {
                    // NEXT: no one can answer a Steam Guard prompt here — fail fast instead of hanging the update.
                    try { p.StandardInput.Close(); } catch { }
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }

                return p;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return null;
            }
        }
    }
}