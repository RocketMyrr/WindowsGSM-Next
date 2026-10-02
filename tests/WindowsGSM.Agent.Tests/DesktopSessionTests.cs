using System.Net;
using WindowsGSM.Agent.Api;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>The desktop app on the server stays signed in — only from this computer, only with the key.</summary>
[Collection("Agent")]
public class DesktopSessionTests
{
    private readonly AgentFixture _f;
    public DesktopSessionTests(AgentFixture f) => _f = f;

    private string Key => File.ReadAllText(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", LocalEndpoints.KeyFile)).Trim();

    private ApiClient Desktop()
    {
        var c = _f.NewClient("127.0.0.1");
        c.Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Edg/130 " + DesktopSignIn.UserAgentMark + "/2");
        return c;
    }

    [Fact]
    public async Task The_desktop_app_signs_back_in_as_whoever_last_signed_in_through_it()
    {
        // A sign-in in a normal browser isn't remembered.
        var browser = _f.NewClient("127.0.0.1");
        await browser.PostAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, AgentFixture.OwnerPassword, null));
        File.Delete(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "desktop-user.txt"));
        var fresh = Desktop();
        fresh.Http.DefaultRequestHeaders.Add(LocalEndpoints.KeyHeader, Key);
        Assert.Equal(HttpStatusCode.NotFound, (await fresh.PostAsync("/api/v2/local/desktop-session")).StatusCode);

        // Signing in through the desktop app remembers who.
        var app = Desktop();
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, AgentFixture.OwnerPassword, null))).StatusCode);

        // Without the key, or from another computer: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await Desktop().PostAsync("/api/v2/local/desktop-session")).StatusCode);
        var remote = _f.NewClient("198.51.100.7");
        remote.Http.DefaultRequestHeaders.Add(LocalEndpoints.KeyHeader, Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.PostAsync("/api/v2/local/desktop-session")).StatusCode);

        // With the key, on this computer: a working session for that user.
        var again = Desktop();
        again.Http.DefaultRequestHeaders.Add(LocalEndpoints.KeyHeader, Key);
        Assert.Equal(HttpStatusCode.OK, (await again.PostAsync("/api/v2/local/desktop-session")).StatusCode);
        var me = await again.GetJsonAsync<MeDto>("/api/v2/auth/me");
        Assert.Equal(AgentFixture.OwnerName, me.Username);

        // Signing out in the desktop app means it.
        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsync("/api/v2/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await again.PostAsync("/api/v2/local/desktop-session")).StatusCode);
    }

    [Fact]
    public async Task Stopping_the_agent_from_the_start_menu_needs_the_key()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.NewClient("127.0.0.1").PostAsync("/api/v2/local/stop-agent")).StatusCode);
        var remote = _f.NewClient("198.51.100.8");
        remote.Http.DefaultRequestHeaders.Add(LocalEndpoints.KeyHeader, Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.PostAsync("/api/v2/local/stop-agent")).StatusCode);
    }
}
