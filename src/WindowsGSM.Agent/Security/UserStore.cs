using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Security;

/// <summary>An account on this agent.</summary>
public sealed class AgentUser
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Role Role { get; set; } = Role.Member;
    public bool Enabled { get; set; } = true;

    /// <summary>Extra capabilities by scope: "machine/server", "machine/*" or "*/*".</summary>
    public Dictionary<string, Capability> Grants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TwoFactorEnabled { get; set; }
    public string? TotpSecret { get; set; }

    /// <summary>Last accepted TOTP step — a code is never accepted twice.</summary>
    public long LastTotpStep { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }

    /// <summary>The id passkeys know this account by (random; made when the first passkey is added).</summary>
    public string? PasskeyUserHandle { get; set; }

    /// <summary>Passkeys (fingerprint, face, security key) that sign this account in without a password.</summary>
    public List<Passkey> Passkeys { get; set; } = new();

    /// <summary>Everything this user may do to one server.</summary>
    public Capability On(string machine, string server)
    {
        if (!Enabled) { return Capability.None; }
        var caps = Roles.Baseline(Role);
        foreach (string scope in new[] { "*/*", $"{machine}/*", $"*/{server}", Roles.Scope(machine, server) })
        {
            if (Grants.TryGetValue(scope, out var extra)) { caps |= extra; }
        }
        return caps;
    }

    public bool Can(Capability needed, string machine, string server) => (On(machine, server) & needed) == needed;

    /// <summary>Machine-wide rights (e.g. Install), from the role and "machine/*" / "*/*" grants.</summary>
    public bool CanOnMachine(Capability needed, string machine)
    {
        if (!Enabled) { return false; }
        var caps = Roles.Baseline(Role);
        if (Grants.TryGetValue("*/*", out var all)) { caps |= all; }
        if (Grants.TryGetValue($"{machine}/*", out var m)) { caps |= m; }
        return (caps & needed) == needed;
    }

    public bool IsAdmin => Enabled && Role >= Role.Admin;
    public bool IsOwner => Enabled && Role == Role.Owner;

    /// <summary>Set when this user is acting through the hub (the hub's name), for the audit log. Never stored.</summary>
    [JsonIgnore] public string? Via { get; set; }

    /// <summary>Admins manage lower roles; owners manage everyone.</summary>
    public bool CanManage(Role other) => IsOwner || (IsAdmin && other < Role.Admin);

    internal AgentUser Clone() => new()
    {
        Username = Username, PasswordHash = PasswordHash, Role = Role, Enabled = Enabled,
        Grants = new Dictionary<string, Capability>(Grants, StringComparer.OrdinalIgnoreCase),
        TwoFactorEnabled = TwoFactorEnabled, TotpSecret = TotpSecret, LastTotpStep = LastTotpStep,
        CreatedAt = CreatedAt, LastLoginAt = LastLoginAt, LastLoginIp = LastLoginIp,
        PasskeyUserHandle = PasskeyUserHandle, Passkeys = Passkeys.Select(k => k with { }).ToList(),
    };
}

/// <summary>One passkey: the public half of a key pair the person's device keeps (WebAuthn).</summary>
/// <param name="Id">The credential id (base64url).</param>
/// <param name="PublicKey">COSE public key (base64).</param>
/// <param name="RpId">The address it was made for (passkeys only work on that one).</param>
public sealed record Passkey(string Id, string PublicKey, uint SignCount, string RpId, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt = null);

public enum LoginOutcome { Ok, BadCredentials, TwoFactorRequired, BadCode }

/// <summary>
/// Accounts (configs/next/users.json), with a brute-force lockout per account. Thread-safe; every write is
/// atomic. On first start it adopts the legacy dashboard's accounts (same password and 2FA formats), so
/// nobody has to re-enrol.
/// </summary>
public sealed class UserStore
{
    public const int MinPasswordLength = 8;
    private const int MaxFails = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly Dictionary<string, (int fails, DateTimeOffset until)> _lockouts = new(StringComparer.OrdinalIgnoreCase);
    private List<AgentUser> _users = new();

    public UserStore(string configDir)
    {
        Directory.CreateDirectory(configDir);
        _file = Path.Combine(configDir, "users.json");
        if (File.Exists(_file))
        {
            try { _users = JsonSerializer.Deserialize<List<AgentUser>>(File.ReadAllText(_file), Json) ?? new(); }
            catch (Exception ex) { throw new InvalidDataException($"{_file} can't be read ({ex.Message}). Fix or remove it; the agent won't start with an unreadable account list.", ex); }
        }
    }

    /// <summary>Set by tests to control time.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public int Count { get { lock (_gate) { return _users.Count; } } }

    public bool Exists => File.Exists(_file);

    public IReadOnlyList<AgentUser> All() { lock (_gate) { return _users.Select(u => u.Clone()).ToList(); } }

    public AgentUser? Get(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) { return null; }
        lock (_gate) { return Find(username)?.Clone(); }
    }

    /// <summary>
    /// Checks a sign-in. Wrong passwords (and wrong codes) count towards a 5-minute lockout; while locked out
    /// even the right password fails, and the answer is the same as for a wrong one.
    /// </summary>
    public LoginOutcome Validate(string username, string password, string? code, out AgentUser? user)
    {
        user = null;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(username) || password == null) { return LoginOutcome.BadCredentials; }
            var now = Clock();
            if (_lockouts.TryGetValue(username, out var lk) && now < lk.until) { return LoginOutcome.BadCredentials; }

            var u = Find(username);
            if (u == null || !u.Enabled || !PasswordHasher.Verify(password, u.PasswordHash))
            {
                Fail(username, now);
                return LoginOutcome.BadCredentials;
            }

            if (u.TwoFactorEnabled)
            {
                if (string.IsNullOrWhiteSpace(code)) { return LoginOutcome.TwoFactorRequired; }
                if (!Totp.Verify(u.TotpSecret, code, out long step) || step <= u.LastTotpStep)
                {
                    Fail(username, now);
                    return LoginOutcome.BadCode;
                }
                u.LastTotpStep = step;
                Save();
            }

            _lockouts.Remove(username);
            user = u.Clone();
            return LoginOutcome.Ok;
        }
    }

    private void Fail(string username, DateTimeOffset now)
    {
        _lockouts.TryGetValue(username, out var lk);
        int fails = lk.fails + 1;
        _lockouts[username] = fails >= MaxFails ? (0, now + LockFor) : (fails, DateTimeOffset.MinValue);
    }

    public string? Create(string username, string password, Role role, bool enabled, IReadOnlyDictionary<string, Capability>? grants)
    {
        username = username?.Trim() ?? string.Empty;
        string? problem = CheckUsername(username) ?? CheckPassword(password);
        if (problem != null) { return problem; }
        lock (_gate)
        {
            if (Find(username) != null) { return "A user with that name already exists."; }
            _users.Add(new AgentUser
            {
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                Role = role,
                Enabled = enabled,
                Grants = new Dictionary<string, Capability>(grants ?? new Dictionary<string, Capability>(), StringComparer.OrdinalIgnoreCase),
            });
            Save();
            return null;
        }
    }

    public string? Update(string username, Role role, bool enabled, IReadOnlyDictionary<string, Capability>? grants, string? newPassword)
    {
        if (!string.IsNullOrEmpty(newPassword) && CheckPassword(newPassword) is string bad) { return bad; }
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return "User not found."; }
            bool losesOwner = u.Role == Role.Owner && u.Enabled && (role != Role.Owner || !enabled);
            if (losesOwner && _users.Count(x => x.Role == Role.Owner && x.Enabled) <= 1) { return "There must always be at least one enabled owner."; }

            u.Role = role;
            u.Enabled = enabled;
            if (grants != null) { u.Grants = new Dictionary<string, Capability>(grants, StringComparer.OrdinalIgnoreCase); }
            if (!string.IsNullOrEmpty(newPassword)) { u.PasswordHash = PasswordHasher.Hash(newPassword); }
            Save();
            return null;
        }
    }

    /// <summary>
    /// The recovery path (wgsm-agent --reset-password, on the machine): a new password, the account enabled and
    /// any lockout cleared; optionally two-factor off. No current password needed — see PasswordReset.
    /// </summary>
    public string? ResetCredentials(string username, string newPassword, bool disableTwoFactor)
    {
        if (CheckPassword(newPassword) is string bad) { return bad; }
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return "User not found."; }
            u.PasswordHash = PasswordHasher.Hash(newPassword);
            u.Enabled = true;
            if (disableTwoFactor) { u.TwoFactorEnabled = false; u.TotpSecret = null; }
            _lockouts.Remove(u.Username);
            Save();
            return null;
        }
    }

    // ── Passkeys ──

    /// <summary>The account's passkey user handle, made on first use.</summary>
    public string PasskeyHandle(string username)
    {
        lock (_gate)
        {
            var u = Find(username) ?? throw new InvalidOperationException("User not found.");
            if (string.IsNullOrEmpty(u.PasskeyUserHandle))
            {
                u.PasskeyUserHandle = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
                Save();
            }
            return u.PasskeyUserHandle!;
        }
    }

    public string? AddPasskey(string username, Passkey key)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return "User not found."; }
            if (_users.Any(x => x.Passkeys.Any(k => k.Id == key.Id))) { return "That passkey is already registered."; }
            if (u.Passkeys.Count >= 20) { return "An account can have at most 20 passkeys."; }
            u.Passkeys.Add(key);
            Save();
            return null;
        }
    }

    public bool RemovePasskey(string username, string id)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null || u.Passkeys.RemoveAll(k => k.Id == id) == 0) { return false; }
            Save();
            return true;
        }
    }

    /// <summary>The account a passkey belongs to (by credential id), and the passkey.</summary>
    public (AgentUser User, Passkey Key)? FindPasskey(string id)
    {
        lock (_gate)
        {
            foreach (var u in _users)
            {
                var k = u.Passkeys.FirstOrDefault(x => x.Id == id);
                if (k != null) { return (u.Clone(), k); }
            }
            return null;
        }
    }

    /// <summary>After a passkey sign-in: its new signature counter and when it was used.</summary>
    public void PasskeyUsed(string username, string id, uint signCount)
    {
        lock (_gate)
        {
            var u = Find(username);
            int i = u?.Passkeys.FindIndex(k => k.Id == id) ?? -1;
            if (u == null || i < 0) { return; }
            u.Passkeys[i] = u.Passkeys[i] with { SignCount = signCount, LastUsedAt = DateTimeOffset.UtcNow };
            Save();
        }
    }

    public string? Delete(string username)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return "User not found."; }
            if (u.Role == Role.Owner && _users.Count(x => x.Role == Role.Owner && x.Enabled && x != u) == 0) { return "There must always be at least one enabled owner."; }
            _users.Remove(u);
            Save();
            return null;
        }
    }

    public string? ChangePassword(string username, string current, string next)
    {
        if (CheckPassword(next) is string bad) { return bad; }
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return "User not found."; }
            if (current == null || !PasswordHasher.Verify(current, u.PasswordHash)) { return "Current password is incorrect."; }
            u.PasswordHash = PasswordHasher.Hash(next);
            Save();
            return null;
        }
    }

    /// <summary>Starts 2FA enrolment: stores a new secret (not yet active) and returns it.</summary>
    public string? BeginTwoFactor(string username)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return null; }
            u.TotpSecret = Totp.GenerateSecret();
            u.TwoFactorEnabled = false;
            Save();
            return u.TotpSecret;
        }
    }

    /// <summary>Turns 2FA on (needs a code from the new secret) or off (needs a current code).</summary>
    public bool SetTwoFactor(string username, bool enable, string code)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null || string.IsNullOrWhiteSpace(u.TotpSecret)) { return false; }
            if (enable == u.TwoFactorEnabled) { return enable; }
            if (!Totp.Verify(u.TotpSecret, code, out long step) || step <= u.LastTotpStep) { return false; }
            u.LastTotpStep = step;
            u.TwoFactorEnabled = enable;
            if (!enable) { u.TotpSecret = null; }
            Save();
            return true;
        }
    }

    public void RecordLogin(string username, string? ip)
    {
        lock (_gate)
        {
            var u = Find(username);
            if (u == null) { return; }
            u.LastLoginAt = Clock();
            u.LastLoginIp = ip;
            Save();
        }
    }

    /// <summary>
    /// First start only: adopt the legacy dashboard's users (configs/webdashboard/users.json). Legacy admins
    /// become owners (they had every right, including managing users); members keep their per-server
    /// permissions as grants on this machine, and "may install" becomes an Install grant.
    /// </summary>
    public int ImportLegacy(string legacyUsersFile, string machineId)
    {
        if (!File.Exists(legacyUsersFile)) { return 0; }
        List<LegacyUser>? legacy;
        try { legacy = JsonSerializer.Deserialize<List<LegacyUser>>(File.ReadAllText(legacyUsersFile)); }
        catch { return 0; }
        if (legacy == null) { return 0; }

        lock (_gate)
        {
            int imported = 0;
            foreach (var l in legacy)
            {
                if (string.IsNullOrWhiteSpace(l.Username) || string.IsNullOrWhiteSpace(l.PasswordHash) || Find(l.Username) != null) { continue; }
                var grants = new Dictionary<string, Capability>(StringComparer.OrdinalIgnoreCase);
                foreach (var (server, caps) in l.ServerPermissions ?? new())
                {
                    grants[Roles.Scope(machineId, server)] = (Capability)caps;
                }
                if (l.CanInstall) { grants[$"{machineId}/*"] = Capability.Install; }

                _users.Add(new AgentUser
                {
                    Username = l.Username.Trim(),
                    PasswordHash = l.PasswordHash,
                    Role = l.Role == 1 ? Role.Owner : Role.Member,
                    Enabled = l.Enabled,
                    Grants = grants,
                    TwoFactorEnabled = l.TwoFactorEnabled && !string.IsNullOrWhiteSpace(l.TotpSecret),
                    TotpSecret = l.TwoFactorEnabled ? l.TotpSecret : null,
                    CreatedAt = l.CreatedAt == default ? DateTimeOffset.UtcNow : new DateTimeOffset(DateTime.SpecifyKind(l.CreatedAt, DateTimeKind.Utc)),
                });
                imported++;
            }
            if (imported > 0) { Save(); }
            return imported;
        }
    }

    private sealed class LegacyUser
    {
        public string? Username { get; set; }
        public string? PasswordHash { get; set; }
        public int Role { get; set; } // 0 Member, 1 Admin
        public bool Enabled { get; set; } = true;
        public Dictionary<string, long>? ServerPermissions { get; set; }
        public bool CanInstall { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public string? TotpSecret { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public static string? CheckUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) { return "Username is required."; }
        if (username.Length > 64) { return "Username is too long."; }
        if (username.Any(c => char.IsControl(c) || c == '/' || c == '\\')) { return "Username contains characters that aren't allowed."; }
        return null;
    }

    public static string? CheckPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) { return "Password is required."; }
        if (password.Length < MinPasswordLength) { return $"Password must be at least {MinPasswordLength} characters."; }
        return null;
    }

    private AgentUser? Find(string username) =>
        _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

    private void Save()
    {
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_users, Json));
        File.Move(temp, _file, overwrite: true);
    }
}
