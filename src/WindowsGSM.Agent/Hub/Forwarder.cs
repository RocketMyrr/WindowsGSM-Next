using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Hub;

/// <summary>
/// On a hub: API calls for another machine (/api/v2/machines/{member}/…) are sent down that machine's link. The
/// machine's own summary is answered here from what the hub knows; while a machine is offline its server list
/// comes from the last snapshot (so the panel still shows what it was running) and everything else fails fast.
/// </summary>
public static class Forwarder
{
    private static readonly Regex MachinePath = new(@"^/api/v2/machines/([^/]+)(/.*)?$", RegexOptions.Compiled);

    public static async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        var match = MachinePath.Match(http.Request.Path.Value ?? "");
        var ctx = http.RequestServices.GetRequiredService<AgentContext>();
        if (!match.Success || ctx.IsThisMachine(match.Groups[1].Value)) { await next(http); return; }

        var registry = http.RequestServices.GetRequiredService<MachineRegistry>();
        var machine = registry.Get(match.Groups[1].Value);
        if (machine == null) { await next(http); return; } // the endpoint answers "Unknown machine"

        var user = ctx.CurrentUser(http);
        if (user == null) { await ApiResults.Unauthorized().ExecuteAsync(http); return; }

        var links = http.RequestServices.GetRequiredService<HubLinks>();
        string rest = match.Groups[2].Value;
        bool get = HttpMethods.IsGet(http.Request.Method);

        if (get && rest.Length == 0)
        {
            await Results.Json(Describe(machine, links)).ExecuteAsync(http);
            return;
        }
        if (!links.IsOnline(machine.Id))
        {
            if (get && rest == "/servers")
            {
                var cached = machine.Snapshot
                    .Where(s => user.Can(Capability.View, machine.Id, s.Id))
                    .Select(s => s with { Machine = machine.Id, Can = user.On(machine.Id, s.Id), Players = null, CpuPercent = null, MemoryMb = null })
                    .ToList();
                http.Response.Headers["X-WGSM-Stale"] = "1";
                await Results.Json(cached).ExecuteAsync(http);
                return;
            }
            if (get && (rest == "/jobs" || rest == "/prompts")) { await Results.Json(Array.Empty<object>()).ExecuteAsync(http); return; }
            await ApiResults.Error(503, "machine_offline", $"{machine.Name} is offline right now — it will reconnect by itself when it's back.").ExecuteAsync(http);
            return;
        }
        await links.ForwardAsync(http, machine.Id, user);
    }

    public static MachineDto Describe(RemoteMachine m, HubLinks links) =>
        new(m.Id, m.Name, false, links.IsOnline(m.Id), links.VersionOf(m.Id) ?? m.Version ?? "?", links.IsOnline(m.Id) ? DateTimeOffset.UtcNow : m.LastSeen,
            m.Metrics, m.Snapshot.Count);
}
