using System.Text;
using System.Text.RegularExpressions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>One file under the data folder's logs\ that the Logs page can show.</summary>
/// <param name="Path">Relative to logs\, with forward slashes (e.g. "servers/2/crash_20260930_101500.log").</param>
/// <param name="Kind">app, discord, crash, plugins, schedules or diagnostics.</param>
public sealed record LogFileDto(string Path, string Kind, string Label, long Size, DateTimeOffset Modified, string? Server);

/// <summary>A log's text: the end of the file when it's big.</summary>
public sealed record LogTextDto(string Path, string Text, long Size, bool Truncated);

/// <summary>
/// The log files the agent, the desktop app and the engine write (the legacy names — see INSTALL.md "Where the
/// logs are"), listed by kind for the panel. Reading is limited to files this listing returns, so a request can't
/// reach anything else on disk.
/// </summary>
public static class LogFiles
{
    public const int MaxRead = 1024 * 1024;

    private static readonly Regex AppLog = new(@"^L(\d{8})\.log$", RegexOptions.IgnoreCase);
    private static readonly Regex DiscordLog = new(@"^L(\d{8})-DiscordBot\.log$", RegexOptions.IgnoreCase);
    private static readonly Regex CrashLog = new(@"^CRASH_(\d{8})\.log$", RegexOptions.IgnoreCase);
    private static readonly Regex ServerCrash = new(@"^crash_(\d{8})_(\d{6})\.log$", RegexOptions.IgnoreCase);
    private static readonly Regex ExecLog = new(@"^Server_(\w+?)_.+_execLog\.log$", RegexOptions.IgnoreCase);
    private static readonly Regex AgentLog = new(@"^agent-(\d{8})\.jsonl$", RegexOptions.IgnoreCase);

    public static IReadOnlyList<LogFileDto> List(string dataRoot)
    {
        string logs = Path.Combine(dataRoot, "logs");
        var list = new List<LogFileDto>();
        if (!Directory.Exists(logs)) { return list; }

        foreach (var f in new DirectoryInfo(logs).EnumerateFiles())
        {
            Match m;
            if ((m = AppLog.Match(f.Name)).Success) { list.Add(Dto(f, f.Name, "app", Day(m.Groups[1].Value), null)); }
            else if ((m = DiscordLog.Match(f.Name)).Success) { list.Add(Dto(f, f.Name, "discord", Day(m.Groups[1].Value), null)); }
            else if ((m = CrashLog.Match(f.Name)).Success) { list.Add(Dto(f, f.Name, "crash", $"WindowsGSM · {Day(m.Groups[1].Value)}", null)); }
            else if ((m = ExecLog.Match(f.Name)).Success) { list.Add(Dto(f, f.Name, "schedules", f.Name, m.Groups[1].Value)); }
            else if (f.Name.Equals("pluginsImportError.log", StringComparison.OrdinalIgnoreCase)) { list.Add(Dto(f, f.Name, "plugins", "Plugin import errors", null)); }
        }

        string servers = Path.Combine(logs, "servers");
        if (Directory.Exists(servers))
        {
            foreach (var dir in new DirectoryInfo(servers).EnumerateDirectories())
            {
                foreach (var f in dir.EnumerateFiles("crash_*.log"))
                {
                    var m = ServerCrash.Match(f.Name);
                    if (!m.Success) { continue; }
                    string when = DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var at)
                        ? at.ToString("g") : f.Name;
                    list.Add(Dto(f, $"servers/{dir.Name}/{f.Name}", "crash", when, dir.Name));
                }
            }
        }

        string plugins = Path.Combine(logs, "plugins");
        if (Directory.Exists(plugins))
        {
            foreach (var f in new DirectoryInfo(plugins).EnumerateFiles("*.log"))
            {
                list.Add(Dto(f, "plugins/" + f.Name, "plugins", Path.GetFileNameWithoutExtension(f.Name), null));
            }
        }

        string agent = Path.Combine(logs, "agent");
        if (Directory.Exists(agent))
        {
            foreach (var f in new DirectoryInfo(agent).EnumerateFiles("agent-*.jsonl"))
            {
                var m = AgentLog.Match(f.Name);
                if (m.Success) { list.Add(Dto(f, "agent/" + f.Name, "diagnostics", Day(m.Groups[1].Value), null)); }
            }
        }

        return list.OrderByDescending(l => l.Modified).ToList();
    }

    /// <summary>A listed log's text (the last <see cref="MaxRead"/> bytes of a big one), or null if it isn't a listed log.</summary>
    public static LogTextDto? Read(string dataRoot, string path)
    {
        string? full = Resolve(dataRoot, path);
        if (full == null) { return null; }
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long size = stream.Length;
        bool truncated = size > MaxRead;
        if (truncated) { stream.Seek(size - MaxRead, SeekOrigin.Begin); }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        if (truncated)
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0 && nl < 4096) { text = text[(nl + 1)..]; } // start on a whole line
        }
        return new LogTextDto(path, text, size, truncated);
    }

    /// <summary>The file on disk for a listed log (downloads), or null.</summary>
    public static string? Resolve(string dataRoot, string path)
    {
        var match = List(dataRoot).FirstOrDefault(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : Path.Combine(dataRoot, "logs", match.Path.Replace('/', Path.DirectorySeparatorChar));
    }

    private static LogFileDto Dto(FileInfo f, string path, string kind, string label, string? server) =>
        new(path, kind, label, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), server);

    private static string Day(string yyyymmdd) =>
        DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("D") : yyyymmdd;
}
