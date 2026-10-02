using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Passkeys (WebAuthn): sign in with a fingerprint, face or security key instead of a password and code. Adding
/// one needs you signed in; signing in with one counts as both factors (the device checks it's you). Browsers
/// only allow passkeys over HTTPS — or on localhost — and a passkey works on the address it was made on.
/// </summary>
public static class PasskeyEndpoints
{
    public sealed record AddPasskeyRequest(string? Name, AuthenticatorAttestationRawResponse Response);
    public sealed record PasskeyLoginRequest(string Token, AuthenticatorAssertionRawResponse Response);
    public sealed record PasskeyDto(string Id, string Name, string RpId, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

    // Challenges waiting for an answer: registration by session, sign-in by a one-time token. A few minutes each.
    private static readonly ConcurrentDictionary<string, (CredentialCreateOptions Options, DateTimeOffset At)> Pending = new();
    private static readonly ConcurrentDictionary<string, (AssertionOptions Options, DateTimeOffset At)> PendingLogins = new();
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    private static Fido2 For(HttpContext http)
    {
        string host = http.Request.Host.Host;
        string origin = $"{http.Request.Scheme}://{http.Request.Host}";
        return new Fido2(new Fido2Configuration { RPID = host, RPName = "WindowsGSM", Origins = new HashSet<string> { origin } });
    }

    private static void Tidy()
    {
        var cutoff = DateTimeOffset.UtcNow - Wait;
        foreach (var k in Pending.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToList()) { Pending.TryRemove(k, out _); }
        foreach (var k in PendingLogins.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToList()) { PendingLogins.TryRemove(k, out _); }
    }

    public static void Map(RouteGroupBuilder api)
    {
        // ── Signed in: list, add, remove ──
        var mine = api.MapGroup("/auth/passkeys").AddEndpointFilter(async (efc, next) =>
            efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext) == null
                ? ApiResults.Unauthorized()
                : await next(efc));

        mine.MapGet("", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.CurrentUser(http)!.Passkeys.Select(k => new PasskeyDto(k.Id, k.Name, k.RpId, k.CreatedAt, k.LastUsedAt))));

        mine.MapPost("/options", (HttpContext http, AgentContext ctx) =>
        {
            Tidy();
            var user = ctx.CurrentUser(http)!;
            var options = For(http).RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User { Id = Convert.FromBase64String(ctx.Users.PasskeyHandle(user.Username)), Name = user.Username, DisplayName = user.Username },
                ExcludeCredentials = user.Passkeys.Select(k => new PublicKeyCredentialDescriptor(System.Buffers.Text.Base64Url.DecodeFromChars(k.Id))).ToList(),
                AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
                AttestationPreference = AttestationConveyancePreference.None,
            });
            Pending[AgentContext.SessionId(http) ?? user.Username] = (options, DateTimeOffset.UtcNow);
            return Results.Text(options.ToJson(), "application/json");
        });

        mine.MapPost("", async (HttpContext http, AgentContext ctx, AddPasskeyRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            if (!Pending.TryRemove(AgentContext.SessionId(http) ?? user.Username, out var pending)) { return ApiResults.BadRequest("Start again — that took too long."); }
            try
            {
                var made = await For(http).MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = body.Response,
                    OriginalOptions = pending.Options,
                    IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(ctx.Users.FindPasskey(System.Buffers.Text.Base64Url.EncodeToString(args.CredentialId)) == null),
                }, http.RequestAborted);
                string name = string.IsNullOrWhiteSpace(body.Name) ? "Passkey" : body.Name.Trim()[..Math.Min(body.Name.Trim().Length, 60)];
                var key = new Passkey(System.Buffers.Text.Base64Url.EncodeToString(made.Id), Convert.ToBase64String(made.PublicKey), made.SignCount, http.Request.Host.Host, name, DateTimeOffset.UtcNow);
                string? problem = ctx.Users.AddPasskey(user.Username, key);
                ctx.Record(http, "passkey-add", null, problem == null, problem ?? name);
                return problem == null ? Results.Json(new PasskeyDto(key.Id, key.Name, key.RpId, key.CreatedAt, null)) : ApiResults.BadRequest(problem);
            }
            catch (Fido2VerificationException ex)
            {
                ctx.Record(http, "passkey-add", null, false, ex.Message);
                return ApiResults.BadRequest("The passkey couldn't be checked: " + ex.Message);
            }
        });

        mine.MapDelete("/{id}", (HttpContext http, AgentContext ctx, string id) =>
        {
            var user = ctx.CurrentUser(http)!;
            bool ok = ctx.Users.RemovePasskey(user.Username, id);
            ctx.Record(http, "passkey-remove", null, ok, id);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such passkey.");
        });

        // ── Signing in ──
        api.MapPost("/auth/passkey/options", (HttpContext http) =>
        {
            Tidy();
            var options = For(http).GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = new List<PublicKeyCredentialDescriptor>(), // the device offers its passkeys for this address
                UserVerification = UserVerificationRequirement.Required,
            });
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            PendingLogins[token] = (options, DateTimeOffset.UtcNow);
            return Results.Json(new { token, options = System.Text.Json.JsonDocument.Parse(options.ToJson()).RootElement });
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        api.MapPost("/auth/passkey/login", async (HttpContext http, AgentContext ctx, PasskeyLoginRequest body) =>
        {
            string? ip = AgentContext.Ip(http);
            if (!PendingLogins.TryRemove(body.Token ?? "", out var pending)) { return ApiResults.Error(401, "passkey_expired", "That took too long — try again."); }
            var found = ctx.Users.FindPasskey(body.Response.Id);
            if (found is { } wrongHost && !string.Equals(wrongHost.Key.RpId, http.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) { found = null; }
            if (found is not { } f || !f.User.Enabled)
            {
                ctx.Audit.Write(null, ip, "login", null, false, "unknown passkey");
                return ApiResults.Error(401, "bad_passkey", "This passkey isn't registered here. Sign in with your password, then add it under Account & security.");
            }
            try
            {
                var result = await For(http).MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = body.Response,
                    OriginalOptions = pending.Options,
                    StoredPublicKey = Convert.FromBase64String(f.Key.PublicKey),
                    StoredSignatureCounter = f.Key.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                        f.User.PasskeyUserHandle != null && args.UserHandle.AsSpan().SequenceEqual(Convert.FromBase64String(f.User.PasskeyUserHandle))),
                }, http.RequestAborted);
                ctx.Users.PasskeyUsed(f.User.Username, f.Key.Id, result.SignCount);
            }
            catch (Fido2VerificationException ex)
            {
                ctx.Audit.Write(f.User.Username, ip, "login", null, false, "passkey refused: " + ex.Message);
                return ApiResults.Error(401, "bad_passkey", "The passkey wasn't accepted.");
            }
            await AuthEndpoints.SignInAsync(http, ctx, f.User);
            DesktopSignIn.Remember(http, f.User.Username);
            ctx.Users.RecordLogin(f.User.Username, ip);
            ctx.Audit.Write(f.User.Username, ip, "login", null, true, $"passkey \"{f.Key.Name}\" · {AuthEndpoints.Device(http.Request.Headers.UserAgent.ToString())}");
            return Results.Json(new LoginResult(true, false, AuthEndpoints.Me(ctx, f.User)));
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);
    }
}
