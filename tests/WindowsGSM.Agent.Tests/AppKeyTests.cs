using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>The WindowsGSM app on another PC staying signed in: its key, signing back in, and every way it ends.</summary>
[Collection("Agent")]
public class AppKeyTests
{
    private const string AppAgent = "Mozilla/5.0 (Windows NT 10.0) Edg/130.0 WindowsGSM-Desktop/2";
    private readonly AgentFixture _f;
    public AppKeyTests(AgentFixture f) => _f = f;

    /// <summary>A user signed in through "the app" (its user-agent), with a key for it.</summary>
    private async Task<(ApiClient App, string Key, string User)> SignedInAppAsync(string name)
    {
        await _f.UserAsync(name, Role.Operator);
        var app = _f.NewClient();
        app.Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppAgent);
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync("/api/v2/auth/login", new LoginRequest(name, "password-" + name))).StatusCode);
        var res = await app.PostAsync("/api/v2/auth/app-key", new { device = "LAPTOP" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string key = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        return (app, key, name);
    }

    private async Task<HttpResponseMessage> AppLoginAsync(string key, ApiClient? into = null)
    {
        var c = into ?? _f.NewClient();
        if (into == null) { c.Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppAgent); }
        return await c.PostAsync("/api/v2/app-login", new { key });
    }

    [Fact]
    public async Task The_key_signs_the_app_back_in_and_shows_under_sessions()
    {
        var (_, key, user) = await SignedInAppAsync("appkey1");
        var later = _f.NewClient();
        later.Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppAgent);
        Assert.Equal(HttpStatusCode.OK, (await AppLoginAsync(key, later)).StatusCode);
        var me = await later.GetJsonAsync<JsonElement>("/api/v2/auth/me");
        Assert.Equal(user, me.GetProperty("username").GetString());

        var sessions = await later.GetJsonAsync<List<SessionDto>>("/api/v2/auth/sessions");
        Assert.Contains(sessions, s => s.Id.StartsWith("app-") && s.Device!.Contains("LAPTOP"));
        // Only a hash is kept.
        string file = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "app-keys.json");
        Assert.DoesNotContain(key.Split('.')[1], File.ReadAllText(file));
    }

    [Fact]
    public async Task Only_the_app_gets_a_key_and_a_wrong_key_signs_nobody_in()
    {
        var browser = await _f.UserAsync("appkey2", Role.Operator); // a browser: no app mark
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/v2/auth/app-key", new { device = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync("abc.def")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync("nonsense")).StatusCode);
        var (_, key, _) = await SignedInAppAsync("appkey2b");
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key.Split('.')[0] + "." + new string('0', 64))).StatusCode);
    }

    [Fact]
    public async Task Signing_out_in_the_app_means_it()
    {
        var (app, key, _) = await SignedInAppAsync("appkey3");
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync("/api/v2/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_after_signing_back_in_also_forgets_it()
    {
        var (_, key, _) = await SignedInAppAsync("appkey4");
        var later = _f.NewClient();
        later.Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppAgent);
        await AppLoginAsync(key, later);
        Assert.Equal(HttpStatusCode.NoContent, (await later.PostAsync("/api/v2/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key)).StatusCode);
    }

    [Fact]
    public async Task It_can_be_removed_from_account_and_security()
    {
        var (_, key, user) = await SignedInAppAsync("appkey5");
        var browser = _f.NewClient();
        await browser.PostAsync("/api/v2/auth/login", new LoginRequest(user, "password-" + user));
        var entry = (await browser.GetJsonAsync<List<SessionDto>>("/api/v2/auth/sessions")).Single(s => s.Id.StartsWith("app-"));
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync($"/api/v2/auth/sessions/{entry.Id}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key)).StatusCode);
        Assert.DoesNotContain(await browser.GetJsonAsync<List<SessionDto>>("/api/v2/auth/sessions"), s => s.Id.StartsWith("app-"));
    }

    [Fact]
    public async Task Sign_out_everywhere_else_includes_apps_but_not_the_one_asking()
    {
        var (app, key, user) = await SignedInAppAsync("appkey6");
        var (_, otherKey, _) = await SecondAppAsync(user);
        // From the first app: the other app goes, this one stays.
        await app.PostAsync("/api/v2/auth/sessions/revoke-others");
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(otherKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppLoginAsync(key)).StatusCode);
    }

    [Fact]
    public async Task Disabling_the_account_or_resetting_its_password_ends_it()
    {
        var (_, key, user) = await SignedInAppAsync("appkey7");
        var res = await _f.Owner.PutAsync($"/api/v2/users/{user}", new UserRequest(user, Role.Operator, false, null, null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key)).StatusCode);
    }

    [Fact]
    public async Task The_app_can_drop_its_own_key()
    {
        var (_, key, _) = await SignedInAppAsync("appkey8");
        var anyone = _f.NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anyone.PostAsync("/api/v2/app-key/forget", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppLoginAsync(key)).StatusCode);
    }

    private async Task<(ApiClient, string, string)> SecondAppAsync(string user)
    {
        var app = _f.NewClient();
        app.Http.DefaultRequestHeaders.UserAgent.ParseAdd(AppAgent);
        await app.PostAsync("/api/v2/auth/login", new LoginRequest(user, "password-" + user));
        var res = await app.PostAsync("/api/v2/auth/app-key", new { device = "DESKTOP-2" });
        return (app, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!, user);
    }
}
