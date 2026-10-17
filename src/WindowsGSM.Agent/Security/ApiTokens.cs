using System.Security.Cryptography;
using System.Text;

namespace WindowsGSM.Agent.Security;

/// <summary>One API token as stored: never the token itself, only its SHA-256.</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    /// <summary>The token's first characters ("wgsm_Ab12…"), so people can tell their tokens apart.</summary>
    public string Hint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
}

/// <summary>
/// NEXT: personal API tokens, for scripts and tools (Home Assistant, a Stream Deck, a batch file) to use the API
/// without a browser sign-in: "Authorization: Bearer wgsm_…". A token acts as the user who made it — the same role
/// and server permissions, and it stops working when that user is disabled — but it can't manage tokens, passwords,
/// two-factor or sessions (a leaked token can't lock its owner out or make itself permanent). Stored hashed in
/// configs\next\apitokens.json; the token is shown once, when it's made.
/// </summary>
public sealed class ApiTokens
{
    public const string Prefix = "wgsm_";
    public const int MaxPerUser = 20;

    private readonly string _file;
    private readonly object _gate = new();
    private readonly List<ApiToken> _tokens;

    public ApiTokens(string configDir)
    {
        _file = Path.Combine(configDir, "apitokens.json");
        _tokens = global::WindowsGSM.Hosting.SafeJson.Read<List<ApiToken>>(_file) ?? new();
    }

    public IReadOnlyList<ApiToken> ForUser(string user)
    {
        lock (_gate) { return _tokens.Where(t => t.User.Equals(user, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.CreatedAt).ToList(); }
    }

    /// <summary>Makes a token; returns it (shown once) and its record, or an error.</summary>
    public (string? Token, ApiToken? Record, string? Error) Create(string user, string name, int? days)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 60) { return (null, null, "Give the token a name (up to 60 characters), e.g. \"Home Assistant\"."); }
        if (days is < 1 or > 3650) { return (null, null, "Expiry is 1 to 3650 days, or never."); }
        string token = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var record = new ApiToken
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
            User = user,
            Name = name,
            Hash = HashOf(token),
            Hint = token[..9] + "…",
            ExpiresAt = days is int d ? DateTimeOffset.UtcNow.AddDays(d) : null,
        };
        lock (_gate)
        {
            if (_tokens.Count(t => t.User.Equals(user, StringComparison.OrdinalIgnoreCase)) >= MaxPerUser) { return (null, null, $"You have {MaxPerUser} tokens; delete one first."); }
            _tokens.Add(record);
            Save();
        }
        return (token, record, null);
    }

    /// <summary>The token's record when <paramref name="token"/> is valid and unexpired; notes when it was used.</summary>
    public ApiToken? Validate(string token, string? ip)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 100) { return null; }
        string hash = HashOf(token);
        lock (_gate)
        {
            var t = _tokens.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.Hash), Encoding.ASCII.GetBytes(hash)));
            if (t == null || (t.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow)) { return null; }
            // Written at most once a minute per token, not on every call.
            if (t.LastUsedAt is not { } last || DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(1))
            {
                t.LastUsedAt = DateTimeOffset.UtcNow;
                t.LastUsedIp = ip;
                Save();
            }
            return t;
        }
    }

    public bool Revoke(string user, string id)
    {
        lock (_gate)
        {
            if (_tokens.RemoveAll(t => t.Id == id && t.User.Equals(user, StringComparison.OrdinalIgnoreCase)) == 0) { return false; }
            Save();
            return true;
        }
    }

    /// <summary>A user was deleted or renamed away: their tokens go too.</summary>
    public void RemoveAllForUser(string user)
    {
        lock (_gate)
        {
            if (_tokens.RemoveAll(t => t.User.Equals(user, StringComparison.OrdinalIgnoreCase)) > 0) { Save(); }
        }
    }

    private void Save()
    {
        try { global::WindowsGSM.Hosting.SafeJson.Write(_file, _tokens); }
        catch { /* the next change tries again */ }
    }

    private static string HashOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
