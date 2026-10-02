using WindowsGSM.Agent.Api;
using System.Text;
using System.Text.Json;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>One saved earlier version of a file.</summary>
/// <param name="Path">"settings" for the server's settings (WindowsGSM.cfg), else the path inside serverfiles.</param>
/// <param name="Why">What replaced it: "Game config", "File editor", "Settings", "Put back".</param>
public sealed record ConfigVersion(string Id, string Path, DateTimeOffset At, string? By, string Why, long Size);

/// <summary>
/// Config history: before the panel changes a file — a game config, a file in the editor, the server's settings —
/// the version being replaced is kept (servers/&lt;id&gt;/configs/history). "What changed" compares any version with
/// the file as it is now, and "Put back" restores one (keeping the current version too, so that's undoable).
/// The last <see cref="KeepPerFile"/> versions of each file are kept; files over 1 MB aren't (they aren't configs).
/// </summary>
public sealed class ConfigHistory
{
    public const string SettingsPath = "settings";
    public const int KeepPerFile = 30;
    private const long MaxSize = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    private static string Dir(string id) => ServerPath.GetServersConfigs(id, "history");
    private static string Index(string id) => Path.Combine(Dir(id), "index.jsonl");

    /// <summary>The file on disk for a history path (settings → WindowsGSM.cfg).</summary>
    public static string FileFor(AgentContext ctx, string id, string path) =>
        path == SettingsPath ? ServerPath.GetServersConfigs(id, "WindowsGSM.cfg") : ctx.Engine.Files.Resolve(id, path);

    /// <summary>
    /// Keeps the current content of <paramref name="file"/> before it's replaced — unless <paramref name="replacement"/>
    /// (when known) is the same thing, i.e. nothing is changing. Never throws — history is a bonus.
    /// </summary>
    public void Keep(string id, string path, string file, string? by, string why, string? replacement = null)
    {
        try
        {
            if (!File.Exists(file)) { return; }
            var info = new FileInfo(file);
            if (info.Length > MaxSize) { return; }
            byte[] content = File.ReadAllBytes(file);
            if (replacement != null && Decode(content) == replacement) { return; }
            lock (_gate)
            {
                var all = ReadIndex(id);
                // Saving the same thing twice keeps one copy.
                var last = all.LastOrDefault(v => v.Path == path);
                if (last != null && File.Exists(Blob(id, last.Id)) && File.ReadAllBytes(Blob(id, last.Id)).AsSpan().SequenceEqual(content)) { return; }

                Directory.CreateDirectory(Dir(id));
                string vid = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}";
                File.WriteAllBytes(Blob(id, vid), content);
                var entry = new ConfigVersion(vid, path, DateTimeOffset.Now, by, why, content.Length);
                File.AppendAllText(Index(id), JsonSerializer.Serialize(entry, Json) + "\n");

                // Only the newest versions of each file stay.
                var mine = all.Where(v => v.Path == path).Append(entry).ToList();
                if (mine.Count > KeepPerFile)
                {
                    var drop = mine.Take(mine.Count - KeepPerFile).Select(v => v.Id).ToHashSet();
                    foreach (string d in drop) { try { File.Delete(Blob(id, d)); } catch { } }
                    WriteIndex(id, all.Append(entry).Where(v => !drop.Contains(v.Id)));
                }
            }
        }
        catch { /* no history this time */ }
    }

    /// <summary>Versions, newest first (optionally of one file).</summary>
    public IReadOnlyList<ConfigVersion> List(string id, string? path = null)
    {
        lock (_gate)
        {
            return ReadIndex(id).Where(v => path == null || string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase))
                .Where(v => File.Exists(Blob(id, v.Id))).OrderByDescending(v => v.At).ToList();
        }
    }

    public (ConfigVersion Version, string Content)? Get(string id, string versionId)
    {
        var v = List(id).FirstOrDefault(x => x.Id == versionId);
        if (v == null) { return null; }
        return (v, Decode(File.ReadAllBytes(Blob(id, v.Id))));
    }

    /// <summary>Writes a version back (keeping the current one first). Returns the file's path inside the server.</summary>
    public string Restore(AgentContext ctx, string id, string versionId, string? by)
    {
        var v = List(id).FirstOrDefault(x => x.Id == versionId) ?? throw new FileNotFoundException("That version isn't there any more.");
        string file = FileFor(ctx, id, v.Path);
        Keep(id, v.Path, file, by, "Put back");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + ".wgsm-save";
        File.WriteAllBytes(temp, File.ReadAllBytes(Blob(id, v.Id)));
        File.Move(temp, file, overwrite: true);
        if (v.Path == SettingsPath) { ctx.Engine.Servers.Get(id)?.ReloadConfig(); }
        return v.Path;
    }

    /// <summary>Text for showing (a byte-order mark is dropped; non-UTF-8 files read as Latin-1 rather than failing).</summary>
    public static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string Blob(string id, string versionId) => Path.Combine(Dir(id), versionId + ".bak");

    private static List<ConfigVersion> ReadIndex(string id)
    {
        var list = new List<ConfigVersion>();
        if (!File.Exists(Index(id))) { return list; }
        foreach (string line in File.ReadAllLines(Index(id)))
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            try { if (JsonSerializer.Deserialize<ConfigVersion>(line, Json) is { } v) { list.Add(v); } } catch { /* torn line */ }
        }
        return list;
    }

    private static void WriteIndex(string id, IEnumerable<ConfigVersion> versions)
    {
        string temp = Index(id) + ".tmp";
        File.WriteAllLines(temp, versions.Select(v => JsonSerializer.Serialize(v, Json)));
        File.Move(temp, Index(id), overwrite: true);
    }
}
