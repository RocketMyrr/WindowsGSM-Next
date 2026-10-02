using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Notifications;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Events;

namespace WindowsGSM.Agent.Tests;

/// <summary>The notification centre (history, who sees what, read markers) and channels (rules, delivery, secrets).</summary>
[Collection("Agent")]
public class NotificationTests
{
    private readonly AgentFixture _f;
    public NotificationTests(AgentFixture f) => _f = f;

    private NotificationChannels Channels => _f.App.Services.GetRequiredService<NotificationChannels>();

    private sealed record Item(long Id, string Kind, string Severity, string Title, string Text, string Machine, string? Server, string? ServerName);
    private sealed record Page(List<Item> Items, int Unread, long ReadUpTo);
    private sealed record ChannelDto(string Id, string Name, string Type, string Url, bool Enabled, List<string> Events, List<string> Scope, string? LastError);

    [Fact]
    public async Task Alerts_are_kept_shown_only_to_those_who_may_see_them_and_can_be_marked_read()
    {
        string marker = "crash-" + Guid.NewGuid().ToString("N")[..6];
        _f.Engine.Events.Publish(new ServerAlert("102", AlertKind.Crashed, $"Server 102 crashed {marker}", "Exit code 1."));

        Page page = null!;
        await EngineFixture.WaitUntil(() =>
        {
            page = _f.Owner.GetJsonAsync<Page>("/api/v2/notifications").GetAwaiter().GetResult();
            return page.Items.Any(i => i.Title.Contains(marker));
        }, "the crash to be recorded");
        var item = page.Items.First(i => i.Title.Contains(marker));
        Assert.Equal("crashed", item.Kind);
        Assert.Equal("bad", item.Severity);
        Assert.Equal("102", item.Server);
        Assert.Equal(_f.MachineId, item.Machine);
        Assert.True(page.Unread >= 1);

        // Someone who can only see server 101 doesn't hear about 102; someone with 102 does.
        var other = await _f.UserAsync("notify101", Role.Member, new() { [$"{_f.MachineId}/101"] = Capability.View });
        Assert.DoesNotContain((await other.GetJsonAsync<Page>("/api/v2/notifications")).Items, i => i.Title.Contains(marker));
        var friend = await _f.UserAsync("notify102", Role.Member, new() { [$"{_f.MachineId}/102"] = Capability.View });
        var theirs = await friend.GetJsonAsync<Page>("/api/v2/notifications");
        Assert.Contains(theirs.Items, i => i.Title.Contains(marker));
        Assert.True(theirs.Unread >= 1);

        // Reading is per person.
        var read = await ApiClient.Read<JsonElement>(await friend.PostAsync("/api/v2/notifications/read", new { upTo = theirs.Items.Max(i => i.Id) }));
        Assert.Equal(0, read.GetProperty("unread").GetInt32());
        Assert.True((await _f.Owner.GetJsonAsync<Page>("/api/v2/notifications")).Unread >= 1);
    }

    [Fact]
    public async Task Channels_are_owner_managed_keep_their_url_secret_and_get_what_they_asked_for()
    {
        var sent = new ConcurrentQueue<(string Channel, NotificationEntry Entry)>();
        Channels.SendOverride = (c, e, _) => { sent.Enqueue((c.Name, e)); return Task.CompletedTask; };
        try
        {
            string url = "https://discord.com/api/webhooks/123/very-secret-token";
            var admin = await _f.UserAsync("notifyadmin", Role.Admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/notifications/channels", new { name = "x", type = "discord", url, enabled = true, events = new[] { "crashed" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/notifications/channels", new { name = "Bad", type = "discord", url = "https://example.com/hook", enabled = true, events = new[] { "crashed" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/notifications/channels", new { name = "None", type = "discord", url, enabled = true, events = Array.Empty<string>() })).StatusCode);

            // Crashes on server 101 only.
            var created = await _f.Owner.PostAsync("/api/v2/notifications/channels", new { name = "Crashes on 101", type = "discord", url, enabled = true, events = new[] { "crashed" }, scope = new[] { $"{_f.MachineId}/101" } });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var channel = await ApiClient.Read<ChannelDto>(created);
            Assert.DoesNotContain("very-secret-token", channel.Url);

            // Admins can see the list — still masked.
            var listed = await admin.GetJsonAsync<List<ChannelDto>>("/api/v2/notifications/channels");
            Assert.Contains(listed, c => c.Id == channel.Id && !c.Url.Contains("very-secret"));

            _f.Engine.Events.Publish(new ServerAlert("102", AlertKind.Crashed, "Server 102 crashed", "not covered"));
            _f.Engine.Events.Publish(new ServerAlert("101", AlertKind.AutoRestarted, "Server 101 restarted", "not subscribed"));
            _f.Engine.Events.Publish(new ServerAlert("101", AlertKind.Crashed, "Server 101 crashed", "covered"));
            await EngineFixture.WaitUntil(() => sent.Any(s => s.Channel == "Crashes on 101"), "the crash to be sent");
            await Task.Delay(300);
            Assert.All(sent.Where(s => s.Channel == "Crashes on 101"), s => Assert.Equal("covered", s.Entry.Text));

            // A second crash straight after is throttled (a crash loop mustn't flood the channel).
            int before = sent.Count(s => s.Channel == "Crashes on 101");
            _f.Engine.Events.Publish(new ServerAlert("101", AlertKind.Crashed, "Server 101 crashed", "covered again"));
            await Task.Delay(300);
            Assert.Equal(before, sent.Count(s => s.Channel == "Crashes on 101"));

            // Editing without a URL keeps the saved one; "send a test" goes through the same path.
            var put = await _f.Owner.PutAsync($"/api/v2/notifications/channels/{channel.Id}", new { name = "Crashes on 101", type = "discord", url = "", enabled = true, events = new[] { "crashed", "crashLoop" }, scope = new[] { $"{_f.MachineId}/101" } });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(url, Channels.Get(channel.Id)!.Url);
            Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PostAsync($"/api/v2/notifications/channels/{channel.Id}/test")).StatusCode);
            Assert.Contains(sent, s => s.Entry.Title == "Test from WindowsGSM");

            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.DeleteAsync($"/api/v2/notifications/channels/{channel.Id}")).StatusCode);
            Assert.Null(Channels.Get(channel.Id));
        }
        finally { Channels.SendOverride = null; }
    }

    [Fact]
    public async Task A_failing_channel_reports_why()
    {
        Channels.SendOverride = (_, _, _) => throw new HttpRequestException("404 Unknown Webhook");
        try
        {
            var created = await ApiClient.Read<ChannelDto>(await _f.Owner.PostAsync("/api/v2/notifications/channels",
                new { name = "Broken", type = "webhook", url = "https://example.com/hook", enabled = true, events = new[] { "crashed" } }));
            var test = await _f.Owner.PostAsync($"/api/v2/notifications/channels/{created.Id}/test");
            Assert.Equal(HttpStatusCode.BadGateway, test.StatusCode);
            Assert.Contains("Unknown Webhook", (await ApiClient.Read<ApiError>(test)).Error);
            Assert.Contains("Unknown Webhook", Channels.Get(created.Id)!.LastError);
            await _f.Owner.DeleteAsync($"/api/v2/notifications/channels/{created.Id}");
        }
        finally { Channels.SendOverride = null; }
    }
}
