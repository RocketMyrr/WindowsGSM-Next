using System.Text.Json;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Security;

/// <summary>
/// Who did what, from where (configs/next/audit/audit-yyyyMM.jsonl, one JSON object per line). Append-only;
/// a new file each month keeps any one file small. Every state-changing API call and every sign-in lands here.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _machine;

    public AuditLog(string configDir, string machine)
    {
        _dir = Path.Combine(configDir, "audit");
        _machine = machine;
        Directory.CreateDirectory(_dir);
    }

    public void Write(string? user, string? ip, string action, string? server, bool ok, string? detail = null)
    {
        var entry = new AuditDto(DateTimeOffset.UtcNow, user, ip, action, _machine, server, ok, Trim(detail));
        string line = JsonSerializer.Serialize(entry, Json);
        try
        {
            lock (_gate)
            {
                string file = Path.Combine(_dir, $"audit-{entry.At:yyyyMM}.jsonl");
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
        }
        catch { /* auditing must never fail the action it records */ }
    }

    /// <summary>Newest first, filtered. Reads back through the monthly files until <paramref name="limit"/> is met.</summary>
    public IReadOnlyList<AuditDto> Read(int limit, string? user = null, string? server = null, string? action = null)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var result = new List<AuditDto>();
        string[] files;
        lock (_gate) { files = Directory.GetFiles(_dir, "audit-*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal).ToArray(); }
        foreach (string file in files)
        {
            List<string> lines;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = new List<string>();
                while (reader.ReadLine() is string l) { lines.Add(l); }
            }
            catch { continue; }

            for (int i = lines.Count - 1; i >= 0 && result.Count < limit; i--)
            {
                AuditDto? e;
                try { e = JsonSerializer.Deserialize<AuditDto>(lines[i], Json); } catch { continue; }
                if (e == null) { continue; }
                if (user != null && !string.Equals(e.User, user, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (server != null && e.Server != server) { continue; }
                if (action != null && !e.Action.StartsWith(action, StringComparison.OrdinalIgnoreCase)) { continue; }
                result.Add(e);
            }
            if (result.Count >= limit) { break; }
        }
        return result;
    }

    private static string? Trim(string? s) => s is { Length: > 500 } ? s[..500] + "…" : s;
}
