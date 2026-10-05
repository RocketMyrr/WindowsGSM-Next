using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Agent.Api;

namespace WindowsGSM.Agent.Notifications;

/// <summary>Somewhere notifications go: a Discord channel (webhook) or any URL that accepts JSON.</summary>
public sealed class NotificationChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>"discord" or "webhook".</summary>
    public string Type { get; set; } = "discord";
    [System.Text.Json.Serialization.JsonConverter(typeof(global::WindowsGSM.Hosting.SecretJsonConverter))] public string Url { get; set; } = string.Empty;
    /// <summary>Discord only: text put before the message, e.g. "@here" or "&lt;@&amp;role id&gt;".</summary>
    public string? Mention { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Notification kinds to send (<see cref="NotificationKinds"/>).</summary>
    public List<string> Events { get; set; } = new();
    /// <summary>What it covers: "machine/*" or "machine/server". Empty = everything.</summary>
    public List<string> Scope { get; set; } = new();
    public DateTimeOffset? LastSentAt { get; set; }
    public string? LastError { get; set; }

    public bool Covers(NotificationEntry e) =>
        Scope.Count == 0 || Scope.Any(s =>
            string.Equals(s, $"{e.Machine}/*", StringComparison.OrdinalIgnoreCase) ||
            (e.Server != null && string.Equals(s, $"{e.Machine}/{e.Server}", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The agent's notification channels (configs/next/notify-channels.json) and delivery. Every new
/// notification that a channel covers and subscribes to is posted to it, off the caller's thread, with
/// repeats of the same thing throttled so a crash loop can't flood a channel. On a hub this covers every
/// machine; per-server Discord alerts in each server's settings keep working as before, on their own machine.
/// </summary>
public sealed class NotificationChannels : IDisposable
{
    public static TimeSpan RepeatWindow { get; set; } = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly NotificationCentre _centre;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();
    private List<NotificationChannel> _channels = new();

    public NotificationChannels(AgentContext ctx, NotificationCentre centre, HttpClient http, Action<string> log)
    {
        _file = Path.Combine(ctx.ConfigDir, "notify-channels.json");
        _centre = centre;
        _http = http;
        _log = log;
        _channels = global::WindowsGSM.Hosting.SafeJson.Read<List<NotificationChannel>>(_file, Json) ?? new();
        // Webhook URLs are secrets: encrypt older files.
        try { if (_channels.Any(c => c.Url.Length > 0) && File.Exists(_file) && !File.ReadAllText(_file).Contains("dpapi:")) { SaveFile(); } } catch { /* next save */ }
        centre.Added += OnAdded;
    }

    /// <summary>Swappable for tests: (channel, entry) → delivery.</summary>
    public Func<NotificationChannel, NotificationEntry, CancellationToken, Task>? SendOverride { get; set; }

    public IReadOnlyList<NotificationChannel> All() { lock (_gate) { return _channels.Select(Clone).ToList(); } }

    public NotificationChannel? Get(string id) { lock (_gate) { var c = _channels.FirstOrDefault(x => x.Id == id); return c == null ? null : Clone(c); } }

    /// <summary>Validates and saves a channel (new when <paramref name="id"/> is null). Returns an error or null.</summary>
    public string? Save(string? id, NotificationChannel input, out NotificationChannel? saved)
    {
        saved = null;
        string? problem = Validate(input);
        if (problem != null) { return problem; }
        lock (_gate)
        {
            NotificationChannel? target;
            if (id == null)
            {
                if (_channels.Count >= 50) { return "That's a lot of channels — remove one first (50 at most)."; }
                target = new NotificationChannel { Id = Guid.NewGuid().ToString("N")[..12] };
                _channels.Add(target);
            }
            else
            {
                target = _channels.FirstOrDefault(c => c.Id == id);
                if (target == null) { return "No such channel."; }
            }
            target.Name = input.Name.Trim();
            target.Type = input.Type;
            // An empty URL on edit keeps the saved one (the UI never gets it back in full).
            if (!string.IsNullOrWhiteSpace(input.Url)) { target.Url = input.Url.Trim(); }
            target.Mention = string.IsNullOrWhiteSpace(input.Mention) ? null : input.Mention.Trim();
            target.Enabled = input.Enabled;
            target.Events = input.Events.Distinct().ToList();
            target.Scope = input.Scope.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrWhiteSpace(target.Url)) { _channels.Remove(target); return "Enter the webhook URL."; }
            SaveFile();
            saved = Clone(target);
        }
        return null;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            bool removed = _channels.RemoveAll(c => c.Id == id) > 0;
            if (removed) { SaveFile(); }
            return removed;
        }
    }

    private static string? Validate(NotificationChannel c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 60) { return "Give the channel a name (up to 60 characters)."; }
        if (c.Type is not ("discord" or "webhook")) { return "Choose Discord or webhook."; }
        if (!string.IsNullOrWhiteSpace(c.Url))
        {
            if (!Uri.TryCreate(c.Url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            {
                return "The URL should start with https://";
            }
            if (c.Type == "discord" && !(u.Scheme == Uri.UriSchemeHttps
                && (u.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase))
                && u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)))
            {
                return "That isn't a Discord webhook URL. In Discord: Server settings → Integrations → Webhooks → Copy webhook URL.";
            }
        }
        if (c.Mention?.Length > 100) { return "Keep the mention short (100 characters)."; }
        if (c.Events.Count == 0) { return "Pick at least one thing to be told about."; }
        if (c.Events.Any(e => NotificationKinds.Get(e) == null)) { return "Unknown notification type."; }
        if (c.Scope.Any(s => s.Split('/').Length != 2 || s.Length > 130)) { return "Invalid machine or server in the selection."; }
        return null;
    }

    // ───────────────────────────── Delivery ─────────────────────────────

    private void OnAdded(NotificationEntry entry)
    {
        List<NotificationChannel> targets;
        lock (_gate) { targets = _channels.Where(c => c.Enabled && c.Events.Contains(entry.Kind) && c.Covers(entry)).Select(Clone).ToList(); }
        foreach (var channel in targets)
        {
            string key = $"{channel.Id}|{entry.Kind}|{entry.Machine}|{entry.Server}";
            var now = DateTimeOffset.UtcNow;
            if (_lastSent.TryGetValue(key, out var last) && now - last < RepeatWindow) { continue; }
            _lastSent[key] = now;
            _ = Task.Run(() => DeliverAsync(channel, entry, CancellationToken.None));
        }
    }

    /// <summary>Sends one entry to one channel and records how it went. Returns the error, or null.</summary>
    public async Task<string?> DeliverAsync(NotificationChannel channel, NotificationEntry entry, CancellationToken token)
    {
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (SendOverride != null) { await SendOverride(channel, entry, timeout.Token); }
            else
            {
                object body = channel.Type == "discord" ? DiscordBody(channel, entry) : WebhookBody(entry);
                using var res = await _http.PostAsJsonAsync(channel.Url, body, timeout.Token);
                if (!res.IsSuccessStatusCode) { error = $"{(int)res.StatusCode} {res.ReasonPhrase}"; }
            }
        }
        catch (OperationCanceledException) { error = "Timed out."; }
        catch (Exception ex) { error = ex.Message; }

        lock (_gate)
        {
            var saved = _channels.FirstOrDefault(c => c.Id == channel.Id);
            if (saved != null)
            {
                if (error == null) { saved.LastSentAt = DateTimeOffset.UtcNow; }
                saved.LastError = error;
                SaveFile();
            }
        }
        if (error != null) { _log($"Notification channel \"{channel.Name}\" failed: {error}"); }
        return error;
    }

    private static readonly Dictionary<string, int> Colours = new() { ["bad"] = 0xF0506E, ["warn"] = 0xF5A524, ["good"] = 0x2DBD6E, ["info"] = 0x3B82F6 };

    private object DiscordBody(NotificationChannel channel, NotificationEntry e)
    {
        var fields = new List<object> { new { name = "Machine", value = _centre.MachineName(e.Machine), inline = true } };
        if (e.Server != null) { fields.Add(new { name = "Server", value = $"#{e.Server}{(e.ServerName != null ? " " + e.ServerName : "")}", inline = true }); }
        return new
        {
            username = "WindowsGSM",
            content = channel.Mention,
            allowed_mentions = new { parse = new[] { "everyone", "roles", "users" } },
            embeds = new[]
            {
                new
                {
                    title = e.Title,
                    description = e.Text,
                    color = Colours.GetValueOrDefault(e.Severity, Colours["info"]),
                    timestamp = e.At.ToString("o"),
                    fields,
                    footer = new { text = "WindowsGSM · " + (NotificationKinds.Get(e.Kind)?.Label ?? e.Kind) },
                },
            },
        };
    }

    private object WebhookBody(NotificationEntry e) => new
    {
        source = "WindowsGSM",
        kind = e.Kind,
        severity = e.Severity,
        title = e.Title,
        text = e.Text,
        at = e.At,
        machine = new { id = e.Machine, name = _centre.MachineName(e.Machine) },
        server = e.Server == null ? null : new { id = e.Server, name = e.ServerName },
    };

    private void SaveFile()
    {
        try
        {
            global::WindowsGSM.Hosting.SafeJson.Write(_file, _channels, Json);
        }
        catch (Exception ex) { _log($"Couldn't save notification channels: {ex.Message}"); }
    }

    private static NotificationChannel Clone(NotificationChannel c) => new()
    {
        Id = c.Id, Name = c.Name, Type = c.Type, Url = c.Url, Mention = c.Mention, Enabled = c.Enabled,
        Events = c.Events.ToList(), Scope = c.Scope.ToList(), LastSentAt = c.LastSentAt, LastError = c.LastError,
    };

    public void Dispose() => _centre.Added -= OnAdded;
}
