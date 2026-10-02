#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WindowsGSM.Engine.Events;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// WindowsGSM's own activity log ("Server: Started", "Action: Stop", …). Writes the daily
    /// logs/LyyyyMMdd.log in exactly the legacy line format — existing tooling and the Logs tab read it —
    /// and publishes each line as a <see cref="ServerLogged"/> event for live views.
    /// </summary>
    public sealed class ServerLog
    {
        private readonly object _fileLock = new object();
        private readonly EventBus _events;

        public ServerLog(EventBus events) => _events = events;

        /// <summary>
        /// Logs for a server id ("7" → "[#7]") or a named source ("System", "Web"). Messages starting with
        /// "[ERROR]" or "[NOTICE]" get that level on the event, matching how the legacy UI coloured them.
        /// </summary>
        public void Write(string source, string message)
        {
            string title = int.TryParse(source, out int n) ? $"#{n}" : source;
            string line = $"[{DateTime.Now:MM/dd/yyyy-HH:mm:ss}][{title}] {message}";

            try
            {
                string dir = Path.Combine(WgsmEnvironment.DataRoot, "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"L{DateTime.Now:yyyyMMdd}.log");
                lock (_fileLock)
                {
                    // Share read/write/delete: the web Logs tab and other tools read this file while we append.
                    using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch { /* logging must never take the engine down */ }

            LogLevel level = message.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error
                           : message.StartsWith("[NOTICE]", StringComparison.OrdinalIgnoreCase) ? LogLevel.Notice
                           : LogLevel.Info;
            _events.Publish(new ServerLogged(source, level, message));
        }

        /// <summary>
        /// The last <paramref name="count"/> lines for a server (or source) — today's file first, topped up
        /// from yesterday's so a view isn't blank right after midnight.
        /// </summary>
        public IReadOnlyList<string> Tail(string source, int count)
        {
            string title = int.TryParse(source, out int n) ? $"#{n}" : source;
            string token = $"[{title}]";
            var result = new List<string>();
            try
            {
                for (int daysBack = 0; daysBack <= 1 && result.Count < count; daysBack++)
                {
                    string file = Path.Combine(WgsmEnvironment.DataRoot, "logs", $"L{DateTime.Now.AddDays(-daysBack):yyyyMMdd}.log");
                    result.InsertRange(0, ReadMatchingTail(file, token, count - result.Count));
                }
            }
            catch { /* best effort — return what we have */ }
            return result;
        }

        /// <summary>
        /// Last <paramref name="count"/> lines of <paramref name="file"/> containing <paramref name="token"/>, read
        /// backwards in growing windows instead of scanning the whole all-servers daily log.
        /// </summary>
        private static List<string> ReadMatchingTail(string file, string token, int count)
        {
            if (count <= 0 || !File.Exists(file)) { return new List<string>(); }

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            long window = 1L << 20; // 1 MB, grown ×4 until there are enough matches or the whole file is read

            while (true)
            {
                long take = Math.Min(window, length);
                fs.Seek(length - take, SeekOrigin.Begin);
                var buffer = new byte[take];
                int read = 0;
                while (read < take)
                {
                    int got = fs.Read(buffer, read, (int)(take - read));
                    if (got <= 0) { break; }
                    read += got;
                }

                var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (take < length && lines.Count > 0) { lines.RemoveAt(0); } // first line is cut mid-way

                var matched = lines.Where(l => l.Contains(token)).ToList();
                if (matched.Count >= count || take >= length)
                {
                    return matched.Skip(Math.Max(0, matched.Count - count)).ToList();
                }
                window *= 4;
            }
        }
    }
}
