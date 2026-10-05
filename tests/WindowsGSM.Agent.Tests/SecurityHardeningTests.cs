using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Findings from the pre-beta security review, each kept fixed.</summary>
public class SecurityHardeningTests
{
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wgsm-sec-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Passwords_hashed_the_old_way_are_upgraded_at_the_next_sign_in()
    {
        string dir = TempDir();
        try
        {
            var store = new UserStore(dir);
            Assert.Null(store.Create("old", "old-password-1", Role.Owner, true, null));
            Assert.StartsWith("pbkdf2.sha256$600000$", store.Get("old")!.PasswordHash);

            // As saved by an earlier version: 100 000 iterations.
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2("old-password-1", salt, 100_000, HashAlgorithmName.SHA256, 32);
            string old = $"pbkdf2.sha256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            var users = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "users.json")))!.AsArray();
            users[0]!["PasswordHash"] = old;
            File.WriteAllText(Path.Combine(dir, "users.json"), users.ToJsonString());

            var reloaded = new UserStore(dir);
            Assert.True(PasswordHasher.NeedsRehash(reloaded.Get("old")!.PasswordHash));
            Assert.Equal(LoginOutcome.BadCredentials, reloaded.Validate("old", "wrong-password", null, out _));
            Assert.Equal(old, reloaded.Get("old")!.PasswordHash); // a wrong password changes nothing
            Assert.Equal(LoginOutcome.Ok, reloaded.Validate("old", "old-password-1", null, out _));
            Assert.StartsWith("pbkdf2.sha256$600000$", reloaded.Get("old")!.PasswordHash);
            Assert.Equal(LoginOutcome.Ok, new UserStore(dir).Validate("old", "old-password-1", null, out _)); // saved, and still works
        }
        finally { Core.Tests.TestData.DeleteDirectory(dir); }
    }

    [Fact]
    public void A_made_up_username_takes_as_long_as_a_real_one()
    {
        string dir = TempDir();
        try
        {
            var store = new UserStore(dir);
            Assert.Null(store.Create("real", "real-password-1", Role.Owner, true, null));
            _ = PasswordHasher.Dummy; // made once, up front
            // Median of five each, alternating, so a busy machine slows both alike.
            var known = new List<double>(); var unknown = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                store.Validate("real", "wrong", null, out _);
                known.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                store.Validate("nobody" + i, "wrong", null, out _); // a different name each time: no lockout in the way
                unknown.Add(sw.Elapsed.TotalMilliseconds);
            }
            double k = known.OrderBy(t => t).ElementAt(2), u = unknown.OrderBy(t => t).ElementAt(2);
            // Same work either way (a full password check). Before the fix an unknown name took a tiny fraction.
            Assert.True(u > k * 0.25, $"unknown {u:0} ms vs known {k:0} ms");
        }
        finally { Core.Tests.TestData.DeleteDirectory(dir); }
    }

    [Fact]
    public void The_setup_code_is_replaced_after_too_many_wrong_guesses()
    {
        var logged = new List<string>();
        var tokens = new SetupTokens(logged.Add);
        string first = tokens.Current!;
        for (int i = 0; i < SetupTokens.WrongGuessesAllowed - 1; i++) { Assert.False(tokens.Matches("00000000")); }
        Assert.True(tokens.Matches(first.ToLowerInvariant())); // still the same code, any case
        for (int i = 0; i < SetupTokens.WrongGuessesAllowed; i++) { tokens.Matches("00000000"); }
        Assert.NotEqual(first, tokens.Current);
        Assert.False(tokens.Matches(first));
        Assert.Contains(logged, l => l.Contains(tokens.Current!));
    }
}

[Collection("Agent")]
public class SecurityHardeningApiTests
{
    private readonly AgentFixture _f;
    public SecurityHardeningApiTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task Cancelling_someone_elses_server_move_needs_the_install_permission()
    {
        var viewer = await _f.UserAsync("importviewer", Role.Viewer);
        var res = await viewer.DeleteAsync("/api/v2/machines/local/imports/" + new string('a', 32));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
