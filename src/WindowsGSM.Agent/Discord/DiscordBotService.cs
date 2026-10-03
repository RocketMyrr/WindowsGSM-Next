using System.Net.Http.Json;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Discord;

/// <summary>
/// The Discord bot: /panel (a private control panel with buttons), /list and /stats — for every machine this
/// agent can see. Only people on its admin list can use it, and it acts for each of them through the agent's
/// own API with just their servers and the panel's actions, so permissions, forwarding to other machines and
/// the audit log ("Name (Discord)") all work exactly as in the web panel.
/// </summary>
public sealed class DiscordBotService : IAsyncDisposable
{
    public enum BotState { Off, Connecting, Online, Error }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly AgentContext _ctx;
    private readonly LocalApi _api;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscordSocketClient? _client;
    private CancellationTokenSource? _presence;

    public DiscordBotService(AgentContext ctx, LocalApi api, Action<string> log)
    {
        _ctx = ctx;
        _api = api;
        _log = log;
        Settings = DiscordBotSettings.Load(ctx.ConfigDir, Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs"), ctx.MachineId);
    }

    public DiscordBotSettings Settings { get; private set; }
    public BotState State { get; private set; } = BotState.Off;
    public string? LastError { get; private set; }
    public string? BotUser { get; private set; }
    public IReadOnlyList<string> Guilds { get; private set; } = Array.Empty<string>();
    public ulong? ApplicationId { get; private set; }

    /// <summary>
    /// Something worth knowing while it runs — e.g. another copy answering with the same token. Shown on the page.
    /// </summary>
    public string? Warning { get; private set; }

    /// <summary>
    /// The hub this machine reports to, if it's a member. A member doesn't run the bot: the hub's bot already
    /// covers it (and every other machine), while a member's would see only its own servers — and two copies on
    /// one token race to answer every command.
    /// </summary>
    public string? MemberOf => !string.IsNullOrWhiteSpace(_ctx.Settings.HubUrl) && !string.IsNullOrWhiteSpace(_ctx.Settings.HubCredential)
        ? (string.IsNullOrWhiteSpace(_ctx.Settings.HubName) ? _ctx.Settings.HubUrl : _ctx.Settings.HubName)
        : null;

    /// <summary>How long one machine may take to answer before the panel shows the rest without it.</summary>
    internal static TimeSpan MachineTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private static readonly object BotLogLock = new();

    /// <summary>
    /// The bot's own log, logs\L&lt;date&gt;-DiscordBot.log like the legacy bot kept: every command and button,
    /// who used it, and refusals. (Problems also go to the agent's daily log.)
    /// </summary>
    private static void BotLog(string message)
    {
        try
        {
            string dir = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "logs");
            Directory.CreateDirectory(dir);
            lock (BotLogLock)
            {
                File.AppendAllText(Path.Combine(dir, $"L{DateTime.Now:yyyyMMdd}-DiscordBot.log"), $"[{DateTime.Now:MM/dd/yyyy-HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the bot */ }
    }

    private static string Who(SocketInteraction i) => $"{i.User.Username} ({i.User.Id})";

    public string? InviteUrl => ApplicationId is { } id
        ? $"https://discord.com/oauth2/authorize?client_id={id}&permissions=2048&scope=bot%20applications.commands"
        : null;

    // ───────────────────────────── Lifecycle ─────────────────────────────

    public async Task StartAsync()
    {
        if (_client != null) { return; } // already running
        if (Settings.Enabled && MemberOf is { } hub)
        {
            _log($"Discord bot: not started — this machine reports to {hub}, and the bot there covers it.");
            return;
        }
        if (Settings.Enabled && !string.IsNullOrWhiteSpace(Settings.Token)) { await ConnectAsync(); }
    }

    /// <summary>This machine just joined a hub: its bot stands down (the hub's covers it). Settings are kept for if it leaves.</summary>
    public async Task StandDownAsync()
    {
        if (_client == null) { return; }
        await DisconnectAsync();
        _log($"Discord bot: stopped — this machine now reports to {MemberOf ?? "a hub"}, whose bot covers it.");
    }

    /// <summary>Saves new settings and reconnects if anything that matters changed.</summary>
    public async Task ApplyAsync(DiscordBotSettings next)
    {
        bool reconnect = next.Enabled != Settings.Enabled || next.Token != Settings.Token || next.GuildId != Settings.GuildId || next.BotName != Settings.BotName;
        next.ImportedFromLegacy = false;
        next.Save(_ctx.ConfigDir);
        Settings = next;
        if (!reconnect) { return; }
        await DisconnectAsync();
        if (next.Enabled && !string.IsNullOrWhiteSpace(next.Token)) { await ConnectAsync(); }
    }

    private async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            State = BotState.Connecting;
            LastError = null;
            Warning = null;
            var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, LogLevel = LogSeverity.Warning });
            client.Log += m => { if (m.Severity <= LogSeverity.Warning) { _log($"Discord bot: {m.Message} {m.Exception?.Message}".Trim()); } return Task.CompletedTask; };
            client.Ready += () => OnReadyAsync(client);
            client.Disconnected += ex =>
            {
                // A revoked or reset token: Discord closes with "authentication failed" and would be retried forever.
                if (ex?.Message.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) == true || ex?.InnerException?.Message.Contains("4004") == true)
                {
                    State = BotState.Error;
                    LastError = "Discord rejected the token (it may have been reset). Paste the current one from the Developer Portal.";
                    _ = Task.Run(async () => { await client.StopAsync(); });
                }
                else if (State == BotState.Online) { State = BotState.Connecting; }
                return Task.CompletedTask;
            };
            client.SlashCommandExecuted += c => Guard(c, () => OnSlashAsync(c));
            client.ButtonExecuted += c => Guard(c, () => OnComponentAsync(c));
            client.SelectMenuExecuted += c => Guard(c, () => OnComponentAsync(c));
            _client = client;
            try
            {
                await client.LoginAsync(TokenType.Bot, Settings.Token.Trim(), validateToken: false);
                // Ask Discord first: a wrong token otherwise just retries in the background forever.
                ApplicationId = (await client.Rest.GetApplicationInfoAsync()).Id;
                await client.StartAsync();
            }
            catch (Exception ex)
            {
                State = BotState.Error;
                LastError = ex is global::Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized }
                    ? "Discord rejected the token. Copy it again from the Developer Portal (Bot → Reset Token)."
                    : ex is HttpRequestException ? $"Couldn't reach Discord: {ex.Message}" : ex.Message;
                _log($"Discord bot: couldn't connect: {LastError}");
                BotLog($"Couldn't connect: {LastError}");
                _client = null;
                await client.DisposeAsync();
            }
        }
        finally { _gate.Release(); }
    }

    private async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _presence?.Cancel();
            if (_client != null)
            {
                try { await _client.StopAsync(); await _client.LogoutAsync(); } catch { /* going anyway */ }
                await _client.DisposeAsync();
                _client = null;
            }
            State = BotState.Off;
            BotUser = null;
            Guilds = Array.Empty<string>();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task OnReadyAsync(DiscordSocketClient client)
    {
        try
        {
            BotUser = client.CurrentUser?.Username;
            Guilds = client.Guilds.Select(g => g.Name).ToList();

            // Exactly these commands — replacing whatever was registered before (the legacy bot had more).
            var commands = new ApplicationCommandProperties[]
            {
                new SlashCommandBuilder().WithName("panel").WithDescription("Open the WindowsGSM control panel (only you see it)").Build(),
                new SlashCommandBuilder().WithName("list").WithDescription("List the game servers").Build(),
                new SlashCommandBuilder().WithName("stats").WithDescription("CPU, memory and disk, servers and players").Build(),
            };
            var target = ulong.TryParse(Settings.GuildId, out ulong gid) ? client.GetGuild(gid) : client.Guilds.Count == 1 ? client.Guilds.First() : null;
            if (target != null) { await target.BulkOverwriteApplicationCommandAsync(commands); }
            else { await client.BulkOverwriteGlobalApplicationCommandsAsync(commands); }

            if (!string.IsNullOrWhiteSpace(Settings.BotName) && client.CurrentUser != null && client.CurrentUser.Username != Settings.BotName)
            {
                try { await client.CurrentUser.ModifyAsync(u => u.Username = Settings.BotName); }
                catch (Exception ex) { _log($"Discord bot: couldn't rename it (Discord allows that twice an hour): {ex.Message}"); }
            }

            // No avatar yet: give it the WindowsGSM logo (never replaces one someone chose).
            if (client.CurrentUser is { AvatarId: null } me)
            {
                try
                {
                    var logo = new Microsoft.Extensions.FileProviders.ManifestEmbeddedFileProvider(typeof(AgentApp).Assembly, "wwwroot").GetFileInfo("img/logo-512.png");
                    if (logo.Exists)
                    {
                        await using var stream = logo.CreateReadStream();
                        using var copy = new MemoryStream();
                        await stream.CopyToAsync(copy);
                        copy.Position = 0;
                        await me.ModifyAsync(u => u.Avatar = new Image(copy));
                    }
                }
                catch (Exception ex) { _log($"Discord bot: couldn't set its picture: {ex.Message}"); }
            }

            State = BotState.Online;
            BotLog($"Online as {BotUser} in: {string.Join(", ", Guilds)}");
            _log($"Discord bot online as {BotUser} in {Guilds.Count} server{(Guilds.Count == 1 ? "" : "s")}.");
            _presence?.Cancel();
            _presence = new CancellationTokenSource();
            _ = PresenceLoopAsync(client, _presence.Token);
        }
        catch (Exception ex)
        {
            State = BotState.Error;
            LastError = ex.Message;
            _log($"Discord bot: setup failed: {ex.Message}");
        }
    }

    /// <summary>"Playing 8 game servers", refreshed every 15 minutes (as before).</summary>
    private async Task PresenceLoopAsync(DiscordSocketClient client, CancellationToken token)
    {
        var everything = new AgentUser { Username = "Discord bot", Role = Role.Member, Enabled = true, Grants = new(StringComparer.OrdinalIgnoreCase) { ["*/*"] = Capability.View } };
        while (!token.IsCancellationRequested)
        {
            try
            {
                var fleet = await FleetAsync(everything);
                await client.SetGameAsync($"{fleet.Servers.Count} game server{(fleet.Servers.Count == 1 ? "" : "s")}");
            }
            catch { /* next time */ }
            try { await Task.Delay(TimeSpan.FromMinutes(15), token); } catch (OperationCanceledException) { return; }
        }
    }

    // ───────────────────────────── Commands ─────────────────────────────

    /// <summary>Only admins get through; anything that goes wrong is answered instead of timing out.</summary>
    private Task Guard(SocketInteraction i, Func<Task> run) => Task.Run(async () =>
    {
        try
        {
            if (Settings.Admin(i.User.Id.ToString()) == null)
            {
                BotLog($"Refused {Describe(i)} from {Who(i)} — not on the admin list");
                await i.RespondAsync("You're not on this bot's admin list. Ask the owner to add your Discord user ID in WindowsGSM → Discord bot.", ephemeral: true);
                return;
            }
            BotLog($"{Describe(i)} by {Who(i)}");
            await run();
        }
        catch (Exception ex) when (AlreadyAnswered(ex))
        {
            // Discord gave the interaction to someone else first: another program is logged in with this token.
            if (Warning == null) { _log("Discord bot: another copy is answering with this bot's token (the old WindowsGSM, or this on another machine). Keep it on in one place only."); }
            Warning = "Another copy is answering commands with this bot's token — the old WindowsGSM's bot, or this bot switched on on another machine too. Keep it on in one place only (on the hub, if you have several machines), or people get answers from whichever is quicker.";
            BotLog($"{Describe(i)} by {Who(i)} was answered by another copy using this token");
        }
        catch (Exception ex)
        {
            BotLog($"Error in {Describe(i)} by {Who(i)}: {ex.Message}");
            _log($"Discord bot: {ex.Message}");
            try
            {
                if (i.HasResponded) { await i.FollowupAsync($"Something went wrong: {ex.Message}", ephemeral: true); }
                else { await i.RespondAsync($"Something went wrong: {ex.Message}", ephemeral: true); }
            }
            catch { /* the interaction expired */ }
        }
    });

    /// <summary>Discord's "Interaction has already been acknowledged" (40060) / unknown interaction (10062).</summary>
    internal static bool AlreadyAnswered(Exception ex) =>
        ex is global::Discord.Net.HttpException http && (http.DiscordCode is DiscordErrorCode.InteractionHasAlreadyBeenAcknowledged or DiscordErrorCode.UnknownInteraction)
        || ex.Message.Contains("already been acknowledged", StringComparison.OrdinalIgnoreCase);

    private static string Describe(SocketInteraction i) => i switch
    {
        SocketSlashCommand c => "/" + c.CommandName,
        SocketMessageComponent m when m.Data.CustomId == DiscordEmbeds.SelectId => $"panel: picked {m.Data.Values?.FirstOrDefault()}",
        SocketMessageComponent m => "panel: " + m.Data.CustomId.Replace(DiscordEmbeds.Prefix, ""),
        _ => "interaction",
    };

    private AgentUser ActingUser(SocketInteraction i) => DiscordBotSettings.ActingUser(Settings.Admin(i.User.Id.ToString())!);

    private async Task OnSlashAsync(SocketSlashCommand c)
    {
        var user = ActingUser(c);
        switch (c.CommandName)
        {
            case "panel":
                await c.DeferAsync(ephemeral: true);
                var fleet = await FleetAsync(user);
                await c.ModifyOriginalResponseAsync(m => { m.Embed = DiscordEmbeds.Panel(fleet); m.Components = DiscordEmbeds.PanelComponents(fleet); });
                break;
            case "list":
                await c.DeferAsync();
                var list = await FleetAsync(user);
                await c.ModifyOriginalResponseAsync(m => m.Embed = DiscordEmbeds.ServerList(list));
                break;
            case "stats":
                await c.DeferAsync();
                var stats = await FleetAsync(user);
                await c.ModifyOriginalResponseAsync(m => m.Embed = DiscordEmbeds.Stats(stats));
                break;
            default:
                await c.RespondAsync("That command isn't used any more — try /panel, /list or /stats.", ephemeral: true);
                break;
        }
    }

    /// <summary>The panel's buttons and dropdown: every one edits the same private message in place.</summary>
    private async Task OnComponentAsync(SocketMessageComponent c)
    {
        string id = c.Data.CustomId;
        if (!id.StartsWith(DiscordEmbeds.Prefix)) { return; }
        await c.DeferAsync(ephemeral: true);
        var user = ActingUser(c);

        if (id == DiscordEmbeds.RefreshId) { await ShowPanel(c, user); return; }
        if (id.StartsWith(DiscordEmbeds.PageId) && int.TryParse(id[DiscordEmbeds.PageId.Length..], out int page)) { await ShowPanel(c, user, page); return; }
        if (id == DiscordEmbeds.ListId)
        {
            var fleet = await FleetAsync(user);
            await c.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = DiscordEmbeds.ServerList(fleet); m.Components = DiscordEmbeds.PanelComponents(fleet); });
            return;
        }
        if (id == DiscordEmbeds.StatsId)
        {
            var fleet = await FleetAsync(user);
            await c.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = DiscordEmbeds.Stats(fleet); m.Components = DiscordEmbeds.PanelComponents(fleet); });
            return;
        }
        if (id == DiscordEmbeds.SelectId)
        {
            string? key = c.Data.Values?.FirstOrDefault();
            if (key == null || key == "none") { await ShowPanel(c, user); return; }
            await ShowServer(c, user, key);
            return;
        }
        if (id.StartsWith(DiscordEmbeds.Prefix + "view:")) { await ShowServer(c, user, id[(DiscordEmbeds.Prefix + "view:").Length..]); return; }

        bool confirm = id.StartsWith(DiscordEmbeds.Confirm);
        if (confirm || id.StartsWith(DiscordEmbeds.Do))
        {
            string rest = id[(confirm ? DiscordEmbeds.Confirm : DiscordEmbeds.Do).Length..];
            int colon = rest.IndexOf(':');
            if (colon <= 0) { return; }
            string action = rest[..colon], key = rest[(colon + 1)..];
            if (!DiscordEmbeds.Actions.ContainsKey(action)) { return; }
            var fleet = await FleetAsync(user);
            var s = fleet.Server(key);
            if (s == null) { await c.ModifyOriginalResponseAsync(m => { m.Content = "That server isn't there any more, or you can't control it."; m.Embed = null; m.Components = DiscordEmbeds.PanelComponents(fleet); }); return; }

            if (confirm)
            {
                var a = DiscordEmbeds.Actions[action];
                await c.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = $"⚠️ **{a.Verb} {s.Name}**{(fleet.MultiMachine ? $" on {fleet.MachineName(s.Machine)}" : "")}? {a.Warning}";
                    m.Embed = null;
                    m.Components = DiscordEmbeds.ConfirmRow(action, key);
                });
                return;
            }

            string? error = await ActAsync(user, s, action);
            BotLog(error == null ? $"{DiscordEmbeds.Actions[action].Past} {s.Name} (#{s.Id} on {fleet.MachineName(s.Machine)}) — by {Who(c)}" : $"{action} {s.Name} failed for {Who(c)}: {error}");
            string where = fleet.MultiMachine ? $" on {fleet.MachineName(s.Machine)}" : "";
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Content = error == null ? $"✅ {DiscordEmbeds.Actions[action].Verb} **{s.Name}**{where} — on its way." : $"❌ Couldn't {DiscordEmbeds.Actions[action].Verb.ToLowerInvariant()} **{s.Name}**: {error}";
                m.Embed = null;
                m.Components = new ComponentBuilder().Build();
            });
            if (error == null && Settings.PostActions)
            {
                try { await c.Channel.SendMessageAsync($"📝 {c.User.Mention} **{DiscordEmbeds.Actions[action].Past}** `{s.Name}`{where}", allowedMentions: AllowedMentions.None); }
                catch { /* the bot may not be allowed to post in this channel */ }
            }
            await Task.Delay(2500);
            await ShowServer(c, user, key);
        }
    }

    private async Task ShowPanel(SocketMessageComponent c, AgentUser user, int page = 0)
    {
        var fleet = await FleetAsync(user);
        await c.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = DiscordEmbeds.Panel(fleet); m.Components = DiscordEmbeds.PanelComponents(fleet, page); });
    }

    private async Task ShowServer(SocketMessageComponent c, AgentUser user, string key)
    {
        var fleet = await FleetAsync(user);
        var s = fleet.Server(key);
        if (s == null)
        {
            await c.ModifyOriginalResponseAsync(m => { m.Content = "That server isn't there any more, or you can't control it."; m.Embed = DiscordEmbeds.Panel(fleet); m.Components = DiscordEmbeds.PanelComponents(fleet); });
            return;
        }
        await c.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = DiscordEmbeds.ServerView(fleet, s); m.Components = DiscordEmbeds.ServerActions(fleet, s); });
    }

    // ───────────────────────────── The agent's API, as the admin ─────────────────────────────

    /// <summary>
    /// Every machine's servers, asked all at once. NEXT: one at a time with no limit, a machine that was slow to
    /// answer held up the whole panel; now a machine gets <see cref="MachineTimeout"/> and the panel says which
    /// one didn't answer.
    /// </summary>
    public async Task<BotFleet> FleetAsync(AgentUser user)
    {
        using var http = _api.Client();
        var machines = await GetAsync<List<MachineDto>>(http, "/machines", user) ?? new();
        var asks = machines.Select(async m =>
        {
            using var limit = new CancellationTokenSource(MachineTimeout);
            try
            {
                var list = await GetAsync<List<ServerDto>>(http, $"/machines/{Uri.EscapeDataString(m.Id)}/servers", user, limit.Token) ?? new();
                // Labelled with the machine that was asked, so buttons always go back to the right one.
                return (m.Id, Servers: list.Select(s => s with { Machine = m.Id }).ToList());
            }
            catch { return (m.Id, Servers: (List<ServerDto>?)null); }
        }).ToList();
        var answers = await Task.WhenAll(asks);
        var order = machines.Select(m => m.Id).ToList();
        var servers = answers.Where(a => a.Servers != null).SelectMany(a => a.Servers!)
            .OrderBy(s => order.IndexOf(s.Machine)).ThenBy(s => int.TryParse(s.Id, out int n) ? n : int.MaxValue).ToList();
        return new BotFleet(machines, servers) { NotAnswering = answers.Where(a => a.Servers == null).Select(a => a.Id).ToList() };
    }

    private async Task<T?> GetAsync<T>(HttpClient http, string path, AgentUser user, CancellationToken token = default)
    {
        using var req = _api.Request(HttpMethod.Get, path, user, "Discord");
        using var res = await http.SendAsync(req, token);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(Json, token);
    }

    /// <summary>Runs a panel action. Returns the reason it didn't happen, or null.</summary>
    public async Task<string?> ActAsync(AgentUser user, ServerDto s, string action)
    {
        using var http = _api.Client();
        using var req = _api.Request(HttpMethod.Post, $"/machines/{Uri.EscapeDataString(s.Machine)}/servers/{Uri.EscapeDataString(s.Id)}/{action}", user, "Discord", new { });
        using var res = await http.SendAsync(req);
        if (res.IsSuccessStatusCode) { return null; }
        try { return (await res.Content.ReadFromJsonAsync<ApiError>(Json))?.Error ?? res.ReasonPhrase; }
        catch { return res.ReasonPhrase; }
    }
}
