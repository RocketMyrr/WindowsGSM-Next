using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsGSM.Engine.Services;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Core.Tests;

/// <summary>The A2S (Steam/Source) query against a fake server: challenges, split replies, odd player lists.</summary>
public class A2SQueryTests
{
    /// <summary>A tiny A2S server on localhost. Answers with whatever the test builds.</summary>
    internal sealed class FakeServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public bool Challenge { get; init; } = true;
        public Func<byte[], List<byte[]>> Reply { get; init; } = _ => new();
        private static readonly byte[] Token = { 1, 2, 3, 4 };

        public FakeServer() { _ = Task.Run(LoopAsync); }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(_stop.Token); } catch { return; }
                byte[] req = r.Buffer;
                byte kind = req[4];
                bool hasToken = kind == 0x54 ? req.Length >= 25 + 4 : req.Length >= 9 && !req.AsSpan(5, 4).SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
                if (Challenge && !hasToken)
                {
                    await _udp.SendAsync(Single(new byte[] { 0x41 }.Concat(Token).ToArray()), r.RemoteEndPoint);
                    continue;
                }
                foreach (var packet in Reply(req)) { await _udp.SendAsync(packet, r.RemoteEndPoint); }
            }
        }

        public void Dispose() { _stop.Cancel(); _udp.Dispose(); }
    }

    internal static byte[] Single(byte[] payload) => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.Concat(payload).ToArray();

    internal static byte[] Info(int players, int max)
    {
        var b = new List<byte> { 0x49, 17 };
        void Str(string s) { b.AddRange(Encoding.UTF8.GetBytes(s)); b.Add(0); }
        Str("My Server"); Str("de_dust2"); Str("cstrike"); Str("Counter-Strike");
        b.AddRange(BitConverter.GetBytes((short)240));
        b.Add((byte)players); b.Add((byte)max); b.Add(0); b.Add((byte)'d'); b.Add((byte)'w'); b.Add(0); b.Add(1);
        Str("1.0");
        return b.ToArray();
    }

    internal static byte[] Players(params string[] names)
    {
        var b = new List<byte> { 0x44, (byte)names.Length };
        foreach (string n in names)
        {
            b.Add(0);
            b.AddRange(Encoding.UTF8.GetBytes(n)); b.Add(0);
            b.AddRange(BitConverter.GetBytes(7));
            b.AddRange(BitConverter.GetBytes(90f));
        }
        return b.ToArray();
    }

    /// <summary>Source-style split: -2, id, total, number, size, chunk (packet 0's chunk starts with -1).</summary>
    private static List<byte[]> SplitSource(byte[] payload, int parts)
    {
        byte[] whole = Single(payload);
        int size = (whole.Length + parts - 1) / parts;
        var list = new List<byte[]>();
        for (int i = 0; i < parts; i++)
        {
            var chunk = whole.Skip(i * size).Take(size).ToArray();
            list.Add(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }.Concat(BitConverter.GetBytes(77)).Concat(new byte[] { (byte)parts, (byte)i }).Concat(BitConverter.GetBytes((short)1248)).Concat(chunk).ToArray());
        }
        list.Reverse(); // out of order on purpose
        return list;
    }

    /// <summary>GoldSource split: -2, id, (number &lt;&lt; 4 | total), chunk.</summary>
    private static List<byte[]> SplitGoldSource(byte[] payload, int parts)
    {
        byte[] whole = Single(payload);
        int size = (whole.Length + parts - 1) / parts;
        return Enumerable.Range(0, parts).Select(i => new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }.Concat(BitConverter.GetBytes(78))
            .Concat(new byte[] { (byte)((i << 4) | parts) }).Concat(whole.Skip(i * size).Take(size)).ToArray()).ToList();
    }

    [Fact]
    public async Task Info_and_players_come_through_the_challenge()
    {
        using var server = new FakeServer { Reply = req => new() { Single(req[4] == 0x54 ? Info(2, 10) : Players("Alice", "Bob")) } };
        var a2s = new A2S("127.0.0.1", server.Port, 2);
        Assert.Equal("2/10", await a2s.GetPlayersAndMaxPlayers());
        var players = await a2s.GetPlayersData();
        Assert.Equal(new[] { "Alice", "Bob" }, players.Select(p => p.Name));
        Assert.Equal(7, players[0].Score);
    }

    [Fact]
    public async Task A_big_player_list_split_over_packets_is_put_back_together()
    {
        string[] names = Enumerable.Range(1, 120).Select(i => $"Player number {i} with a long name").ToArray();
        using var server = new FakeServer { Reply = req => req[4] == 0x54 ? new() { Single(Info(120, 200)) } : SplitSource(Players(names), 4) };
        var players = await new A2S("127.0.0.1", server.Port, 2).GetPlayersData();
        Assert.Equal(names, players.Select(p => p.Name));
    }

    [Fact]
    public async Task GoldSource_split_replies_work_too()
    {
        string[] names = Enumerable.Range(1, 60).Select(i => $"hl1 player {i}").ToArray();
        using var server = new FakeServer { Challenge = false, Reply = req => req[4] == 0x54 ? new() { Single(Info(60, 64)) } : SplitGoldSource(Players(names), 3) };
        var players = await new A2S("127.0.0.1", server.Port, 2).GetPlayersData();
        Assert.Equal(names, players.Select(p => p.Name));
    }

    [Fact]
    public async Task A_player_list_that_says_more_than_it_sends_still_reads()
    {
        byte[] list = Players("Alice", "Bob");
        list[1] = 5; // claims five, sends two
        using var server = new FakeServer { Reply = req => new() { Single(req[4] == 0x54 ? Info(2, 10) : list) } };
        var players = await new A2S("127.0.0.1", server.Port, 2).GetPlayersData();
        Assert.Equal(new[] { "Alice", "Bob" }, players.Select(p => p.Name));
    }

    [Fact]
    public async Task Nothing_listening_is_a_quick_null_not_a_hang()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) { port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port; }
        var started = DateTime.UtcNow;
        Assert.Null(await new A2S("127.0.0.1", port, 2).GetPlayersAndMaxPlayers());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_query_asks_this_machine_not_the_public_address()
    {
        Assert.Equal("127.0.0.1", MonitorService.QueryHost(null));
        Assert.Equal("127.0.0.1", MonitorService.QueryHost("0.0.0.0"));
        Assert.Equal("127.0.0.1", MonitorService.QueryHost("203.0.113.9"));   // a public address — for players, not for us
        Assert.Equal("127.0.0.1", MonitorService.QueryHost("not an ip"));
    }
}
