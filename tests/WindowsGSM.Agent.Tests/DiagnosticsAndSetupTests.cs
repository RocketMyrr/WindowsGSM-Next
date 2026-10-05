using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Taking secrets out of diagnostics.</summary>
public class DiagnosticsRedactionTests
{
    [Fact]
    public void Secrets_in_settings_files_are_removed_and_the_rest_kept()
    {
        string json = Diagnostics.RedactJson("""
            { "Enabled": true, "Token": "abc", "GuildId": "123", "CertPassword": "pw", "HubCredential": "cred", "HubUrl": "https://hub",
              "MachineName": "Box", "Port": 8971, "Other": "dpapi:AAAA",
              "Channels": [{ "Name": "Crashes", "Url": "https://discord.com/api/webhooks/1/x", "Events": ["crashed"] }],
              "SecretAccessKey": "s", "AccessKeyId": "id", "Admins": [{ "DiscordId": "42" }] }
            """);
        foreach (string gone in new[] { "\"abc\"", "\"pw\"", "\"cred\"", "https://hub", "dpapi:", "webhooks", "\"s\"", "\"id\"" }) { Assert.DoesNotContain(gone, json); }
        foreach (string kept in new[] { "\"Box\"", "8971", "\"123\"", "Crashes", "crashed", "\"42\"" }) { Assert.Contains(kept, json); }
    }

    [Fact]
    public void Passwords_in_server_configs_and_start_parameters_are_removed()
    {
        string cfg = Diagnostics.RedactCfg("servername=\"My Rust\"\r\nserverport=\"28015\"\r\ndiscordwebhook=\"https://discord.com/api/webhooks/9/y\"\r\nserverrconpassword=\"hunter2\"\r\nserverparam=\"+server.seed 1 +rcon.password \"\"s3cret\"\" +rcon.port 28016\"\r\n");
        Assert.Contains("My Rust", cfg);
        Assert.Contains("28015", cfg);
        Assert.Contains("+server.seed 1", cfg);
        Assert.DoesNotContain("webhooks/9", cfg);
        Assert.DoesNotContain("hunter2", cfg);
        Assert.DoesNotContain("s3cret", cfg);
    }
}

/// <summary>The endpoints: who can use them, and what comes out.</summary>
[Collection("Agent")]
public class DiagnosticsAndSetupApiTests
{
    private readonly AgentFixture _f;
    public DiagnosticsAndSetupApiTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task Admins_get_a_diagnostics_zip_without_any_secrets()
    {
        var res = await _f.Owner.GetAsync("/api/v2/machines/local/diagnostics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("about.json", names);
        Assert.Contains("health.json", names);
        Assert.Contains("servers.json", names);
        Assert.Contains("settings/agent.json", names);
        Assert.Contains(names, n => n.StartsWith("servers/101/", StringComparison.Ordinal));
        foreach (var e in zip.Entries)
        {
            string text = new StreamReader(e.Open()).ReadToEnd();
            Assert.DoesNotContain("dpapi:", text);
            Assert.DoesNotContain("pbkdf2", text); // no password hashes
        }
        var viewer = await _f.UserAsync("diagviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/machines/local/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task Owners_back_up_the_setup_and_stage_a_restore_for_the_next_start()
    {
        var admin = await _f.UserAsync("setupadmin", Role.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/machines/local/setup-backup", new { passphrase = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/machines/local/setup-backup", new { passphrase = "short" })).StatusCode);

        // Something secret in the setup (a webhook address), so the passphrase matters.
        var channel = await _f.Owner.PostAsync("/api/v2/notifications/channels", new { name = "Setup test", type = "discord", url = "https://discord.com/api/webhooks/7/setup-test", enabled = false, events = new[] { "crashed" }, scope = Array.Empty<string>() });
        Assert.True(channel.IsSuccessStatusCode, await channel.Content.ReadAsStringAsync());
        string channelId = (await channel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var res = await _f.Owner.PostAsync("/api/v2/machines/local/setup-backup", new { passphrase = "a long passphrase" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        byte[] backup = await res.Content.ReadAsByteArrayAsync();
        using (var zip = new ZipArchive(new MemoryStream(backup))) { Assert.Contains(zip.Entries, e => e.FullName == "configs/next/users.json"); }

        async Task<HttpResponseMessage> Restore(string? passphrase)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(backup), "file", "setup.zip" } };
            if (passphrase != null) { form.Add(new StringContent(passphrase), "passphrase"); }
            return await _f.Owner.Http.PostAsync("/api/v2/machines/local/setup-restore", form);
        }
        try
        {
            var wrong = await Restore("not it at all");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            var ok = await Restore("a long passphrase");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var staged = await ok.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(staged.GetProperty("sameMachine").GetBoolean());

            var state = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/machines/local/setup-restore");
            Assert.Equal(JsonValueKind.Object, state.GetProperty("pending").ValueKind);
        }
        finally
        {
            await _f.Owner.DeleteAsync($"/api/v2/notifications/channels/{channelId}");
            // Never leave a restore waiting for the shared test data folder's next start.
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.DeleteAsync("/api/v2/machines/local/setup-restore")).StatusCode);
        }
        var after = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/machines/local/setup-restore");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("pending").ValueKind);
    }
}
