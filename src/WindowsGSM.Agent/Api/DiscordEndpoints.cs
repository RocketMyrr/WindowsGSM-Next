using System.Text.RegularExpressions;
using WindowsGSM.Agent.Discord;

namespace WindowsGSM.Agent.Api;

/// <summary>The Discord bot's settings and status (owners only — the token controls the bot).</summary>
public static class DiscordEndpoints
{
    public sealed record AdminRequest(string DiscordId, string? Name, List<string>? Servers);
    public sealed record BotRequest(bool Enabled, string? Token, string? GuildId, string? BotName, bool PostActions, List<AdminRequest>? Admins);

    public static void Map(RouteGroupBuilder api)
    {
        var owner = api.MapGroup("/discord-bot").AddEndpointFilter(async (efc, next) =>
        {
            var user = efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (!user.IsOwner) { return ApiResults.Forbidden("Only owners can manage the Discord bot."); }
            return await next(efc);
        });

        owner.MapGet("", (DiscordBotService bot) => Results.Json(ToDto(bot)));

        owner.MapPut("", async (HttpContext http, AgentContext ctx, DiscordBotService bot, BotRequest body) =>
        {
            var admins = new List<DiscordAdmin>();
            foreach (var a in body.Admins ?? new())
            {
                string id = (a.DiscordId ?? "").Trim();
                if (!Regex.IsMatch(id, @"^\d{15,21}$")) { return ApiResults.BadRequest($"\"{a.DiscordId}\" isn't a Discord user ID (a long number — in Discord turn on Developer Mode, then right-click someone → Copy User ID)."); }
                var servers = (a.Servers ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (servers.Count == 0) { return ApiResults.BadRequest($"Choose which servers {(string.IsNullOrWhiteSpace(a.Name) ? id : a.Name)} can control."); }
                if (servers.Any(s => s != "*" && s.Split('/').Length != 2)) { return ApiResults.BadRequest("Invalid server in the selection."); }
                if (admins.Any(x => x.DiscordId == id)) { continue; }
                admins.Add(new DiscordAdmin { DiscordId = id, Name = (a.Name ?? "").Trim(), Servers = servers });
            }
            string? guild = string.IsNullOrWhiteSpace(body.GuildId) ? null : body.GuildId.Trim();
            if (guild != null && !Regex.IsMatch(guild, @"^\d{15,21}$")) { return ApiResults.BadRequest("The server ID should be a long number (right-click the server icon → Copy Server ID)."); }
            string token = body.Token == null ? bot.Settings.Token : body.Token.Trim();
            if (body.Enabled && string.IsNullOrWhiteSpace(token)) { return ApiResults.BadRequest("Paste the bot's token first."); }
            if (body.BotName is { Length: > 32 }) { return ApiResults.BadRequest("Discord names are 32 characters at most."); }
            if (body.Enabled && bot.MemberOf is { } hub)
            {
                return ApiResults.BadRequest($"This machine reports to {hub}. Turn the bot on there instead — it covers this machine and every other one, and a second copy on the same token would race it to answer.");
            }

            await bot.ApplyAsync(new DiscordBotSettings
            {
                Enabled = body.Enabled, Token = token, GuildId = guild, BotName = string.IsNullOrWhiteSpace(body.BotName) ? null : body.BotName.Trim(),
                PostActions = body.PostActions, Admins = admins,
            });
            ctx.Record(http, "discord-bot", null, true, $"{(body.Enabled ? "on" : "off")}, {admins.Count} admin{(admins.Count == 1 ? "" : "s")}{(body.Token != null ? ", token changed" : "")}");
            // Give a fresh connection a moment, so the page shows how it went.
            for (int i = 0; i < 20 && bot.State == DiscordBotService.BotState.Connecting; i++) { await Task.Delay(250); }
            return Results.Json(ToDto(bot));
        });
    }

    private static object ToDto(DiscordBotService bot)
    {
        var s = bot.Settings;
        return new
        {
            enabled = s.Enabled,
            hasToken = !string.IsNullOrWhiteSpace(s.Token),
            tokenHint = s.Token.Length > 6 ? "…" + s.Token[^6..] : null,
            guildId = s.GuildId,
            botName = s.BotName,
            postActions = s.PostActions,
            admins = s.Admins.Select(a => new { discordId = a.DiscordId, name = a.Name, servers = a.Servers }),
            importedFromLegacy = s.ImportedFromLegacy,
            memberOf = bot.MemberOf,
            status = new { state = bot.State.ToString(), error = bot.LastError, warning = bot.Warning, botUser = bot.BotUser, guilds = bot.Guilds, inviteUrl = bot.InviteUrl },
        };
    }
}
