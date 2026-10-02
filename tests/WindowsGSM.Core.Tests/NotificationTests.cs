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
        await Settle();

        Assert.Equal(new[] { "Crashed", "Restarted | Auto Restart" }, sent.Select(n => n.Status).ToArray());
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
        await Settle();

        Assert.Equal(new[] { "193", "194" }, sent.Select(n => n.ServerId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task A_suppressed_repeat_does_not_extend_the_quiet_period()
    {
        EngineFixture.CreateServer("195", extraSettings: Discord());
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Notifications.RepeatWindow = TimeSpan.FromMilliseconds(400);
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };

        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // sent at t0
        await Task.Delay(250);
        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // t0+250: suppressed
        await Task.Delay(250);
        engine.Events.Publish(new ServerAlert("195", AlertKind.Crashed, "x", "y")); // t0+500: past the window → sent
        await Settle();                                                             // (legacy would have reset at 250 and dropped this)

        Assert.Equal(2, sent.Count);
    }
}
