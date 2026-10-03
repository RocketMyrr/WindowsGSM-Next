using Discord;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Discord;

/// <summary>What the bot knows about the fleet when it answers (from the agent's API, as the asking admin).</summary>
public sealed record BotFleet(IReadOnlyList<MachineDto> Machines, IReadOnlyList<ServerDto> Servers)
{
    /// <summary>Machines that are connected but didn't answer in time (their servers are missing from this answer).</summary>
    public IReadOnlyList<string> NotAnswering { get; init; } = Array.Empty<string>();
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
    public const string PageId = Prefix + "page:";     // wgsm:page:<n> — the dropdown's next/previous 25

    /// <summary>Discord's limits: 25 options in a dropdown, 25 fields and 6000 characters in an embed.</summary>
    public const int PageSize = 25, MaxFields = 25, MaxEmbed = 6000;
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
        e.Description = $"**{running}** of {fleet.Servers.Count} running · **{players}** player{(players == 1 ? "" : "s")} online" + NotAnsweringNote(fleet);
        int shown = 0;
        foreach (var group in fleet.Servers.GroupBy(s => s.Machine))
        {
            string heading = fleet.MultiMachine ? $"🖥️ {fleet.MachineName(group.Key)}{(fleet.IsOnline(group.Key) ? "" : " — offline (last known)")}" : "Servers";
            shown += AddChunked(e, heading, group.OrderBy(s => int.TryParse(s.Id, out int n) ? n : int.MaxValue).Select(Line));
        }
        // Too many to list in one message: say so rather than have Discord refuse it.
        if (shown < fleet.Servers.Count) { e.AddField("…", $"and {fleet.Servers.Count - shown} more — pick them from /panel's list."); }
        e.WithFooter("WindowsGSM").WithCurrentTimestamp();
        return e.Build();
    }

    /// <summary>
    /// /stats — each machine's CPU, memory and disk, plus servers and players. NEXT: one field per machine (it was
    /// four: Discord's 25-field limit made /stats fail outright from 7 machines on).
    /// </summary>
    public static Embed Stats(BotFleet fleet)
    {
        var e = new EmbedBuilder { Title = "📊 System stats", Color = Brand };
        int running = fleet.Servers.Count(s => s.State == "Running");
        int players = fleet.Servers.Where(s => s.State == "Running").Sum(s => s.Players ?? 0);
        e.Description = $"Servers online **{running}/{fleet.Servers.Count}** · Players **{players}**" + NotAnsweringNote(fleet);
        int size = e.Title.Length + e.Description.Length + 40;
        int left = fleet.Machines.Count;
        foreach (var m in fleet.Machines)
        {
            string name = fleet.MultiMachine ? $"🖥️ {m.Name}" : m.Name;
            if (name.Length > 250) { name = name[..250]; }
            string value;
            if (m.Online == false || m.Metrics == null) { value = m.Online == false ? "Offline" : "No readings yet"; }
            else
            {
                var mm = m.Metrics;
                int serversHere = fleet.Servers.Count(s => s.Machine == m.Id);
                int runningHere = fleet.Servers.Count(s => s.Machine == m.Id && s.State == "Running");
                value = $"{runningHere}/{serversHere} servers running{(mm.CpuName != null ? " · " + mm.CpuName.Trim() : "")}\n"
                    + $"CPU {Bar(mm.CpuPercent)}\n"
                    + $"RAM {Bar(mm.RamPercent)} {Math.Round(mm.RamTotalGb * mm.RamPercent / 100, 1)}/{Math.Round(mm.RamTotalGb, 1)} GB\n"
                    + $"Disk {Bar(mm.DiskPercent)} {Math.Round(mm.DiskTotalGb)} GB";
                if (value.Length > 1024) { value = value[..1024]; }
            }
            if (e.Fields.Count >= MaxFields - 1 || size + name.Length + value.Length > MaxEmbed - 200) { break; }
            e.AddField(name, value, inline: false);
            size += name.Length + value.Length;
            left--;
        }
        if (left > 0) { e.AddField("…", $"and {left} more machine{(left == 1 ? "" : "s")} — see the web panel."); }
        e.WithFooter("WindowsGSM · System stats").WithCurrentTimestamp();
        return e.Build();
    }

    private static string NotAnsweringNote(BotFleet fleet) => fleet.NotAnswering.Count == 0 ? ""
        : $"\n⚠️ Didn't answer in time: {string.Join(", ", fleet.NotAnswering.Select(fleet.MachineName))} — its servers aren't shown.";

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

    /// <summary>
    /// The panel's buttons and server dropdown. NEXT: a dropdown holds 25 servers, and everything after the first 25
    /// (on a hub: usually every other machine's servers) couldn't be picked at all — now it pages, 25 at a time.
    /// </summary>
    public static MessageComponent PanelComponents(BotFleet fleet, int page = 0)
    {
        int pages = Math.Max(1, (fleet.Servers.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var b = new ComponentBuilder()
            .WithButton("Refresh", RefreshId, ButtonStyle.Secondary, new Emoji("🔄"), row: 0)
            .WithButton("List", ListId, ButtonStyle.Primary, new Emoji("📋"), row: 0)
            .WithButton("Stats", StatsId, ButtonStyle.Primary, new Emoji("📊"), row: 0);
        var onPage = fleet.Servers.Skip(page * PageSize).Take(PageSize).ToList();
        string placeholder = pages == 1 ? "Pick a server to control…"
            : $"Pick a server… ({page * PageSize + 1}–{page * PageSize + onPage.Count} of {fleet.Servers.Count}{PageMachines(fleet, onPage)})";
        if (placeholder.Length > 150) { placeholder = placeholder[..149] + "…"; }
        var menu = new SelectMenuBuilder().WithCustomId(SelectId).WithPlaceholder(placeholder).WithMinValues(1).WithMaxValues(1);
        foreach (var s in onPage)
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
        if (pages > 1)
        {
            b.WithButton("Previous 25", PageId + (page - 1), ButtonStyle.Secondary, new Emoji("◀️"), disabled: page == 0, row: 2);
            b.WithButton($"Page {page + 1} of {pages}", Prefix + "noop", ButtonStyle.Secondary, disabled: true, row: 2);
            b.WithButton("Next 25", PageId + (page + 1), ButtonStyle.Secondary, new Emoji("▶️"), disabled: page == pages - 1, row: 2);
        }
        return b.Build();
    }

    /// <summary>" · Box A, Box B" — which machines this page's servers are on.</summary>
    private static string PageMachines(BotFleet fleet, IReadOnlyList<ServerDto> onPage) =>
        fleet.MultiMachine ? " · " + string.Join(", ", onPage.Select(s => s.Machine).Distinct().Select(fleet.MachineName)) : "";

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

    /// <summary>
    /// Adds lines as one field, continuing in more fields when a field would pass Discord's 1024 characters, and
    /// stopping before the embed passes its 25 fields or 6000 characters (one field is kept for "…and N more").
    /// Returns how many lines went in.
    /// </summary>
    private static int AddChunked(EmbedBuilder e, string heading, IEnumerable<string> lines)
    {
        int Size() => (e.Title?.Length ?? 0) + (e.Description?.Length ?? 0) + e.Fields.Sum(f => f.Name.Length + (f.Value?.ToString()?.Length ?? 0)) + 60;
        var chunk = new List<string>();
        int length = 0, added = 0;
        bool first = true;
        bool Flush()
        {
            if (chunk.Count == 0) { return true; }
            string name = first ? heading : heading + " (cont.)";
            if (e.Fields.Count >= MaxFields - 1 || Size() + name.Length + length > MaxEmbed - 150) { return false; }
            e.AddField(name, string.Join("\n", chunk));
            added += chunk.Count;
            first = false;
            chunk.Clear();
            length = 0;
            return true;
        }
        foreach (string line in lines)
        {
            if (length + line.Length + 1 > 1000 && !Flush()) { return added; }
            chunk.Add(line);
            length += line.Length + 1;
        }
        Flush();
        return added;
    }
}
