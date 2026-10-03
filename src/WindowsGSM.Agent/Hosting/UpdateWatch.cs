using System.Collections.Concurrent;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Knows which servers have a game update waiting: checks each one against Steam (or its plugin) every
/// <see cref="Every"/>, one at a time, and again right after it's been updated. Servers then show an
/// "Update available" badge, and a notification goes out once per new version.
/// </summary>
public sealed class UpdateWatch : IDisposable
{
    public static TimeSpan Every { get; set; } = TimeSpan.FromMinutes(30);
    public static TimeSpan FirstCheckAfter { get; set; } = TimeSpan.FromMinutes(2);

    public sealed record Result(bool Available, string? LocalBuild, string? RemoteBuild, DateTimeOffset CheckedAt, string? Error);

    private readonly AgentContext _ctx;
    private readonly NotificationCentre _notifications;
    private readonly ConcurrentDictionary<string, Result> _results = new();
    private readonly ConcurrentDictionary<string, string> _announced = new(); // server → remote build we notified about
    private readonly IDisposable _subscription;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _checking = new(1, 1);

    public UpdateWatch(AgentContext ctx, NotificationCentre notifications)
    {
        _ctx = ctx;
        _notifications = notifications;
        ctx.UpdateAvailable = id => _results.TryGetValue(id, out var r) && r.Available;
        _subscription = ctx.Engine.Events.Subscribe<ServerStateChanged>(OnState);
    }

    /// <summary>Swappable for tests.</summary>
    public Func<string, Task<UpdateCheck>> Check { get; set; } = null!;

    public void Start()
    {
        Check ??= id => _ctx.Engine.Updates.CheckAsync(id);
        _ = LoopAsync(_stop.Token);
    }

    public Result? Get(string id) => _results.TryGetValue(id, out var r) ? r : null;

    private async Task LoopAsync(CancellationToken token)
    {
        try { await Task.Delay(FirstCheckAfter, token); } catch (OperationCanceledException) { return; }
        while (!token.IsCancellationRequested)
        {
            foreach (var s in _ctx.Engine.Servers.All.ToList())
            {
                if (token.IsCancellationRequested) { return; }
                if (s.State is ServerState.Updating or ServerState.Installing or ServerState.Deleting or ServerState.Moving) { continue; }
                await CheckNowAsync(s.Id);
            }
            try { await Task.Delay(Every, token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Checks one server now (also used by the "Check for updates" button).</summary>
    public async Task<Result> CheckNowAsync(string id)
    {
        Check ??= x => _ctx.Engine.Updates.CheckAsync(x);
        await _checking.WaitAsync();
        try
        {
            UpdateCheck check;
            try { check = await Check(id); }
            catch (Exception ex) { check = new UpdateCheck(null, null, false, false, ex.Message); }
            var before = Get(id);
            // A failed check (Steam unreachable) keeps what we knew rather than hiding a pending update.
            var result = check.Error != null && before != null
                ? before with { Error = check.Error, CheckedAt = DateTimeOffset.UtcNow }
                : new Result(check.UpdateAvailable, check.LocalBuild, check.RemoteBuild, DateTimeOffset.UtcNow, check.Error);
            _results[id] = result;
            if (before?.Available != result.Available)
            {
                _ctx.Engine.Events.Publish(new ServerConfigChanged(id, new[] { "update-available" }));
            }
            if (result.Available && result.RemoteBuild != null && _announced.GetValueOrDefault(id) != result.RemoteBuild)
            {
                _announced[id] = result.RemoteBuild;
                var s = _ctx.Engine.Servers.Get(id);
                if (s != null && !WindowsGSM.Engine.Services.UpdateService.IsHeld(s)) // rolled back on purpose: don't nag
                {
                    _notifications.Record("updateAvailable", $"{s.Name}: update available",
                        $"A new version is out (build {result.RemoteBuild}; this server has {result.LocalBuild ?? "an older one"}).{(s.Config.AutoUpdate ? " Auto-update will install it." : " Update it when it suits you.")}",
                        _ctx.MachineId, id, s.Name, Visibility.Server());
                }
            }
            return result;
        }
        finally { _checking.Release(); }
    }

    /// <summary>Just updated (or reinstalled): look again shortly, so the badge goes away.</summary>
    private void OnState(ServerStateChanged e)
    {
        if (e.From is ServerState.Updating or ServerState.Installing && e.To is ServerState.Stopped or ServerState.Running)
        {
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(5)); await CheckNowAsync(e.ServerId); });
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _subscription.Dispose();
    }
}
