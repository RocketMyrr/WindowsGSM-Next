using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Upgrading keeps everything. Fixtures/data-&lt;version&gt; is a data folder written by that published release —
/// made by running it once and creating one of each kind of setting through its own API (users, tags, schedules,
/// backup settings, automations, notification channels, restart warnings, a template, the Discord bot, an edited
/// server setting). This starts the agent being built on a copy, as a separate process exactly as installed, and
/// checks every one of them is still there, nothing was set aside as unreadable, and the folder got its stamp.
/// To add a release: run it the same way and put its folder next to the others.
/// </summary>
public class UpgradeTests
{
    public static IEnumerable<object[]> Releases() =>
        Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "data-*").Select(d => new object[] { Path.GetFileName(d)["data-".Length..] });

    [Theory]
    [MemberData(nameof(Releases))]
    public async Task Everything_a_release_saved_is_still_there_after_upgrading(string release)
    {
        string root = Path.Combine(Path.GetTempPath(), $"wgsm-upgrade-{release}-{Guid.NewGuid():N}"[..40]);
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "data-" + release), root);
        int port = FreePort();
        var agentJson = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "configs", "next", "agent.json")))!;
        agentJson["Port"] = port;
        File.WriteAllText(Path.Combine(root, "configs", "next", "agent.json"), agentJson.ToJsonString());

        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "wgsm-agent.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string a in new[] { "--data", root, "--no-autostart" }) { psi.ArgumentList.Add(a); }
        using var agent = Process.Start(psi)!;
        var output = new System.Text.StringBuilder();
        agent.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        agent.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        agent.BeginOutputReadLine();
        agent.BeginErrorReadLine();
        try
        {
            var jar = new CookieContainer();
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = jar }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v2/") };
            http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
            await Core.Tests.EngineFixture.WaitUntil(async () =>
            {
                if (agent.HasExited) { lock (output) { Assert.Fail($"The agent stopped on the {release} data:\n{output}"); } }
                try { return (await http.GetAsync("info")).IsSuccessStatusCode; } catch { return false; }
            }, "the agent to start on the upgraded data", 90000);

            var info = await http.GetFromJsonAsync<JsonElement>("info");
            Assert.False(info.GetProperty("setupRequired").GetBoolean()); // the accounts came through
            var login = await http.PostAsJsonAsync("auth/login", new { username = "owner", password = "upgrade-test-password-1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            async Task<JsonElement> Get(string path)
            {
                var res = await http.GetAsync(path);
                Assert.True(res.IsSuccessStatusCode, $"GET {path} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
                return await res.Content.ReadFromJsonAsync<JsonElement>();
            }
            static IEnumerable<JsonElement> Items(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : e.GetProperty("items").EnumerateArray();
            static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            // Accounts
            Assert.Contains(Items(await Get("users")), u => Str(u, "username") == "ops" && Str(u, "role") == "Operator");

            // Servers, their settings and tags
            var servers = Items(await Get("machines/local/servers")).ToList();
            Assert.Equal(2, servers.Count);
            var s2 = await Get("machines/local/servers/2/settings");
            Assert.Contains($"Second (edited by {release.Replace("2.0.0-", "")})", s2.ToString());
            var s1 = servers.Single(s => Str(s, "id") == "1");
            Assert.Equal(new[] { "eu", "modded" }, s1.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

            // Schedules (both, one switched off) and backup settings
            var schedules = Items(await Get("machines/local/servers/1/schedules")).Where(e => Str(e, "source") == "Managed").ToList();
            Assert.Contains(schedules, e => Str(e, "cron") == "0 6 * * *" && Str(e, "action") == "Restart" && e.GetProperty("enabled").GetBoolean());
            Assert.Contains(schedules, e => Str(e, "cron") == "*/30 * * * *" && Str(e, "payload") == "say hello" && !e.GetProperty("enabled").GetBoolean());
            var backup = await Get("machines/local/servers/1/backups/settings");
            Assert.Equal((7, true), (backup.GetProperty("keepCount").GetInt32(), backup.GetProperty("beforeStart").GetBoolean()));
            Assert.Contains("server", backup.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));

            // Automations, notification channels, restart warnings, templates, Discord bot
            Assert.Contains(Items(await Get("machines/local/automations")), r => Str(r, "id") == "empty1" && Str(r, "name") == "Empty restart");
            Assert.Contains(Items(await Get("notifications/channels")), c => Str(c, "name") == "Crashes");
            var warnings = await Get("machines/local/servers/1/restart-warnings");
            Assert.True(warnings.GetProperty("enabled").GetBoolean());
            Assert.Equal("Restart in {time}", Str(warnings, "message"));
            Assert.Contains(Items(await Get("machines/local/templates")), t => Str(t, "name") == "Rust base");
            var bot = await Get("discord-bot");
            Assert.Equal("876543210987654321", Str(bot, "guildId"));
            Assert.Single(bot.GetProperty("admins").EnumerateArray());

            // Off-site backups (from alpha.6 on).
            if (File.Exists(Path.Combine(root, "configs", "next", "offsite.json")))
            {
                var offsite = await Get("machines/local/offsite");
                Assert.Equal(("wgsm-upgrade-test", 5), (Str(offsite, "bucket"), offsite.GetProperty("keepCount").GetInt32()));
            }

            // The audit log and metrics history it wrote are still readable.
            Assert.NotEmpty(Items(await Get("audit")));
            await Get("machines/local/servers/1/history?range=24h");

            // Nothing was unreadable; the folder is stamped; Health checks agree.
            Assert.Empty(Directory.GetFiles(root, "*.unreadable-*", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(root, "configs", "next", "data-format.json")));
            var checks = Items(await Get("machines/local/readiness?network=false"));
            Assert.Contains(checks, c => Str(c, "name") == "Settings files" && Str(c, "status") == "Pass");
        }
        finally
        {
            try { if (!agent.HasExited) { agent.Kill(entireProcessTree: true); agent.WaitForExit(10000); } } catch { }
            Core.Tests.TestData.DeleteDirectory(root);
        }
    }

    /// <summary>A copy of a release's data folder on a free port, ready for the agent.</summary>
    private static (string Root, int Port) Prepare(string release)
    {
        string root = Path.Combine(Path.GetTempPath(), $"wgsm-upgrade-{release}-{Guid.NewGuid():N}"[..40]);
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "data-" + release), root);
        int port = FreePort();
        var agentJson = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "configs", "next", "agent.json")))!;
        agentJson["Port"] = port;
        File.WriteAllText(Path.Combine(root, "configs", "next", "agent.json"), agentJson.ToJsonString());
        return (root, port);
    }

    private static (Process Agent, System.Text.StringBuilder Output) Start(string root)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "wgsm-agent.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string a in new[] { "--data", root, "--no-autostart" }) { psi.ArgumentList.Add(a); }
        var agent = Process.Start(psi)!;
        var output = new System.Text.StringBuilder();
        agent.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        agent.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        agent.BeginOutputReadLine();
        agent.BeginErrorReadLine();
        return (agent, output);
    }

    private static void Stop(Process agent, string root)
    {
        try { if (!agent.HasExited) { agent.Kill(entireProcessTree: true); agent.WaitForExit(10000); } } catch { }
        agent.Dispose();
        Core.Tests.TestData.DeleteDirectory(root);
    }

    [Fact]
    public async Task A_damaged_settings_file_is_kept_aside_and_reported_not_lost()
    {
        var (root, port) = Prepare("2.0.0-alpha.6");
        string next = Path.Combine(root, "configs", "next");
        const string CutOff = "[{ \"Id\": \"empty1\", \"Na";
        File.WriteAllText(Path.Combine(next, "automations.json"), CutOff); // a save cut off half-way, no previous copy
        File.WriteAllText(Path.Combine(next, "tags.json"), "not json");
        var (agent, output) = Start(root);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v2/") };
            http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
            await Core.Tests.EngineFixture.WaitUntil(async () =>
            {
                if (agent.HasExited) { lock (output) { Assert.Fail($"The agent stopped:\n{output}"); } }
                try { return (await http.GetAsync("info")).IsSuccessStatusCode; } catch { return false; }
            }, "the agent to start", 90000);
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("auth/login", new { username = "owner", password = "upgrade-test-password-1" })).StatusCode);

            // Health checks name both files and where the originals are.
            var checks = await http.GetFromJsonAsync<JsonElement>("machines/local/readiness?network=false");
            var settings = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Settings files");
            Assert.Equal("Fail", settings.GetProperty("status").GetString());
            Assert.Contains("automations.json", settings.GetProperty("message").GetString());
            Assert.Contains("tags.json", settings.GetProperty("message").GetString());

            // The originals are kept, and saving the (now empty) settings doesn't touch them.
            string kept = Assert.Single(Directory.GetFiles(next, "automations.json.unreadable-*"));
            Assert.Equal(CutOff, File.ReadAllText(kept));
            Assert.Equal(HttpStatusCode.OK, (await http.PutAsJsonAsync("machines/local/automations/new1", new { name = "After", trigger = "empty", minutes = 30, action = "notify", servers = new[] { "1" }, cooldownMinutes = 60, enabled = true })).StatusCode);
            Assert.Equal(CutOff, File.ReadAllText(kept));
            Assert.Equal("not json", File.ReadAllText(Assert.Single(Directory.GetFiles(next, "tags.json.unreadable-*"))));
            // Everything else carried on as normal.
            Assert.Contains("ops", await http.GetStringAsync("users"));
        }
        finally { Stop(agent, root); }
    }

    [Fact]
    public async Task A_damaged_account_list_stops_the_agent_rather_than_leaving_it_unclaimed()
    {
        var (root, _) = Prepare("2.0.0-alpha.6");
        File.WriteAllText(Path.Combine(root, "configs", "next", "users.json"), "[{ \"Username\": \"own");
        var (agent, output) = Start(root);
        try
        {
            Assert.True(await Task.Run(() => agent.WaitForExit(90000)), "The agent should refuse to start with an unreadable account list.");
            agent.WaitForExit(); // and let its printed output finish arriving (the timed wait doesn't wait for that)
            Assert.NotEqual(0, agent.ExitCode);
            lock (output) { Assert.Contains("users.json", output.ToString()); }
            Assert.Single(Directory.GetFiles(Path.Combine(root, "configs", "next"), "users.json.unreadable-*"));
        }
        finally { Stop(agent, root); }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        // Folders the installer would have made.
        foreach (string d in new[] { "servers", "backups", "logs", "plugins" }) { Directory.CreateDirectory(Path.Combine(to, d)); }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
