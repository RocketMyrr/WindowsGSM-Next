using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WindowsGSM.Agent.Hub;
using WindowsGSM.Contracts;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Multi-machine: pairing machines with this hub, removing them, and — on a member — joining or leaving a hub.
/// A machine is either a hub (others report to it) or a member (it reports to one hub), never both.
/// </summary>
public static class HubEndpoints
{
    public sealed record PairRequest(string Code, string MachineId, string MachineName, string? Version);
    public sealed record JoinRequest(string HubUrl, string Code);
    public sealed record RenameRequest(string Name);

    public static void Map(RouteGroupBuilder api)
    {
        // ── Hub side ──

        api.MapPost("/hub/pairing-codes", (HttpContext http, AgentContext ctx, PairingCodes codes, MemberLink member) =>
        {
            var user = ctx.CurrentUser(http);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (!user.IsOwner) { return ApiResults.Forbidden("Only owners can add machines."); }
            if (member.Joined) { return ApiResults.Conflict($"This machine reports to the hub {ctx.Settings.HubName}. Add machines on that hub instead."); }
            var (code, expires) = codes.Create();
            ctx.Record(http, "pairing-code", null, true);
            return Results.Json(new { code, expiresAt = expires, hubUrls = HubAddresses(ctx), reachable = ctx.Settings.ExposeToNetwork || ctx.Settings.AcmeEnabled });
        });

        // Called by the machine being added (it has no account here — the one-time code is its proof).
        api.MapPost("/hub/pair", (HttpContext http, AgentContext ctx, PairingCodes codes, MachineRegistry registry, MemberLink member, PairRequest body) =>
        {
            if (member.Joined) { return ApiResults.Conflict("This machine is itself a member of another hub, so it can't accept machines."); }
            if (!codes.Consume(body.Code))
            {
                ctx.Audit.Write(null, AgentContext.Ip(http), "machine-pair", null, false, $"wrong or expired code from {body.MachineName}");
                return ApiResults.Error(403, "bad_code", "That pairing code is wrong or has expired. Create a new one on the hub.");
            }
            if (string.IsNullOrWhiteSpace(body.MachineId) || body.MachineId.Length > 64 || body.MachineId.Contains('/')) { return ApiResults.BadRequest("Invalid machine id."); }
            if (string.Equals(body.MachineId, ctx.MachineId, StringComparison.OrdinalIgnoreCase)) { return ApiResults.BadRequest("A machine can't join itself."); }
            string credential = registry.Pair(body.MachineId, body.MachineName, body.Version);
            ctx.Audit.Write(null, AgentContext.Ip(http), "machine-pair", null, true, $"{body.MachineName} ({body.MachineId})");
            return Results.Json(new { credential, hubName = ctx.Settings.MachineName, hubMachineId = ctx.MachineId });
        }).RequireRateLimiting(AuthEndpoints.LoginRateLimit);

        api.Map("/hub/link", (HttpContext http, HubLinks links) => links.HandleLinkAsync(http));

        api.MapPatch("/hub/machines/{id}", (HttpContext http, AgentContext ctx, MachineRegistry registry, string id, RenameRequest body) =>
        {
            if (ctx.CurrentUser(http) is not { IsOwner: true }) { return ApiResults.Forbidden("Only owners can rename machines."); }
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 60) { return ApiResults.BadRequest("Give the machine a name (up to 60 characters)."); }
            if (!registry.Rename(id, body.Name)) { return ApiResults.NotFound("No such machine."); }
            ctx.Record(http, "machine-rename", null, true, $"{id} → {body.Name}");
            return Results.NoContent();
        });

        api.MapDelete("/hub/machines/{id}", async (HttpContext http, AgentContext ctx, MachineRegistry registry, HubLinks links, string id) =>
        {
            if (ctx.CurrentUser(http) is not { IsOwner: true }) { return ApiResults.Forbidden("Only owners can remove machines."); }
            var machine = registry.Get(id);
            if (machine == null) { return ApiResults.NotFound("No such machine."); }
            await links.DisconnectAsync(id, unpaired: true);
            registry.Remove(id);
            ctx.Record(http, "machine-remove", null, true, $"{machine.Name} ({id})");
            return Results.NoContent();
        });

        // ── Member side ──

        api.MapGet("/link", (HttpContext http, AgentContext ctx, MemberLink member) =>
        {
            if (ctx.CurrentUser(http) is not { IsAdmin: true }) { return ApiResults.Forbidden(); }
            return Results.Json(new
            {
                joined = member.Joined,
                state = member.State.ToString(),
                hubUrl = ctx.Settings.HubUrl,
                hubName = ctx.Settings.HubName,
                pinnedCertificate = ctx.Settings.HubCertThumbprint,
                connectedSince = member.ConnectedSince,
                lastError = member.LastError,
            });
        });

        api.MapPost("/link", async (HttpContext http, AgentContext ctx, MemberLink member, MachineRegistry registry, WindowsGSM.Agent.Discord.DiscordBotService bot, JoinRequest body) =>
        {
            if (ctx.CurrentUser(http) is not { IsOwner: true }) { return ApiResults.Forbidden("Only owners can join a hub."); }
            if (registry.Any) { return ApiResults.Conflict("Other machines report to this one, so it's a hub — it can't also join another hub. Remove its machines first."); }
            string? problem = await member.JoinAsync(body.HubUrl, body.Code);
            if (problem == null) { await bot.StandDownAsync(); } // the hub's bot covers this machine now
            ctx.Record(http, "hub-join", null, problem == null, problem ?? body.HubUrl);
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        });

        api.MapDelete("/link", async (HttpContext http, AgentContext ctx, MemberLink member, WindowsGSM.Agent.Discord.DiscordBotService bot) =>
        {
            if (ctx.CurrentUser(http) is not { IsOwner: true }) { return ApiResults.Forbidden("Only owners can leave a hub."); }
            string? hub = ctx.Settings.HubName;
            await member.LeaveAsync();
            await bot.StartAsync(); // on its own again: its bot (if it was set up) comes back
            ctx.Record(http, "hub-leave", null, true, hub);
            return Results.NoContent();
        });
    }

    /// <summary>Addresses other machines could use to reach this hub (shown with the pairing code).</summary>
    public static IReadOnlyList<string> HubAddresses(AgentContext ctx)
    {
        var s = ctx.Settings;
        string scheme = s.UseHttps || s.AcmeEnabled ? "https" : "http";
        var hosts = new List<string>();
        if (s.AcmeEnabled && !string.IsNullOrWhiteSpace(s.AcmeDomain)) { hosts.Add(s.AcmeDomain); }
        hosts.Add(Environment.MachineName);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)) { hosts.Add(a.Address.ToString()); }
                }
            }
        }
        catch { /* best effort */ }
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(h => $"{scheme}://{h}:{s.Port}").ToList();
    }
}
