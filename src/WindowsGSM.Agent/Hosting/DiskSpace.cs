using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

public sealed record DriveDto(string Name, long Total, long Free);
public sealed record ServerUsageDto(string Id, string Name, long Files, long Backups, long Logs);
public sealed record CleanupDto(string Key, string Label, string Description, int Count, long Bytes, bool Suggested);
public sealed record DiskReportDto(IReadOnlyList<DriveDto> Drives, IReadOnlyList<ServerUsageDto> Servers, IReadOnlyList<CleanupDto> Cleanup, DateTimeOffset At);

/// <summary>
/// Disk space: what each server takes (files, backups, logs), and leftovers that can safely go — old logs and
/// crash reports, game crash dumps, unfinished downloads and restores, old app versions, caches. Nothing a server
/// needs to run, no backup this system keeps, and nothing of a server that's busy. A scan walks every server's
/// files, so it runs on demand and is kept for a few minutes.
/// </summary>
public sealed class DiskSpace
{
    private static readonly TimeSpan KeepScan = TimeSpan.FromMinutes(5);
    public const int OldLogDays = 30, OldDumpDays = 7;

    private readonly AgentContext _ctx;
    private readonly SelfUpdate _update;
    private readonly SemaphoreSlim _scanning = new(1, 1);
    private (DiskReportDto Report, Dictionary<string, List<string>> Paths)? _last;

    public DiskSpace(AgentContext ctx, SelfUpdate update) { _ctx = ctx; _update = update; }

    private static string Root => global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot;

    public async Task<DiskReportDto> ReportAsync(bool fresh)
    {
        await _scanning.WaitAsync();
        try
        {
            if (!fresh && _last is { } cached && DateTimeOffset.Now - cached.Report.At < KeepScan) { return cached.Report; }
            var scan = await Task.Run(Scan);
            _last = scan;
            return scan.Report;
        }
        finally { _scanning.Release(); }
    }

    /// <summary>Deletes the chosen categories (from a fresh scan). Returns bytes freed and anything that couldn't go.</summary>
    public async Task<(long Freed, int Files, List<string> Failed)> CleanAsync(IReadOnlyCollection<string> keys)
    {
        await _scanning.WaitAsync();
        try
        {
            var (_, paths) = await Task.Run(Scan); // never act on a stale list
            long freed = 0; int files = 0;
            var failed = new List<string>();
            foreach (string key in keys.Distinct())
            {
                if (!paths.TryGetValue(key, out var list)) { continue; }
                foreach (string path in list)
                {
                    try
                    {
                        if (Directory.Exists(path)) { long size = Size(path); Directory.Delete(path, true); freed += size; files++; }
                        else if (File.Exists(path)) { long size = new FileInfo(path).Length; File.Delete(path); freed += size; files++; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
                }
            }
            _last = null;
            return (freed, files, failed);
        }
        finally { _scanning.Release(); }
    }

    private (DiskReportDto Report, Dictionary<string, List<string>> Paths) Scan()
    {
        var servers = _ctx.Engine.Servers.All.ToList();
        var usage = new List<ServerUsageDto>();
        foreach (var s in servers)
        {
            long files = Size(ServerPath.GetServersServerFiles(s.Id));
            long backups = 0;
            try { backups = Size(BackupSettings.Load(s.Id).ResolveLocation()); } catch { /* unreachable share */ }
            long serverLogs = Size(Path.Combine(Root, "logs", "servers", s.Id));
            usage.Add(new ServerUsageDto(s.Id, s.Name, files, backups, serverLogs));
        }

        var paths = new Dictionary<string, List<string>>();
        var items = new List<CleanupDto>();
        void Add(string key, string label, string description, IEnumerable<string> found, bool suggested = true)
        {
            var list = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            paths[key] = list;
            items.Add(new CleanupDto(key, label, description, list.Count, list.Sum(p => Directory.Exists(p) ? Size(p) : SafeLength(p)), suggested && list.Count > 0));
        }
        var cutoffLogs = DateTime.Now.AddDays(-OldLogDays);
        var cutoffDumps = DateTime.Now.AddDays(-OldDumpDays);
        string logs = Path.Combine(Root, "logs");
        bool Busy(ServerInstance s) => s.State != ServerState.Stopped && s.State != ServerState.Running;

        // Daily app logs, Discord bot logs and crash reports older than a month (the Logs page shows the rest).
        Add("old-logs", $"Logs older than {OldLogDays} days", "The daily app log, Discord bot log, crash reports and scheduled-program output.",
            Files(logs, "*.log", SearchOption.TopDirectoryOnly).Concat(Files(Path.Combine(logs, "servers"), "crash_*.log", SearchOption.AllDirectories))
                .Where(f => File.GetLastWriteTime(f) < cutoffLogs));

        // Game crash dumps: Unreal (Saved/Crashes), Unity and Source minidumps. Big, and only useful right after a crash.
        var dumps = new List<string>();
        foreach (var s in servers.Where(s => !Busy(s)))
        {
            string sf = ServerPath.GetServersServerFiles(s.Id);
            dumps.AddRange(Files(sf, "*.dmp", SearchOption.AllDirectories).Concat(Files(sf, "*.mdmp", SearchOption.AllDirectories))
                .Where(f => File.GetLastWriteTime(f) < cutoffDumps));
            foreach (string crashes in Dirs(sf, "Crashes", SearchOption.AllDirectories).Where(d => d.Contains($"{Path.DirectorySeparatorChar}Saved{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                dumps.AddRange(Directory.EnumerateDirectories(crashes).Where(d => Directory.GetLastWriteTime(d) < cutoffDumps));
            }
        }
        Add("crash-dumps", $"Game crash dumps older than {OldDumpDays} days", "Memory dumps games write when they crash (.dmp, Unreal's Saved\\Crashes). Only useful to the game's developers right after a crash.", dumps);

        // Half-finished work: restores and backups that never completed, and the old files a restore swapped out.
        var leftovers = new List<string>();
        foreach (var s in servers.Where(s => !Busy(s)))
        {
            string dir = ServerPath.GetServers(s.Id);
            leftovers.AddRange(Dirs(dir, ".restore-staging-*", SearchOption.TopDirectoryOnly));
            leftovers.AddRange(Entries(ServerPath.GetServersServerFiles(s.Id), "*.wgsm-old-*", SearchOption.AllDirectories));
            try { leftovers.AddRange(Files(BackupSettings.Load(s.Id).ResolveLocation(), "*.partial", SearchOption.TopDirectoryOnly)); } catch { }
        }
        Add("leftovers", "Unfinished restores and backups", "Pieces left behind when a restore or backup was interrupted.", leftovers);

        // Old app versions and downloads (the current and previous versions stay — "Go back" needs the previous).
        var updates = new List<string>();
        if (_update.InstallRoot is { } install)
        {
            string current = global::WindowsGSM.Hosting.WgsmEnvironment.Version.Split('+')[0].TrimStart('v'); // "v2.0.0-alpha.2" → the folder name
            string? previous = _update.Previous;
            // Whatever the version strings say, never the folder this agent is running from.
            string running = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            foreach (string v in Dirs(Path.Combine(install, "versions"), "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(v);
                string full = Path.GetFullPath(v).TrimEnd(Path.DirectorySeparatorChar);
                if (running.Equals(full, StringComparison.OrdinalIgnoreCase) || running.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && !string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)))
                {
                    updates.Add(v);
                }
            }
            updates.AddRange(Files(Path.Combine(install, "downloads"), "*", SearchOption.TopDirectoryOnly));
        }
        Add("old-versions", "Old WindowsGSM versions and downloads", "Versions older than the one before this one, and update downloads. The current and previous versions are kept.", updates);

        // Caches the agent rebuilds on its own.
        Add("caches", "Picture caches", "Game artwork and plugin logos — downloaded again when needed.",
            Dirs(Path.Combine(Root, "cache"), "art", SearchOption.TopDirectoryOnly).Concat(Dirs(Path.Combine(Root, "cache"), "plugin-icons", SearchOption.TopDirectoryOnly)), suggested: false);

        var drives = new List<DriveDto>();
        foreach (string path in new[] { Root }.Concat(servers.Select(s => ServerPath.GetServers(s.Id))))
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
                if (d.IsReady && !drives.Any(x => x.Name == d.Name)) { drives.Add(new DriveDto(d.Name, d.TotalSize, d.AvailableFreeSpace)); }
            }
            catch { /* network path */ }
        }
        return (new DiskReportDto(drives, usage.OrderByDescending(u => u.Files + u.Backups).ToList(), items, DateTimeOffset.Now), paths);
    }

    // ── File helpers that never throw over one unreadable folder ──


    private static IEnumerable<string> Files(string dir, string pattern, SearchOption option)
    {
        if (!Directory.Exists(dir)) { return Array.Empty<string>(); }
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = option == SearchOption.AllDirectories, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> Dirs(string dir, string pattern, SearchOption option)
    {
        if (!Directory.Exists(dir)) { return Array.Empty<string>(); }
        try { return Directory.EnumerateDirectories(dir, pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = option == SearchOption.AllDirectories, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> Entries(string dir, string pattern, SearchOption option) => Files(dir, pattern, option).Concat(Dirs(dir, pattern, option));

    public static long Size(string dir)
    {
        if (!Directory.Exists(dir)) { return 0; }
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { total += f.Length; } catch { /* gone */ }
            }
        }
        catch { /* unreadable */ }
        return total;
    }

    private static long SafeLength(string file) { try { return new FileInfo(file).Length; } catch { return 0; } }
}
