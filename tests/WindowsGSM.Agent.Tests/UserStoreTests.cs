using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Account storage on its own: legacy import and the permission maths.</summary>
public class UserStoreTests
{
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wgsm-users-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void Legacy_dashboard_accounts_are_adopted_with_their_passwords_2fa_and_permissions()
    {
        string dir = TempDir();
        string legacy = Path.Combine(dir, "legacy-users.json");
        string secret = Totp.GenerateSecret();
        // Exactly what the legacy WebUserStore (Newtonsoft, numeric enums) writes.
        File.WriteAllText(legacy, $$"""
            [
              { "Username": "boss", "PasswordHash": "{{PasswordHasher.Hash("boss-pass")}}", "Role": 1, "Enabled": true,
                "ServerPermissions": {}, "CanInstall": false, "TwoFactorEnabled": true, "TotpSecret": "{{secret}}",
                "CreatedAt": "2025-01-02T03:04:05Z" },
              { "Username": "friend", "PasswordHash": "{{PasswordHasher.Hash("friend-pass")}}", "Role": 0, "Enabled": true,
                "ServerPermissions": { "3": 131, "5": 1 }, "CanInstall": true, "TwoFactorEnabled": false, "TotpSecret": null }
            ]
            """);

        var store = new UserStore(dir);
        Assert.Equal(2, store.ImportLegacy(legacy, "m-box"));

        var boss = store.Get("boss")!;
        Assert.Equal(Role.Owner, boss.Role);
        Assert.True(boss.TwoFactorEnabled);
        Assert.Equal(LoginOutcome.TwoFactorRequired, store.Validate("boss", "boss-pass", null, out _));

        var friend = store.Get("friend")!;
        Assert.Equal(Role.Member, friend.Role);
        // 131 = View|Start|Console; "may install" became a machine-wide Install grant, so it shows on every server.
        Assert.Equal(Capability.View | Capability.Start | Capability.Console | Capability.Install, friend.On("m-box", "3"));
        Assert.Equal(Capability.View | Capability.Install, friend.On("m-box", "5"));
        Assert.False(friend.Can(Capability.View, "m-box", "4"));
        Assert.True(friend.CanOnMachine(Capability.Install, "m-box"));
        Assert.Equal(LoginOutcome.Ok, store.Validate("friend", "friend-pass", null, out _));

        // Importing again adds nothing.
        Assert.Equal(0, store.ImportLegacy(legacy, "m-box"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Roles_give_a_baseline_and_grants_add_to_it_per_scope()
    {
        var op = new AgentUser { Role = Role.Operator, Grants = new(StringComparer.OrdinalIgnoreCase) { ["m1/7"] = Capability.Files, ["*/*"] = Capability.Schedules } };
        Assert.True(op.Can(Capability.Start | Capability.Console, "m1", "1"));
        Assert.False(op.Can(Capability.Files, "m1", "1"));
        Assert.True(op.Can(Capability.Files, "m1", "7"));
        Assert.False(op.Can(Capability.Files, "m2", "7")); // same server id, other machine
        Assert.True(op.Can(Capability.Schedules, "m2", "9"));
        Assert.False(op.Can(Capability.Kill, "m1", "1"));

        op.Enabled = false;
        Assert.Equal(Capability.None, op.On("m1", "7"));
    }

    [Fact]
    public void An_unreadable_account_file_stops_the_agent_rather_than_starting_with_no_accounts()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "users.json"), "{ not json");
        Assert.Throws<InvalidDataException>(() => new UserStore(dir));
        Directory.Delete(dir, true);
    }
}
