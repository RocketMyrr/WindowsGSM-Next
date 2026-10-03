using WindowsGSM.Agent;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Engine;
using WindowsGSM.Hosting;

// wgsm-agent — runs game servers for this machine and serves the web panel + v2 API.
//
//   wgsm-agent [--data <folder>] [--no-autostart]
//   wgsm-agent --register-startup [--data <folder>]     start with Windows sign-in (and restart if it stops)
//   wgsm-agent --unregister-startup
//   wgsm-agent --check-data <folder> [--json]            dry run: what switching this folder to Next means
//   wgsm-agent --reset-password <user> [--disable-2fa] [--data <folder>]   can't sign in: a new temporary password
//
// --data defaults to the agent's own folder, which is where the legacy app keeps its data. While both apps
// are in use, point the agent at a separate COPY: two managers must never drive the same servers (the agent
// refuses to open a folder that a running WindowsGSM is using).

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: wgsm-agent [--data <folder>] [--no-autostart] | --register-startup [--data <folder>] | --unregister-startup | --check-data <folder> [--json] | --reset-password <user> [--disable-2fa] [--data <folder>]");
    return 0;
}

if (Arg("--check-data") is { } checkFolder)
{
    var report = await DataCheck.RunAsync(checkFolder);
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(args.Contains("--json")
        ? System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true })
        : DataCheck.Format(report));
    return report.CanAdopt ? 0 : 1;
}

if (Arg("--reset-password") is { } resetUser)
{
    string root = Path.GetFullPath(Arg("--data") ?? PasswordReset.InstalledDataRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory);
    var reset = PasswordReset.Run(root, resetUser, args.Contains("--disable-2fa"));
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(reset.Message);
    return reset.Ok ? 0 : 1;
}

string dataRoot = Path.GetFullPath(Arg("--data") ?? AppContext.BaseDirectory);

if (args.Contains("--register-startup"))
{
    string? problem = StartupTask.Register(Environment.ProcessPath ?? "wgsm-agent.exe", dataRoot);
    Console.WriteLine(problem ?? $"Registered: the agent starts when {Environment.UserName} signs in (data: {dataRoot}).");
    return problem == null ? 0 : 1;
}
if (args.Contains("--unregister-startup"))
{
    string? problem = StartupTask.Unregister();
    Console.WriteLine(problem ?? "The agent no longer starts at sign-in.");
    return problem == null ? 0 : 1;
}

void Say(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

// A crash of the agent itself goes to logs\CRASH_<date>.log (as the legacy app did), and is reported in the
// notification centre on the next start. Game servers keep running either way.
CrashLog.Install(dataRoot, "agent");

// Game servers whose console isn't captured get a console window of their own that "Show the console window"
// can show and hide (an agent run from a terminal keeps sharing that terminal instead).
WindowsGSM.Functions.ConsoleHost.Enable();

WgsmEngine engine;
try { engine = await WgsmEngine.StartAsync(dataRoot); }
catch (DataRootInUseException ex) { Say(ex.Message); return 2; }

using (engine)
{
    // Everything the agent says also goes to the daily log (logs\L<date>.log) — an installed agent has no
    // console. Its own lines are "[Agent]"; the Discord bot's "[Discord]" (it also keeps its own file).
    engine.Events.Subscribe<WindowsGSM.Engine.Events.ServerLogged>(l => Say($"[{l.ServerId}] {l.Message}"));
    void Log(string message) => engine.Log.Write(message.StartsWith("Discord bot:") ? "Discord" : "Agent", message);
    Log($"WindowsGSM agent {WgsmEnvironment.Version} started — data: {WgsmEnvironment.DataRoot}");
    Log($"{engine.Servers.All.Count} server(s), {engine.Plugins.Plugins.Count(p => p.IsLoaded)} plugin(s).");

    WebApplication app;
    try { app = await AgentApp.BuildAsync(engine, new AgentAppOptions { Log = Log }); }
    catch (Exception ex) { Log("Couldn't start the web API: " + ex.Message); return 3; }

    var settings = app.Services.GetRequiredService<WindowsGSM.Agent.Api.AgentContext>().Settings;
    try { await app.StartAsync(); }
    catch (Exception ex)
    {
        Log($"Couldn't listen on port {settings.Port}: {ex.Message}");
        return 4;
    }

    bool https = app.Services.GetService<CertificateService>()?.Current != null;
    string host = settings.ExposeToNetwork ? Environment.MachineName : "localhost";
    Log($"Web panel: {(https ? "https" : "http")}://{host}:{settings.Port}/  (machine id {settings.MachineId})");
    if (settings.ExposeToNetwork || settings.AcmeEnabled)
    {
        string? hint = await Firewall.EnsureAsync(Firewall.AgentRule, settings.Port);
        if (hint != null) { Log(hint); }
    }

    engine.StartBackgroundServices(autoStartServers: !args.Contains("--no-autostart"));
    CrashLog.ReportPrevious(dataRoot, app.Services.GetRequiredService<WindowsGSM.Agent.Notifications.NotificationCentre>(), settings.MachineId);

    var stopping = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.TrySetResult(); };
    app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
    await stopping.Task;

    Log("Stopping… (game servers keep running and are re-adopted next start)");
    await app.StopAsync();
    if (app.Services.GetService<CertificateService>() is { } certs) { await certs.DisposeAsync(); }
    await app.DisposeAsync();
}
return 0;
