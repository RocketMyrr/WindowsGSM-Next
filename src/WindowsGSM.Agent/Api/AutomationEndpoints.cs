using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>Automations on a machine ("if this, then that"). Admins: rules act on servers with the agent's rights.</summary>
public static class AutomationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var machine = api.MapGroup("/machines/{machine}/automations").RequireMachine();

        machine.MapGet("", (HttpContext http, AgentContext ctx, Automations rules) =>
            ctx.CurrentUser(http)!.IsAdmin ? Results.Json(rules.All()) : ApiResults.Forbidden());

        machine.MapPut("/{id}", (HttpContext http, AgentContext ctx, Automations rules, string id, AutomationRule body) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can change automations."); }
            body.Id = id;
            body.Servers = (body.Servers ?? new()).Where(s => ctx.Engine.Servers.Get(s) != null).Distinct().ToList();
            string? problem = rules.Save(body);
            ctx.Record(http, "automation", null, problem == null, problem ?? $"{body.Name}: {body.Trigger} → {body.Action}");
            return problem == null ? Results.Json(rules.All().First(r => r.Id == id)) : ApiResults.BadRequest(problem);
        });

        machine.MapDelete("/{id}", (HttpContext http, AgentContext ctx, Automations rules, string id) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            bool ok = rules.Delete(id);
            ctx.Record(http, "automation-delete", null, ok, id);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such automation.");
        });
    }
}
