using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Hosting;
using WindowsGSM.Installer;

namespace WindowsGSM.Core.Tests;

public class SecretTests
{
    private sealed class Holder
    {
        [JsonConverter(typeof(SecretJsonConverter))] public string? Token { get; set; }
    }

    [Fact]
    public void Secrets_are_encrypted_in_the_file_and_plain_in_memory()
    {
        string json = JsonSerializer.Serialize(new Holder { Token = "bot-token-123" });
        Assert.DoesNotContain("bot-token-123", json);
        Assert.Contains("dpapi:", json);
        Assert.Equal("bot-token-123", JsonSerializer.Deserialize<Holder>(json)!.Token);

        // Older files with a plain value still read.
        Assert.Equal("plain", JsonSerializer.Deserialize<Holder>("{\"Token\":\"plain\"}")!.Token);
        Assert.Null(JsonSerializer.Deserialize<Holder>("{\"Token\":null}")!.Token);
        // Something encrypted elsewhere (or corrupted) reads as empty rather than failing.
        Assert.Equal("", Secret.Unprotect("dpapi:AAAA"));
    }

    [Theory]
    [InlineData("STEAM GUARD! Please enter the auth code sent to the email at a***@b.com:", true)]
    [InlineData("Please enter your 2 factor auth code from your authenticator app:", true)]
    [InlineData("Downloading depot 258550 - 45.2%", false)]
    public void Steam_Guard_prompts_are_recognised(string line, bool prompt) => Assert.Equal(prompt, DepotDownloader.IsSteamGuardPrompt(line));
}
