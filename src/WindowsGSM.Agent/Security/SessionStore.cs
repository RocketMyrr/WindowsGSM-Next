using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsGSM.Agent.Security;

public sealed class AgentSession
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>
/// Signed-in browsers/devices (configs/next/sessions.json). The auth cookie carries a session id; a request is
/// only valid while its session exists and hasn't been idle longer than the session lifetime — which is what
/// makes "sign out other devices" and revoking a session take effect immediately.
/// </summary>
public sealed class SessionStore
{
    private static readonly TimeSpan TouchSaveInterval = TimeSpan.FromSeconds(60); // throttle last-seen writes
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly TimeSpan _lifetime;
    private List<AgentSession> _sessions = new();
    private DateTimeOffset _lastTouchSave = DateTimeOffset.MinValue;

    public SessionStore(string configDir, TimeSpan lifetime)
    {
        Directory.CreateDirectory(configDir);
        _file = Path.Combine(configDir, "sessions.json");
        _lifetime = lifetime;
        try { if (File.Exists(_file)) { _sessions = JsonSerializer.Deserialize<List<AgentSession>>(File.ReadAllText(_file), Json) ?? new(); } }
        catch { _sessions = new(); } // unreadable → everyone signs in again; nothing worse
    }

    public TimeSpan Lifetime => _lifetime;

    public AgentSession Create(string username, string? ip, string? userAgent)
    {
        lock (_gate)
        {
            Prune();
            var now = DateTimeOffset.UtcNow;
            var s = new AgentSession
            {
                // 256 random bits: the session id is what the cookie ultimately proves.
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                Username = username,
                CreatedAt = now,
                LastSeenAt = now,
                Ip = ip,
                UserAgent = userAgent is { Length: > 300 } ? userAgent[..300] : userAgent,
            };
            _sessions.Add(s);
            Save();
            return Clone(s);
        }
    }

    /// <summary>True if the session exists, belongs to the user and hasn't expired; refreshes last-seen.</summary>
    public bool Touch(string? id, string? username, string? ip)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(username)) { return false; }
        lock (_gate)
        {
            var s = _sessions.FirstOrDefault(x => x.Id == id);
            if (s == null || !string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase)) { return false; }
            var now = DateTimeOffset.UtcNow;
            if (now - s.LastSeenAt > _lifetime) { _sessions.Remove(s); Save(); return false; }

            s.LastSeenAt = now;
            if (!string.IsNullOrEmpty(ip)) { s.Ip = ip; }
            if (now - _lastTouchSave > TouchSaveInterval) { _lastTouchSave = now; Save(); }
            return true;
        }
    }

    /// <summary>Validity check without refreshing (used by long-lived WebSockets).</summary>
    public bool IsValid(string? id, string? username)
    {
        lock (_gate)
        {
            var s = _sessions.FirstOrDefault(x => x.Id == id);
            return s != null && string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.UtcNow - s.LastSeenAt <= _lifetime;
        }
    }

    public bool Remove(string? id)
    {
        if (string.IsNullOrEmpty(id)) { return false; }
        lock (_gate)
        {
            bool removed = _sessions.RemoveAll(x => x.Id == id) > 0;
            if (removed) { Save(); }
            return removed;
        }
    }

    /// <summary>Removes one of a user's own sessions (so users can't revoke other people's).</summary>
    public bool RemoveForUser(string username, string id)
    {
        lock (_gate)
        {
            bool removed = _sessions.RemoveAll(x => x.Id == id && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) { Save(); }
            return removed;
        }
    }

    /// <summary>Signs a user out everywhere except <paramref name="keepId"/> (null = everywhere).</summary>
    public int RemoveAllForUser(string username, string? keepId = null)
    {
        lock (_gate)
        {
            int n = _sessions.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) && x.Id != keepId);
            if (n > 0) { Save(); }
            return n;
        }
    }

    public IReadOnlyList<AgentSession> ForUser(string username)
    {
        lock (_gate)
        {
            Prune();
            return _sessions.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastSeenAt).Select(Clone).ToList();
        }
    }

    public IReadOnlyList<AgentSession> All()
    {
        lock (_gate) { Prune(); return _sessions.OrderByDescending(x => x.LastSeenAt).Select(Clone).ToList(); }
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - _lifetime;
        if (_sessions.RemoveAll(x => x.LastSeenAt < cutoff) > 0) { Save(); }
    }

    private void Save()
    {
        try
        {
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_sessions, Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch { /* sessions are recoverable state; a failed save must not fail a request */ }
    }

    private static AgentSession Clone(AgentSession s) => new()
    {
        Id = s.Id, Username = s.Username, CreatedAt = s.CreatedAt, LastSeenAt = s.LastSeenAt, Ip = s.Ip, UserAgent = s.UserAgent,
    };
}
