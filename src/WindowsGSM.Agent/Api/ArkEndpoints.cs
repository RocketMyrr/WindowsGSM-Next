using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>ARK servers: the ASA mod list and clusters.</summary>
public static class ArkEndpoints
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        server.MapGet("/ark", (HttpContext http, AgentContext ctx, ArkTools ark) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            string? kind = ark.Kind(s);
            if (kind == null) { return ApiResults.BadRequest("Only for ARK servers."); }
            var user = Scopes.User(http);
            var cluster = ark.Cluster(s);
            return Results.Json(new
            {
                kind,
                mods = kind == "asa" ? ark.Mods(s) : null,
                cluster = new { id = cluster.ClusterId, dir = cluster.Dir, members = cluster.Members },
                defaultDir = ArkTools.DefaultClusterDir("my-cluster"),
                // Other ARK servers of the same kind this user may change, to pick cluster members from.
                candidates = ctx.Engine.Servers.All
                    .Where(o => ark.Kind(o) == kind && user.Can(Capability.EditConfig, ctx.MachineId, o.Id))
                    .Select(o => new { id = o.Id, name = o.Name, cluster = ArkTools.GetFlag(o.Config.ServerParam, "clusterid") }),
            });
        }).Needs(Capability.View);

        server.MapPut("/ark/mods", (HttpContext http, AgentContext ctx, ArkTools ark, ArkModsRequest body) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (ark.Kind(s) != "asa") { return ApiResults.BadRequest("The mod list is for ARK: Survival Ascended (CurseForge mods)."); }
            var mods = (body.Mods ?? Array.Empty<ArkTools.ModEntry>()).Select(m => new ArkTools.ModEntry(m.Id?.Trim() ?? "", m.Name)).ToList();
            string? problem = ark.SetMods(s, mods);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            ctx.Record(http, "ark-mods", s.Id, true, mods.Count == 0 ? "no mods" : string.Join(",", mods.Select(m => m.Id)));
            ctx.Engine.Events.Publish(new WindowsGSM.Engine.Events.ServerConfigChanged(s.Id, new[] { "serverparam" }));
            return Results.Json(ark.Mods(s));
        }).Needs(Capability.EditConfig);

        server.MapPut("/ark/cluster", (HttpContext http, AgentContext ctx, ArkTools ark, ArkClusterRequest body) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (ark.Kind(s) == null) { return ApiResults.BadRequest("Only for ARK servers."); }
            var user = Scopes.User(http);
            var ids = (body.Servers ?? Array.Empty<string>()).Append(s.Id).Distinct().ToList();
            // Leaving: everyone in the cluster now who isn't picked any more — they must be changeable too.
            var before = ark.Cluster(s).Members;
            if (ids.Concat(before).Any(id => !user.Can(Capability.EditConfig, ctx.MachineId, id))) { return ApiResults.Forbidden("You can't change the settings of every server in that cluster."); }
            string? problem = string.IsNullOrWhiteSpace(body.ClusterId) ? ark.SetCluster(null, new[] { s.Id }, null) : ark.SetCluster(body.ClusterId, ids, body.Dir);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            ctx.Record(http, "ark-cluster", s.Id, true, string.IsNullOrWhiteSpace(body.ClusterId) ? "left the cluster" : $"{body.ClusterId}: {string.Join(", ", ids.Select(i => "#" + i))}");
            foreach (string id in ids.Concat(before).Distinct()) { ctx.Engine.Events.Publish(new WindowsGSM.Engine.Events.ServerConfigChanged(id, new[] { "serverparam" })); }
            var c = ark.Cluster(ctx.Engine.Servers.Get(s.Id)!);
            return Results.Json(new { id = c.ClusterId, dir = c.Dir, members = c.Members });
        }).Needs(Capability.EditConfig);
    }
}

public sealed record ArkModsRequest(IReadOnlyList<ArkTools.ModEntry>? Mods);
public sealed record ArkClusterRequest(string? ClusterId, IReadOnlyList<string>? Servers, string? Dir);
