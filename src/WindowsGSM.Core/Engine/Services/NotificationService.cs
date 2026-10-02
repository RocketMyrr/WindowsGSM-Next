#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>A notification ready to send: a status card (<see cref="Status"/> set) or plain text.</summary>
    public sealed record Notification(string ServerId, string WebhookUrl, string CustomMessage, bool SkipUserSetup,
                                      string? Status, string? PlainText, string Game, string Name, string Ip, string Port);

    /// <summary>
    /// Sends Discord notifications for server events, using each server's existing alert settings
    /// (DiscordAlert, DiscordWebhook, DiscordMessage and the per-event switches). Port of the webhook calls
    /// scattered through the legacy MainWindow, CrontabManager and ServerConsole.
    ///
    /// NEXT: repeats are throttled per server and per kind of alert. Legacy used app-wide timestamps, so one
    /// server's crash could silence the alert for a different server crashing shortly after — and the
    /// window restarted on every suppressed alert, so a server crashing every 20 s never alerted again.
    /// </summary>
    public sealed class NotificationService : IDisposable
    {
        private readonly ServerRegistry _servers;
        private readonly IDisposable _subscription;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new ConcurrentDictionary<string, DateTimeOffset>();

        public NotificationService(ServerRegistry servers, EventBus events)
        {
            _servers = servers;
            _subscription = events.Subscribe<ServerAlert>(OnAlert);
        }

        /// <summary>Minimum time between two alerts of the same kind for the same server (legacy: 30 s).</summary>
        public TimeSpan RepeatWindow { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Delivers a notification. Defaults to the legacy Discord webhook sender; swappable for tests.</summary>
        public Func<Notification, Task> Sender { get; set; } = SendToDiscordAsync;

        private void OnAlert(ServerAlert alert)
        {
            var s = _servers.Get(alert.ServerId);
            if (s == null) { return; }
            var cfg = s.Config;
            if (!cfg.DiscordAlert || string.IsNullOrWhiteSpace(cfg.DiscordWebhook)) { return; }

            // Which switch governs which alert, and what legacy sent for it.
            (bool enabled, string? status, string? text) = alert.Kind switch
            {
                AlertKind.Crashed => (cfg.CrashAlert, "Crashed", (string?)null),
                AlertKind.AutoRestarted => (cfg.AutoRestartAlert, "Restarted | Auto Restart", null),
                AlertKind.AutoStarted => (cfg.AutoStartAlert, "Started | Auto Start", null),
                AlertKind.ScheduledRestart => (cfg.RestartCrontabAlert, "Restarted | Restart Crontab", null),
                AlertKind.AutoUpdated => (cfg.AutoUpdateAlert, "Updated | Auto Update", null),
                AlertKind.CrashLoopSuspended => (cfg.CrashAlert, null,
                    $":octagonal_sign: **{s.Name}** keeps crashing. Auto-restart has been **suspended** — please check the server. ({alert.Text})"),
                AlertKind.MemoryGuard => (cfg.AutoRestartAlert, null, $":warning: **{s.Name}**: {alert.Text} Restarting to reclaim memory."),
                AlertKind.JoinCode => (cfg.AutoStartAlert || cfg.AutoRestartAlert || cfg.RestartCrontabAlert, null,
                    $"Server {s.Id}, {s.Name}:  Join Code Found: {alert.Text}"),
                _ => (false, null, null),
            };
            if (!enabled) { return; }

            string key = $"{s.Id}|{alert.Kind}";
            var now = DateTimeOffset.UtcNow;
            if (_lastSent.TryGetValue(key, out var last) && now - last < RepeatWindow) { return; }
            _lastSent[key] = now; // only a sent alert starts a new window

            var n = new Notification(s.Id, cfg.DiscordWebhook, cfg.DiscordMessage ?? string.Empty, cfg.SkipUserSetup,
                                     status, text, s.Game, s.Name, cfg.ServerIP ?? string.Empty, cfg.ServerPort ?? string.Empty);
            // Off the publishing thread: the event bus must never wait on the network.
            _ = Task.Run(async () =>
            {
                try { await Sender(n).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[Notifications] {s.Id} {alert.Kind}: {ex.Message}"); }
            });
        }

        private static Task SendToDiscordAsync(Notification n)
        {
            var webhook = new DiscordWebhook(n.WebhookUrl, n.CustomMessage, string.Empty, n.SkipUserSetup);
            return n.Status != null
                ? webhook.Send(n.ServerId, n.Game, n.Status, n.Name, n.Ip, n.Port)
                : webhook.SendPlain(n.PlainText ?? string.Empty);
        }

        public void Dispose() => _subscription.Dispose();
    }
}
