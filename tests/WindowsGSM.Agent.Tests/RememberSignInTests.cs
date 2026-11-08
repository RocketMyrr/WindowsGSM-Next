using System.Net;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>"Keep me signed in": the cookie gets an expiry date, so a phone keeps it when you switch apps.</summary>
[Collection("Agent")]
public class RememberSignInTests
{
    private readonly AgentFixture _f;
    public RememberSignInTests(AgentFixture f) => _f = f;

    private async Task<string> SignInCookie(string name, bool remember)
    {
        var res = await _f.NewClient().PostAsync("/api/v2/auth/login", new LoginRequest(name, "password-" + name, null, remember));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AgentApp.CookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keep_me_signed_in_gives_the_cookie_an_expiry_and_without_it_the_browser_session_ends_it()
    {
        await _f.UserAsync("remember-me", Role.Viewer);
        Assert.Contains("expires=", await SignInCookie("remember-me", remember: true), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", await SignInCookie("remember-me", remember: false), StringComparison.OrdinalIgnoreCase);
    }
}
