#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// A server's own scripts: one run before every start (rotate logs, clean up files…) and one after every stop.
    /// Legacy had the before-start one ("batchfile"), but only Rust's plugin ran it and nothing else did; here every
    /// game runs both. Only .bat and .ps1 files; choosing one is for admins (it runs a program on the PC), and the
    /// script runs hidden in the server's files folder, its output going to the server's log.
    /// Settings (WindowsGSM.cfg): batchfile (before start — legacy's key, so old setups carry over), afterstopscript,
    /// scripttimeout (seconds, default 60), scriptblocksstart ("1": a failed before-start script stops the start).
    /// </summary>
    public static class ServerScripts
    {
        public const string BeforeStartKey = ServerConfig.SettingName.BatchFile;
        public const string AfterStopKey = "afterstopscript", TimeoutKey = "scripttimeout", BlocksStartKey = "scriptblocksstart";

        /// <summary>The settings that name a program to run: only admins may change them.</summary>
        public static readonly IReadOnlyList<string> AdminKeys = new[] { BeforeStartKey, AfterStopKey };

        /// <summary>The kinds of script that can be run — nothing else (no .exe, .cmd, .vbs…).</summary>
        public static readonly IReadOnlyList<string> Extensions = new[] { ".bat", ".ps1" };

        /// <summary>Most lines of a script's output written to the server's log; the whole of it goes to a file.</summary>
        private const int MaxLoggedLines = 20;

        /// <summary>Why <paramref name="path"/> can't be used as a script, or null when it can (blank means none).</summary>
        public static string? Validate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return null; }
            string p = Clean(path);
            if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(p)) { return "Use the script's full path, e.g. D:\\Scripts\\rotate-logs.bat."; }
            if (!Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) { return "Only .bat and .ps1 scripts can be run."; }
            if (!File.Exists(p)) { return $"There's no file at {p}."; }
            return null;
        }

        /// <summary>The path as stored: trimmed, without surrounding quotes (pasted from Explorer's "Copy as path").</summary>
        public static string Clean(string path) => path.Trim().Trim('"').Trim();

        public static string BeforeStart(ServerConfig cfg) => Clean(cfg.BatchFile ?? string.Empty);
        public static string AfterStop(ServerConfig cfg) => Clean(cfg.GetCustomSetting(AfterStopKey, string.Empty));
        public static int Timeout(ServerConfig cfg) => int.TryParse(cfg.GetCustomSetting(TimeoutKey, string.Empty), out int t) ? Math.Clamp(t, 5, 1800) : 60;
        public static bool BlocksStart(ServerConfig cfg) => cfg.GetCustomSetting(BlocksStartKey, string.Empty) == "1";

        /// <summary>
        /// Runs a script for <paramref name="s"/> and waits for it (up to the server's script timeout). True when it
        /// ran and finished with exit code 0. Never throws; what happened goes to the server's log.
        /// </summary>
        /// <param name="when">"before start" or "after stop" — for the log, and as WGSM_SCRIPT_WHEN.</param>
        public static async Task<bool> RunAsync(ServerInstance s, string path, string when, ServerLog log)
        {
            string name = Path.GetFileName(path);
            string? problem = Validate(path);
            if (problem != null)
            {
                log.Write(s.Id, $"[NOTICE] Script ({when}) not run: {problem}");
                return false;
            }
            string script = Clean(path);
            int timeout = Timeout(s.Config);
            log.Write(s.Id, $"Script ({when}): running {name}");

            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var psi = string.Equals(Path.GetExtension(script), ".ps1", StringComparison.OrdinalIgnoreCase)
                // Windows PowerShell, which every Windows has. The admin chose this script, so it runs even unsigned.
                ? new ProcessStartInfo(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
                // /d: no AutoRun commands from the registry; the doubled quotes keep a path with spaces whole.
                : new ProcessStartInfo(Path.Combine(system, "cmd.exe"), $"/d /c \"\"{script}\"\"");
            string folder = ServerLocation.RealPath(s.Id);
            psi.WorkingDirectory = Directory.Exists(folder) ? folder : Path.GetDirectoryName(script)!;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true; // closed straight away: a "pause" in the script doesn't wait forever
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.Environment["WGSM_SERVER_ID"] = s.Id;
            psi.Environment["WGSM_SERVER_NAME"] = s.Name;
            psi.Environment["WGSM_SERVER_GAME"] = s.Game;
            psi.Environment["WGSM_SERVER_FILES"] = folder;
            psi.Environment["WGSM_SCRIPT_WHEN"] = when == "after stop" ? "stop" : "start";

            // NEXT: robocopy or a "set" in a script put hundreds of lines into the server log; now only the first few.
            var output = new List<string>();
            void Output(object _, DataReceivedEventArgs e)
            {
                if (e.Data == null || e.Data.Length == 0) { return; }
                int n;
                lock (output) { output.Add(e.Data); n = output.Count; }
                if (n <= MaxLoggedLines) { log.Write(s.Id, $"[Script] {e.Data}"); }
            }
            void SaveOutput()
            {
                string[] all;
                lock (output) { all = output.ToArray(); }
                if (all.Length <= MaxLoggedLines) { return; }
                string file = Path.Combine(WgsmEnvironment.DataRoot, "logs", "servers", s.Id, $"script-{(when == "after stop" ? "after-stop" : "before-start")}.log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllLines(file, all);
                    log.Write(s.Id, $"[Script] … {all.Length - MaxLoggedLines} more lines in {Path.GetRelativePath(WgsmEnvironment.DataRoot, file)}");
                }
                catch (Exception ex) { log.Write(s.Id, $"[Script] … {all.Length - MaxLoggedLines} more lines not saved: {ex.Message}"); }
            }

            var started = Stopwatch.StartNew();
            try
            {
                using var p = new Process { StartInfo = psi };
                p.OutputDataReceived += Output;
                p.ErrorDataReceived += Output;
                p.Start();
                p.StandardInput.Close();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    SaveOutput();
                    log.Write(s.Id, $"[NOTICE] Script ({when}): {name} was still running after {timeout} s, so it was stopped (Settings → Scripts → time limit).");
                    return false;
                }
                p.WaitForExit(); // the last output lines
                SaveOutput();
                if (p.ExitCode != 0)
                {
                    log.Write(s.Id, $"[NOTICE] Script ({when}): {name} finished with exit code {p.ExitCode} after {started.Elapsed.TotalSeconds:0.#} s.");
                    return false;
                }
                log.Write(s.Id, $"Script ({when}): {name} finished in {started.Elapsed.TotalSeconds:0.#} s");
                return true;
            }
            catch (Exception ex)
            {
                log.Write(s.Id, $"[NOTICE] Script ({when}): couldn't run {name}: {ex.Message}");
                return false;
            }
        }
    }
}
