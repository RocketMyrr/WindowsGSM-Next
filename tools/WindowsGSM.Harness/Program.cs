using System.Collections;
using System.Globalization;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

// wgsm-harness — drive the WindowsGSM Next engine by hand.
//
//   wgsm-harness <data folder> [--autostart] [--background]
//
// Point it at a COPY of a WindowsGSM data folder while trying things out. It refuses to open a folder
// that another WindowsGSM (legacy or Next) is managing.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: wgsm-harness <data folder> [--autostart] [--background]");
    Console.WriteLine("  --background  run the monitor and scheduler loops (players, health, schedules, auto-update)");
    Console.WriteLine("  --autostart   also start servers that have auto-start on (implies --background)");
    return 1;
}

bool autoStart = args.Contains("--autostart");
bool background = autoStart || args.Contains("--background");

WgsmEngine engine;
try { engine = await WgsmEngine.StartAsync(args[0]); }
catch (DataRootInUseException ex) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(ex.Message); Console.ResetColor(); return 2; }

using (engine)
{
    string? watchConsole = null; // server id whose game console is being streamed
    engine.Events.Subscribe(e =>
    {
        switch (e)
        {
            case ServerStateChanged s: Say(ConsoleColor.Cyan, $"#{s.ServerId} {s.From} → {s.To}"); break;
            case ServerLogged l when l.ServerId != "System" || l.Level != LogLevel.Info:
                Say(l.Level == LogLevel.Error ? ConsoleColor.Red : l.Level == LogLevel.Notice ? ConsoleColor.Yellow : ConsoleColor.DarkGray, $"[{l.ServerId}] {l.Message}");
                break;
            case ServerAlert a: Say(ConsoleColor.Magenta, $"⚑ {a.Title} — {a.Text}"); break;
            case ConsoleLineAdded c when c.ServerId == watchConsole: Say(ConsoleColor.Gray, $"  │ {c.Line}"); break;
            case JobChanged j when j.Job.Status != JobStatus.Running:
                Say(j.Job.Status == JobStatus.Succeeded ? ConsoleColor.Green : ConsoleColor.Red, $"✔ job {j.Job.Title}: {j.Job.Status}{(j.Job.Error != null ? " — " + j.Job.Error : "")}");
                break;
            case JobChanged j when j.Job.Percent is int pct && j.Job.Stage != null:
                Say(ConsoleColor.DarkCyan, $"  … {j.Job.Title}: {j.Job.Stage} {pct}%");
                break;
        }
    });

    if (background) { engine.StartBackgroundServices(autoStartServers: autoStart); }

    Say(ConsoleColor.White, $"WindowsGSM Next {WgsmEnvironment.Version} — data: {WgsmEnvironment.DataRoot}");
    Say(ConsoleColor.White, $"{engine.Servers.All.Count} server(s), {engine.Plugins.Plugins.Count(p => p.IsLoaded)} plugin(s). Type 'help'.");
    Help();

    while (true)
    {
        Console.Write("> ");
        string? line = Console.ReadLine();
        if (line == null) { break; }
        var words = Split(line);
        if (words.Count == 0) { continue; }
        string cmd = words[0].ToLowerInvariant();
        string Arg(int i) => words.Count > i ? words[i] : string.Empty;

        try
        {
            switch (cmd)
            {
                case "help": Help(); break;
                case "quit" or "exit": return 0;
                case "list": List(); break;
                case "games": Games(); break;
                case "start": Report(engine.Lifecycle.Start(Arg(1))); break;
                case "stop": Report(engine.Lifecycle.Stop(Arg(1))); break;
                case "restart": Report(engine.Lifecycle.Restart(Arg(1))); break;
                case "kill": Report(engine.Lifecycle.Kill(Arg(1))); break;
                case "update": Report(engine.Updates.Update(Arg(1))); break;
                case "validate": Report(engine.Updates.Update(Arg(1), validate: true)); break;
                case "backup": Report(engine.Backups.Backup(Arg(1), everything: words.Contains("--all"))); break;
                case "backups":
                    foreach (var b in engine.Backups.List(Arg(1))) { Console.WriteLine($"  {b.Name,-45} {b.Size / 1048576.0,8:0.0} MB  {b.Created:yyyy-MM-dd HH:mm}  {b.Format}"); }
                    break;
                case "restore": Report(engine.Backups.Restore(Arg(1), Arg(2), includeConfig: words.Contains("--config"))); break;
                case "delete": Report(engine.Provisioning.Delete(Arg(1))); break;
                case "install":
                    Report(engine.Provisioning.Install(new InstallRequest(Arg(1), Arg(2),
                        SteamBranch: words.FirstOrDefault(w => w.StartsWith("--branch="))?.Substring(9),
                        Consents: words.Contains("--eula") ? new[] { UserPrompt.Keys.Eula, UserPrompt.Keys.InstallJava } : null)));
                    break;
                case "import": Report(engine.Provisioning.Import(Arg(1), Arg(2), Arg(3))); break;
                case "console":
                    watchConsole = Arg(1);
                    foreach (string l in engine.Servers.Get(watchConsole)?.Console.Get().Split('\n').TakeLast(20) ?? Array.Empty<string>()) { Say(ConsoleColor.Gray, "  │ " + l.TrimEnd('\r')); }
                    Say(ConsoleColor.White, $"Streaming #{watchConsole}'s console. 'console off' to stop.");
                    if (Arg(1) == "off") { watchConsole = null; }
                    break;
                case "cmd":
                case "rcon":
                    var result = await engine.Console.SendAsync(Arg(1), string.Join(' ', words.Skip(2)), "harness", preferRcon: cmd == "rcon");
                    Say(result.Sent ? ConsoleColor.Green : ConsoleColor.Red, result.Sent ? $"sent via {result.Route}" : result.Error ?? "failed");
                    if (!string.IsNullOrWhiteSpace(result.Reply)) { Console.WriteLine(result.Reply); }
                    break;
                case "jobs":
                    foreach (var j in engine.Jobs.Snapshot().Take(15)) { Console.WriteLine($"  {j.Id}  {j.Status,-9} {j.Percent,3}%  {j.Title}  {j.Stage}"); }
                    break;
                case "cancel": Say(ConsoleColor.Yellow, engine.Jobs.Cancel(Arg(1)) ? "cancelling" : "no such running job"); break;
                case "wait": await Task.Delay(TimeSpan.FromSeconds(double.TryParse(Arg(1), out double sec) ? sec : 1)); break; // for scripted sessions
                case "schedules":
                    foreach (var s in engine.Scheduler.GetSchedules(Arg(1)))
                    {
                        Console.WriteLine($"  {s.Cron,-15} {s.Action,-8} {s.Payload,-30} {s.Source}{(s.Enabled ? "" : " (off)")}  next: {engine.Scheduler.NextOccurrence(s):g}");
                    }
                    break;
                case "addons":
                    foreach (var a in engine.Addons.List(Arg(1))) { Console.WriteLine($"  {a.Key,-12} {a.Label,-24} present={a.Present} managed={a.Managed}"); }
                    foreach (var c in engine.Addons.ListCustom(Arg(1))) { Console.WriteLine($"  custom {c.Id}  {c.Name}  {c.Url}"); }
                    break;
                default: Say(ConsoleColor.Yellow, "Unknown command. Type 'help'."); break;
            }
        }
        catch (Exception ex) { Say(ConsoleColor.Red, ex.Message); }
    }
    return 0;

    void List()
    {
        foreach (var s in engine.Servers.All)
        {
            var sample = engine.Monitor.Latest(s.Id);
            string live = sample == null ? "" : $"  cpu {sample.CpuPercent,5:0.0}%  ram {sample.MemoryMb,6:0} MB  players {(sample.Players?.ToString() ?? "-")}/{(sample.MaxPlayers?.ToString() ?? "-")}";
            Console.WriteLine($"  #{s.Id,-4} {s.State,-11} {s.Name,-28} {s.Game}{live}");
        }
    }

    void Games()
    {
        var builtIn = WindowsGSM.GameServer.Data.Icon.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, true)!
            .Cast<DictionaryEntry>().Select(e => (string)e.Key);
        foreach (var g in builtIn.Concat(engine.Plugins.Plugins.Where(p => p.IsLoaded).Select(p => p.FullName)).OrderBy(g => g)) { Console.WriteLine("  " + g); }
    }
}

static void Report(OperationRequest r) =>
    Say(r.Accepted ? ConsoleColor.Green : ConsoleColor.Red, r.Accepted ? $"started job {r.Job!.Id}: {r.Job.Title}" : r.Error ?? "refused");

static void Say(ConsoleColor color, string text)
{
    lock (Console.Out)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

static void Help() => Console.WriteLine("""
  list | games | jobs | cancel <job>
  start|stop|restart|kill|update|validate|delete <id>
  backup <id> [--all] | backups <id> | restore <id> <backup name> [--config]
  install "<game>" "<name>" [--eula] [--branch=<branch>] | import "<game>" "<name>" "<folder>"
  console <id> | console off | cmd <id> <command> | rcon <id> <command>
  schedules <id> | addons <id> | help | quit
""");

static List<string> Split(string line)
{
    var result = new List<string>();
    var current = new System.Text.StringBuilder();
    bool quoted = false;
    foreach (char c in line)
    {
        if (c == '"') { quoted = !quoted; continue; }
        if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); } continue; }
        current.Append(c);
    }
    if (current.Length > 0) { result.Add(current.ToString()); }
    return result;
}
