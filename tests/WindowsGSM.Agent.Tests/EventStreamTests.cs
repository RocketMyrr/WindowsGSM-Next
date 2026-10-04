using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Events;

namespace WindowsGSM.Agent.Tests;

/// <summary>The WebSocket feed: topics, permission filtering, resume and the origin check.</summary>
[Collection("Agent")]
public class EventStreamTests
{
    private readonly AgentFixture _f;
    public EventStreamTests(AgentFixture f) => _f = f;

    private sealed class Feed : IAsyncDisposable
    {
        private readonly WebSocket _ws;
        private readonly List<JsonElement> _received = new();
        private readonly Task _pump;
        private readonly CancellationTokenSource _stop = new();

        public Feed(WebSocket ws)
        {
            _ws = ws;
            _pump = Task.Run(async () =>
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    while (_ws.State == WebSocketState.Open)
                    {
                        var sb = new StringBuilder();
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await _ws.ReceiveAsync(buffer, _stop.Token);
                            if (r.MessageType == WebSocketMessageType.Close) { return; }
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                        } while (!r.EndOfMessage);
                        lock (_received) { _received.Add(JsonDocument.Parse(sb.ToString()).RootElement.Clone()); }
                    }
                }
                catch { /* closed */ }
            });
        }

        public List<JsonElement> Received { get { lock (_received) { return _received.ToList(); } } }

        public Task Send(object message) =>
            _ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, AgentFixture.Json), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> WaitFor(Func<JsonElement, bool> match, string what)
        {
            JsonElement found = default;
            await EngineFixture.WaitUntil(() => { var hit = Received.Where(match).ToList(); if (hit.Count == 0) { return false; } found = hit[0]; return true; }, what, 10000);
            return found;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); }
            catch (OperationCanceledException) { Assert.Fail("The server never completed the close handshake."); }
            // The in-memory test server tears the pipe down as soon as the server finishes closing, so the
            // client may see its end vanish instead of the close frame — either way, the server ended it.
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
            _stop.Cancel();
            await _pump;
        }
    }

    private async Task<Feed> Connect(ApiClient client, string? origin = null)
    {
        var ws = _f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r =>
        {
            r.Headers["Cookie"] = client.CookieHeader;
            r.Headers["X-Test-Ip"] = client.Ip;
            if (origin != null) { r.Headers["Origin"] = origin; }
        };
        var socket = await ws.ConnectAsync(new Uri(_f.Server.BaseAddress, "/api/v2/events"), CancellationToken.None);
        return new Feed(socket);
    }

    private static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    [Fact]
    public async Task Subscribers_get_the_topics_they_asked_for()
    {
        await using var feed = await Connect(_f.Owner);
        var hello = await feed.WaitFor(e => Type(e) == "hello", "hello");
        Assert.Equal(_f.MachineId, hello.GetProperty("data").GetProperty("machine").GetString());

        await feed.Send(new SubscribeMessage("subscribe", new[] { "servers" }, null));
        await feed.WaitFor(e => Type(e) == "subscribed", "the subscription to be confirmed");
        _f.Engine.Events.Publish(new ServerConfigChanged("102", new[] { "servername" }));
        _f.Engine.Events.Publish(new ServerAlert("102", AlertKind.Other, "t", "x")); // "alerts" topic: not subscribed

        var config = await feed.WaitFor(e => Type(e) == "serverConfig", "serverConfig event");
        Assert.Equal("102", config.GetProperty("server").GetString());
        Assert.Equal(_f.MachineId, config.GetProperty("machine").GetString());
        await Task.Delay(300);
        Assert.DoesNotContain(feed.Received, e => Type(e) == "alert");
    }

    [Fact]
    public async Task Events_for_servers_you_cannot_see_never_arrive()
    {
        var member = await _f.UserAsync("feedmember", Role.Member,
            new Dictionary<string, Capability> { [$"{_f.MachineId}/102"] = Capability.View });
        await using var feed = await Connect(member);
        await feed.WaitFor(e => Type(e) == "hello", "hello");
        await feed.Send(new SubscribeMessage("subscribe", new[] { "servers", "console:local/102" }, null));
        await feed.WaitFor(e => Type(e) == "subscribed", "the subscription to be confirmed");

        _f.Engine.Events.Publish(new ServerConfigChanged("101", new[] { "x" }));      // can't see 101
        _f.Engine.Events.Publish(new ConsoleLineAdded("102", "secret console line")); // can see 102, but no Console right
        _f.Engine.Events.Publish(new ServerConfigChanged("102", new[] { "y" }));      // visible

        await feed.WaitFor(e => Type(e) == "serverConfig" && e.GetProperty("server").GetString() == "102", "event for 102");
        await Task.Delay(300);
        Assert.DoesNotContain(feed.Received, e => Type(e) == "serverConfig" && e.GetProperty("server").GetString() == "101");
        Assert.DoesNotContain(feed.Received, e => Type(e) == "console");
    }

    [Fact]
    public async Task Reconnecting_with_since_replays_what_was_missed()
    {
        long seq;
        await using (var first = await Connect(_f.Owner))
        {
            var hello = await first.WaitFor(e => Type(e) == "hello", "hello");
            seq = hello.GetProperty("data").GetProperty("seq").GetInt64();
        }

        _f.Engine.Events.Publish(new ServerConfigChanged("103", new[] { "missed-while-away" })); // nobody listening

        await using var second = await Connect(_f.Owner);
        await second.WaitFor(e => Type(e) == "hello", "hello");
        await second.Send(new SubscribeMessage("subscribe", new[] { "servers" }, seq));
        var missed = await second.WaitFor(e => Type(e) == "serverConfig" && e.GetProperty("data").GetProperty("keys")[0].GetString() == "missed-while-away", "replayed event");
        Assert.True(missed.GetProperty("seq").GetInt64() > seq);
    }

    [Fact]
    public async Task A_gap_bigger_than_the_buffer_says_reset()
    {
        await using var feed = await Connect(_f.Owner);
        await feed.WaitFor(e => Type(e) == "hello", "hello");
        for (int i = 0; i < Realtime.EventStream.BufferSize + 5; i++) { _f.Engine.Events.Publish(new ServerConfigChanged("103", new[] { "flood" })); }
        await feed.Send(new SubscribeMessage("subscribe", new[] { "servers" }, 0));
        await feed.WaitFor(e => Type(e) == "reset", "reset");
    }

    [Fact]
    public async Task Other_sites_and_anonymous_visitors_cannot_open_the_stream()
    {
        var ws = _f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers["Cookie"] = _f.Owner.CookieHeader;
        ws.ConfigureRequest += r => r.Headers["Origin"] = "https://evil.example";
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri(_f.Server.BaseAddress, "/api/v2/events"), CancellationToken.None));

        var anon = _f.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => anon.ConnectAsync(new Uri(_f.Server.BaseAddress, "/api/v2/events"), CancellationToken.None));

        // Same origin is fine.
        await using var ok = await Connect(_f.Owner, origin: _f.Server.BaseAddress.GetLeftPart(UriPartial.Authority));
        await ok.WaitFor(e => Type(e) == "hello", "hello");
        Assert.Equal(HttpStatusCode.OK, (await _f.Owner.GetAsync("/api/v2/health")).StatusCode);
    }
}
