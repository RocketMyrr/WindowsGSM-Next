using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Windows Firewall rules for game servers: what each server's status is, and "Allow through firewall" — one
/// Windows administrator prompt on this machine for one server or all of them (admins; it changes Windows).
/// </summary>
public static class FirewallEndpoints
{
    private static object Dto(ServerInstance s, FirewallStatus f) =>
        new { server = s.Id, name = s.Name, program = f.Program, state = f.State, message = f.Message };

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        server.MapGet("/firewall", (HttpContext http, AgentContext ctx) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            return Results.Json(new { status = Dto(s, GameFirewall.Status(s, ctx.Engine.Plugins)), elevated = GameFirewall.IsElevated });
        });

        server.MapPost("/firewall", async (HttpContext http, AgentContext ctx) =>
        {
            if (!Scopes.User(http).IsAdmin) { return ApiResults.Forbidden("Only admins and owners can change Windows Firewall."); }
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            var f = GameFirewall.Status(s, ctx.Engine.Plugins);
            if (f.Program == null) { return ApiResults.BadRequest(f.Message); }
            string? error = await GameFirewall.AllowAsync(new[] { (s, f.Program) });
            ctx.Record(http, "firewall", s.Id, error == null, error ?? $"allowed {Path.GetFileName(f.Program)}");
            return error == null ? Results.Json(Dto(s, GameFirewall.Status(s, ctx.Engine.Plugins))) : ApiResults.BadRequest(error);
        });

        // Router port forwarding (UPnP) for this server.
        server.MapGet("/port-forwarding", (HttpContext http, Hosting.PortForwarding upnp) => Results.Json(upnp.Get(Scopes.Server(http).Id)));

        server.MapPost("/port-forwarding", async (HttpContext http, AgentContext ctx, Hosting.PortForwarding upnp, PortForwardingRequest body) =>
        {
            if (!Scopes.User(http).IsAdmin) { return ApiResults.Forbidden("Only admins and owners can change the router's port forwarding."); }
            var s = Scopes.Server(http);
            Hosting.PortForwarding.Status status;
            try { status = await upnp.SetAsync(s.Id, body.Enabled, http.RequestAborted); }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or HttpRequestException or IOException)
            {
                return ApiResults.Error(502, "router", $"Couldn't talk to the router: {ex.Message}");
            }
            ctx.Record(http, "port-forwarding", s.Id, status.Problem == null, body.Enabled ? status.Problem ?? $"forwarded on {status.Router}" : "removed");
            return Results.Json(status);
        });

        var machine = api.MapGroup("/machines/{machine}/firewall").RequireMachine();

        machine.MapGet("", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            var rules = GameFirewall.ReadRules();
            return Results.Json(new
            {
                elevated = GameFirewall.IsElevated,
                servers = ctx.Engine.Servers.All.Select(s => Dto(s, GameFirewall.Status(s, ctx.Engine.Plugins, rules))).ToList(),
            });
        });

        // Every server that has no rule (or a blocking one), with one prompt.
        machine.MapPost("", async (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can change Windows Firewall."); }
            var rules = GameFirewall.ReadRules();
            var todo = ctx.Engine.Servers.All
                .Select(s => (s, f: GameFirewall.Status(s, ctx.Engine.Plugins, rules)))
                .Where(x => x.f.Program != null && x.f.State is "missing" or "blocked")
                .Select(x => (x.s, x.f.Program!)).ToList();
            if (todo.Count == 0) { return Results.Json(new { allowed = 0 }); }
            string? error = await GameFirewall.AllowAsync(todo);
            ctx.Record(http, "firewall", null, error == null, error ?? $"allowed {todo.Count} server(s): {string.Join(", ", todo.Select(t => "#" + t.s.Id))}");
            return error == null ? Results.Json(new { allowed = todo.Count }) : ApiResults.BadRequest(error);
        });
    }
}

public sealed record PortForwardingRequest(bool Enabled);
