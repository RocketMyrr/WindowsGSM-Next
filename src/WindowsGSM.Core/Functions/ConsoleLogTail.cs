#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: after the agent restarts, a running server's console output can't be reconnected (it went to the old
    /// agent's pipe), so the Console tab stayed empty until the server's next restart. When the game also writes its
    /// output to a log file — Unity games' -logfile (Rust…), Minecraft's logs\latest.log — that file is followed instead.
    /// </summary>
    public static class ConsoleLogTail
    {
        /// <summary>The log file the game writes its console to, when there's one we know of.</summary>
        public static string? LogFileFor(ServerInstance s)
        {
            string files = ServerPath.GetServersServerFiles(s.Id);
            if (string.Equals(s.Game, GameServer.MC.FullName, StringComparison.OrdinalIgnoreCase))
            {
                string latest = Path.Combine(files, "logs", "latest.log");
                return File.Exists(latest) ? latest : null;
            }
            return LogFileArgument(s.Config.ServerParam, files);
        }

        /// <summary>-logfile "server.log" (or -logFile x) in the start parameters, as a full path that exists; else null.</summary>
        public static string? LogFileArgument(string? parameters, string baseDir)
        {
            var m = Regex.Match(parameters ?? "", @"(?:^|\s)-log[Ff]ile\s+(?:""([^""]+)""|(\S+))");
            if (!m.Success) { return null; }
            string value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (value == "-" || value.Length == 0) { return null; } // "-": to the console itself
            try
            {
                string full = Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(baseDir, value));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>Follows <paramref name="file"/> into the server's console until <paramref name="p"/> is no longer its process.</summary>
        public static void Follow(ServerInstance s, Process p, string file, int recentLines = 50)
        {
            _ = Task.Run(async () =>
            {
                var console = s.Console;
                console.Add($"[WindowsGSM] Reconnected after the agent restarted: showing {Path.GetFileName(file)}. Commands still work over RCON if it's set up; the live console returns at the server's next restart.");
                long position = 0;
                var partial = new StringBuilder();
                bool first = true;
                while (ReferenceEquals(s.Process, p) && !Exited(p))
                {
                    try
                    {
                        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (first)
                        {
                            position = Math.Max(0, f.Length - 64 * 1024); // the last part of the file, for context
                            first = false;
                        }
                        if (f.Length < position) { position = 0; partial.Clear(); } // started over (a new log)
                        if (f.Length > position)
                        {
                            f.Position = position;
                            var buffer = new byte[f.Length - position];
                            int read = f.Read(buffer, 0, buffer.Length);
                            position += read;
                            partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            string text = partial.ToString();
                            int end = text.LastIndexOf('\n');
                            if (end >= 0)
                            {
                                var lines = text.Substring(0, end).Split('\n');
                                partial.Clear().Append(text.Substring(end + 1));
                                int skip = recentLines > 0 && lines.Length > recentLines ? lines.Length - recentLines : 0;
                                recentLines = 0; // only the first read is trimmed
                                for (int i = skip; i < lines.Length; i++) { console.Add(lines[i].TrimEnd('\r')); }
                            }
                        }
                    }
                    catch (IOException) { /* briefly locked or rotated: next time */ }
                    catch (UnauthorizedAccessException) { return; }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            });
        }

        private static bool Exited(Process p)
        {
            try { return p.HasExited; } catch { return true; }
        }
    }
}
