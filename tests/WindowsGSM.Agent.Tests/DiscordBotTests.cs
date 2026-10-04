using WindowsGSM.Agent.Discord;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// The Discord bot without Discord: importing the legacy bot's setup, what each admin may do, the messages it
/// builds, and its calls into the agent's API as that admin (permissions and audit included).
/// </summary>
[Collection("Agent")]
public class DiscordBotTests
{
    private readonly AgentFixture _f;
    public DiscordBotTests(AgentFixture f) => _f = f;

    private DiscordBotService Bot() => new(_f.Context,
        new LocalApi(_f.Context, () => new HttpClient(_f.Server.CreateHandler()) { BaseAddress = _f.Server.BaseAddress }), _ => { });

    [Fact]
    public void The_legacy_bots_setup_is_imported_but_left_off()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wgsm-legacybot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(DiscordBotSettings.ImportLegacy(folder, "m-a"));
            File.WriteAllText(Path.Combine(folder, "token.txt"), " abc.def.ghi \n");
            File.WriteAllText(Path.Combine(folder, "guildID.txt"), "123456789012345678");
            File.WriteAllText(Path.Combine(folder, "adminIDs.txt"), "111111111111111111|Alice|0\n222222222222222222|Bob|1, 3\n\n");
            var s = DiscordBotSettings.ImportLegacy(folder, "m-a")!;
            Assert.False(s.Enabled);
            Assert.True(s.ImportedFromLegacy);
            Assert.Equal("abc.def.ghi", s.Token);
            Assert.Equal("123456789012345678", s.GuildId);
            Assert.Equal(new[] { "m-a/*" }, s.Admin("111111111111111111")!.Servers);
            Assert.Equal(new[] { "m-a/1", "m-a/3" }, s.Admin("222222222222222222")!.Servers);
        }
        finally { WindowsGSM.Core.Tests.TestData.DeleteDirectory(folder); }
    }

    [Fact]
    public void An_admin_gets_the_panels_actions_on_their_servers_only()
    {
        var bob = DiscordBotSettings.ActingUser(new DiscordAdmin { DiscordId = "2", Name = "Bob", Servers = new() { "m-a/1" } });
        Assert.Equal("Bob (Discord)", bob.Username);
        Assert.True(bob.Can(Capability.Start, "m-a", "1"));
        Assert.True(bob.Can(Capability.Kill, "m-a", "1"));
        Assert.False(bob.Can(Capability.Delete, "m-a", "1"));
        Assert.False(bob.Can(Capability.Console, "m-a", "1"));
        Assert.False(bob.Can(Capability.View, "m-a", "2"));
        var all = DiscordBotSettings.ActingUser(new DiscordAdmin { DiscordId = "1", Servers = new() { "*" } });
        Assert.True(all.Can(Capability.Restart, "m-b", "7"));
    }

    [Fact]
    public async Task It_sees_and_acts_through_the_API_as_the_admin()
    {
        var bot = Bot();
        var admin = DiscordBotSettings.ActingUser(new DiscordAdmin { DiscordId = "3", Name = "Cara", Servers = new() { $"{_f.MachineId}/102" } });

        var fleet = await bot.FleetAsync(admin);
        Assert.Equal(new[] { "102" }, fleet.Servers.Select(s => s.Id).ToArray());
        Assert.Single(fleet.Machines);

        // A server they don't have is refused by the agent itself.
        var other = fleet.Servers[0] with { Id = "103" };
        Assert.NotNull(await bot.ActAsync(admin, other, "start"));

        // Their own works, and the audit log says who (via Discord).
        var s = fleet.Servers[0];
        string? error = s.State == "Stopped" ? await bot.ActAsync(admin, s, "start") : await bot.ActAsync(admin, s, "restart");
        Assert.Null(error);
        await Core.Tests.EngineFixture.WaitUntil(() => _f.Owner.GetJsonAsync<List<AuditDto>>("/api/v2/audit").GetAwaiter().GetResult()
            .Any(e => e.User == "Cara (Discord) (via Discord)" && e.Server == "102"), "the action in the audit log");
        await _f.StopAndWait("102");
    }

    [Fact]
    public void Messages_show_the_right_buttons_and_fit_discords_limits()
    {
        var machines = new List<MachineDto>
        {
            new("m-a", "Box A", true, true, "2", null, new HostMetricsDto(42, 50, 32, 81, 931, 8, "Ryzen", DateTimeOffset.UtcNow), 2),
            new("m-b", "Box B", false, false, "2", null, null, 1),
        };
        ServerDto S(string m, string id, string state, Capability can) =>
            new(m, id, "Server " + id, "Game", state, null, "1.2.3.4", "2456", null, state == "Running" ? 3 : null, 10, null, null, null, false, false, false, false, null, can);
        var servers = Enumerable.Range(1, 60).Select(i => S("m-a", i.ToString(), i % 2 == 0 ? "Running" : "Stopped", Capability.All)).Append(S("m-b", "1", "Running", Capability.All)).ToList();
        var fleet = new BotFleet(machines, servers);

        var list = DiscordEmbeds.ServerList(fleet);
        Assert.All(list.Fields, f => Assert.True(f.Value.Length <= 1024));
        Assert.True(list.Fields.Length <= 25);
        Assert.Contains(list.Fields, f => f.Name.Contains("Box B") && f.Name.Contains("offline"));

        var stats = DiscordEmbeds.Stats(fleet);
        Assert.Contains(stats.Fields, f => f.Name.Contains("Box A") && f.Value.Contains("CPU") && f.Value.Contains("42%"));
        Assert.Contains(stats.Fields, f => f.Name.Contains("Box B") && f.Value == "Offline");

        // Discord allows 25 options in a dropdown: the rest are on further pages — the other machine's server included.
        string Ids(global::Discord.MessageComponent c) => string.Join(",", c.Components.OfType<global::Discord.ActionRowComponent>().SelectMany(r => r.Components).OfType<global::Discord.IInteractableComponent>().Select(x => x.CustomId));
        global::Discord.SelectMenuComponent Menu(int page) => DiscordEmbeds.PanelComponents(fleet, page).Components.OfType<global::Discord.ActionRowComponent>().SelectMany(r => r.Components).OfType<global::Discord.SelectMenuComponent>().Single();
        Assert.Equal(25, Menu(0).Options.Count);
        Assert.Contains("wgsm:page:1", Ids(DiscordEmbeds.PanelComponents(fleet, 0)));
        Assert.Contains(Menu(2).Options, o => o.Value == "m-b/1");
        Assert.Equal(11, Menu(2).Options.Count);
        Assert.Contains("Box B", Menu(2).Placeholder);
        Assert.Equal(Menu(2).Options.Count, Menu(99).Options.Count); // past the end: the last page

        // A big fleet still fits one message (Discord refuses more than 25 fields or 6000 characters).
        var many = new BotFleet(
            Enumerable.Range(1, 30).Select(i => new MachineDto($"m-{i}", $"Machine number {i}", i == 1, true, "2", null, new HostMetricsDto(10, 20, 64, 30, 2000, 16, "Some long CPU name here", DateTimeOffset.UtcNow), 20)).ToList(),
            Enumerable.Range(1, 30).SelectMany(m => Enumerable.Range(1, 20).Select(i => S($"m-{m}", i.ToString(), "Running", Capability.All))).ToList())
        { NotAnswering = new[] { "m-7" } };
        foreach (var embed in new[] { DiscordEmbeds.ServerList(many), DiscordEmbeds.Stats(many) })
        {
            Assert.True(embed.Fields.Length <= 25, $"{embed.Title}: {embed.Fields.Length} fields");
            Assert.True(embed.Title.Length + embed.Description.Length + embed.Fields.Sum(f => f.Name.Length + f.Value.Length) + (embed.Footer?.Text.Length ?? 0) <= 6000, embed.Title);
            Assert.Contains("Machine number 7", embed.Description); // didn't answer in time
            Assert.Contains(embed.Fields, f => f.Value.Contains("more"));
        }
        Assert.Contains("wgsm:do:start:m-a/1", Ids(DiscordEmbeds.ServerActions(fleet, servers[0])));
        Assert.Contains("wgsm:confirm:stop:m-a/2", Ids(DiscordEmbeds.ServerActions(fleet, servers[1])));
        Assert.DoesNotContain("stop", Ids(DiscordEmbeds.ServerActions(fleet, servers[1] with { Can = Capability.View | Capability.Start })));
        Assert.DoesNotContain("wgsm:do", Ids(DiscordEmbeds.ServerActions(fleet, servers[^1]))); // its machine is offline
        Assert.Equal("`████░░░░░░░░` **33%**", DiscordEmbeds.Bar(33));
    }
}
