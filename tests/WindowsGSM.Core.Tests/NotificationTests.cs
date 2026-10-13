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
        "discordwebhook=\"https://discord.com/api/webhooks/1/test\"",
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
        Assert.All(sent, n => Assert.Equal(("191", "https://discord.com/api/webhooks/1/test", "@here"), (n.ServerId, n.WebhookUrl, n.CustomMessage)));
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
    [Fact]
    public async Task A_start_by_hand_is_announced_only_when_its_switch_is_on()
    {
        EngineFixture.CreateServer("152", "long", false, Discord().Append("startalert=\"1\"").ToArray());
        EngineFixture.CreateServer("153", extraSettings: Discord()); // no "startalert": off
        using var engine = await EngineFixture.StartEngineAsync();
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };
        try
        {
            foreach (string id in new[] { "152", "153" })
            {
                var start = engine.Lifecycle.Start(id);
                Assert.True(start.Accepted, start.Error);
                await start.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            }
            await EngineFixture.WaitUntil(() => sent.Count >= 1, "the start alert");
            await Settle();
            Assert.Equal(("152", "Started"), sent.Select(n => (n.ServerId, n.Status)).Single());
        }
        finally
        {
            foreach (string id in new[] { "152", "153" }) { var stop = engine.Lifecycle.Stop(id); if (stop.Accepted) { await stop.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60)); } }
        }
    }

    [Theory]
    [InlineData("https://example.com/api/webhooks/1/x")]
    [InlineData("http://discord.com/api/webhooks/1/x")]
    [InlineData("https://discord.com/channels/1/2")]
    [InlineData("not a url")]
    public async Task The_test_button_only_posts_to_Discord_webhooks(string url)
    {
        Assert.Contains("isn't a Discord webhook", await NotificationService.TestWebhookAsync(url, "", false, "Test"));
    }
    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", true)]
    [InlineData("http://192.168.1.1/api/webhooks/1/x", false)]          // a router inside the network
    [InlineData("https://127.0.0.1:8971/api/v2/servers", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/x", false)]
    [InlineData("https://user@discord.com/api/webhooks/1/x", false)]
    [InlineData("https://discord.com:8443/api/webhooks/1/x", false)]
    [InlineData("https://discord.com/channels/1/2", false)]
    public void A_servers_webhook_must_be_Discords(string url, bool ok) => Assert.Equal(ok, NotificationService.IsDiscordWebhook(url));

    [Fact]
    public async Task A_webhook_saved_before_the_check_is_never_posted_to()
    {
        EngineFixture.CreateServer("154", extraSettings: new[] { "discordalert=\"1\"", "discordwebhook=\"http://192.168.1.1/hook\"", "crashalert=\"1\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        var sent = new ConcurrentQueue<Notification>();
        engine.Notifications.Sender = n => { sent.Enqueue(n); return Task.CompletedTask; };
        engine.Events.Publish(new ServerAlert("154", AlertKind.Crashed, "x", "y"));
        await Settle();
        Assert.Empty(sent);
        // And it can't be saved as one now.
        Assert.NotEmpty(engine.Settings.Update("154", new Dictionary<string, string?> { ["discordwebhook"] = "http://192.168.1.1/hook" }));
    }
}