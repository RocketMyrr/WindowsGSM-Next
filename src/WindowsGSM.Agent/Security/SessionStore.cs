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
    /// <summary>The remembered app that signed this session in, if any (signing out there forgets the app).</summary>
    public string? AppKey { get; set; }
}

/// <summary>
/// A WindowsGSM app on another PC that stays signed in: it was given a key after a normal sign-in (password and
/// two-factor), and signs itself back in with it whenever its session ends. Only a hash is kept here. It lapses
/// after <see cref="SessionStore.AppKeyLifetime"/> unused, and goes with "sign out everywhere", a password change or
/// reset, the account being disabled, or signing out in that app.
/// </summary>
public sealed class AppKey
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string? Device { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
    public string? Ip { get; set; }
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
    private readonly string _keysFile;
    private List<AppKey> _keys = new();

    /// <summary>A remembered app that isn't used for this long has to sign in again.</summary>
    public static readonly TimeSpan AppKeyLifetime = TimeSpan.FromDays(90);
    private DateTimeOffset _lastTouchSave = DateTimeOffset.MinValue;

    public SessionStore(string configDir, TimeSpan lifetime)
    {
        Directory.CreateDirectory(configDir);
        _file = Path.Combine(configDir, "sessions.json");
        _lifetime = lifetime;
        _sessions = global::WindowsGSM.Hosting.SafeJson.Read<List<AgentSession>>(_file, Json) ?? new(); // unreadable → everyone signs in again; nothing worse
        _keysFile = Path.Combine(configDir, "app-keys.json");
        _keys = global::WindowsGSM.Hosting.SafeJson.Read<List<AppKey>>(_keysFile, Json) ?? new(); // unreadable → those apps sign in once more
    }

    public TimeSpan Lifetime => _lifetime;

    public AgentSession Create(string username, string? ip, string? userAgent, string? appKey = null)
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
                AppKey = appKey,
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

    /// <summary>Signs a session out — and forgets the app that signed it in, if one did (signing out there means it).</summary>
    public bool Remove(string? id)
    {
        if (string.IsNullOrEmpty(id)) { return false; }
        lock (_gate)
        {
            var gone = _sessions.Where(x => x.Id == id).ToList();
            _sessions.RemoveAll(x => x.Id == id);
            ForgetKeysOf(gone);
            if (gone.Count > 0) { Save(); }
            return gone.Count > 0;
        }
    }

    /// <summary>Removes one of a user's own sessions (so users can't revoke other people's).</summary>
    public bool RemoveForUser(string username, string id)
    {
        lock (_gate)
        {
            var gone = _sessions.Where(x => x.Id == id && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).ToList();
            _sessions.RemoveAll(gone.Contains);
            ForgetKeysOf(gone);
            if (gone.Count > 0) { Save(); }
            return gone.Count > 0;
        }
    }

    /// <summary>
    /// Signs a user out everywhere except <paramref name="keepId"/> (null = everywhere), remembered apps included —
    /// except the one that session came from.
    /// </summary>
    public int RemoveAllForUser(string username, string? keepId = null)
    {
        lock (_gate)
        {
            string? keepKey = keepId == null ? null : _sessions.FirstOrDefault(x => x.Id == keepId)?.AppKey;
            int n = _sessions.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) && x.Id != keepId);
            int k = _keys.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) && x.Id != keepKey);
            if (n > 0) { Save(); }
            if (k > 0) { SaveKeys(); }
            return n + k;
        }
    }

    // ── Remembered apps ──

    /// <summary>A new key for the app behind session <paramref name="sessionId"/>: "id.secret", shown once.</summary>
    public string CreateAppKey(string username, string? sessionId, string? device, string? ip)
    {
        lock (_gate)
        {
            PruneKeys();
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            // The app's earlier key (it asked again, e.g. after its key was lost) goes: one per app.
            var session = _sessions.FirstOrDefault(x => x.Id == sessionId);
            if (session?.AppKey != null) { _keys.RemoveAll(x => x.Id == session.AppKey); }
            _keys.Add(new AppKey
            {
                Id = id, Username = username, Hash = HashOf(secret), CreatedAt = now, LastUsedAt = now, Ip = ip,
                Device = device is { Length: > 80 } ? device[..80] : device,
            });
            if (session != null) { session.AppKey = id; Save(); }
            SaveKeys();
            return $"{id}.{secret}";
        }
    }

    /// <summary>The key's record if it's valid (and marks it used), else null.</summary>
    public AppKey? UseAppKey(string? key, string? ip)
    {
        if (string.IsNullOrEmpty(key) || key.Split('.') is not [string id, string secret] || id.Length == 0 || secret.Length == 0) { return null; }
        lock (_gate)
        {
            PruneKeys();
            var k = _keys.FirstOrDefault(x => x.Id == id);
            if (k == null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(k.Hash), Convert.FromHexString(HashOf(secret)))) { return null; }
            k.LastUsedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(ip)) { k.Ip = ip; }
            SaveKeys();
            return CloneKey(k);
        }
    }

    /// <summary>Forgets a key — by its holder (with the whole key) or by its user (with its id).</summary>
    public bool RemoveAppKey(string id, string? username = null)
    {
        lock (_gate)
        {
            int n = _keys.RemoveAll(x => x.Id == id && (username == null || string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)));
            if (n > 0) { SaveKeys(); }
            return n > 0;
        }
    }

    public IReadOnlyList<AppKey> AppKeysFor(string username)
    {
        lock (_gate)
        {
            PruneKeys();
            return _keys.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.LastUsedAt).Select(CloneKey).ToList();
        }
    }

    private static string HashOf(string secret) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));

    private void ForgetKeysOf(IEnumerable<AgentSession> sessions)
    {
        var ids = sessions.Select(s => s.AppKey).Where(k => k != null).ToHashSet();
        if (ids.Count > 0 && _keys.RemoveAll(k => ids.Contains(k.Id)) > 0) { SaveKeys(); }
    }

    private void PruneKeys()
    {
        var cutoff = DateTimeOffset.UtcNow - AppKeyLifetime;
        if (_keys.RemoveAll(x => x.LastUsedAt < cutoff) > 0) { SaveKeys(); }
    }

    private void SaveKeys()
    {
        try
        {
            global::WindowsGSM.Hosting.SafeJson.Write(_keysFile, _keys, Json);
        }
        catch { /* worst case an app signs in once more */ }
    }

    private static AppKey CloneKey(AppKey k) => new()
    {
        Id = k.Id, Username = k.Username, Hash = k.Hash, Device = k.Device, CreatedAt = k.CreatedAt, LastUsedAt = k.LastUsedAt, Ip = k.Ip,
    };

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
            global::WindowsGSM.Hosting.SafeJson.Write(_file, _sessions, Json);
        }
        catch { /* sessions are recoverable state; a failed save must not fail a request */ }
    }

    private static AgentSession Clone(AgentSession s) => new()
    {
        Id = s.Id, Username = s.Username, CreatedAt = s.CreatedAt, LastSeenAt = s.LastSeenAt, Ip = s.Ip, UserAgent = s.UserAgent, AppKey = s.AppKey,
    };
}
