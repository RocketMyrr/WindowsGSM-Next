// wgsm-console-harness
//
//   wgsm-console-harness run <data folder> <results file>   drive the real engine through the console scenarios
//   wgsm-console-harness game stay|move                      the stand-in game the scenarios start
//
// Started windowless by ConsoleHostScenarioTests (as the launcher starts the agent), so the console handling
// (ConsoleHost) switches on exactly as in the agent. Each scenario writes "PASS name: detail" or "FAIL name: detail".

using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

return args.FirstOrDefault() switch
{
    "game" => StandIn.Run(args.ElementAtOrDefault(1) ?? "stay"),
    "run" when args.Length >= 3 => await Scenarios.RunAsync(args[1], args[2]),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("wgsm-console-harness run <data folder> <results file> | game stay|move");
    return 64;
}

/// <summary>
/// A console game. "stay" keeps the console it's started in; "move" behaves like Rust (Facepunch's console): a
/// moment in, it leaves that console and joins its parent's. Either way it reads commands key by key (Enter ends
/// one), logs them to harness-game.log in its working folder, and exits on "quit".
/// </summary>
internal static class StandIn
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    public static int Run(string mode)
    {
        string log = Path.Combine(Environment.CurrentDirectory, "harness-game.log");
        void Log(string line)
        {
            for (int i = 0; i < 20; i++)
            {
                try { File.AppendAllText(log, line + Environment.NewLine); return; }
                catch (IOException) { Thread.Sleep(25); }
            }
        }
        File.WriteAllText(log, "");
        // Like a server that saves and shuts down on Ctrl+C.
        Console.CancelKeyPress += (_, e) => { Log("ctrl-c: saved and stopping"); Environment.Exit(0); };
        Log($"bootstrap window={GetConsoleWindow().ToInt64()}");
        if (mode == "captured")
        {
            // Started with its output captured (no window): commands come in on its input.
            Log("ready window=0");
            string? input;
            while ((input = Console.In.ReadLine()) != null)
            {
                Log($"got: {input}");
                if (input.Trim() == "quit") { Log("quitting"); return 0; }
            }
            Thread.Sleep(Timeout.Infinite);
        }
        if (mode == "move")
        {
            Thread.Sleep(2000);
            FreeConsole();
            // Rust would open a console of its own here; the stand-in doesn't (no stray windows from a failed test).
            if (!AttachConsole(0xFFFFFFFF)) { Log($"no parent console at {DateTime.Now:HH:mm:ss.fff} error={Marshal.GetLastWin32Error()}"); return 2; }
            Log($"switched window={GetConsoleWindow().ToInt64()}");
        }
        Log($"ready window={GetConsoleWindow().ToInt64()}");

        var line = new System.Text.StringBuilder();
        while (true)
        {
            if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }
            var key = Console.ReadKey(intercept: true);
            if (key.Key != ConsoleKey.Enter) { if (key.KeyChar != '\0') { line.Append(key.KeyChar); } continue; }
            string command = line.ToString();
            line.Clear();
            Log($"got: {command}");
            if (command.Trim() == "quit") { Log("quitting"); return 0; }
        }
    }
}

internal static class Scenarios
{
    private const string GameName = "WindowsGSM Harness Game [HarnessGame.cs]";

    private static string _results = "";
    private static int _failures;

    private static void Result(string name, bool ok, string detail)
    {
        if (!ok) { _failures++; }
        File.AppendAllText(_results, $"{(ok ? "PASS" : "FAIL")} {name}: {detail}{Environment.NewLine}");
    }

    public static async Task<int> RunAsync(string root, string results)
    {
        _results = results;
        File.WriteAllText(_results, "");
        string trace = Path.ChangeExtension(_results, ".trace.txt");
        File.WriteAllText(trace, "");
        var traceLock = new object();
        ConsoleHost.Trace = line => { lock (traceLock) { File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} agent: {line}{Environment.NewLine}"); } };
        ConsoleHost.Enable();
        if (!ConsoleHost.Active)
        {
            File.AppendAllText(_results, "SKIP all: this harness has a visible console, so console hosting stays off" + Environment.NewLine);
            return 0;
        }

        if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        Directory.CreateDirectory(Path.Combine(root, "plugins", "HarnessGame.cs"));
        File.WriteAllText(Path.Combine(root, "plugins", "HarnessGame.cs", "HarnessGame.cs"), Plugin.Replace("{{HARNESS}}", Environment.ProcessPath!.Replace("\\", "\\\\")));
        Server(root, "1", "stay", 45010);
        Server(root, "2", "move", 45020);
        Server(root, "3", "move", 45030);
        Server(root, "4", "captured", 45040);
        Server(root, "5", "killer", 45050, "stopctrlcfirst=\"1\"");

        var engine = await WgsmEngine.StartAsync(root);
        try
        {
            if (!engine.Plugins.Plugins.Any(p => p.IsLoaded && p.FullName == GameName))
            {
                Result("setup", false, "the harness game plugin didn't load: " + string.Join("; ", engine.Plugins.Plugins.Select(p => $"{p.FileName}: {p.Error}")));
                return 1;
            }

            await Stays(engine);
            await Moves(engine);
            await TwoMoveTogether(engine);
            await PluginStyleKeys(engine);
            await CapturedCtrlC(engine);
            await CtrlCFirst(engine);
            engine = await AgentRestart(engine, root);
        }
        catch (Exception ex) { Result("harness", false, ex.ToString()); }
        finally
        {
            foreach (var s in engine.Servers.All) { try { s.Process?.Kill(entireProcessTree: true); } catch { } }
            engine.Dispose();
        }
        return _failures == 0 ? 0 : 1;
    }

    // ── Scenarios ──

    /// <summary>A console game that keeps its own console: a window, commands, the plugin's "quit".</summary>
    private static async Task Stays(WgsmEngine engine)
    {
        var s = engine.Servers.Get("1")!;
        if (!await Start(engine, "1")) { Result("stays/start", false, State(s)); return; }
        long window = await GameWindow("1", "ready");
        Result("stays/window", window != 0 && s.ConsoleWindow.ToInt64() == window && ConsoleHost.IsConsoleWindow(s.ConsoleWindow),
            $"game says {window}, WindowsGSM has {s.ConsoleWindow.ToInt64()}");
        Result("stays/main-window-handle", s.Process?.MainWindowHandle == s.ConsoleWindow, "plugins type their stop command into Process.MainWindowHandle");
        await engine.Console.SendAsync("1", "hello stays", "harness");
        Result("stays/command", await Got("1", "hello stays"), Log("1"));
        await StopCleanly(engine, "1", "stays");
    }

    /// <summary>A game that moves to its parent's console, like Rust: followed, then commands and "quit" reach it.</summary>
    private static async Task Moves(WgsmEngine engine)
    {
        var s = engine.Servers.Get("2")!;
        if (!await Start(engine, "2")) { Result("moves/start", false, State(s)); return; }
        long window = await GameWindow("2", "switched");
        bool followed = await Until(() => s.ConsoleWindow.ToInt64() == window, 5);
        Result("moves/followed", window != 0 && followed && ConsoleHost.IsConsoleWindow(s.ConsoleWindow), $"game moved to {window}, WindowsGSM has {s.ConsoleWindow.ToInt64()}");
        Result("moves/main-window-handle", s.Process?.MainWindowHandle == s.ConsoleWindow, "adopted after the move");
        await engine.Console.SendAsync("2", "hello moves", "harness");
        Result("moves/command", await Got("2", "hello moves"), Log("2"));
        await StopCleanly(engine, "2", "moves");
    }

    /// <summary>
    /// Two such games started close together (a second apart, as auto-start does) each end up in a console of
    /// their own. (Moving within the same millisecond as the other's hand-over can't be guarded against: a program
    /// is in one console at a time, so the agent changes consoles in two steps.)
    /// </summary>
    private static async Task TwoMoveTogether(WgsmEngine engine)
    {
        var first = Start(engine, "2");
        await Task.Delay(1000);
        var started = await Task.WhenAll(first, Start(engine, "3"));
        if (!started.All(x => x)) { Result("together/start", false, $"{State(engine.Servers.Get("2")!)} / {State(engine.Servers.Get("3")!)}"); return; }
        long w2 = await GameWindow("2", "switched"), w3 = await GameWindow("3", "switched");
        var s2 = engine.Servers.Get("2")!; var s3 = engine.Servers.Get("3")!;
        await Until(() => s2.ConsoleWindow.ToInt64() == w2 && s3.ConsoleWindow.ToInt64() == w3, 5);
        Result("together/own-consoles", w2 != 0 && w3 != 0 && w2 != w3 && s2.ConsoleWindow.ToInt64() == w2 && s3.ConsoleWindow.ToInt64() == w3,
            $"games in {w2} and {w3}; WindowsGSM has {s2.ConsoleWindow.ToInt64()} and {s3.ConsoleWindow.ToInt64()}");
        await engine.Console.SendAsync("2", "to two", "harness");
        await engine.Console.SendAsync("3", "to three", "harness");
        bool each = await Got("2", "to two") && await Got("3", "to three");
        bool crossed = Log("2").Contains("to three") || Log("3").Contains("to two");
        Result("together/commands", each && !crossed, $"2: {Log("2")} | 3: {Log("3")}");
        await StopCleanly(engine, "2", "together/stop-2");
        await StopCleanly(engine, "3", "together/stop-3");
    }

    /// <summary>
    /// Plugins that "press keys" (Conan Exiles, 7 Days to Die, Space Engineers, Bedrock): SetMainWindow +
    /// SendWaitToMainWindow reach the game's own console — text, {ENTER} and ^c — never the window in front.
    /// </summary>
    private static async Task PluginStyleKeys(WgsmEngine engine)
    {
        var s = engine.Servers.Get("1")!;
        if (!await Start(engine, "1")) { Result("keys/start", false, State(s)); return; }
        long window = await GameWindow("1", "ready");
        // As in real life, a plugin's keys come once WindowsGSM has set the window up (not the instant it started).
        await Until(() => s.ConsoleWindow.ToInt64() == window, 10);
        var p = s.Process!;
        await Task.Run(() => { ServerConsole.SetMainWindow(p.MainWindowHandle); ServerConsole.SendWaitToMainWindow("typed by a plugin{ENTER}"); });
        Result("keys/text-and-enter", await Got("1", "typed by a plugin"), Log("1"));
        await Task.Run(() => { ServerConsole.SetMainWindow(p.MainWindowHandle); ServerConsole.SendWaitToMainWindow("^c"); });
        Result("keys/ctrl-c", await Until(() => p.HasExited, 10), "Ctrl+C reached the game's console");
        await Until(() => s.State == ServerState.Stopped, 10);
    }

    /// <summary>
    /// A server with no window (its output captured) whose plugin stops it the common community way — "press" Ctrl+C
    /// on Process.MainWindowHandle, wait 2 s, kill. There's no window, so the keys used to go nowhere and it was killed
    /// without saving; now the Ctrl+C reaches its console.
    /// </summary>
    private static async Task CapturedCtrlC(WgsmEngine engine)
    {
        var s = engine.Servers.Get("4")!;
        if (!await Start(engine, "4")) { Result("captured/start", false, State(s)); return; }
        await GameWindow("4", "ready");
        var request = engine.Lifecycle.Stop("4");
        if (request.Accepted) { await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        Result("captured/ctrl-c-reached-it", Log("4").Contains("ctrl-c: saved and stopping"), Log("4"));
    }

    /// <summary>A plugin that just ends the process, with "Send Ctrl+C before the game's own stop" on: the game gets Ctrl+C first.</summary>
    private static async Task CtrlCFirst(WgsmEngine engine)
    {
        var s = engine.Servers.Get("5")!;
        if (!await Start(engine, "5")) { Result("ctrl-c-first/start", false, State(s)); return; }
        long window = await GameWindow("5", "ready");
        await Until(() => s.ConsoleWindow.ToInt64() == window, 10);
        var request = engine.Lifecycle.Stop("5");
        if (request.Accepted) { await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        Result("ctrl-c-first/saved", Log("5").Contains("ctrl-c: saved and stopping"), Log("5"));
    }

    /// <summary>The agent restarts while a moved game runs: it's re-adopted and its window found again.</summary>
    private static async Task<WgsmEngine> AgentRestart(WgsmEngine engine, string root)
    {
        if (!await Start(engine, "2")) { Result("restart/start", false, State(engine.Servers.Get("2")!)); return engine; }
        long window = await GameWindow("2", "switched");
        await Until(() => engine.Servers.Get("2")!.ConsoleWindow.ToInt64() == window, 5);
        int pid = engine.Servers.Get("2")!.Process!.Id;

        engine.Dispose(); // as when the agent stops: game servers keep running
        engine = await WgsmEngine.StartAsync(root);
        var s = engine.Servers.Get("2")!;
        Result("restart/reattached", s.State == ServerState.Running && s.Process?.Id == pid && s.ConsoleWindow.ToInt64() == window,
            $"{State(s)}, pid {s.Process?.Id} (was {pid}), window {s.ConsoleWindow.ToInt64()} (game's {window})");
        await engine.Console.SendAsync("2", "after restart", "harness");
        Result("restart/command", await Got("2", "after restart"), Log("2"));
        // The plugin asks p.StartInfo first: answered for a re-adopted server, so its clean "quit" is used.
        await StopCleanly(engine, "2", "restart/plugin-stop-after-restart");
        return engine;
    }

    // ── Helpers ──

    private static async Task<bool> Start(WgsmEngine engine, string id)
    {
        try { File.Delete(Path.Combine(Folder(id), "harness-game.log")); } catch { } // so nothing reads the last run's
        var request = engine.Lifecycle.Start(id);
        if (!request.Accepted) { return false; }
        await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        return engine.Servers.Get(id)!.State == ServerState.Running;
    }

    /// <summary>Stops through the plugin's own way (typing "quit"): it must exit by itself, quickly.</summary>
    private static async Task StopCleanly(WgsmEngine engine, string id, string name)
    {
        var watch = Stopwatch.StartNew();
        var request = engine.Lifecycle.Stop(id);
        if (request.Accepted) { await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(90)); }
        Result($"{name}/stop", Log(id).Contains("quitting") && watch.Elapsed < TimeSpan.FromSeconds(10),
            $"{watch.Elapsed.TotalSeconds:0.#} s; game log: {Log(id)}");
    }

    private static string Folder(string id) => ServerPath.GetServersServerFiles(id);

    private static string Log(string id)
    {
        try { return File.ReadAllText(Path.Combine(Folder(id), "harness-game.log")).Replace(Environment.NewLine, " | ").Trim(); }
        catch { return ""; }
    }

    /// <summary>Waits for the game's "<paramref name="stage"/> window=…" line and returns that window.</summary>
    private static async Task<long> GameWindow(string id, string stage)
    {
        long window = 0;
        await Until(() =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(Log(id), stage + @" window=(\d+)");
            return match.Success && long.TryParse(match.Groups[1].Value, out window);
        }, 20);
        return window;
    }

    private static Task<bool> Got(string id, string command) => Until(() => Log(id).Contains("got: " + command), 10);

    private static async Task<bool> Until(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) { return true; }
            await Task.Delay(50);
        }
        return condition();
    }

    private static string State(ServerInstance s) => $"#{s.Id} is {s.State}";

    private static void Server(string root, string id, string mode, int port, params string[] extra)
    {
        string configs = Path.Combine(root, "servers", id, "configs");
        Directory.CreateDirectory(configs);
        Directory.CreateDirectory(Path.Combine(root, "servers", id, "serverfiles"));
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[]
        {
            $"servergame=\"{GameName}\"", $"servername=\"Harness {id}\"", "serverip=\"127.0.0.1\"",
            $"serverport=\"{port}\"", $"serverqueryport=\"{port + 1}\"", "autorestart=\"0\"",
            "embedconsole=\"0\"", "showconsole=\"0\"", "stoptimeout=\"15\"", $"test_mode=\"{mode}\"",
        }.Concat(extra));
    }

    /// <summary>The scenarios' game: starts this harness in "game" mode, in its own console (no capture), and
    /// stops it the way most real plugins do — by typing "quit" into Process.MainWindowHandle.</summary>
    private const string Plugin = """
        using System.Diagnostics;
        using System.Threading.Tasks;
        using WindowsGSM.Functions;

        namespace WindowsGSM.Plugins
        {
            public class HarnessGame
            {
                public Plugin Plugin = new Plugin { name = "HarnessGame", author = "tests", description = "console harness game", version = "1", url = "", color = "#ffffff" };
                private readonly ServerConfig _serverData;
                public string Error, Notice;
                public string FullName = "WindowsGSM Harness Game";
                public string StartPath = "";
                public bool AllowsEmbedConsole = false;
                public int PortIncrements = 1;
                public object QueryMethod = null;
                public string Port = "45000", QueryPort = "45001", Defaultmap = "", Maxplayers = "1", Additional = "";

                public HarnessGame(ServerConfig serverData) { _serverData = serverData; }

                public async Task<Process> Start()
                {
                    string mode = Mode();
                    bool captured = mode == "captured";
                    var p = new Process
                    {
                        StartInfo =
                        {
                            FileName = "{{HARNESS}}", Arguments = "game " + (mode == "killer" ? "stay" : mode),
                            WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID), UseShellExecute = false,
                            // "captured": output captured, no window (as some plugins do whatever the setting says).
                            CreateNoWindow = captured, RedirectStandardInput = captured, RedirectStandardOutput = captured,
                        },
                        EnableRaisingEvents = true,
                    };
                    p.Start();
                    if (captured) { p.BeginOutputReadLine(); }
                    return p;
                }

                private string Mode() => _serverData.CustomSettings.TryGetValue("test_mode", out var m) ? m : "stay";

                public async Task Stop(Process p)
                {
                    string mode = Mode();
                    if (mode == "killer") { await Task.Run(() => p.Kill()); return; } // ends the process, like ARK's plugin
                    if (mode == "captured")
                    {
                        // The most common community plugin stop: "press" Ctrl+C on the window, wait a moment, kill.
                        await Task.Run(() =>
                        {
                            ServerConsole.SetMainWindow(p.MainWindowHandle);
                            ServerConsole.SendWaitToMainWindow("^c");
                            p.WaitForExit(2000);
                            if (!p.HasExited) { p.Kill(); }
                        });
                        return;
                    }
                    // Like Minecraft's and many others': stdin when captured, otherwise type into the window.
                    await Task.Run(() =>
                    {
                        if (p.StartInfo.RedirectStandardInput) { p.StandardInput.WriteLine("quit"); }
                        else { ServerConsole.SendMessageToMainWindow(p.MainWindowHandle, "quit"); }
                    });
                }

                public async Task<Process> Install() { return null; }
                public async Task<Process> Update(bool validate = false, string custom = null) { return null; }
                public bool IsInstallValid() => true;
                public bool IsImportValid(string path) => true;
                public string GetLocalBuild() => "";
                public async Task<string> GetRemoteBuild() => "";
            }
        }
        """;
}
