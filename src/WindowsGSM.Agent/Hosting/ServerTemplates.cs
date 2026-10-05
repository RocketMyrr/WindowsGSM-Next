using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Server templates: a server's settings and game config files saved under a name, to start new servers of that
/// game the same way (or bring an existing one in line). Kept in configs\next\templates\&lt;id&gt;.json.
///
/// Left out on purpose: what has to differ per server (name, address, ports, RCON port) and secrets (RCON and
/// branch passwords, the Steam login token, Discord webhook). Port numbers inside the copied config files are
/// changed to the new server's ports.
/// </summary>
public sealed class ServerTemplates
{
    public sealed class Template
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Game { get; set; } = "";
        public string? Description { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? FromServer { get; set; }
        public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TemplateFile> Files { get; set; } = new();
        /// <summary>The source server's game / query / RCON port, to swap for the new server's in the files.</summary>
        public Dictionary<string, int> Ports { get; set; } = new();
    }

    public sealed record TemplateFile(string Path, string Content);

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ServerConfig.SettingName.ServerGame, ServerConfig.SettingName.ServerName, ServerConfig.SettingName.ServerIP,
        ServerConfig.SettingName.ServerPort, ServerConfig.SettingName.ServerQueryPort, ServerConfig.SettingName.RconIp,
        ServerConfig.SettingName.RconPort, ServerConfig.SettingName.RconPassword, ServerConfig.SettingName.ServerGSLT,
        ServerConfig.SettingName.SteamBranchPassword, ServerConfig.SettingName.SteamBranchLastInstalled, ServerConfig.SettingName.DiscordWebhook,
        "updatehold", "upnp",
        // Scripts run a program on a particular PC, and choosing one is for admins.
        WindowsGSM.Engine.Services.ServerScripts.BeforeStartKey, WindowsGSM.Engine.Services.ServerScripts.AfterStopKey,
    };

    private const long MaxFileBytes = 256 * 1024, MaxTotalBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AgentContext _ctx;
    private readonly string _dir;

    public ServerTemplates(AgentContext ctx)
    {
        _ctx = ctx;
        _dir = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "templates");
    }

    public IReadOnlyList<Template> List()
    {
        if (!Directory.Exists(_dir)) { return Array.Empty<Template>(); }
        var list = new List<Template>();
        foreach (string f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (global::WindowsGSM.Hosting.SafeJson.Read<Template>(f, Json) is { } t) { list.Add(t); } // a broken one is skipped (and kept aside)
        }
        return list.OrderBy(t => t.Game).ThenBy(t => t.Name).ToList();
    }

    public Template? Get(string id) => Regex.IsMatch(id ?? "", "^[a-f0-9]{12}$") ? List().FirstOrDefault(t => t.Id == id) : null;

    /// <summary>Saves a server's settings (and, if asked, its game config files) as a new template.</summary>
    public Template Create(string serverId, string name, string? description, bool includeFiles, string? user)
    {
        var s = _ctx.Engine.Servers.Get(serverId) ?? throw new ArgumentException("No such server.");
        s.ReloadConfig();
        var t = new Template
        {
            Id = Guid.NewGuid().ToString("N")[..12], Name = name.Trim(), Game = s.Game, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedBy = user, CreatedAt = DateTimeOffset.UtcNow, FromServer = s.Name,
        };
        foreach (var (key, value) in ReadConfig(serverId))
        {
            if (!Excluded.Contains(key)) { t.Settings[key] = value; }
        }
        foreach (var (key, raw) in new[] { ("game", s.Config.ServerPort), ("query", s.Config.ServerQueryPort), ("rcon", s.Config.RconPort) })
        {
            if (int.TryParse(raw, out int p) && p > 0) { t.Ports[key] = p; }
        }
        if (includeFiles)
        {
            long total = 0;
            foreach (var f in _ctx.Engine.GameConfigs.Discover(serverId))
            {
                if (f.Size > MaxFileBytes || total + f.Size > MaxTotalBytes) { continue; }
                try
                {
                    var file = _ctx.Engine.Files.Read(serverId, f.Path);
                    if (file.Binary || file.Content == null) { continue; }
                    t.Files.Add(new TemplateFile(f.Path, file.Content));
                    total += f.Size;
                }
                catch { /* unreadable: skip */ }
            }
        }
        Directory.CreateDirectory(_dir);
        global::WindowsGSM.Hosting.SafeJson.Write(Path.Combine(_dir, t.Id + ".json"), t, Json);
        return t;
    }

    public bool Delete(string id)
    {
        if (Get(id) == null) { return false; }
        File.Delete(Path.Combine(_dir, id + ".json"));
        return true;
    }

    /// <summary>Applies a template to a stopped server: its settings, then its files (with this server's ports). Returns what was done.</summary>
    public async Task<string> ApplyAsync(Template t, string serverId)
    {
        var s = _ctx.Engine.Servers.Get(serverId) ?? throw new ArgumentException("No such server.");
        if (!string.Equals(s.Game, t.Game, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException($"That template is for {t.Game}."); }
        // The source server's ports (in its start parameters and config files) become this server's.
        var swap = new Dictionary<int, int>();
        foreach (var (key, raw) in new[] { ("game", s.Config.ServerPort), ("query", s.Config.ServerQueryPort), ("rcon", s.Config.RconPort) })
        {
            if (t.Ports.TryGetValue(key, out int from) && int.TryParse(raw, out int to) && to > 0 && from != to) { swap[from] = to; }
        }
        string SwapPorts(string text) => swap.Count == 0 ? text
            : Regex.Replace(text, @"(?<![\d.])(\d{2,5})(?![\d.])", m => swap.TryGetValue(int.Parse(m.Value), out int to) ? to.ToString() : m.Value);

        foreach (var (key, value) in t.Settings)
        {
            if (!Excluded.Contains(key) && Regex.IsMatch(key, "^[A-Za-z0-9_.-]{1,64}$"))
            {
                ServerConfig.SetSetting(serverId, key, SwapPorts(value).Replace("\"", "").Replace("\r", "").Replace("\n", " "));
            }
        }
        s.ReloadConfig();

        int written = 0;
        foreach (var f in t.Files)
        {
            string content = SwapPorts(f.Content);
            try
            {
                string full = _ctx.Engine.Files.Resolve(serverId, f.Path); // refuses paths outside the server's folder
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content);
                written++;
            }
            catch (Exception ex) { _ctx.Engine.Log.Write(serverId, $"[NOTICE] Template \"{t.Name}\": couldn't write {f.Path}: {ex.Message}"); }
        }
        _ctx.Engine.Events.Publish(new WindowsGSM.Engine.Events.ServerConfigChanged(serverId, t.Settings.Keys.ToList()));
        string done = $"{t.Settings.Count} setting(s){(t.Files.Count > 0 ? $" and {written} config file(s)" : "")}";
        _ctx.Engine.Log.Write(serverId, $"Template \"{t.Name}\" applied: {done}.");
        return done;
    }

    /// <summary>After an install job finishes well, apply the template to the new server.</summary>
    public void ApplyAfter(Job job, Template t)
    {
        _ = job.Completion.ContinueWith(async done =>
        {
            if (done.Result.Status != JobStatus.Succeeded || job.ServerId == null) { return; }
            try { await ApplyAsync(t, job.ServerId); }
            catch (Exception ex) { _ctx.Engine.Log.Write(job.ServerId, $"[ERROR] Template \"{t.Name}\" couldn't be applied: {ex.Message}"); }
        }, TaskScheduler.Default);
    }

    private static IEnumerable<(string Key, string Value)> ReadConfig(string serverId)
    {
        string file = ServerPath.GetServersConfigs(serverId, "WindowsGSM.cfg");
        if (!File.Exists(file)) { yield break; }
        foreach (string line in File.ReadAllLines(file))
        {
            var kv = line.Split('=', 2);
            if (kv.Length != 2 || kv[0].Trim().Length == 0) { continue; }
            string v = kv[1].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') { v = v[1..^1]; }
            yield return (kv[0].Trim(), v);
        }
    }
}
