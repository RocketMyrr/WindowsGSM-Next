using System.Diagnostics;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Watchdog;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Real engine + real processes. A throwaway game plugin is installed into the data folder's plugins/
/// directory (loaded by the same compiler community plugins go through); its "game server" is ping.exe,
/// so tests can start, stop, crash and reattach real processes without any game installed.
/// </summary>
internal static class EngineFixture
{
    public const string GameName = "WindowsGSM Test Game [WgsmTestGame.cs]";

    private const string PluginSource = """
        using System.Collections.Generic;
        using System.Diagnostics;
        using System.Threading.Tasks;
        using WindowsGSM.Functions;
        using WindowsGSM.GameServer.Query;

        namespace WindowsGSM.Plugins
        {
            // A game query that either answers (3 of 10 players) or never answers (a hung server).
            public class FakeQuery : QueryTemplate
            {
                private readonly bool _answers;
                public FakeQuery(bool answers) { _answers = answers; }
                public void SetAddressPort(string address, int port, int timeout = 5) { }
                public Task<Dictionary<string, string>> GetInfo() => Task.FromResult<Dictionary<string, string>>(null);
                public Task<string> GetPlayersAndMaxPlayers() => Task.FromResult(_answers ? "3/10" : null);
                public Task<List<PlayerData>> GetPlayersData() => Task.FromResult(_answers
                    ? new List<PlayerData> { new PlayerData(1, "Alice"), new PlayerData(2, "Bob"), new PlayerData(3, "Cara") }
                    : null);
            }

            public class WgsmTestGame
            {
                public Plugin Plugin = new Plugin { name = "WgsmTestGame", author = "tests", description = "test game", version = "1", url = "", color = "#ffffff" };
                private readonly ServerConfig _serverData;
                public string Error, Notice;
                public string FullName = "WindowsGSM Test Game";
                public string StartPath = "";
                public bool AllowsEmbedConsole = true;
                public int PortIncrements = 1;
                public object QueryMethod = null;
                public string AppId;
                public string Port = "40000", QueryPort = "40001", Defaultmap = "", Maxplayers = "1", Additional = "";

                public WgsmTestGame(ServerConfig serverData)
                {
                    _serverData = serverData;
                    string mode = serverData != null && serverData.CustomSettings.TryGetValue("test_mode", out var m) ? m : "long";
                    if (mode == "players") { QueryMethod = new FakeQuery(true); }
                    if (mode == "hung") { QueryMethod = new FakeQuery(false); }
                    if (mode == "a2s") { QueryMethod = new A2S(); }
                    if (mode == "steam") { AppId = "4000"; } // a Steam game (roll back)
                }

                // test_mode: "long" (runs ~2 min), "crash" (exits after ~2 s), "fail" (refuses to start)
                public async Task<Process> Start()
                {
                    string mode = _serverData.CustomSettings.TryGetValue("test_mode", out var m) ? m : "long";
                    if (mode == "fail") { Error = "Test plugin refused to start"; return null; }
                    int count = mode == "crash" ? 3 : 120;
                    var p = new Process
                    {
                        StartInfo = { FileName = "ping.exe", Arguments = "-n " + count + " 127.0.0.1", CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true },
                        EnableRaisingEvents = true
                    };
                    var console = new ServerConsole(_serverData.ServerID);
                    p.OutputDataReceived += console.AddOutput;
                    p.Start();
                    p.BeginOutputReadLine();
                    return p;
                }

                public async Task Stop(Process p) { await Task.Run(() => { try { p.Kill(); } catch { } }); }

                // Install: asks for EULA consent through the legacy plugin prompt API, "downloads" (progress +
                // a game.exe marker) and exits 0.
                public async Task<Process> Install()
                {
                    if (!await UI.CreateYesNoPromptV1("Agreement to the EULA", "Accept the test EULA?", "Agree", "Decline"))
                    {
                        Error = "EULA declined";
                        return null;
                    }
                    WindowsGSM.Installer.DownloadContext.Current?.OnLine("fake install 70%");
                    WindowsGSM.Installer.DownloadContext.Current?.OnProgress(70);
                    System.IO.File.WriteAllText(ServerPath.GetServersServerFiles(_serverData.ServerID, "game.exe"), "fake");
                    return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false });
                }

                public bool IsInstallValid() => System.IO.File.Exists(ServerPath.GetServersServerFiles(_serverData.ServerID, "game.exe"));

                public bool IsImportValid(string path)
                {
                    Error = "game.exe not found";
                    return System.IO.File.Exists(System.IO.Path.Combine(path, "game.exe"));
                }

                public async void CreateServerCFG() => System.IO.File.WriteAllText(ServerPath.GetServersServerFiles(_serverData.ServerID, "server.cfg"), "created");

                // Emulates a downloader: reports progress the way DepotDownloader does, leaves a marker file,
                // and exits 0 (or 3 in "update_fail" mode).
                public async Task<Process> Update(bool validate = false, string custom = null)
                {
                    string mode = _serverData.CustomSettings.TryGetValue("test_mode", out var m) ? m : "long";
                    WindowsGSM.Installer.DownloadContext.Current?.OnLine("fake download 50%");
                    WindowsGSM.Installer.DownloadContext.Current?.OnProgress(50);
                    System.IO.File.AppendAllText(ServerPath.GetServersServerFiles(_serverData.ServerID, "updated.txt"), (validate ? "validate" : "update") + "\n");
                    // Takes ~1 s, like a real (small) download.
                    return Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & exit " + (mode == "update_fail" ? "3" : "0")) { CreateNoWindow = true, UseShellExecute = false });
                }
            }
        }
        """;

    private static readonly object _installLock = new();
    private static bool _installed;

    /// <summary>Installs the test plugin once per test run.</summary>
    public static void EnsurePlugin()
    {
        lock (_installLock)
        {
            if (_installed) { return; }
            _ = TestData.DataRoot;
            string dir = Path.Combine(WgsmEnvironment.DataRoot, "plugins", "WgsmTestGame.cs");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "WgsmTestGame.cs"), PluginSource);
            _installed = true;
        }
    }

    public static int Port(string id) => 40000 + (int.TryParse(id, out int n) ? n % 5000 : Math.Abs(id.GetHashCode()) % 5000) * 2;

    /// <summary>Writes a server config for the test game.</summary>
    public static void CreateServer(string id, string mode = "long", bool autoRestart = false, params string[] extraSettings)
    {
        _ = TestData.DataRoot;
        string configs = Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "configs");
        string serverFiles = Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "serverfiles");
        Directory.CreateDirectory(configs);
        if (Directory.Exists(serverFiles)) { TestData.DeleteDirectory(serverFiles); }
        Directory.CreateDirectory(serverFiles);
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[]
        {
            $"servergame=\"{GameName}\"",
            $"servername=\"Test {id}\"",
            "serverip=\"127.0.0.1\"",
            // Each test server gets its own ports: starting one whose port another running server uses is refused.
            $"serverport=\"{Port(id)}\"",
            $"serverqueryport=\"{Port(id) + 1}\"",
            $"autorestart=\"{(autoRestart ? 1 : 0)}\"",
            $"test_mode=\"{mode}\"",
        }.Concat(extraSettings));
        // A stale PID cache from an earlier run must not make the engine adopt some unrelated process.
        string cache = Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "cache");
        // A running engine may still be writing it (a server just killed is being noticed): the helper retries.
        TestData.DeleteDirectory(cache);
    }

    public static async Task<WgsmEngine> StartEngineAsync(CrashLoopOptions? crashLoop = null)
    {
        EnsurePlugin();
        var engine = await WgsmEngine.StartAsync(TestData.DataRoot, crashLoop);
        Assert.Contains(engine.Plugins.Plugins, p => p.IsLoaded && p.FullName == GameName);
        return engine;
    }

    /// <summary>Polls until <paramref name="condition"/> holds, failing the test with <paramref name="what"/> on timeout.</summary>
    public static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) { Assert.Fail($"Timed out after {timeoutMs} ms waiting for: {what}"); }
            await Task.Delay(50);
        }
    }

    /// <summary>The same, for a condition that has to ask something (e.g. the API).</summary>
    public static async Task WaitUntil(Func<Task<bool>> condition, string what, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) { Assert.Fail($"Timed out after {timeoutMs} ms waiting for: {what}"); }
            await Task.Delay(50);
        }
    }

    /// <summary>Collects every event for one server.</summary>
    public static List<EngineEvent> Record(WgsmEngine engine, string serverId)
    {
        var list = new List<EngineEvent>();
        engine.Events.Subscribe(e =>
        {
            string? id = e switch
            {
                ServerStateChanged s => s.ServerId,
                ServerLogged l => l.ServerId,
                ConsoleLineAdded c => c.ServerId,
                ServerAlert a => a.ServerId,
                JobChanged j => j.Job.ServerId,
                WindowsGSM.Engine.Services.PlayersChanged pc => pc.ServerId,
                WindowsGSM.Engine.Services.ServerMetricsSampled ms => ms.ServerId,
                _ => null,
            };
            if (id == serverId) { lock (list) { list.Add(e); } }
        });
        return list;
    }

    public static void KillQuietly(Process? p)
    {
        try { if (p != null && !p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } } catch { /* gone */ }
    }
}
