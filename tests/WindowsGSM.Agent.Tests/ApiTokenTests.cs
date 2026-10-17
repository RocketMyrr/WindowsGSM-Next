using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Personal API tokens: made in a signed-in session, used as "Authorization: Bearer …", and what they can't do.</summary>
[Collection("Agent")]
public class ApiTokenTests
{
    private readonly AgentFixture _f;
    public ApiTokenTests(AgentFixture f) => _f = f;

    private ApiClient Script(string token)
    {
        var c = new ApiClient(_f.Server, "198.51.100.77", csrf: false); // a script: no cookie, no CSRF header
        c.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task A_token_works_like_its_user_until_it_is_deleted()
    {
        var me = await _f.UserAsync("tokenuser", Role.Operator);

        var made = await me.PostAsync("/api/v2/auth/tokens", new ApiTokenRequest("Home Assistant", 30));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var body = await made.Content.ReadFromJsonAsync<JsonElement>(AgentFixture.Json);
        string token = body.GetProperty("token").GetString()!;
        string id = body.GetProperty("item").GetProperty("id").GetString()!;
        Assert.StartsWith(ApiTokens.Prefix, token);

        var script = Script(token);
        Assert.Equal("tokenuser", (await script.GetJsonAsync<JsonElement>("/api/v2/auth/me")).GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.OK, (await script.GetAsync("/api/v2/machines/local/servers")).StatusCode);
        // No CSRF header needed (a browser never sends a token by itself).
        var post = await script.PostAsync(_f.ServerUrl("103", "/console"), new { command = "status" });
        Assert.DoesNotContain("csrf", await post.Content.ReadAsStringAsync());

        // It can't manage the account: tokens, password, sessions, users.
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsync("/api/v2/auth/tokens", new ApiTokenRequest("another", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.PostAsync("/api/v2/auth/password", new { currentPassword = "x", newPassword = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/api/v2/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/api/v2/users")).StatusCode);

        // Listed (never the token itself), and last-used is noted.
        var listed = Assert.Single(await me.GetJsonAsync<List<ApiTokenDto>>("/api/v2/auth/tokens"));
        Assert.Equal(("Home Assistant", id), (listed.Name, listed.Id));
        Assert.NotNull(listed.LastUsedAt);
        Assert.DoesNotContain(token, await (await me.GetAsync("/api/v2/auth/tokens")).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/v2/auth/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/v2/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_bad_token_is_never_rescued_by_the_browser_cookie()
    {
        var browser = await _f.UserAsync("cookieuser", Role.Operator);
        browser.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiTokens.Prefix + "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v2/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v2/machines/local/servers")).StatusCode);
    }
}
