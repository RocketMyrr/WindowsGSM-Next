using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>Accounts, the audit log and the agent's own settings.</summary>
public static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("").AddEndpointFilter(async (efc, next) =>
        {
            var user = efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (!user.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can do that."); }
            return await next(efc);
        });

        // ── Users ──

        admin.MapGet("/users", (AgentContext ctx) => Results.Json(ctx.Users.All().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Select(ToDto)));

        admin.MapPost("/users", (HttpContext http, AgentContext ctx, UserRequest body) =>
        {
            var me = ctx.CurrentUser(http)!;
            if (!me.CanManage(body.Role)) { return ApiResults.Forbidden("Only owners can create admins and owners."); }
            string? problem = ValidateGrants(body.Grants) ?? ctx.Users.Create(body.Username, body.Password ?? string.Empty, body.Role, body.Enabled, body.Grants);
            ctx.Record(http, "user-create", null, problem == null, problem ?? $"{body.Username} ({body.Role})");
            return problem == null ? Results.Json(ToDto(ctx.Users.Get(body.Username)!), statusCode: 201) : ApiResults.BadRequest(problem);
        });

        admin.MapPut("/users/{username}", (HttpContext http, AgentContext ctx, string username, UserRequest body) =>
        {
            var me = ctx.CurrentUser(http)!;
            var target = ctx.Users.Get(username);
            if (target == null) { return ApiResults.NotFound("No such user."); }
            // Must be allowed to manage the account both as it is now and as it would become.
            if (!me.CanManage(target.Role) || !me.CanManage(body.Role)) { return ApiResults.Forbidden("Only owners can change admins and owners."); }

            string? problem = ValidateGrants(body.Grants) ?? ctx.Users.Update(username, body.Role, body.Enabled, body.Grants, body.Password);
            if (problem == null && (!body.Enabled || !string.IsNullOrEmpty(body.Password)))
            {
                ctx.Sessions.RemoveAllForUser(username); // disabled, or password reset: signed out everywhere
            }
            ctx.Record(http, "user-update", null, problem == null,
                problem ?? $"{username}: {body.Role}, {(body.Enabled ? "enabled" : "disabled")}{(string.IsNullOrEmpty(body.Password) ? "" : ", password reset")}");
            return problem == null ? Results.Json(ToDto(ctx.Users.Get(username)!)) : ApiResults.BadRequest(problem);
        });

        admin.MapDelete("/users/{username}", (HttpContext http, AgentContext ctx, string username) =>
        {
            var me = ctx.CurrentUser(http)!;
            var target = ctx.Users.Get(username);
            if (target == null) { return ApiResults.NotFound("No such user."); }
            if (!me.CanManage(target.Role)) { return ApiResults.Forbidden("Only owners can remove admins and owners."); }
            if (string.Equals(me.Username, username, StringComparison.OrdinalIgnoreCase)) { return ApiResults.BadRequest("You can't delete your own account."); }
            string? problem = ctx.Users.Delete(username);
            if (problem == null) { ctx.Sessions.RemoveAllForUser(username); }
            ctx.Record(http, "user-delete", null, problem == null, problem ?? username);
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        });

        // Everyone signed in right now (the legacy "who's on the dashboard" view).
        admin.MapGet("/sessions", (AgentContext ctx) =>
            Results.Json(ctx.Sessions.All().Select(s => new { user = s.Username, s.CreatedAt, s.LastSeenAt, s.Ip, device = AuthEndpoints.Device(s.UserAgent) })));

        // ── Audit ──

        admin.MapGet("/audit", (AgentContext ctx, int? limit, string? user, string? server, string? action) =>
            Results.Json(ctx.Audit.Read(limit ?? 200, user, server, action)));

        // ── Agent settings (owners) ──

        admin.MapGet("/agent/settings", (HttpContext http, AgentContext ctx) =>
            ctx.CurrentUser(http)!.IsOwner ? Results.Json(ToDto(ctx.Settings)) : ApiResults.Forbidden("Only owners can see the agent's settings."));

        admin.MapPut("/agent/settings", (HttpContext http, AgentContext ctx, AgentSettingsDto body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change the agent's settings."); }
            if (body.Port is < 1 or > 65535) { return ApiResults.BadRequest("Port must be between 1 and 65535."); }
            if (body.AcmeEnabled && (string.IsNullOrWhiteSpace(body.AcmeDomain) || string.IsNullOrWhiteSpace(body.AcmeEmail)))
            {
                return ApiResults.BadRequest("Let's Encrypt needs a domain name and a contact email.");
            }
            if (body.SessionHours is < 1 or > 24 * 30) { return ApiResults.BadRequest("Sign-ins can last between 1 hour and 30 days."); }

            var s = ctx.Settings;
            s.MachineName = string.IsNullOrWhiteSpace(body.MachineName) ? s.MachineName : body.MachineName.Trim();
            s.Port = body.Port;
            s.ExposeToNetwork = body.ExposeToNetwork;
            s.UseHttps = body.UseHttps;
            s.CertPath = body.CertPath?.Trim() ?? string.Empty;
            s.KeyPath = body.KeyPath?.Trim() ?? string.Empty;
            if (body.CertPassword != null) { s.CertPassword = body.CertPassword; } // null keeps the saved one
            s.AcmeEnabled = body.AcmeEnabled;
            s.AcmeDomain = body.AcmeDomain?.Trim() ?? string.Empty;
            s.AcmeEmail = body.AcmeEmail?.Trim() ?? string.Empty;
            s.AcmeStaging = body.AcmeStaging;
            s.SessionHours = body.SessionHours;
            s.Save();
            ctx.Record(http, "agent-settings", null, true,
                $"port={s.Port}, network={s.ExposeToNetwork}, https={s.UseHttps || s.AcmeEnabled}{(s.AcmeEnabled ? $", acme={s.AcmeDomain}" : "")}");
            return Results.Json(new { restartRequired = true, settings = ToDto(s) });
        });

        admin.MapGet("/agent/startup", () => Results.Json(new { registered = StartupTask.IsRegistered(), taskName = StartupTask.TaskName }));

        admin.MapPost("/agent/startup", (HttpContext http, AgentContext ctx, StartupRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change how the agent starts."); }
            string? problem = body.Enabled
                ? StartupTask.Register(Environment.ProcessPath ?? "wgsm-agent.exe", global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot)
                : StartupTask.Unregister();
            ctx.Record(http, body.Enabled ? "startup-on" : "startup-off", null, problem == null, problem);
            return problem == null ? Results.NoContent() : ApiResults.Error(500, "startup_task", problem);
        });
    }

    private static UserDto ToDto(AgentUser u) =>
        new(u.Username, u.Role, u.Enabled, u.Grants, u.TwoFactorEnabled, u.CreatedAt, u.LastLoginAt, u.LastLoginIp);

    private static AgentSettingsDto ToDto(AgentSettings s) =>
        new(s.MachineId, s.MachineName, s.Port, s.ExposeToNetwork, s.UseHttps, s.CertPath, s.KeyPath, null,
            s.AcmeEnabled, s.AcmeDomain, s.AcmeEmail, s.AcmeStaging, s.SessionHours);

    /// <summary>Grant scopes look like "machine/server", "machine/*" or "*/*".</summary>
    private static string? ValidateGrants(IReadOnlyDictionary<string, Capability>? grants)
    {
        foreach (var (scope, caps) in grants ?? new Dictionary<string, Capability>())
        {
            var parts = scope.Split('/');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Length > 64)) { return $"\"{scope}\" isn't a valid permission scope (use machine/server, machine/* or */*)."; }
            if ((caps & ~Capability.All) != 0) { return $"Unknown permission in \"{scope}\"."; }
        }
        return null;
    }
}

/// <summary>The agent's settings as the API shows them. <see cref="CertPassword"/> is never returned; send null to keep it.</summary>
public sealed record AgentSettingsDto(string MachineId, string MachineName, int Port, bool ExposeToNetwork, bool UseHttps,
    string? CertPath, string? KeyPath, string? CertPassword, bool AcmeEnabled, string? AcmeDomain, string? AcmeEmail, bool AcmeStaging, int SessionHours);

public sealed record StartupRequest(bool Enabled);
