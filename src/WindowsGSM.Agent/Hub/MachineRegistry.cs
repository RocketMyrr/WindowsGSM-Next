using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Hub;

/// <summary>A machine paired with this hub.</summary>
public sealed class RemoteMachine
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>SHA-256 of the machine's credential (a 256-bit random secret, so a fast hash is fine).</summary>
    public string CredentialHash { get; set; } = string.Empty;
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public string? Version { get; set; }
    /// <summary>What it was running last time we heard — shown while it's offline.</summary>
    public List<ServerDto> Snapshot { get; set; } = new();
    public HostMetricsDto? Metrics { get; set; }
}

/// <summary>
/// The machines paired with this hub (configs/next/machines.json): identity, credential hash, and the last
/// known servers so an offline machine still shows what it was running.
/// </summary>
public sealed class MachineRegistry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();
    private readonly string _file;
    private List<RemoteMachine> _machines = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    public MachineRegistry(string configDir)
    {
        _file = Path.Combine(configDir, "machines.json");
        _machines = global::WindowsGSM.Hosting.SafeJson.Read<List<RemoteMachine>>(_file, Json) ?? new();
    }

    public IReadOnlyList<RemoteMachine> All() { lock (_gate) { return _machines.ToList(); } }

    public RemoteMachine? Get(string id) { lock (_gate) { return _machines.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)); } }

    public bool Any { get { lock (_gate) { return _machines.Count > 0; } } }

    /// <summary>Registers (or re-pairs) a machine and returns its new credential. The credential is shown once.</summary>
    public string Pair(string id, string name, string? version)
    {
        string credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            var m = Get(id);
            if (m == null) { _machines.Add(m = new RemoteMachine { Id = id }); }
            m.Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
            m.Version = version;
            m.PairedAt = DateTimeOffset.UtcNow;
            m.CredentialHash = Hash(credential);
            Save(force: true);
        }
        return credential;
    }

    public bool Verify(string id, string credential)
    {
        var m = Get(id);
        if (m == null || string.IsNullOrEmpty(credential)) { return false; }
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(m.CredentialHash), Encoding.ASCII.GetBytes(Hash(credential)));
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            bool removed = _machines.RemoveAll(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) { Save(force: true); }
            return removed;
        }
    }

    public bool Rename(string id, string name)
    {
        lock (_gate)
        {
            var m = Get(id);
            if (m == null) { return false; }
            m.Name = name.Trim();
            Save(force: true);
            return true;
        }
    }

    /// <summary>Records what the member reported; saved to disk at most every minute.</summary>
    public void Seen(string id, string? name, string? version, List<ServerDto>? snapshot, HostMetricsDto? metrics)
    {
        lock (_gate)
        {
            var m = Get(id);
            if (m == null) { return; }
            m.LastSeen = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(version)) { m.Version = version; }
            if (snapshot != null) { m.Snapshot = snapshot; }
            if (metrics != null) { m.Metrics = metrics; }
            Save(force: false);
        }
    }

    private void Save(bool force)
    {
        if (!force && DateTimeOffset.UtcNow - _lastSave < TimeSpan.FromMinutes(1)) { return; }
        _lastSave = DateTimeOffset.UtcNow;
        try
        {
            global::WindowsGSM.Hosting.SafeJson.Write(_file, _machines, Json);
        }
        catch { /* retried on the next change */ }
    }

    public void Flush() { lock (_gate) { Save(force: true); } }

    private static string Hash(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}

/// <summary>One-time pairing codes (8 characters, 15 minutes, single use). Held in memory only.</summary>
public sealed class PairingCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _codes = new(StringComparer.OrdinalIgnoreCase);

    public (string Code, DateTimeOffset Expires) Create()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(8);
        string raw = new(bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray());
        string code = raw[..4] + "-" + raw[4..];
        var expires = DateTimeOffset.UtcNow + Lifetime;
        lock (_gate)
        {
            foreach (var old in _codes.Where(c => c.Value < DateTimeOffset.UtcNow).Select(c => c.Key).ToList()) { _codes.Remove(old); }
            _codes[code] = expires;
        }
        return (code, expires);
    }

    /// <summary>True (and the code is used up) if it's valid now.</summary>
    public bool Consume(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) { return false; }
        string normalised = code.Trim().ToUpperInvariant().Replace(" ", "");
        if (normalised.Length == 8) { normalised = normalised[..4] + "-" + normalised[4..]; }
        lock (_gate)
        {
            if (!_codes.TryGetValue(normalised, out var expires)) { return false; }
            _codes.Remove(normalised);
            return expires > DateTimeOffset.UtcNow;
        }
    }
}
