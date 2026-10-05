using System.Text.Json;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Discord;

/// <summary>Someone allowed to use the bot, and which servers they can control through it.</summary>
public sealed class DiscordAdmin
{
    /// <summary>Their Discord user id (Developer Mode → right-click → Copy User ID).</summary>
    public string DiscordId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>"*" for everything, or "machine/*" / "machine/server" scopes.</summary>
    public List<string> Servers { get; set; } = new() { "*" };
}

/// <summary>
/// The Discord bot's settings (configs/next/discord-bot.json). On first use they're imported from the legacy
/// bot's files (configs/discordbot/: token.txt, adminIDs.txt, guildID.txt, name.txt) — but left switched off,
/// because two apps logged in with the same token would both answer every command.
/// </summary>
public sealed class DiscordBotSettings
{
    public bool Enabled { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(global::WindowsGSM.Hosting.SecretJsonConverter))] public string Token { get; set; } = string.Empty;
    /// <summary>The Discord server (guild) to register commands in. Blank: the only one the bot is in, else everywhere.</summary>
    public string? GuildId { get; set; }
    public string? BotName { get; set; }
    /// <summary>Post "@someone started Valheim" in the channel when the panel's buttons are used.</summary>
    public bool PostActions { get; set; } = true;
    public List<DiscordAdmin> Admins { get; set; } = new();
    /// <summary>Set when these settings came from the legacy bot (shown once in the UI).</summary>
    public bool ImportedFromLegacy { get; set; }

    /// <summary>What a bot admin may do on the servers they have: exactly what the panel's buttons offer.</summary>
    public const Capability AdminCaps = Capability.View | Capability.Start | Capability.Stop | Capability.Restart | Capability.Kill | Capability.Update;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public DiscordAdmin? Admin(string discordId) => Admins.FirstOrDefault(a => a.DiscordId == discordId);

    /// <summary>The account the bot acts as for this admin: their servers, the panel's actions, nothing else.</summary>
    public static AgentUser ActingUser(DiscordAdmin admin)
    {
        var grants = new Dictionary<string, Capability>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in admin.Servers)
        {
            string scope = s == "*" ? "*/*" : s;
            if (scope.Split('/').Length == 2) { grants[scope] = AdminCaps; }
        }
        return new AgentUser
        {
            Username = $"{(string.IsNullOrWhiteSpace(admin.Name) ? admin.DiscordId : admin.Name)} (Discord)",
            Role = Role.Member,
            Enabled = true,
            Grants = grants,
        };
    }

    public static DiscordBotSettings Load(string configDir, string legacyConfigsDir, string machineId)
    {
        string file = Path.Combine(configDir, "discord-bot.json");
        if (File.Exists(file) || File.Exists(file + ".bak"))
        {
            var loaded = global::WindowsGSM.Hosting.SafeJson.Read<DiscordBotSettings>(file, Json) ?? new();
            // A token saved before encryption existed is encrypted now.
            try { if (loaded.Token.Length > 0 && File.Exists(file) && !File.ReadAllText(file).Contains("dpapi:")) { loaded.Save(configDir); } } catch { }
            return loaded;
        }
        return ImportLegacy(Path.Combine(legacyConfigsDir, "discordbot"), machineId) ?? new();
    }

    public void Save(string configDir)
    {
        global::WindowsGSM.Hosting.SafeJson.Write(Path.Combine(configDir, "discord-bot.json"), this, Json);
    }

    /// <summary>
    /// The legacy bot kept one value per text file, and admins as "discordId|name|serverIds" lines, where the
    /// server ids are this machine's (comma-separated) and "0" means all of them.
    /// </summary>
    public static DiscordBotSettings? ImportLegacy(string folder, string machineId)
    {
        string Read(string name) { try { return File.ReadAllText(Path.Combine(folder, name)).Trim(); } catch { return string.Empty; } }
        string token = Read("token.txt");
        if (token.Length == 0) { return null; }
        var settings = new DiscordBotSettings
        {
            Token = token,
            GuildId = Read("guildID.txt") is { Length: > 0 } g ? g : null,
            BotName = Read("name.txt") is { Length: > 0 } n ? n : null,
            ImportedFromLegacy = true,
        };
        string admins = Read("adminIDs.txt");
        foreach (string line in admins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length == 0 || parts[0].Trim().Length == 0) { continue; }
            var ids = parts.Length > 2 ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
            settings.Admins.Add(new DiscordAdmin
            {
                DiscordId = parts[0].Trim(),
                Name = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                Servers = ids.Contains("0") ? new List<string> { $"{machineId}/*" } : ids.Select(id => $"{machineId}/{id}").ToList(),
            });
        }
        return settings;
    }
}
