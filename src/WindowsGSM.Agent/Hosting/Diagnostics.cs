using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// "Export diagnostics": one zip to attach to a bug report — versions, the PC, health checks, recent logs and
/// WindowsGSM's settings, with passwords, tokens, keys and webhook addresses taken out. Server names, paths and IP
/// addresses stay (they're usually what the problem is about); the page says so before downloading.
/// </summary>
public static class Diagnostics
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Settings files worth seeing (users.json is summarised instead: no hashes, no 2FA secrets).</summary>
    private static readonly string[] SettingsFiles =
    {
        "agent.json", "automations.json", "discord-bot.json", "notify-channels.json", "offsite.json", "restart-warnings.json",
        "tags.json", "machines.json", "data-format.json", "last-restore.json",
    };

    public static async Task WriteAsync(AgentContext ctx, Stream output)
    {
        string root = WgsmEnvironment.DataRoot;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Text(string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        void Obj(string name, object value) => Text(name, JsonSerializer.Serialize(value, Json));

        var engine = ctx.Engine;
        // About this PC and WindowsGSM.
        var drive = Safe(() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!));
        Obj("about.json", new
        {
            windowsgsm = WgsmEnvironment.Version,
            createdAt = DateTimeOffset.Now,
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            memoryGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
            dataFolder = root,
            dataDriveFreeGb = drive == null ? (double?)null : Math.Round(drive.AvailableFreeSpace / 1073741824.0, 1),
            agentUptime = (DateTimeOffset.UtcNow - ctx.StartedAt).ToString(@"d\.hh\:mm\:ss"),
            machine = new { id = ctx.MachineId, name = ctx.Settings.MachineName, port = ctx.Settings.Port, exposedToNetwork = ctx.Settings.ExposeToNetwork, https = ctx.Settings.UseHttps || ctx.Settings.AcmeEnabled, joinedToHub = !string.IsNullOrEmpty(ctx.Settings.HubUrl) },
            settingsProblems = SafeJson.Problems.Select(p => new { file = Path.GetFileName(p.File), p.Message, recovered = p.Recovered }),
            dataFormat = DataFormat.NewerDataWarning,
        });

        Obj("servers.json", engine.Servers.All.Select(s => new
        {
            s.Id, s.Name, s.Game, state = s.State.ToString(),
            ip = s.Config.ServerIP, port = s.Config.ServerPort, queryPort = s.Config.ServerQueryPort,
            autoRestart = s.Config.AutoRestart, autoStart = s.Config.AutoStart, s.CrashLoopSuspended,
        }));
        Obj("plugins.json", new
        {
            loaded = engine.Plugins.Plugins.Where(p => p.IsLoaded).Select(p => new { p.FileName, p.FullName }),
            broken = engine.Games.BrokenPlugins(),
        });
        Obj("health.json", new
        {
            machine = Safe(() => engine.Readiness.CheckMachineAsync(network: false).GetAwaiter().GetResult()),
            servers = engine.Servers.All.ToDictionary(s => s.Id, s => Safe(() => engine.Readiness.CheckServer(s.Id))),
        });
        Obj("users.json", ctx.Users.All().Select(u => new { u.Username, role = u.Role.ToString(), u.Enabled, twoFactor = u.TwoFactorEnabled, passkeys = u.Passkeys.Count, grants = u.Grants.Count }));

        // Settings, with secrets taken out.
        string next = Path.Combine(root, "configs", "next");
        foreach (string name in SettingsFiles)
        {
            string f = Path.Combine(next, name);
            if (File.Exists(f)) { Text("settings/" + name, RedactJson(ReadShared(f))); }
        }
        foreach (var s in engine.Servers.All)
        {
            string configs = ServerPathOf(s.Id);
            if (!Directory.Exists(configs)) { continue; }
            foreach (string f in Directory.EnumerateFiles(configs, "*", SearchOption.TopDirectoryOnly))
            {
                string n = Path.GetFileName(f);
                if (n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length < 1024 * 1024) { Text($"servers/{s.Id}/{n}", RedactJson(ReadShared(f))); }
                else if (n.Equals("WindowsGSM.cfg", StringComparison.OrdinalIgnoreCase)) { Text($"servers/{s.Id}/{n}", RedactCfg(ReadShared(f))); }
            }
            Text($"servers/{s.Id}/log-tail.txt", string.Join(Environment.NewLine, engine.Log.Tail(s.Id, 500)));
        }

        // Recent logs: the last three days, crash reports, the agent's own log (each capped to its last 2 MB).
        string logs = Path.Combine(root, "logs");
        if (Directory.Exists(logs))
        {
            foreach (string f in Directory.EnumerateFiles(logs, "L*.log").OrderByDescending(f => f).Take(3)) { Text("logs/" + Path.GetFileName(f), Tail(f)); }
            foreach (string f in Directory.EnumerateFiles(logs, "CRASH_*.log").OrderByDescending(f => f).Take(5)) { Text("logs/" + Path.GetFileName(f), Tail(f)); }
            string agent = Path.Combine(logs, "agent");
            if (Directory.Exists(agent))
            {
                foreach (string f in Directory.EnumerateFiles(agent).OrderByDescending(File.GetLastWriteTimeUtc).Take(3)) { Text("logs/agent/" + Path.GetFileName(f), Tail(f)); }
            }
        }
        Text("README.txt",
            "WindowsGSM diagnostics" + Environment.NewLine + Environment.NewLine +
            "Passwords, tokens, keys and webhook addresses have been removed. Server names, folders and IP addresses are still in here:" + Environment.NewLine +
            "look through it before posting it publicly." + Environment.NewLine);
        await Task.CompletedTask;
    }

    private static string ServerPathOf(string id) => global::WindowsGSM.Functions.ServerPath.GetServersConfigs(id);

    private static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }

    private static string ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static string Tail(string file, int maxBytes = 2 * 1024 * 1024)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxBytes) { fs.Seek(-maxBytes, SeekOrigin.End); }
            using var r = new StreamReader(fs);
            string text = r.ReadToEnd();
            return fs.Length > maxBytes ? "[…earlier lines left out…]" + Environment.NewLine + text : text;
        }
        catch (Exception ex) { return $"[couldn't read: {ex.Message}]"; }
    }

    // ── Taking secrets out ──

    private static readonly Regex SecretName = new(@"pass|secret|token|credential|hash|totp|apikey|api_key|accesskey|webhook|cookie|privatekey|^url$|^huburl$|key$", RegexOptions.IgnoreCase);

    /// <summary>Any value under a secret-sounding name, and anything encrypted ("dpapi:…"), becomes "[removed]".</summary>
    public static string RedactJson(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return "[not readable as JSON — left out]"; }
        Redact(node, null);
        return node?.ToJsonString(Json) ?? "null";
    }

    private static void Redact(JsonNode? node, string? name)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v) { if (ShouldRemove(key, v)) { o[key] = "[removed]"; } }
                    else { Redact(o[key], key); }
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v) { if (ShouldRemove(name, v)) { a[i] = "[removed]"; } }
                    else { Redact(a[i], name); }
                }
                break;
        }
    }

    private static bool ShouldRemove(string? name, JsonValue v)
    {
        if (!v.TryGetValue<string>(out var s) || s.Length == 0) { return false; }
        return s.StartsWith("dpapi:", StringComparison.Ordinal) || (name != null && SecretName.IsMatch(name));
    }

    // "+rcon.password x", "-password=x", "+rcon.password ""x""" (quotes doubled inside a cfg value)…
    private static readonly Regex InlineSecret = new(@"\b((?:rcon\.)?password|passwd|pass|token|secret)\b(\s*[=:]?\s*(?:""){0,2})[^\s""]+", RegexOptions.IgnoreCase);

    /// <summary>WindowsGSM.cfg (key="value" lines): secret keys emptied, and passwords inside start parameters too.</summary>
    public static string RedactCfg(string cfg)
    {
        var lines = cfg.Split('\n').Select(line =>
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) { return line; }
            string key = line[..eq].Trim();
            if (SecretName.IsMatch(key) || key.Contains("rcon", StringComparison.OrdinalIgnoreCase) && key.Contains("pass", StringComparison.OrdinalIgnoreCase) || key.Equals("discordwebhook", StringComparison.OrdinalIgnoreCase))
            {
                return line[..(eq + 1)] + "\"[removed]\"" + (line.EndsWith('\r') ? "\r" : "");
            }
            return line[..(eq + 1)] + InlineSecret.Replace(line[(eq + 1)..], "$1$2[removed]");
        });
        return string.Join('\n', lines);
    }
}
