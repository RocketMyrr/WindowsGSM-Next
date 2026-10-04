using System.Net;
using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Tests;

/// <summary>The Steam account: the password is stored encrypted, Steam Guard codes are answered from the panel, the old plain-text file is cleaned up.</summary>
[Collection("Agent")]
public class SteamAccountTests
{
    private readonly AgentFixture _f;
    public SteamAccountTests(AgentFixture f) => _f = f;

    private const string Base = "/api/v2/machines/local/steam-account";

    [Fact]
    public async Task Signing_in_answers_Steam_Guard_and_stores_the_password_encrypted()
    {
        SteamSignIn.Reset();
        SteamAccount.Clear();
        string? sentCode = null;
        SteamEndpoints.Runner = async (user, pass, onPrompt, onLine, token) =>
        {
            onLine($"Logging '{user}' into Steam3... password {pass}");
            sentCode = await onPrompt("STEAM GUARD! Please enter the auth code sent to the email at x***@example.com:");
            onLine("Done!");
            return sentCode == "ABCDE" ? null : "Steam didn't accept the Steam Guard code.";
        };
        try
        {
            var start = await _f.Owner.PostAsync(Base, new { username = "serverbot", password = "hunter2-secret" });
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

            JsonElement state = default;
            await EngineFixture.WaitUntil(async () => (state = await _f.Owner.GetJsonAsync<JsonElement>(Base + "/signin")).GetProperty("state").GetString() == "code", "asks for a code");
            Assert.Contains("email", state.GetProperty("prompt").GetString());
            Assert.DoesNotContain(state.GetProperty("output").EnumerateArray(), l => l.GetString()!.Contains("hunter2-secret"));

            Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PostAsync(Base + "/signin/code", new { code = "ABCDE" })).StatusCode);
            await EngineFixture.WaitUntil(async () => (state = await _f.Owner.GetJsonAsync<JsonElement>(Base + "/signin")).GetProperty("state").GetString() == "done", "signed in");
            Assert.Equal("ABCDE", sentCode);

            Assert.Equal(("serverbot", "hunter2-secret"), SteamAccount.Get());
            string file = File.ReadAllText(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "steam.json"));
            Assert.DoesNotContain("hunter2-secret", file);
            Assert.Contains("dpapi:", file);

            var status = await _f.Owner.GetJsonAsync<JsonElement>(Base);
            Assert.Equal("serverbot", status.GetProperty("username").GetString());
            Assert.True(status.GetProperty("hasPassword").GetBoolean());
            Assert.DoesNotContain("hunter2", status.GetRawText());

            // Admins (not owners) can't see or change it.
            var admin = await _f.UserAsync("steamadmin", Role.Admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Base)).StatusCode);
        }
        finally { SteamSignIn.Reset(); SteamAccount.Clear(); }
    }

    [Fact]
    public async Task A_wrong_code_fails_and_nothing_is_saved()
    {
        SteamSignIn.Reset();
        SteamAccount.Clear();
        SteamEndpoints.Runner = async (user, pass, onPrompt, onLine, token) =>
            await onPrompt("Please enter your 2 factor auth code from your authenticator app:") == "RIGHT" ? null : "Steam didn't accept the Steam Guard code.";
        try
        {
            await _f.Owner.PostAsync(Base, new { username = "serverbot", password = "pw" });
            await EngineFixture.WaitUntil(async () => (await _f.Owner.GetJsonAsync<JsonElement>(Base + "/signin")).GetProperty("state").GetString() == "code", "asks for a code");
            Assert.Equal(HttpStatusCode.Conflict, (await _f.Owner.PostAsync(Base, new { username = "other", password = "pw" })).StatusCode); // one at a time
            await _f.Owner.PostAsync(Base + "/signin/code", new { code = "WRONG" });
            JsonElement state = default;
            await EngineFixture.WaitUntil(async () => (state = await _f.Owner.GetJsonAsync<JsonElement>(Base + "/signin")).GetProperty("state").GetString() == "failed", "fails");
            Assert.Contains("Steam Guard", state.GetProperty("message").GetString());
            Assert.Null(SteamAccount.Get().Username);
        }
        finally { SteamSignIn.Reset(); SteamAccount.Clear(); }
    }

    [Fact]
    public async Task The_legacy_plain_text_password_is_imported_then_removed()
    {
        SteamSignIn.Reset();
        SteamAccount.Clear();
        Directory.CreateDirectory(Path.GetDirectoryName(SteamAccount.LegacyFile)!);
        File.WriteAllText(SteamAccount.LegacyFile, "// legacy\nsteamUser=\"olduser\"\nsteamPass=\"oldpass\"\n");
        try
        {
            var status = await _f.Owner.GetJsonAsync<JsonElement>(Base);
            Assert.True(status.GetProperty("legacyPlainText").GetBoolean());
            Assert.Equal("olduser", status.GetProperty("username").GetString());

            var res = await _f.Owner.PostAsync(Base + "/remove-legacy-password");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.DoesNotContain("oldpass", File.ReadAllText(SteamAccount.LegacyFile));
            Assert.Equal(("olduser", "oldpass"), SteamAccount.Get()); // kept, encrypted
            Assert.False((await _f.Owner.GetJsonAsync<JsonElement>(Base)).GetProperty("legacyPlainText").GetBoolean());
        }
        finally { WindowsGSM.Core.Tests.TestData.DeleteFile(SteamAccount.LegacyFile); SteamAccount.Clear(); }
    }
}
