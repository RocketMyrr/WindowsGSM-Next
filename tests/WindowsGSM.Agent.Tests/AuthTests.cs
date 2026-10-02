using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

[Collection("Agent")]
public class AuthTests
{
    private readonly AgentFixture _f;
    public AuthTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task First_run_setup_needs_the_code_from_another_computer_and_only_happens_once()
    {
        Assert.Equal(HttpStatusCode.Forbidden, _f.SetupWithoutCodeStatus);
        Assert.Null(_f.App.Services.GetRequiredService<SetupTokens>().Current); // used up

        var again = await _f.NewClient().PostAsync("/api/v2/setup", new SetupRequest("intruder", "some long password", null, "whatever"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var info = await _f.NewClient().GetJsonAsync<AgentInfoDto>("/api/v2/info");
        Assert.False(info.SetupRequired);
        Assert.Equal("Test Box", info.MachineName);
    }

    [Fact]
    public async Task Health_is_public_everything_else_needs_a_sign_in()
    {
        var anon = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/v2/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v2/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(_f.ServerUrl("101"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v2/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changes_without_the_csrf_header_are_refused()
    {
        var noCsrf = new ApiClient(_f.Server, "198.51.100.200", csrf: false);
        var res = await noCsrf.PostAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, AgentFixture.OwnerPassword));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("csrf", (await ApiClient.Read<ApiError>(res)).Code);
    }

    [Fact]
    public async Task Security_headers_are_sent_and_hsts_is_not_without_a_trusted_certificate()
    {
        var res = await _f.NewClient().GetAsync("/api/v2/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_one()
    {
        await _f.UserAsync("locky", Role.Viewer);
        var c = _f.NewClient();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync("/api/v2/auth/login", new LoginRequest("locky", "nope"))).StatusCode);
        }
        var right = await _f.NewClient().PostAsync("/api/v2/auth/login", new LoginRequest("locky", "password-locky"));
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
    }

    [Fact]
    public async Task Two_factor_codes_are_required_and_never_accepted_twice()
    {
        var c = await _f.UserAsync("tfa", Role.Viewer);
        var setup = await ApiClient.Read<TwoFactorSetupDto>(await c.PostAsync("/api/v2/auth/2fa/setup"));
        Assert.StartsWith("otpauth://totp/", setup.Uri);

        string code = Totp.CurrentCode(setup.Secret);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/v2/auth/2fa/enable", new CodeRequest(code))).StatusCode);

        var fresh = _f.NewClient();
        var noCode = await ApiClient.Read<LoginResult>(await fresh.PostAsync("/api/v2/auth/login", new LoginRequest("tfa", "password-tfa")));
        Assert.True(noCode.TwoFactorRequired);
        Assert.False(noCode.Ok);

        // The code that just turned 2FA on can't be replayed to sign in.
        var replay = await fresh.PostAsync("/api/v2/auth/login", new LoginRequest("tfa", "password-tfa", code));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("bad_code", (await ApiClient.Read<ApiError>(replay)).Code);
    }

    [Fact]
    public async Task Signing_out_another_device_ends_its_session_immediately()
    {
        var phone = await _f.UserAsync("twodevices", Role.Viewer);
        var laptop = _f.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await laptop.PostAsync("/api/v2/auth/login", new LoginRequest("twodevices", "password-twodevices"))).StatusCode);

        var sessions = await laptop.GetJsonAsync<List<SessionDto>>("/api/v2/auth/sessions");
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.Current);

        var revoked = await laptop.PostAsync("/api/v2/auth/sessions/revoke-others");
        Assert.Equal(1, (await revoked.Content.ReadFromJsonAsync<Dictionary<string, int>>())!["revoked"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/v2/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/v2/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_old_one_and_a_long_enough_new_one()
    {
        var c = await _f.UserAsync("pw", Role.Viewer);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v2/auth/password", new PasswordChangeRequest("wrong", "a new long password"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v2/auth/password", new PasswordChangeRequest("password-pw", "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/v2/auth/password", new PasswordChangeRequest("password-pw", "a new long password"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/v2/auth/login", new LoginRequest("pw", "a new long password"))).StatusCode);
    }

    [Fact]
    public async Task Sign_ins_and_failures_are_audited()
    {
        await _f.NewClient().PostAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, "not it"));
        var entries = await _f.Owner.GetJsonAsync<List<AuditDto>>("/api/v2/audit?action=login&user=owner");
        Assert.Contains(entries, e => !e.Ok && e.Detail!.Contains("wrong username or password"));
    }
}
