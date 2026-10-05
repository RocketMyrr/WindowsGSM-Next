using System.IO.Compression;
using System.Text.Json.Nodes;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Backing up and restoring WindowsGSM's own setup.</summary>
public class SetupArchiveTests : IDisposable
{
    private readonly string _from = Path.Combine(Path.GetTempPath(), "wgsm-setup-from-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _to = Path.Combine(Path.GetTempPath(), "wgsm-setup-to-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();

    public void Dispose() { TestData.DeleteDirectory(_from); TestData.DeleteDirectory(_to); }

    private static void Write(string root, string rel, string text)
    {
        string f = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, text);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A small but complete setup on the "old PC".</summary>
    private void OldPc()
    {
        string token = Secret.Protect("bot-token-123");
        Write(_from, "configs/next/agent.json", """{ "MachineId": "m-old", "MachineName": "Old box", "Port": 9000, "HubUrl": "https://hub:8971/", "HubCredential": "x" }""");
        Write(_from, "configs/next/users.json", """[{ "Username": "owner", "PasswordHash": "pbkdf2.sha256$600000$a$b", "Role": "Owner", "Enabled": true }]""");
        Write(_from, "configs/next/discord-bot.json", $$"""{ "Enabled": true, "Token": "{{token}}", "GuildId": "1" }""");
        Write(_from, "configs/next/automations.json", """[{ "Id": "a1", "Name": "Empty restart" }]""");
        Write(_from, "configs/next/templates/t1.json", """{ "Id": "t1", "Name": "Rust base" }""");
        Write(_from, "configs/next/sessions.json", "[]");                       // not part of a setup
        Write(_from, "configs/next/history.db", "binary");                      // nor is history
        Write(_from, "configs/next/automations.json.bak", "old");               // nor leftovers
        Write(_from, "plugins/MyGame.cs/MyGame.cs", "// plugin");
        Write(_from, "servers/1/configs/WindowsGSM.cfg", "servername=\"One\"");
        Write(_from, "servers/1/configs/schedules.json", """[{ "Cron": "0 6 * * *", "Action": "Restart" }]""");
        Write(_from, "servers/1/configs/history/123.bak", "old version");     // config history isn't the setup
        Write(_from, "servers/2/configs/WindowsGSM.cfg", "servername=\"Two\"");
        Write(_from, "servers/1/serverfiles/world.sav", "game data");           // game files never are
    }

    private MemoryStream Export(string? passphrase)
    {
        var ms = new MemoryStream();
        SetupArchive.Export(_from, ms, "v2.0.0-beta.1", "m-old", "Old box", passphrase);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void The_backup_has_the_setup_and_nothing_else()
    {
        OldPc();
        using var ms = Export(null);
        using var zip = new ZipArchive(ms);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(x => x).ToList();
        Assert.Equal(new[]
        {
            "configs/next/agent.json", "configs/next/automations.json", "configs/next/discord-bot.json", "configs/next/templates/t1.json",
            "configs/next/users.json", "plugins/MyGame.cs/MyGame.cs", "servers/1/configs/schedules.json", "servers/1/configs/WindowsGSM.cfg",
            "servers/2/configs/WindowsGSM.cfg", "wgsm-setup.json",
        }.OrderBy(x => x), names);
        // Without a passphrase, the token is left out — not copied in a form only the old account could read.
        string bot = new StreamReader(zip.GetEntry("configs/next/discord-bot.json")!.Open()).ReadToEnd();
        Assert.DoesNotContain("dpapi:", bot);
        Assert.DoesNotContain("bot-token-123", bot);
        var manifest = JsonNode.Parse(new StreamReader(zip.GetEntry("wgsm-setup.json")!.Open()).ReadToEnd())!;
        Assert.Equal(1, manifest["SecretsLeftOut"]!.GetValue<int>());
    }

    [Fact]
    public void Passwords_travel_with_a_passphrase_and_come_back_encrypted_for_this_account()
    {
        OldPc();
        using var ms = Export("correct horse battery");
        string bot;
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true)) { bot = new StreamReader(zip.GetEntry("configs/next/discord-bot.json")!.Open()).ReadToEnd(); }
        Assert.Contains("wgsm-export:", bot);
        Assert.DoesNotContain("bot-token-123", bot);

        ms.Position = 0;
        var wrong = Assert.Throws<InvalidDataException>(() => SetupArchive.Stage(_to, ms, "wrong passphrase", false, "owner"));
        Assert.Contains("passphrase isn't the one", wrong.Message);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => SetupArchive.Stage(_to, ms, null, false, "owner"));
        Assert.Null(SetupArchive.PendingRestore(_to)); // nothing half-staged

        ms.Position = 0;
        SetupArchive.Stage(_to, ms, "correct horse battery", false, "owner");
        SetupArchive.ApplyPending(_to, _log.Add);
        var restored = JsonNode.Parse(Read(_to, "configs/next/discord-bot.json"))!;
        string stored = restored["Token"]!.GetValue<string>();
        Assert.StartsWith("dpapi:", stored);
        Assert.Equal("bot-token-123", Secret.Unprotect(stored));
    }

    [Fact]
    public void Restoring_waits_for_the_next_start_backs_up_first_and_keeps_this_pcs_identity()
    {
        OldPc();
        Write(_to, "configs/next/agent.json", """{ "MachineId": "m-new", "MachineName": "New box" }""");
        Write(_to, "configs/next/automations.json", """[{ "Id": "mine", "Name": "This PC's own" }]""");
        Directory.CreateDirectory(Path.Combine(_to, "servers", "1")); // server 1 exists here, server 2 doesn't

        using var ms = Export(null);
        var staged = SetupArchive.Stage(_to, ms, null, keepIdentity: false, "owner");
        Assert.Equal(new[] { "2" }, staged.ServersSkipped);
        Assert.Contains("This PC's own", Read(_to, "configs/next/automations.json")); // nothing changed yet
        Assert.NotNull(SetupArchive.PendingRestore(_to));

        SetupArchive.ApplyPending(_to, _log.Add);

        Assert.Contains("Empty restart", Read(_to, "configs/next/automations.json"));
        Assert.Contains("Rust base", Read(_to, "configs/next/templates/t1.json"));
        Assert.Contains("0 6 * * *", Read(_to, "servers/1/configs/schedules.json"));
        Assert.False(Directory.Exists(Path.Combine(_to, "servers", "2")));           // never creates servers
        var agent = JsonNode.Parse(Read(_to, "configs/next/agent.json"))!;
        Assert.Equal("m-new", agent["MachineId"]!.GetValue<string>());             // this PC stays itself…
        Assert.Equal(9000, agent["Port"]!.GetValue<int>());                          // …with the backup's settings
        Assert.Null(agent["HubUrl"]);                                                 // and isn't joined to the old PC's hub
        Assert.Single(Directory.GetFiles(Path.Combine(_to, "backups"), "wgsm-settings-before-restore-*.zip"));
        Assert.Null(SetupArchive.PendingRestore(_to));
        Assert.False(Directory.Exists(Path.Combine(_to, "configs", "next-restore")));
        var result = JsonNode.Parse(Read(_to, "configs/next/last-restore.json"))!;
        Assert.Contains(result["Notes"]!.AsArray(), n => n!.GetValue<string>().Contains("aren't on this PC"));

        SetupArchive.ApplyPending(_to, _log.Add); // nothing pending: nothing happens
        Assert.Single(Directory.GetFiles(Path.Combine(_to, "backups"), "wgsm-settings-before-restore-*.zip"));
    }

    [Fact]
    public void Restoring_without_passwords_keeps_the_ones_this_pc_already_has()
    {
        OldPc();
        // This PC already has the bot set up, and a channel with a webhook.
        Write(_to, "configs/next/discord-bot.json", $$"""{ "Enabled": true, "Token": "{{Secret.Protect("this-pcs-token")}}", "GuildId": "9" }""");
        Write(_to, "configs/next/notify-channels.json", $$"""[{ "Id": "c1", "Name": "Mine", "Url": "{{Secret.Protect("https://discord.com/api/webhooks/1/mine")}}" }]""");
        Write(_from, "configs/next/notify-channels.json", $$"""[{ "Id": "c2", "Name": "New" }, { "Id": "c1", "Name": "Mine (renamed)", "Url": "{{Secret.Protect("https://discord.com/api/webhooks/1/old")}}" }]""");

        using var ms = Export(null); // passwords left out
        SetupArchive.Stage(_to, ms, null, false, "owner");
        SetupArchive.ApplyPending(_to, _log.Add);

        var bot = JsonNode.Parse(Read(_to, "configs/next/discord-bot.json"))!;
        Assert.Equal("this-pcs-token", Secret.Unprotect(bot["Token"]!.GetValue<string>()));
        Assert.Equal("1", bot["GuildId"]!.GetValue<string>()); // everything else from the backup
        var channels = JsonNode.Parse(Read(_to, "configs/next/notify-channels.json"))!.AsArray();
        var mine = channels.Single(c => c!["Id"]!.GetValue<string>() == "c1")!;
        Assert.Equal("Mine (renamed)", mine["Name"]!.GetValue<string>());
        Assert.Equal("https://discord.com/api/webhooks/1/mine", Secret.Unprotect(mine["Url"]!.GetValue<string>())); // matched by Id, not position
    }

    [Fact]
    public void Taking_over_a_gone_pc_keeps_its_identity()
    {
        OldPc();
        Write(_to, "configs/next/agent.json", """{ "MachineId": "m-new" }""");
        using var ms = Export(null);
        SetupArchive.Stage(_to, ms, null, keepIdentity: true, "owner");
        SetupArchive.ApplyPending(_to, _log.Add);
        var agent = JsonNode.Parse(Read(_to, "configs/next/agent.json"))!;
        Assert.Equal("m-old", agent["MachineId"]!.GetValue<string>());
        Assert.Equal("https://hub:8971/", agent["HubUrl"]!.GetValue<string>());
    }

    [Fact]
    public void Anything_that_isnt_a_setup_backup_is_refused()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { zip.CreateEntry("hello.txt"); }
        ms.Position = 0;
        Assert.Contains("isn't a WindowsGSM setup backup", Assert.Throws<InvalidDataException>(() => SetupArchive.Stage(_to, ms, null, false, "o")).Message);

        foreach (string bad in new[] { "../../evil.txt", "configs/next/../../evil.json", "configs/next/keys/key-1.xml", "servers/1/serverfiles/game.exe", "C:/Windows/evil.dll" })
        {
            var b = new MemoryStream();
            using (var zip = new ZipArchive(b, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("wgsm-setup.json").Open())) { w.Write("""{ "Format": 1 }"""); }
                zip.CreateEntry(bad);
            }
            b.Position = 0;
            Assert.Throws<InvalidDataException>(() => SetupArchive.Stage(_to, b, null, false, "o"));
            Assert.Null(SetupArchive.PendingRestore(_to));
        }
    }
}
