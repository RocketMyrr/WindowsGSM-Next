using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Agent.Api;

/// <summary>Endpoint metadata: the capability a server endpoint requires (View if absent).</summary>
public sealed record Needs(Capability Capability);

/// <summary>
/// Route-group filters that do the checks every endpoint would otherwise repeat: signed in, machine is this
/// one, server exists and is visible to the caller, caller has the endpoint's capability. A server the
/// caller can't even view answers 404, not 403, so its existence isn't revealed.
/// </summary>
public static class Scopes
{
    private const string ServerItem = "wgsm.server";

    public static RouteHandlerBuilder Needs(this RouteHandlerBuilder builder, Capability capability) =>
        builder.WithMetadata(new Needs(capability));

    public static AgentUser User(HttpContext http) =>
        http.RequestServices.GetRequiredService<AgentContext>().CurrentUser(http)
        ?? throw new InvalidOperationException("Scope filter didn't run.");

    public static ServerInstance Server(HttpContext http) =>
        http.Items[ServerItem] as ServerInstance ?? throw new InvalidOperationException("Server scope filter didn't run.");

    /// <summary>Signed in, and {machine} is this agent.</summary>
    public static TBuilder RequireMachine<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (efc, next) =>
        {
            var http = efc.HttpContext;
            var ctx = http.RequestServices.GetRequiredService<AgentContext>();
            if (ctx.CurrentUser(http) == null) { return ApiResults.Unauthorized(); }
            if (!ctx.IsThisMachine(http.Request.RouteValues["machine"] as string)) { return ApiResults.NotFound("Unknown machine."); }
            return await next(efc);
        });

    /// <summary>Everything <see cref="RequireMachine"/> checks, plus the server and the endpoint's capability.</summary>
    public static TBuilder RequireServer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (efc, next) =>
        {
            var http = efc.HttpContext;
            var ctx = http.RequestServices.GetRequiredService<AgentContext>();
            var user = ctx.CurrentUser(http);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (!ctx.IsThisMachine(http.Request.RouteValues["machine"] as string)) { return ApiResults.NotFound("Unknown machine."); }

            string? id = http.Request.RouteValues["id"] as string;
            var server = id == null ? null : ctx.Engine.Servers.Get(id);
            if (server == null || !user.Can(Capability.View, ctx.MachineId, server.Id)) { return ApiResults.NotFound("No such server."); }

            var needed = http.GetEndpoint()?.Metadata.GetMetadata<Needs>()?.Capability ?? Capability.View;
            if (!user.Can(needed, ctx.MachineId, server.Id)) { return ApiResults.Forbidden($"You need the {needed} permission on {server.Name}."); }

            http.Items[ServerItem] = server;
            return await next(efc);
        });
}
