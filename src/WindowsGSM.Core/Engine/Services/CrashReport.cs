#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// Writes logs/servers/{id}/crash_{timestamp}.log when a server dies unexpectedly: exit code, command
    /// line, the tail of its console output and diagnostic lines from its newest log files. Ported from the
    /// legacy WriteGameServerCrashLog; same file location and layout.
    /// </summary>
    internal static class CrashReport
    {
        /// <summary>Returns the report's path relative to the data folder (or a failure note) and the exit code.</summary>
        /// <summary>What a Windows exit code means, for the ones that point at a cause.</summary>
        public static string? Meaning(int code) => unchecked((uint)code) switch
        {
            0xC0000135 => "A DLL the server needs wasn't found (STATUS_DLL_NOT_FOUND) — usually a missing Visual C++, DirectX or .NET runtime.",
            0xC000007B => "A DLL is the wrong kind for this program (32-bit vs 64-bit, or damaged) — reinstall the runtime, or verify the server's files.",
            0xC0000142 => "A DLL failed to start (STATUS_DLL_INIT_FAILED) — often a damaged runtime, or the PC is out of desktop heap.",
            0xC0000139 => "A DLL is missing a function the server needs — the installed runtime is too old.",
            0xC0000005 => "The server crashed (access violation).",
            0xC0000409 => "The server stopped itself after detecting a fault (stack buffer overrun / fail-fast).",
            0xC00000FD => "The server ran out of stack (stack overflow).",
            0xE0434352 => "An unhandled .NET exception ended the server.",
            0x80008083 or 0x80008096 => "The .NET runtime this server needs isn't installed.",
            0xC000013A => "The server was stopped with Ctrl+C or its window was closed.",
            _ => null,
        };

        public static (string reference, string exitCode) Write(ServerInstance server, Process? process) =>
            Write(server, process, out _);

        /// <summary>
        /// NEXT: as above, plus <paramref name="hint"/> — what the exit code means when it says something useful, and any
        /// Windows runtime the program needs that isn't installed (legacy just printed "Exit Code: -1073741515").
        /// </summary>
        public static (string reference, string exitCode) Write(ServerInstance server, Process? process, out string? hint)
        {
            hint = null;
            string exitCode = string.Empty;
            try
            {
                string logDirectory = ServerPath.GetLogs("servers", server.Id);
                Directory.CreateDirectory(logDirectory);
                string path = Path.Combine(logDirectory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                var log = new StringBuilder();
                log.AppendLine("WindowsGSM crash details");
                log.AppendLine($"Time: {DateTime.Now:MM/dd/yyyy-HH:mm:ss}");
                log.AppendLine($"Server ID: {server.Id}");
                log.AppendLine($"Server Name: {server.Name}");
                log.AppendLine($"Game: {server.Game}");

                if (process != null)
                {
                    try { log.AppendLine($"PID: {process.Id}"); } catch { /* gone */ }
                    try
                    {
                        exitCode = process.ExitCode.ToString();
                        log.AppendLine($"Exit Code: {exitCode}");
                        if (Meaning(process.ExitCode) is string meaning) { log.AppendLine($"Meaning: {meaning}"); hint = meaning; }
                    }
                    catch (Exception e) { log.AppendLine($"Exit Code: unavailable ({e.Message})"); }
                    try
                    {
                        log.AppendLine($"Executable: {process.StartInfo.FileName}");
                        log.AppendLine($"Arguments: {process.StartInfo.Arguments}");
                        log.AppendLine($"Working Directory: {process.StartInfo.WorkingDirectory}");
                    }
                    catch (Exception e) { log.AppendLine($"Process start info unavailable: {e.Message}"); }

                    // Anything the program needs that Windows doesn't have.
                    try
                    {
                        var missing = Functions.Runtimes.For(process.StartInfo.FileName).Where(n => !n.Installed).ToList();
                        if (missing.Count > 0)
                        {
                            log.AppendLine("Missing runtimes:");
                            foreach (var n in missing) { log.AppendLine("  " + n.Detail); }
                            hint = $"{string.Join(" ", missing.Select(n => n.Detail))} Install it from the server's Overview → Health check.";
                        }
                    }
                    catch { /* re-adopted process: no start info */ }
                }

                log.AppendLine();
                log.AppendLine("Captured console output:");
                AppendTail(log, server.Console.Get(), 200);

                log.AppendLine();
                log.AppendLine("Recent server log files:");
                AppendRecentServerLogs(log, server.Id, maxFiles: 3, maxLinesPerFile: 200);

                File.WriteAllText(path, log.ToString());
                return (Path.GetRelativePath(WgsmEnvironment.DataRoot, path), exitCode);
            }
            catch (Exception e)
            {
                return ($"failed to write crash details ({e.Message})", exitCode);
            }
        }

        private static void AppendTail(StringBuilder builder, string? text, int maxLines)
        {
            if (string.IsNullOrWhiteSpace(text)) { builder.AppendLine("(none captured)"); return; }
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines.Skip(Math.Max(lines.Length - maxLines, 0))) { builder.AppendLine(line); }
        }

        private static void AppendRecentServerLogs(StringBuilder builder, string serverId, int maxFiles, int maxLinesPerFile)
        {
            string root = ServerPath.GetServersServerFiles(serverId);
            if (!Directory.Exists(root)) { builder.AppendLine("(serverfiles directory not found)"); return; }

            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(maxFiles)
                .ToList();
            if (files.Count == 0) { builder.AppendLine("(no .log or .txt files found under serverfiles)"); return; }

            string[] markers = { " ERR ", "ERROR", "EXC", "Exception", "Failed", "Invalid", "Could not", "denied", "shutting down" };
            foreach (var file in files)
            {
                builder.AppendLine();
                builder.AppendLine($"--- {Path.GetRelativePath(root, file.FullName)} ({file.LastWriteTime:MM/dd/yyyy-HH:mm:ss}) ---");
                try
                {
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    string text = new StreamReader(stream).ReadToEnd();
                    var diagnostics = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                        .Where(l => markers.Any(m => l.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Take(50)
                        .ToList();
                    if (diagnostics.Count > 0)
                    {
                        builder.AppendLine("Diagnostic lines:");
                        diagnostics.ForEach(l => builder.AppendLine(l));
                        builder.AppendLine();
                        builder.AppendLine("Log tail:");
                    }
                    AppendTail(builder, text, maxLinesPerFile);
                }
                catch (Exception e) { builder.AppendLine($"Failed to read log file: {e.Message}"); }
            }
        }
    }
}
