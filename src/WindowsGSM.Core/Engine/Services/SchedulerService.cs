#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NCrontab;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    public enum ScheduledAction { Restart, Start, Stop, Backup, Update, Command, Rcon, Exec }

    /// <summary>Where a schedule comes from.</summary>
    public enum ScheduleSource
    {
        /// <summary>The server's "restart on schedule" setting (restartcrontab + crontabformat in WindowsGSM.cfg).</summary>
        RestartSetting,
        /// <summary>A line in servers/{id}/configs/Crontab/*.csv (legacy format, still honoured).</summary>
        CrontabFile,
        /// <summary>servers/{id}/configs/schedules.json — managed from the UI.</summary>
        Managed,
    }

    /// <summary>One scheduled task. <see cref="Payload"/> is the command (or program for Exec); <see cref="Arguments"/> is Exec-only.</summary>
    public sealed record ScheduleEntry(string Cron, ScheduledAction Action, string Payload = "", string Arguments = "", bool Enabled = true)
    {
        [JsonIgnore] public ScheduleSource Source { get; init; } = ScheduleSource.Managed;
    }

    /// <summary>
    /// One scheduler for everything timed: scheduled restarts, start/stop, backups, updates, console and RCON
    /// commands, programs to run — plus the auto-update check and auto-start. Port of the legacy
    /// CrontabManager (per-server loop owned by the window) and StartAutoUpdateCheck.
    ///
    /// Compatibility: servers/{id}/configs/Crontab/*.csv files are read exactly as before, and — as in legacy —
    /// only run while the server's "restart on schedule" switch is on. Schedules created in the new UI live
    /// in schedules.json with their own on/off switch. Exec (run a program) is only accepted from the .csv
    /// files on disk, never from schedules.json, because that file is editable remotely.
    /// </summary>
    public sealed class SchedulerService : IDisposable
    {
        private const string ManagedFile = "schedules.json";
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
        };

        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly LifecycleService _lifecycle;
        private readonly UpdateService _updates;
        private readonly BackupService _backups;
        private readonly ConsoleService _console;
        private readonly PluginCatalog _plugins;
        private readonly ServerLog _log;
        private readonly Events.EventBus _events;

        private readonly ConcurrentDictionary<string, (string signature, List<ScheduleEntry> entries)> _cache =
            new ConcurrentDictionary<string, (string, List<ScheduleEntry>)>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _nextRun = new ConcurrentDictionary<string, DateTime>();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUpdateCheck = new ConcurrentDictionary<string, DateTimeOffset>();
        private CancellationTokenSource? _loop;

        public SchedulerService(ServerRegistry servers, OperationGate gate, JobManager jobs, LifecycleService lifecycle,
                                UpdateService updates, BackupService backups, ConsoleService console, PluginCatalog plugins, ServerLog log,
                                Events.EventBus events)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _lifecycle = lifecycle;
            _updates = updates;
            _backups = backups;
            _console = console;
            _plugins = plugins;
            _log = log;
            _events = events;
        }

        /// <summary>How often schedules are checked. The legacy loop checked every second.</summary>
        public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>How often running servers with auto-update are checked for a new build (legacy: 30 min).</summary>
        public TimeSpan AutoUpdateInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>Supplies "now" — overridable so tests can move time instead of waiting for it.</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        // ─────────────────────────────── Schedules ───────────────────────────────

        /// <summary>Every schedule for the server, from all three sources.</summary>
        public IReadOnlyList<ScheduleEntry> GetSchedules(string id) => Load(id);

        /// <summary>Replaces the UI-managed schedules. Rejects invalid cron expressions and Exec entries.</summary>
        public string? SaveManaged(string id, IEnumerable<ScheduleEntry> entries)
        {
            var list = entries.ToList();
            foreach (var e in list)
            {
                if (e.Action == ScheduledAction.Exec) { return "Running programs can only be scheduled from Crontab .csv files on the server itself."; }
                if (CrontabSchedule.TryParse(e.Cron) == null) { return $"\"{e.Cron}\" isn't a valid schedule (use cron format, e.g. \"0 6 * * *\" for 6am daily)."; }
                if ((e.Action == ScheduledAction.Command || e.Action == ScheduledAction.Rcon) && string.IsNullOrWhiteSpace(e.Payload)) { return "A command schedule needs a command."; }
            }
            string file = ServerPath.GetServersConfigs(id, ManagedFile);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(list, Json));
            File.Move(temp, file, overwrite: true);
            _cache.TryRemove(id, out _);
            return null;
        }

        /// <summary>
        /// Changes the legacy "restart on schedule" setting (crontabformat + restartcrontab in WindowsGSM.cfg) —
        /// the schedules page edits and removes it like any other schedule. Off also stops the Crontab .csv files,
        /// as in legacy.
        /// </summary>
        public string? SetRestartSetting(string id, string? cron, bool enabled)
        {
            var s = _servers.Get(id);
            if (s == null) { return "No such server."; }
            if (cron != null)
            {
                cron = cron.Trim();
                if (CrontabSchedule.TryParse(cron) == null) { return $"\"{cron}\" isn't a valid schedule (use cron format, e.g. \"0 6 * * *\" for 6am daily)."; }
                ServerConfig.SetSetting(id, ServerConfig.SettingName.CrontabFormat, cron);
            }
            ServerConfig.SetSetting(id, ServerConfig.SettingName.RestartCrontab, enabled ? "1" : "0");
            s.ReloadConfig();
            _cache.TryRemove(id, out _);
            return null;
        }

        /// <summary>When a schedule will next fire (null if its expression is invalid).</summary>
        public DateTime? NextOccurrence(ScheduleEntry entry, DateTime? after = null) =>
            CrontabSchedule.TryParse(entry.Cron)?.GetNextOccurrence(after ?? Clock());

        // ─────────────────────────────── Loop ───────────────────────────────

        public void Start()
        {
            if (_loop != null) { return; }
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await TickAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Debug.WriteLine($"[Scheduler] tick failed: {ex}"); }
                    try { await Task.Delay(Tick, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose()
        {
            _loop?.Cancel();
            _loop = null;
        }

        /// <summary>Starts every stopped server that has auto-start on (hosts call this once at startup).</summary>
        public void StartAutoStartServers()
        {
            foreach (var s in _servers.All.Where(s => s.State == ServerState.Stopped))
            {
                s.ReloadConfig();
                if (!s.Config.AutoStart) { continue; }
                var request = _lifecycle.Start(s.Id, StartReason.AutoStart);
                if (!request.Accepted) { _log.Write(s.Id, $"[NOTICE] Auto start skipped: {request.Error}"); }
            }
        }

        /// <summary>One scheduler pass. Public so hosts/tests can drive it without the timer.</summary>
        public async Task TickAsync()
        {
            DateTime now = Clock();
            foreach (var s in _servers.All)
            {
                var entries = Load(s.Id);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var schedule = CrontabSchedule.TryParse(e.Cron);
                    if (schedule == null) { continue; }

                    // Each entry remembers when it's next due; first sight just arms it (never fires "missed" runs).
                    string key = $"{s.Id}|{e.Source}|{i}|{e.Cron}|{e.Action}|{e.Payload}";
                    if (!_nextRun.TryGetValue(key, out DateTime due))
                    {
                        _nextRun[key] = schedule.GetNextOccurrence(now);
                        continue;
                    }
                    if (now < due) { continue; }
                    _nextRun[key] = schedule.GetNextOccurrence(now);

                    if (Active(s, e)) { await FireAsync(s, e).ConfigureAwait(false); }
                }

                await CheckAutoUpdateAsync(s).ConfigureAwait(false);
            }
        }

        private static bool Active(ServerInstance s, ScheduleEntry e) => e.Enabled && e.Source switch
        {
            ScheduleSource.Managed => true,
            _ => s.Config.RestartCrontab, // legacy: restart setting and .csv files only run with "restart on schedule" on
        };

        private async Task FireAsync(ServerInstance s, ScheduleEntry e)
        {
            bool running = s.State == ServerState.Running;
            string label = e.Action is ScheduledAction.Command or ScheduledAction.Rcon or ScheduledAction.Exec ? $"{e.Action}: {e.Payload}" : e.Action.ToString();
            OperationRequest? request = null;

            switch (e.Action)
            {
                case ScheduledAction.Restart:
                    if (!running) { return; }
                    _log.Write(s.Id, "Schedule: Restart");
                    request = _lifecycle.Restart(s.Id);
                    AlertWhenDone(request, s, Events.AlertKind.ScheduledRestart, $"{s.Name} restarted", "Scheduled restart completed.");
                    break;
                case ScheduledAction.Start:
                    if (s.State != ServerState.Stopped) { return; }
                    _log.Write(s.Id, "Schedule: Start");
                    request = _lifecycle.Start(s.Id, StartReason.AutoStart);
                    break;
                case ScheduledAction.Stop:
                    if (!running) { return; }
                    _log.Write(s.Id, "Schedule: Stop");
                    request = _lifecycle.Stop(s.Id);
                    break;
                case ScheduledAction.Backup:
                    _log.Write(s.Id, "Schedule: Backup");
                    request = _backups.Backup(s.Id);
                    break;
                case ScheduledAction.Update:
                    if (UpdateService.IsHeld(s)) { _log.Write(s.Id, "Schedule: Update skipped — updates are on hold (rolled back)."); return; }
                    _log.Write(s.Id, "Schedule: Update");
                    request = UpdateAndRestart(s.Id, "Scheduled update");
                    break;
                case ScheduledAction.Command:
                case ScheduledAction.Rcon:
                    if (!running) { return; }
                    var result = await _console.SendAsync(s.Id, e.Payload, "Schedule", preferRcon: e.Action == ScheduledAction.Rcon).ConfigureAwait(false);
                    if (!result.Sent) { _log.Write(s.Id, $"[ERROR] Scheduled {label} failed: {result.Error}"); }
                    else if (!string.IsNullOrWhiteSpace(result.Reply)) { _log.Write(s.Id, $"RCON response: {result.Reply}"); }
                    return;
                case ScheduledAction.Exec:
                    if (!running || e.Source == ScheduleSource.Managed) { return; } // exec only from files on disk
                    _log.Write(s.Id, $"Execute Scedules: {e.Payload}");
                    _ = Task.Run(() => RunProgram(s.Id, e.Payload, e.Arguments));
                    return;
            }

            if (request is { Accepted: false }) { _log.Write(s.Id, $"[NOTICE] Scheduled {label} skipped: {request.Error}"); }
        }

        // ─────────────────────────────── Auto update ───────────────────────────────

        /// <summary>
        /// Legacy auto-update: every 30 minutes, a running server with auto-update on is checked; if a newer build
        /// (or a Steam branch change) is waiting, it's stopped, updated and started again.
        /// </summary>
        private async Task CheckAutoUpdateAsync(ServerInstance s)
        {
            if (s.State != ServerState.Running || !s.Config.AutoUpdate || UpdateService.IsHeld(s)) { return; }
            var now = DateTimeOffset.Now;
            if (!_lastUpdateCheck.TryGetValue(s.Id, out var last)) { _lastUpdateCheck[s.Id] = now; return; } // wait one interval after start
            if (now - last < AutoUpdateInterval) { return; }
            _lastUpdateCheck[s.Id] = now;

            var check = await _updates.CheckAsync(s.Id).ConfigureAwait(false);
            if (check.Error != null) { _log.Write(s.Id, $"[NOTICE] Auto update check failed: {check.Error}"); return; }
            if (!check.BranchChangePending && (check.LocalBuild == null || check.RemoteBuild == null)) { return; }
            _log.Write(s.Id, $"Checking: build ({check.LocalBuild}) => ({check.RemoteBuild})");
            if (!check.UpdateAvailable) { return; }
            string? remote = check.RemoteBuild;

            var request = UpdateAndRestart(s.Id, "Auto update");
            if (!request.Accepted) { _log.Write(s.Id, $"[NOTICE] Auto update skipped: {request.Error}"); }
            AlertWhenDone(request, s, Events.AlertKind.AutoUpdated, $"{s.Name} updated", $"Auto update installed build {remote}.");
        }

        /// <summary>Publishes an alert once <paramref name="request"/>'s job succeeds (for notifications).</summary>
        private void AlertWhenDone(OperationRequest request, ServerInstance s, Events.AlertKind kind, string title, string text)
        {
            if (!request.Accepted || request.Job == null) { return; }
            _ = request.Job.Completion.ContinueWith(t =>
            {
                if (t.Result.Status == JobStatus.Succeeded) { _events.Publish(new Events.ServerAlert(s.Id, kind, title, text)); }
            }, TaskScheduler.Default);
        }

        /// <summary>Stops the server if it's running, updates it, and starts it again if it was running — as one job.</summary>
        public OperationRequest UpdateAndRestart(string id, string reason)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Running && s.State != ServerState.Stopped) { return OperationRequest.Rejected($"{s.Name} is busy ({s.State})."); }
            if (!_gate.TryBegin(id, OperationKind.Update, reason, out var lease, out var blockedBy)) { return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}."); }

            return OperationRequest.Running(_jobs.Start("update", id, $"{reason}: {s.Name}", async ctx =>
            {
                using (lease)
                {
                    bool wasRunning = s.State == ServerState.Running;
                    if (wasRunning)
                    {
                        string? stopError = await _lifecycle.StopCoreAsync(s, ctx).ConfigureAwait(false);
                        if (stopError != null) { return stopError; }
                    }
                    string? error = await _updates.UpdateCoreAsync(s, validate: false, ctx, notes: $" | {reason}").ConfigureAwait(false);
                    if (wasRunning)
                    {
                        // Start again even if the update failed — the server was running before we touched it.
                        string? startError = await _lifecycle.StartCoreAsync(s, StartReason.Restart, ctx).ConfigureAwait(false);
                        error ??= startError;
                    }
                    return error;
                }
            }));
        }

        // ─────────────────────────────── Loading ───────────────────────────────

        private List<ScheduleEntry> Load(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return new List<ScheduleEntry>(); }
            string crontabDir = ServerPath.GetServersConfigs(id, "Crontab");
            string managed = ServerPath.GetServersConfigs(id, ManagedFile);

            // Cheap change detection: re-read only when a source file or the server's config changed.
            string cfgFile = ServerPath.GetServersConfigs(id, "WindowsGSM.cfg");
            var files = Directory.Exists(crontabDir) ? Directory.GetFiles(crontabDir, "*.csv", SearchOption.AllDirectories) : Array.Empty<string>();
            string signature = string.Join("|", files.Select(f => f + "@" + File.GetLastWriteTimeUtc(f).Ticks))
                               + "|" + (File.Exists(managed) ? File.GetLastWriteTimeUtc(managed).Ticks : 0)
                               + "|" + (File.Exists(cfgFile) ? File.GetLastWriteTimeUtc(cfgFile).Ticks : 0);
            if (_cache.TryGetValue(id, out var cached) && cached.signature == signature) { return cached.entries; }

            s.ReloadConfig(); // settings such as "restart on schedule" may have been edited
            var entries = new List<ScheduleEntry>();
            // Legacy filled crontabformat in for every server with the switch off; Enabled shows whether it (and the
            // .csv files, which follow the same switch) will actually run.
            bool legacyOn = s.Config.RestartCrontab;
            if (!string.IsNullOrWhiteSpace(s.Config.CrontabFormat))
            {
                entries.Add(new ScheduleEntry(s.Config.CrontabFormat, ScheduledAction.Restart, Enabled: legacyOn) { Source = ScheduleSource.RestartSetting });
            }
            foreach (string file in files)
            {
                try { entries.AddRange(ParseCrontabFile(File.ReadAllLines(file)).Select(e => e with { Enabled = e.Enabled && legacyOn })); }
                catch (Exception ex) { _log.Write(id, $"[NOTICE] Couldn't read {Path.GetFileName(file)}: {ex.Message}"); }
            }
            if (File.Exists(managed))
            {
                try
                {
                    var list = JsonConvert.DeserializeObject<List<ScheduleEntry>>(File.ReadAllText(managed), Json) ?? new List<ScheduleEntry>();
                    entries.AddRange(list.Where(e => e.Action != ScheduledAction.Exec).Select(e => e with { Source = ScheduleSource.Managed }));
                }
                catch (Exception ex) { _log.Write(id, $"[NOTICE] Couldn't read {ManagedFile}: {ex.Message}"); }
            }

            _cache[id] = (signature, entries);
            return entries;
        }

        /// <summary>Legacy Crontab .csv format: "cron;type;command[;arguments]", "//" comments, "cron;restart".</summary>
        internal static IEnumerable<ScheduleEntry> ParseCrontabFile(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) { continue; }
                string[] tokens = line.Split(';');
                if (tokens.Length < 2 || CrontabSchedule.TryParse(tokens[0].Trim()) == null) { continue; }
                string cron = tokens[0].Trim();
                string type = tokens[1].Trim().ToLowerInvariant();

                // Everything after the Nth ';' belongs to the command — commands may contain ';' themselves.
                string After(int n)
                {
                    int count = 0;
                    for (int i = 0; i < line.Length; i++) { if (line[i] == ';' && ++count == n) { return line.Substring(i + 1); } }
                    return string.Empty;
                }

                switch (type)
                {
                    case "restart": yield return new ScheduleEntry(cron, ScheduledAction.Restart) { Source = ScheduleSource.CrontabFile }; break;
                    case "serverconsolecommand": yield return new ScheduleEntry(cron, ScheduledAction.Command, After(2)) { Source = ScheduleSource.CrontabFile }; break;
                    case "rconcommand": yield return new ScheduleEntry(cron, ScheduledAction.Rcon, After(2)) { Source = ScheduleSource.CrontabFile }; break;
                    case "exec" when tokens.Length >= 3:
                        yield return new ScheduleEntry(cron, ScheduledAction.Exec, tokens[2].Trim(), tokens.Length >= 4 ? After(3) : string.Empty) { Source = ScheduleSource.CrontabFile };
                        break;
                }
            }
        }

        /// <summary>Runs a scheduled program, appending its output to logs/Server_{id}_{program}_execLog.log (legacy location).</summary>
        private void RunProgram(string id, string program, string arguments)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo =
                    {
                        FileName = program,
                        Arguments = arguments,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                string name = string.Concat(Path.GetFileName(program).Split(Path.GetInvalidFileNameChars()));
                File.AppendAllText(ServerPath.GetLogs($"Server_{id}_{name}_execLog.log"), output);
            }
            catch (Exception ex) { _log.Write(id, $"[ERROR] Scheduled program {program} failed: {ex.Message}"); }
        }
    }
}
