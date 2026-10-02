using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using WindowsGSM.Agent.Hub;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Events;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Multi-machine through the real hub code. The "remote" machine is a real member link whose API calls are
/// replayed against this same agent — so pairing, the link protocol, forwarding (bodies both ways),
/// permission delegation, event relay, offline handling and unpairing all run for real.
/// </summary>
[Collection("Agent")]
public class HubTests
{
    private const string Remote = "m-remote";
    private readonly AgentFixture _f;
    public HubTests(AgentFixture f) => _f = f;

    private MemberLink.Connector Connector => async (uri, auth, _, token) =>
    {
        var ws = _f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Authorization = auth;
        return await ws.ConnectAsync(new Uri("ws://localhost/api/v2/hub/link"), token);
    };

    /// <summary>Pairs "m-remote" with the hub through the API and returns a started member link.</summary>
    private async Task<(MemberLink Link, AgentSettings Settings)> PairAndConnect()
    {
        var code = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/hub/pairing-codes"));
        var paired = await _f.NewClient().PostAsync("/api/v2/hub/pair", new { code = code.GetProperty("code").GetString(), machineId = Remote, machineName = "Remote Box", version = "test" });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        string credential = (await ApiClient.Read<JsonElement>(paired)).GetProperty("credential").GetString()!;

        var settings = new AgentSettings { MachineName = "Remote Box", HubUrl = "http://localhost", HubCredential = credential, HubName = "Test hub" };
        var link = new MemberLink(_f.Context, () => new HttpClient(_f.Server.CreateHandler()) { BaseAddress = _f.Server.BaseAddress }, Connector, _ => { }, Remote, settings);
        link.Start();
        await EngineFixture.WaitUntil(() => MachineOnline(), "the remote machine to come online");
        return (link, settings);
    }

    private bool MachineOnline(bool online = true) =>
        _f.Owner.GetJsonAsync<List<MachineDto>>("/api/v2/machines").GetAwaiter().GetResult().Any(m => m.Id == Remote && m.Online == online);

    [Fact]
    public async Task Pairing_codes_are_owner_only_single_use_and_checked()
    {
        var viewer = await _f.UserAsync("hubviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v2/hub/pairing-codes")).StatusCode);

        var code = (await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/hub/pairing-codes"))).GetProperty("code").GetString();
        var wrong = await _f.NewClient().PostAsync("/api/v2/hub/pair", new { code = "ZZZZ-ZZZZ", machineId = "m-x", machineName = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.NewClient().PostAsync("/api/v2/hub/pair", new { code, machineId = "m-once", machineName = "Once" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.NewClient().PostAsync("/api/v2/hub/pair", new { code, machineId = "m-twice", machineName = "Twice" })).StatusCode);

        // A link with a wrong credential is refused.
        var ws = _f.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Authorization = "WGSM-Machine m-once:not-the-credential";
        await Assert.ThrowsAnyAsync<Exception>(() => ws.ConnectAsync(new Uri("ws://localhost/api/v2/hub/link"), CancellationToken.None));
        await _f.Owner.DeleteAsync("/api/v2/hub/machines/m-once");
    }

    [Fact]
    public async Task A_paired_machine_is_driven_through_the_hub_with_the_callers_permissions()
    {
        var (link, _) = await PairAndConnect();
        try
        {
            // Listing, reading, and a streamed file both ways.
            var servers = await _f.Owner.GetJsonAsync<List<ServerDto>>($"/api/v2/machines/{Remote}/servers");
            Assert.Contains(servers, s => s.Id == "101");
            Assert.Equal(HttpStatusCode.OK, (await _f.Owner.GetAsync($"/api/v2/machines/{Remote}/servers/101/console")).StatusCode);

            string big = new string('x', 300_000); // several link chunks
            File.WriteAllText(ServerPath.GetServersServerFiles("103", "big.txt"), big);
            var download = await _f.Owner.GetAsync($"/api/v2/machines/{Remote}/servers/103/files/download?path=big.txt");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(big, await download.Content.ReadAsStringAsync());

            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('y', 200_000)));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", "via-hub.bin");
            var up = await _f.Owner.Http.PostAsync($"/api/v2/machines/{Remote}/servers/103/files/upload?path=", form);
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
            Assert.Equal(200_000, new FileInfo(ServerPath.GetServersServerFiles("103", "via-hub.bin")).Length);

            // The member decides with the hub user's rights: a viewer can look but not act…
            var viewer = await _f.UserAsync("remoteviewer", Role.Viewer);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v2/machines/{Remote}/servers")).StatusCode);
            var start = await viewer.PostAsync($"/api/v2/machines/{Remote}/servers/101/start");
            Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);

            // …and grants for that machine travel with the request.
            var member = await _f.UserAsync("remotemember", Role.Member, new Dictionary<string, Capability> { [$"{Remote}/102"] = Capability.View });
            var visible = await member.GetJsonAsync<List<ServerDto>>($"/api/v2/machines/{Remote}/servers");
            Assert.Equal(new[] { "102" }, visible.Select(s => s.Id).ToArray());

            // The member's audit log names the hub user and the hub.
            var audit = await _f.Owner.GetJsonAsync<List<AuditDto>>("/api/v2/audit?action=upload");
            Assert.Contains(audit, e => e.User == $"{AgentFixture.OwnerName} (via {_f.Context.Settings.MachineName})");

            // …and the hub can read that machine's audit log (admins only).
            var remoteAudit = await _f.Owner.GetJsonAsync<List<AuditDto>>($"/api/v2/machines/{Remote}/audit?action=upload");
            Assert.Contains(remoteAudit, e => e.User.EndsWith($"(via {_f.Context.Settings.MachineName})"));
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v2/machines/{Remote}/audit")).StatusCode);
        }
        finally
        {
            await link.StopAsync();
            await _f.Owner.DeleteAsync($"/api/v2/hub/machines/{Remote}");
        }
    }

    [Fact]
    public async Task Events_are_relayed_and_an_offline_machine_shows_its_last_state()
    {
        var grace = WindowsGSM.Agent.Notifications.NotificationCentre.OfflineGrace;
        WindowsGSM.Agent.Notifications.NotificationCentre.OfflineGrace = TimeSpan.FromMilliseconds(300);
        var (link, _) = await PairAndConnect();
        try
        {
            var ws = _f.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers["Cookie"] = _f.Owner.CookieHeader;
            using var socket = await ws.ConnectAsync(new Uri("ws://localhost/api/v2/events"), CancellationToken.None);
            var received = new List<JsonElement>();
            var pump = Task.Run(async () =>
            {
                var buffer = new byte[65536];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) { return; }
                        lock (received) { received.Add(JsonDocument.Parse(buffer.AsMemory(0, r.Count)).RootElement.Clone()); }
                    }
                }
                catch { /* closed */ }
            });
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "subscribe", topics = new[] { "servers" } }), WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(300);

            _f.Engine.Events.Publish(new ServerConfigChanged("102", new[] { "relay-test" }));
            await EngineFixture.WaitUntil(() => { lock (received) { return received.Any(e => e.GetProperty("type").GetString() == "serverConfig" && e.GetProperty("machine").GetString() == Remote); } }, "the event relayed from the member");

            // Going offline: the hub says so, keeps the last server list, and refuses actions quickly.
            await link.StopAsync();
            await EngineFixture.WaitUntil(() => MachineOnline(false), "the remote machine to show offline");
            await EngineFixture.WaitUntil(() => { lock (received) { return received.Any(e => e.GetProperty("type").GetString() == "machine" && e.GetProperty("machine").GetString() == Remote); } }, "a machine offline event");

            var cached = await _f.Owner.GetAsync($"/api/v2/machines/{Remote}/servers");
            Assert.Equal("1", cached.Headers.GetValues("X-WGSM-Stale").Single());
            Assert.Contains(await ApiClient.Read<List<ServerDto>>(cached), s => s.Id == "101" && s.Machine == Remote);
            var act = await _f.Owner.PostAsync($"/api/v2/machines/{Remote}/servers/101/start");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, act.StatusCode);
            Assert.Equal("machine_offline", (await ApiClient.Read<ApiError>(act)).Code);

            // …and once it's been gone a while, the notification centre says so.
            await EngineFixture.WaitUntil(() => _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications").GetAwaiter().GetResult()
                .GetProperty("items").EnumerateArray().Any(i => i.GetProperty("kind").GetString() == "machineOffline" && i.GetProperty("machine").GetString() == Remote),
                "a machine-offline notification");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ContinueWith(_ => { });
        }
        finally
        {
            WindowsGSM.Agent.Notifications.NotificationCentre.OfflineGrace = grace;
            await link.StopAsync();
            await _f.Owner.DeleteAsync($"/api/v2/hub/machines/{Remote}");
        }
    }

    [Fact]
    public async Task Removing_a_machine_tells_it_to_forget_the_hub()
    {
        var (link, settings) = await PairAndConnect();
        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.DeleteAsync($"/api/v2/hub/machines/{Remote}")).StatusCode);
        await EngineFixture.WaitUntil(() => link.State == LinkState.Removed, "the member to stop");
        Assert.Null(settings.HubCredential);
        Assert.DoesNotContain(await _f.Owner.GetJsonAsync<List<MachineDto>>("/api/v2/machines"), m => m.Id == Remote);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Owner.GetAsync($"/api/v2/machines/{Remote}/servers")).StatusCode);
        await link.StopAsync();
    }
}
