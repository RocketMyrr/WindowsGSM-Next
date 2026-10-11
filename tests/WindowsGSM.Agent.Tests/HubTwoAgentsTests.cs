using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Two real agents on this PC, as two machines: one is the hub, the other joins it the way the panel does it
/// (a pairing code). Then, through the hub only: the other machine's server is started, that machine's agent
/// dies and comes back (the server kept running and is still running), the hub itself restarts and the other
/// machine finds it again, and the server is stopped. HubTests covers the protocol in detail inside one process;
/// this is the same over real connections, with real restarts.
/// </summary>
public class HubTwoAgentsTests
{
    private const string Password = "hub-test-password-1";

    /// <summary>A stand-in game: a renamed copy of Windows' ping, running until it's stopped (so the test can find and end exactly it).</summary>
    private const string Plugin = """
        using System.Diagnostics;
        using System.Threading.Tasks;
        using WindowsGSM.Functions;

        namespace WindowsGSM.Plugins
        {
            public class HubTestGame
            {
                public Plugin Plugin = new Plugin { name = "HubTestGame", author = "tests", description = "stand-in game", version = "1", url = "", color = "#ffffff" };
                private readonly ServerConfig _serverData;
                public string Error, Notice;
                public string FullName = "WindowsGSM Hub Test Game";
                public string StartPath = "";
                public bool AllowsEmbedConsole = true;
                public int PortIncrements = 1;
                public object QueryMethod = null;
                public string Port = "47000", QueryPort = "47001", Defaultmap = "", Maxplayers = "4", Additional = "";
                public HubTestGame(ServerConfig serverData) { _serverData = serverData; }
                public async Task<Process> Start()
                {
                    var p = new Process { StartInfo = { FileName = @"{{EXE}}", Arguments = "-t 127.0.0.1", WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true }, EnableRaisingEvents = true };
                    var console = new ServerConsole(_serverData.ServerID);
                    p.OutputDataReceived += console.AddOutput;
                    p.Start();
                    p.BeginOutputReadLine();
                    return p;
                }
                public async Task Stop(Process p) { await Task.Run(() => p.Kill()); }
                public async Task<Process> Install() { return null; }
                public async Task<Process> Update(bool validate = false, string custom = null) { return null; }
                public bool IsInstallValid() => true;
                public bool IsImportValid(string path) => true;
                public string GetLocalBuild() => "";
                public async Task<string> GetRemoteBuild() => "";
            }
        }
        """;

    private sealed class Agent : IDisposable
    {
        public string Root { get; }
        public int Port { get; }
        public HttpClient Http { get; }
        public Process? Process { get; private set; }
        public string Name { get; }

        public Agent(string root, string name)
        {
            Root = root;
            Name = name;
            Port = TestTools.FreePorts(1)[0];
            Directory.CreateDirectory(Path.Combine(root, "configs", "next"));
            File.WriteAllText(Path.Combine(root, "configs", "next", "agent.json"), new JsonObject { ["Port"] = Port, ["MachineName"] = name }.ToJsonString());
            Http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}/api/v2/"), Timeout = TimeSpan.FromSeconds(60) };
            Http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
        }

        public async Task StartAsync()
        {
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "wgsm-agent.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "--data", Root, "--no-autostart" }) { psi.ArgumentList.Add(a); }
            Process = System.Diagnostics.Process.Start(psi)!;
            Process.OutputDataReceived += (_, _) => { };
            Process.ErrorDataReceived += (_, _) => { };
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
            await Core.Tests.EngineFixture.WaitUntil(() => { try { return Http.GetAsync("info").GetAwaiter().GetResult().IsSuccessStatusCode; } catch { return false; } }, $"the {Name} agent to answer", 90000);
        }

        /// <summary>Ends the agent the hard way (a crash, a power cut): game servers it started keep running.</summary>
        public void Kill()
        {
            if (Process is { HasExited: false }) { Process.Kill(entireProcessTree: false); Process.WaitForExit(15000); }
            Process?.Dispose();
            Process = null;
        }

        public void Dispose() { Kill(); Http.Dispose(); }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        string body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {res.RequestMessage?.RequestUri}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static bool Online(Agent hub, string machine) =>
        Json(hub.Http.GetAsync("machines").GetAwaiter().GetResult()).GetAwaiter().GetResult().EnumerateArray()
            .Any(m => m.GetProperty("id").GetString() == machine && m.GetProperty("online").GetBoolean());

    private static string? State(Agent hub, string machine, string id)
    {
        try
        {
            var res = hub.Http.GetAsync($"machines/{machine}/servers").GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) { return null; }
            return Json(res).GetAwaiter().GetResult().EnumerateArray().FirstOrDefault(s => s.GetProperty("id").GetString() == id) is { ValueKind: JsonValueKind.Object } s
                ? s.GetProperty("state").GetString() : null;
        }
        catch (HttpRequestException) { return null; }
    }

    [Fact]
    public async Task A_second_PC_is_controlled_through_the_hub_across_crashes_and_restarts()
    {
        string root = Path.Combine(Path.GetTempPath(), "wgsm-hub2-" + Guid.NewGuid().ToString("N")[..8]);
        string gameExe = Path.Combine(root, "wgsm-hubtest-game.exe");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), gameExe);

        using var hub = new Agent(Path.Combine(root, "hub"), "Hub PC");
        using var member = new Agent(Path.Combine(root, "member"), "Second PC");
        string plugins = Path.Combine(member.Root, "plugins", "HubTestGame.cs");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "HubTestGame.cs"), Plugin.Replace("{{EXE}}", gameExe));
        string configs = Path.Combine(member.Root, "servers", "1", "configs");
        Directory.CreateDirectory(configs);
        Directory.CreateDirectory(Path.Combine(member.Root, "servers", "1", "serverfiles"));
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[]
        {
            "servergame=\"WindowsGSM Hub Test Game [HubTestGame.cs]\"", "servername=\"Remote server\"", "serverip=\"127.0.0.1\"",
            "serverport=\"47010\"", "serverqueryport=\"47011\"", "autostart=\"0\"", "autorestart=\"0\"", "embedconsole=\"1\"",
        });

        try
        {
            await Task.WhenAll(hub.StartAsync(), member.StartAsync());
            foreach (var a in new[] { hub, member })
            {
                await Json(await a.Http.PostAsJsonAsync("setup", new { username = "owner", password = Password }));
            }
            string memberId = (await Json(await member.Http.GetAsync("info"))).GetProperty("machine").GetString()!;

            // Join, as the panel does: a pairing code from the hub, entered on the other PC.
            string code = (await Json(await hub.Http.PostAsync("hub/pairing-codes", null))).GetProperty("code").GetString()!;
            var joined = await member.Http.PostAsJsonAsync("link", new { hubUrl = $"http://127.0.0.1:{hub.Port}", code });
            Assert.True(joined.StatusCode == HttpStatusCode.NoContent, await joined.Content.ReadAsStringAsync());
            await Core.Tests.EngineFixture.WaitUntil(() => Online(hub, memberId), "the second PC to show as online on the hub", 60000);
            await Core.Tests.EngineFixture.WaitUntil(() => State(hub, memberId, "1") == "Stopped", "the hub to list the second PC's server", 60000);

            // Started through the hub.
            await Json(await hub.Http.PostAsync($"machines/{memberId}/servers/1/start", null));
            await Core.Tests.EngineFixture.WaitUntil(() => State(hub, memberId, "1") == "Running", "the server to run, as seen from the hub", 60000);

            // The second PC's agent dies: the hub says it's offline; the game keeps running.
            member.Kill();
            await Core.Tests.EngineFixture.WaitUntil(() => !Online(hub, memberId), "the hub to notice the second PC went away", 90000);
            Assert.NotEmpty(GameProcesses(gameExe));

            // It comes back: online again, and its server was picked up still running.
            await member.StartAsync();
            await Core.Tests.EngineFixture.WaitUntil(() => Online(hub, memberId), "the second PC to reconnect to the hub", 90000);
            await Core.Tests.EngineFixture.WaitUntil(() => State(hub, memberId, "1") == "Running", "the server to still be running after the agent restart", 60000);

            // The hub restarts: the second PC finds it again by itself (and the sign-in survives).
            hub.Kill();
            await hub.StartAsync();
            await Core.Tests.EngineFixture.WaitUntil(() => { try { return Online(hub, memberId); } catch { return false; } }, "the second PC to find the restarted hub", 120000);

            // Stopped through the hub.
            await Json(await hub.Http.PostAsync($"machines/{memberId}/servers/1/stop", null));
            await Core.Tests.EngineFixture.WaitUntil(() => State(hub, memberId, "1") == "Stopped", "the server to stop, as seen from the hub", 60000);
            await Core.Tests.EngineFixture.WaitUntil(() => GameProcesses(gameExe).Count == 0, "the game process to end", 30000);
        }
        finally
        {
            hub.Kill();
            member.Kill();
            foreach (var p in GameProcesses(gameExe)) { try { p.Kill(); p.WaitForExit(5000); } catch { } finally { p.Dispose(); } }
            Core.Tests.TestData.DeleteDirectory(root);
        }
    }

    /// <summary>The stand-in game's processes (by its exact path, so nothing else is touched).</summary>
    private static List<Process> GameProcesses(string exe) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))
            .Where(p => { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
            .ToList();
}
