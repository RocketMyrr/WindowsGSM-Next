using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>Per-server restart warnings: "Server restarting in 5 minutes." in chat before scheduled restarts.</summary>
public static class WarningEndpoints
{
    public static void Map(RouteGroupBuilder server)
    {
        server.MapGet("/restart-warnings", (HttpContext http, RestartWarnings warnings) =>
        {
            var s = warnings.Get(Scopes.Server(http).Id);
            return Results.Json(new { s.Enabled, s.Leads, s.Command, s.Message, allowedLeads = RestartWarnings.AllowedLeads });
        });

        server.MapPut("/restart-warnings", (HttpContext http, AgentContext ctx, RestartWarnings warnings, WarningSettings body) =>
        {
            string id = Scopes.Server(http).Id;
            string? problem = warnings.Set(id, body);
            ctx.Record(http, "restart-warnings", id, problem == null, problem ?? (body.Enabled ? $"on: {string.Join(", ", body.Leads.Select(RestartWarnings.TimeWords))}" : "off"));
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        }).Needs(Capability.Schedules);

        // "Can players reach it?" — asks the internet (public IP, Steam's server list), so it's on demand only.
        server.MapGet("/reachability", async (HttpContext http, Reachability reach) =>
            Results.Json(await reach.CheckAsync(Scopes.Server(http).Id)));

        server.MapPost("/restart-warnings/test", async (HttpContext http, AgentContext ctx, RestartWarnings warnings) =>
        {
            string id = Scopes.Server(http).Id;
            string? error = await warnings.SendTestAsync(id);
            ctx.Record(http, "restart-warnings-test", id, error == null, error);
            return error == null ? Results.NoContent() : ApiResults.BadRequest(error);
        }).Needs(Capability.Schedules);
    }
}
