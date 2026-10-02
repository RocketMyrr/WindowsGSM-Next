using System.Text.Json;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Labels people put on servers ("EU", "friends", "modded") to find and filter them. Kept by the agent in
/// configs/next/tags.json rather than in the server's WindowsGSM.cfg, which the legacy app also reads.
/// </summary>
public sealed class ServerTags
{
    public const int MaxPerServer = 8;
    public const int MaxLength = 24;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file;
    private Dictionary<string, List<string>> _tags;

    public ServerTags(string configDir)
    {
        _file = Path.Combine(configDir, "tags.json");
        try { _tags = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_file)) ?? new() : new(); }
        catch { _tags = new(); }
    }

    public IReadOnlyList<string> Get(string server)
    {
        lock (_gate) { return _tags.TryGetValue(server, out var t) ? t.ToList() : Array.Empty<string>(); }
    }

    /// <summary>Replaces a server's tags (trimmed, de-duplicated). Returns an error, or null.</summary>
    public string? Set(string server, IEnumerable<string>? tags)
    {
        var clean = (tags ?? Enumerable.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (clean.Count > MaxPerServer) { return $"Up to {MaxPerServer} tags per server."; }
        if (clean.Any(t => t.Length > MaxLength)) { return $"Keep tags short ({MaxLength} characters at most)."; }
        if (clean.Any(t => t.Any(c => char.IsControl(c) || c is ',' or '<' or '>'))) { return "Tags can't contain commas or angle brackets."; }
        lock (_gate)
        {
            if (clean.Count == 0) { _tags.Remove(server); } else { _tags[server] = clean; }
            Save();
        }
        return null;
    }

    public void Forget(string server)
    {
        lock (_gate) { if (_tags.Remove(server)) { Save(); } }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_tags, Json));
            File.Move(_file + ".tmp", _file, overwrite: true);
        }
        catch { /* next change retries */ }
    }
}
