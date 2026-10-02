using Discord;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Discord;

/// <summary>What the bot knows about the fleet when it answers (from the agent's API, as the asking admin).</summary>
public sealed record BotFleet(IReadOnlyList<MachineDto> Machines, IReadOnlyList<ServerDto> Servers)
{
    public bool MultiMachine => Machines.Count > 1;
    public string MachineName(string id) => Machines.FirstOrDefault(m => m.Id == id)?.Name ?? id;
    public bool IsOnline(string id) => Machines.FirstOrDefault(m => m.Id == id)?.Online != false;
    public ServerDto? Server(string key) => Servers.FirstOrDefault(s => $"{s.Machine}/{s.Id}" == key);
}

/// <summary>The bot's messages: /list, /stats and the /panel control panel (embeds and components).</summary>
public static class DiscordEmbeds
{
    public const string Prefix = "wgsm:";
    public const string RefreshId = Prefix + "refresh";
    public const string ListId = Prefix + "list";
    public const string StatsId = Prefix + "stats";
    public const string SelectId = Prefix + "select";
    public const string Do = Prefix + "do:";          // wgsm:do:<action>:<machine>/<id>
    public const string Confirm = Prefix + "confirm:"; // wgsm:confirm:<action>:<machine>/<id>

    private static readonly Color Brand = new(0x3b, 0x82, 0xf6);

    public static string Icon(string state) => state switch
    {
        "Running" => "🟢",
        "Starting" => "🟡",
        "Stopping" => "🟠",
        "Restarting" => "🔄",
        "Stopped" => "🔴",
        "Updating" or "Installing" or "UpdatingAddons" => "🔵",
        "BackingUp" or "Restoring" => "💾",
        _ => "⚪",
    };

    public static string Label(string state) => state switch { "BackingUp" => "Backing up", "UpdatingAddons" => "Updating add-ons", _ => state };

    private static string Players(ServerDto s) =>
        s.State == "Running" && s.Players != null ? $" · {s.Players}{(s.MaxPlayers is > 0 ? "/" + s.MaxPlayers : "")} players" : "";

    private static string Line(ServerDto s) => $"{Icon(s.State)} **{s.Name}** · `#{s.Id}` · {Label(s.State)}{Players(s)}";

    /// <summary>/list — every server you can see, grouped by machine.</summary>
    public static Embed ServerList(BotFleet fleet, string title = "🎮 Game servers")
    {
        var e = new EmbedBuilder { Title = title, Color = Brand };
        if (fleet.Servers.Count == 0)
        {
            e.Description = "No servers you can control. Ask the owner to give you access in WindowsGSM → Discord bot.";
            return e.WithCurrentTimestamp().Build();
        }
        int running = fleet.Servers.Count(s => s.State == "Running");
        int players = fleet.Servers.Where(s => s.State == "Running").Sum(s => s.Players ?? 0);
        e.Description = $"**{running}** of {fleet.Servers.Count} running · **{players}** player{(players == 1 ? "" : "s")} online";
        foreach (var group in fleet.Servers.GroupBy(s => s.Machine))
        {
            string heading = fleet.MultiMachine ? $"🖥️ {fleet.MachineName(group.Key)}{(fleet.IsOnline(group.Key) ? "" : " — offline (last known)")}" : "Servers";
            AddChunked(e, heading, group.OrderBy(s => int.TryParse(s.Id, out int n) ? n : int.MaxValue).Select(Line));
        }
        e.WithFooter("WindowsGSM").WithCurrentTimestamp();
        return e.Build();
    }

    /// <summary>/stats — each machine's CPU, memory and disk, plus servers and players.</summary>
    public static Embed Stats(BotFleet fleet)
    {
        var e = new EmbedBuilder { Title = "📊 System stats", Color = Brand };
        int running = fleet.Servers.Count(s => s.State == "Running");
        int players = fleet.Servers.Where(s => s.State == "Running").Sum(s => s.Players ?? 0);
        e.Description = $"Servers online **{running}/{fleet.Servers.Count}** · Players **{players}**";
        foreach (var m in fleet.Machines.Take(8))
        {
            string name = fleet.MultiMachine ? $"🖥️ {m.Name}" : m.Name;
            if (m.Online == false || m.Metrics == null)
            {
                e.AddField(name, m.Online == false ? "Offline" : "No readings yet", inline: false);
                continue;
            }
            var mm = m.Metrics;
            int serversHere = fleet.Servers.Count(s => s.Machine == m.Id);
            int runningHere = fleet.Servers.Count(s => s.Machine == m.Id && s.State == "Running");
            e.AddField(name, $"{runningHere}/{serversHere} servers running{(mm.CpuName != null ? " · " + mm.CpuName.Trim() : "")}", inline: false);
            e.AddField("CPU", Bar(mm.CpuPercent), inline: true);
            e.AddField($"Memory · {Math.Round(mm.RamTotalGb * mm.RamPercent / 100, 1)}/{Math.Round(mm.RamTotalGb, 1)} GB", Bar(mm.RamPercent), inline: true);
            e.AddField($"Disk · {Math.Round(mm.DiskTotalGb)} GB", Bar(mm.DiskPercent), inline: true);
        }
        e.WithFooter("WindowsGSM · System stats").WithCurrentTimestamp();
        return e.Build();
    }

    /// <summary>A text progress bar like the legacy bot's: <c>██████▌ 43% ░░░░</c>.</summary>
    public static string Bar(double percent)
    {
        double p = Math.Clamp(percent, 0, 100);
        const int width = 12;
        int full = (int)Math.Round(p / 100 * width);
        return $"`{new string('█', full)}{new string('░', width - full)}` **{Math.Round(p)}%**";
    }

    // ───────────────────────────── /panel ─────────────────────────────

    public static Embed Panel(BotFleet fleet) => ServerList(fleet, "🎮 WindowsGSM control panel");

    public static MessageComponent PanelComponents(BotFleet fleet)
    {
        var b = new ComponentBuilder()
            .WithButton("Refresh", RefreshId, ButtonStyle.Secondary, new Emoji("🔄"), row: 0)
            .WithButton("List", ListId, ButtonStyle.Primary, new Emoji("📋"), row: 0)
            .WithButton("Stats", StatsId, ButtonStyle.Primary, new Emoji("📊"), row: 0);
        var menu = new SelectMenuBuilder().WithCustomId(SelectId).WithPlaceholder("Pick a server to control…").WithMinValues(1).WithMaxValues(1);
        foreach (var s in fleet.Servers.Take(25))
        {
            string label = $"{s.Name} [{Label(s.State)}]";
            if (label.Length > 100) { label = label[..97] + "…"; }
            string where = fleet.MultiMachine ? $"{fleet.MachineName(s.Machine)} · #{s.Id}" : $"#{s.Id}";
            menu.AddOption(label, $"{s.Machine}/{s.Id}", where.Length > 100 ? where[..100] : where, new Emoji(Icon(s.State)));
        }
        if (menu.Options.Count == 0)
        {
            menu.AddOption("No servers", "none", "Nothing to control");
            menu.IsDisabled = true;
        }
        b.WithSelectMenu(menu, row: 1);
        return b.Build();
    }

    public static Embed ServerView(BotFleet fleet, ServerDto s)
    {
        var e = new EmbedBuilder { Title = $"{Icon(s.State)} {s.Name}", Color = s.State == "Running" ? new Color(0x2d, 0xbd, 0x6e) : Brand };
        e.AddField("Status", Label(s.State), inline: true);
        if (fleet.MultiMachine) { e.AddField("Machine", fleet.MachineName(s.Machine) + (fleet.IsOnline(s.Machine) ? "" : " (offline)"), inline: true); }
        e.AddField("ID", $"#{s.Id}", inline: true);
        if (s.State == "Running" && s.Players != null) { e.AddField("Players", $"{s.Players}{(s.MaxPlayers is > 0 ? "/" + s.MaxPlayers : "")}", inline: true); }
        string address = string.Join(":", new[] { s.Ip, s.Port }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (address.Length > 0) { e.AddField("Address", $"`{address}`", inline: true); }
        if (s.BusyWith != null) { e.AddField("Busy", s.BusyWith, inline: true); }
        return e.WithFooter("Use the buttons below, or go back to the panel.").WithCurrentTimestamp().Build();
    }

    public static MessageComponent ServerActions(BotFleet fleet, ServerDto s)
    {
        string key = $"{s.Machine}/{s.Id}";
        var b = new ComponentBuilder();
        bool can(Capability c) => s.Can.HasFlag(c);
        if (!fleet.IsOnline(s.Machine))
        {
            b.WithButton("Machine offline", Prefix + "noop", ButtonStyle.Secondary, disabled: true, row: 0);
        }
        else if (s.State == "Stopped")
        {
            if (can(Capability.Start)) { b.WithButton("Start", Do + "start:" + key, ButtonStyle.Success, new Emoji("▶️"), row: 0); }
            if (can(Capability.Update)) { b.WithButton("Update", Do + "update:" + key, ButtonStyle.Primary, new Emoji("⬇️"), row: 0); }
        }
        else if (s.State == "Running")
        {
            if (can(Capability.Restart)) { b.WithButton("Restart", Confirm + "restart:" + key, ButtonStyle.Primary, new Emoji("🔄"), row: 0); }
            if (can(Capability.Stop)) { b.WithButton("Stop", Confirm + "stop:" + key, ButtonStyle.Secondary, new Emoji("⏹️"), row: 0); }
            if (can(Capability.Kill)) { b.WithButton("Force stop", Confirm + "kill:" + key, ButtonStyle.Danger, new Emoji("💀"), row: 0); }
        }
        else
        {
            b.WithButton($"Busy: {Label(s.State)}", Prefix + "noop", ButtonStyle.Secondary, disabled: true, row: 0);
        }
        b.WithButton("Refresh", Prefix + "view:" + key, ButtonStyle.Secondary, new Emoji("🔄"), row: 1);
        b.WithButton("Back to panel", RefreshId, ButtonStyle.Secondary, new Emoji("⬅️"), row: 1);
        return b.Build();
    }

    public static readonly IReadOnlyDictionary<string, (string Verb, string Past, string Warning)> Actions = new Dictionary<string, (string, string, string)>
    {
        ["start"] = ("Start", "started", ""),
        ["update"] = ("Update", "updated", ""),
        ["restart"] = ("Restart", "restarted", "Players are disconnected while it restarts."),
        ["stop"] = ("Stop", "stopped", "Players on it are disconnected."),
        ["kill"] = ("Force stop", "force-stopped", "Ends the process immediately — unsaved progress may be lost."),
    };

    public static MessageComponent ConfirmRow(string action, string key) => new ComponentBuilder()
        .WithButton($"Yes, {Actions[action].Verb.ToLowerInvariant()}", Do + action + ":" + key, action == "kill" ? ButtonStyle.Danger : ButtonStyle.Primary, new Emoji("⚠️"))
        .WithButton("Cancel", Prefix + "view:" + key, ButtonStyle.Secondary, new Emoji("⬅️"))
        .Build();

    /// <summary>Adds lines as one field, continuing in more fields when a field would pass Discord's 1024 characters.</summary>
    private static void AddChunked(EmbedBuilder e, string heading, IEnumerable<string> lines)
    {
        var chunk = new List<string>();
        int length = 0;
        bool first = true;
        foreach (string line in lines)
        {
            if (length + line.Length + 1 > 1000 && chunk.Count > 0)
            {
                if (e.Fields.Count >= 24) { break; }
                e.AddField(first ? heading : heading + " (cont.)", string.Join("\n", chunk));
                first = false;
                chunk.Clear();
                length = 0;
            }
            chunk.Add(line);
            length += line.Length + 1;
        }
        if (chunk.Count > 0 && e.Fields.Count < 25) { e.AddField(first ? heading : heading + " (cont.)", string.Join("\n", chunk)); }
    }
}
