using System.Collections.Concurrent;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Core.Tests;

/// <summary>Discord alerts follow each server's settings and are throttled per server (legacy throttled app-wide).</summary>
[Collection("Lifecycle")]
public class NotificationTests
{
    private static string[] Discord(bool crashAlert = true) => new[]
    {
        "discordalert=\"1\"",
        "discordwebhook=\"https://discord.example/webhook\"",
        "discordmessage=\"@here\"",
        $"crashalert=\"{(crashAlert ? 1 : 0)}\"",
        "autorestartalert=\"1\"",
    };

    private static async Task Settle() => await Task.Delay(300); // sends happen off the event thread

    [Fact]
    public async Task Alerts_go_out_with_the_legacy_status_text_and_only_when_switched_on()
    {
        EngineFixture.CreateServer("191", extraSettings: Discord());
        EngineFixture.CreateServer("192", extraSettings: Discord(crashAlert: false));
        using var engine = await EngineFixture.StartEngineAsync();
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };

        engine.Events.Publish(new ServerAlert("191", AlertKind.Crashed, "x", "y"));
        engine.Events.Publish(new ServerAlert("191", AlertKind.AutoRestarted, "x", "y"));
        engine.Events.Publish(new ServerAlert("192", AlertKind.Crashed, "x", "y")); // crash alerts off for 192
        // Sends happen in the background, in no fixed order: wait for both, then give a stray third time to show up.
        await EngineFixture.WaitUntil(() => sent.Count >= 2, "two alerts sent");
        await Settle();

        Assert.Equal(new[] { "Crashed", "Restarted | Auto Restart" }, sent.Select(n => n.Status).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(sent, n => Assert.Equal(("191", "https://discord.example/webhook", "@here"), (n.ServerId, n.WebhookUrl, n.CustomMessage)));
    }

    [Fact]
    public async Task Repeats_are_throttled_per_server_not_across_servers()
    {
        EngineFixture.CreateServer("193", extraSettings: Discord());
        EngineFixture.CreateServer("194", extraSettings: Discord());
        using var engine = await EngineFixture.StartEngineAsync();
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };

        engine.Events.Publish(new ServerAlert("193", AlertKind.Crashed, "x", "y"));
        engine.Events.Publish(new ServerAlert("193", AlertKind.Crashed, "x", "y")); // same server, within 30 s
        engine.Events.Publish(new ServerAlert("194", AlertKind.Crashed, "x", "y")); // a different server — legacy dropped this
        await EngineFixture.WaitUntil(() => sent.Count >= 2, "both servers' alerts sent");
        await Settle(); // and no third

        Assert.Equal(new[] { "193", "194" }, sent.Select(n => n.ServerId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task A_suppressed_repeat_does_not_extend_the_quiet_period()
    {
        EngineFixture.CreateServer("195", extraSettings: Discord());
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Notifications.RepeatWindow = TimeSpan.FromSeconds(30);
        var now = DateTimeOffset.UtcNow;
        engine.Notifications.Clock = () => now; // the test moves time, so a slow machine can't stretch the gaps
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };

        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // sent at t0
        now = now.AddSeconds(20);
        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // t0+20 s: suppressed
        now = now.AddSeconds(20);
        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // t0+40 s: past the window → sent
        await EngineFixture.WaitUntil(() => sent.Count >= 2, "two alerts sent"); // (legacy would have reset at 20 s and dropped this)
        await Settle();

        Assert.Equal(2, sent.Count);
    }
}
