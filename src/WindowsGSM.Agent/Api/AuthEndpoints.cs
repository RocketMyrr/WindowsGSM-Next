using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>Health, first-run setup, sign-in, sessions, password and two-factor.</summary>
public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static void Map(RouteGroupBuilder api)
    {
        // ── Public ──

        api.MapGet("/health", (AgentContext ctx) => Results.Json(new
        {
            status = "ok",
            version = WgsmEnvironment.Version,
            machine = ctx.MachineId,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - ctx.StartedAt).TotalSeconds,
        }));

        api.MapGet("/info", (AgentContext ctx) =>
            Results.Json(new AgentInfoDto(ctx.MachineId, ctx.Settings.MachineName, WgsmEnvironment.Version, ctx.StartedAt, ctx.Users.Count == 0)));

        // First run: create the owner. From this machine only, or from anywhere with the one-time setup token
        // the agent printed at start — so an agent exposed to the network can't be claimed by a stranger.
        api.MapPost("/setup", async (HttpContext http, AgentContext ctx, SetupTokens tokens, SetupRequest body) =>
        {
            if (ctx.Users.Count > 0) { return ApiResults.Conflict("Setup is already done. Sign in instead."); }
            bool local = http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);
            if (!local && !tokens.Matches(body.Token))
            {
                return ApiResults.Forbidden("Finish setup on the machine itself, or enter the setup code shown in the agent's window/log.");
            }
            string? problem = ctx.Users.Create(body.Username, body.Password, Role.Owner, true, null);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            if (!string.IsNullOrWhiteSpace(body.MachineName))
            {
                ctx.Settings.MachineName = body.MachineName.Trim();
                ctx.Settings.Save();
            }
            tokens.Consume();
            var user = ctx.Users.Get(body.Username)!;
            ctx.Audit.Write(user.Username, AgentContext.Ip(http), "setup", null, true, "owner account created");
            await SignInAsync(http, ctx, user);
            return Results.Json(new LoginResult(true, false, Me(ctx, user)));
        }).RequireRateLimiting(LoginRateLimit);

        api.MapPost("/auth/login", async (HttpContext http, AgentContext ctx, LoginRequest body) =>
        {
            string? ip = AgentContext.Ip(http);
            var outcome = ctx.Users.Validate(body.Username?.Trim() ?? string.Empty, body.Password, body.Code, out var user);
            switch (outcome)
            {
                case LoginOutcome.TwoFactorRequired:
                    return Results.Json(new LoginResult(false, true, null));
                case LoginOutcome.BadCode:
                    ctx.Audit.Write(body.Username, ip, "login", null, false, "wrong 2FA code");
                    return ApiResults.Error(401, "bad_code", "That code isn't right. Codes change every 30 seconds.");
                case LoginOutcome.BadCredentials:
                    ctx.Audit.Write(body.Username, ip, "login", null, false, "wrong username or password");
                    return ApiResults.Error(401, "bad_credentials", "Wrong username or password.");
            }

            await SignInAsync(http, ctx, user!);
            DesktopSignIn.Remember(http, user!.Username);
            ctx.Users.RecordLogin(user!.Username, ip);
            ctx.Audit.Write(user.Username, ip, "login", null, true, Device(http.Request.Headers.UserAgent.ToString()));
            return Results.Json(new LoginResult(true, false, Me(ctx, user)));
        }).RequireRateLimiting(LoginRateLimit);

        // ── Signed in ──

        var auth = api.MapGroup("/auth").AddEndpointFilter(async (efc, next) =>
            efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext) == null
                ? ApiResults.Unauthorized()
                : await next(efc));

        auth.MapPost("/logout", async (HttpContext http, AgentContext ctx) =>
        {
            ctx.Sessions.Remove(AgentContext.SessionId(http));
            DesktopSignIn.Forget(http); // signing out of the desktop app means it: don't sign back in on its own
            ctx.Record(http, "logout", null, true);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapGet("/me", (HttpContext http, AgentContext ctx) => Results.Json(Me(ctx, ctx.CurrentUser(http)!)));

        auth.MapPost("/password", (HttpContext http, AgentContext ctx, PasswordChangeRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            string? problem = ctx.Users.ChangePassword(user.Username, body.CurrentPassword, body.NewPassword);
            ctx.Record(http, "password-change", null, problem == null, problem);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            // A new password signs out every other device.
            ctx.Sessions.RemoveAllForUser(user.Username, keepId: AgentContext.SessionId(http));
            return Results.NoContent();
        });

        auth.MapGet("/sessions", (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            string? current = AgentContext.SessionId(http);
            return Results.Json(ctx.Sessions.ForUser(user.Username)
                .Select(s => new SessionDto(s.Id[..12], s.Id == current, s.CreatedAt, s.LastSeenAt, s.Ip, Device(s.UserAgent))));
        });

        // Sessions are addressed by the first 12 characters of their id — enough to pick one, useless as a credential.
        auth.MapPost("/sessions/{sid}/revoke", (HttpContext http, AgentContext ctx, string sid) =>
        {
            var user = ctx.CurrentUser(http)!;
            var match = ctx.Sessions.ForUser(user.Username).FirstOrDefault(s => s.Id.StartsWith(sid, StringComparison.Ordinal) && sid.Length >= 12);
            bool ok = match != null && ctx.Sessions.RemoveForUser(user.Username, match.Id);
            ctx.Record(http, "revoke-session", null, ok, sid);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such session.");
        });

        auth.MapPost("/sessions/revoke-others", (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            int n = ctx.Sessions.RemoveAllForUser(user.Username, keepId: AgentContext.SessionId(http));
            ctx.Record(http, "revoke-sessions", null, true, $"{n} session(s)");
            return Results.Json(new { revoked = n });
        });

        auth.MapPost("/2fa/setup", (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            if (user.TwoFactorEnabled) { return ApiResults.Conflict("Two-factor sign-in is already on. Turn it off first to enrol a new device."); }
            string secret = ctx.Users.BeginTwoFactor(user.Username)!;
            return Results.Json(new TwoFactorSetupDto(secret, Totp.GetUri($"{user.Username}@{ctx.Settings.MachineName}", secret)));
        });

        auth.MapPost("/2fa/enable", (HttpContext http, AgentContext ctx, CodeRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            bool ok = ctx.Users.SetTwoFactor(user.Username, true, body.Code);
            ctx.Record(http, "2fa-enable", null, ok);
            return ok ? Results.NoContent() : ApiResults.BadRequest("That code isn't right. Check the time on your phone and try the next code.");
        });

        auth.MapPost("/2fa/disable", (HttpContext http, AgentContext ctx, CodeRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            bool ok = ctx.Users.SetTwoFactor(user.Username, false, body.Code);
            ctx.Record(http, "2fa-disable", null, ok);
            return ok ? Results.NoContent() : ApiResults.BadRequest("That code isn't right.");
        });
    }

    public static MeDto Me(AgentContext ctx, AgentUser user) =>
        new(user.Username, user.Role, user.TwoFactorEnabled, ctx.MachineId, user.IsAdmin, user.IsOwner);

    internal static async Task SignInAsync(HttpContext http, AgentContext ctx, AgentUser user)
    {
        var session = ctx.Sessions.Create(user.Username, AgentContext.Ip(http), http.Request.Headers.UserAgent.ToString());
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AgentContext.SessionClaim, session.Id),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>"Chrome on Windows" from a user-agent string — enough to recognise a session.</summary>
    public static string? Device(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) { return null; }
        string browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("OPR/") ? "Opera" : ua.Contains("Firefox/") ? "Firefox"
            : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Safari/") ? "Safari" : "Browser";
        string os = ua.Contains("Windows") ? "Windows" : ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS"
            : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "unknown OS";
        return $"{browser} on {os}";
    }
}

/// <summary>A one-time code allowing first-run setup from another computer. Lives only in memory.</summary>
public sealed class SetupTokens
{
    private string? _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    public string? Current => _token;

    public bool Matches(string? token) =>
        _token != null && token != null
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(_token), System.Text.Encoding.UTF8.GetBytes(token.Trim().ToUpperInvariant()));

    public void Consume() => _token = null;
}
