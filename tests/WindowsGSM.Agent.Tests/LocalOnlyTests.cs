using System.Net;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>"Only on this PC": an account that signs in and works only on the machine itself — and can't lock you out.</summary>
[Collection("Agent")]
public class LocalOnlyTests
{
    private readonly AgentFixture _f;
    public LocalOnlyTests(AgentFixture f) => _f = f;

    private Task<HttpResponseMessage> Login(ApiClient c, string name) => c.PostAsync("/api/v2/auth/login", new LoginRequest(name, "password-" + name));

    [Fact]
    public async Task Signs_in_only_on_the_machine_and_a_session_elsewhere_stops_working()
    {
        var remote = await _f.UserAsync("lo-rdp", Role.Admin); // signed in from elsewhere before the switch
        Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PutAsync("/api/v2/users/lo-rdp", new UserRequest("lo-rdp", Role.Admin, true, null, null, true))).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await remote.GetAsync("/api/v2/auth/me")).StatusCode);
        // From elsewhere: the usual "wrong password", never a hint the password was right.
        var refused = await Login(_f.NewClient(), "lo-rdp");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("bad_credentials", await refused.Content.ReadAsStringAsync());
        // A reverse proxy on the machine doesn't make a visitor local.
        var proxied = _f.NewClient("127.0.0.1");
        proxied.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(proxied, "lo-rdp")).StatusCode);

        var here = _f.NewClient("127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await Login(here, "lo-rdp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/v2/auth/me")).StatusCode);

        var users = await _f.Owner.GetJsonAsync<List<UserDto>>("/api/v2/users");
        Assert.True(users.Single(u => u.Username == "lo-rdp").LocalOnly);
        // Editing without the field leaves it as it is.
        Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PutAsync("/api/v2/users/lo-rdp", new UserRequest("lo-rdp", Role.Admin, true, null, null))).StatusCode);
        Assert.True((await _f.Owner.GetJsonAsync<List<UserDto>>("/api/v2/users")).Single(u => u.Username == "lo-rdp").LocalOnly);
    }

    [Fact]
    public async Task You_cant_switch_it_on_for_yourself_from_elsewhere()
    {
        var remote = await _f.UserAsync("lo-self", Role.Owner); // owners can edit themselves
        var res = await remote.PutAsync("/api/v2/users/lo-self", new UserRequest("lo-self", Role.Owner, true, null, null, true));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await remote.GetAsync("/api/v2/auth/me")).StatusCode); // still signed in

        var here = _f.NewClient("127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, (await Login(here, "lo-self")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.PutAsync("/api/v2/users/lo-self", new UserRequest("lo-self", Role.Owner, true, null, null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/v2/auth/me")).StatusCode);
    }
}
