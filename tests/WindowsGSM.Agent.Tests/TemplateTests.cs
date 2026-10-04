using System.Net;
using System.Text.Json;
using WindowsGSM.Core.Tests;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Server templates: save a server's setup, apply it to another — without names, ports or passwords.</summary>
[Collection("Agent")]
public class TemplateTests
{
    private readonly AgentFixture _f;
    public TemplateTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task A_template_copies_settings_and_config_files_with_the_new_servers_ports()
    {
        const string from = "102", to = "103";
        await _f.Owner.PostAsync(_f.ServerUrl(to, "/kill"));
        await EngineFixture.WaitUntil(() => _f.Engine.Servers.Get(to)!.State == WindowsGSM.Engine.Servers.ServerState.Stopped, "stopped");
        string fromPort = ServerConfig.GetSetting(from, ServerConfig.SettingName.ServerPort);
        string toPort = ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerPort);
        string toName = ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerName);
        ServerConfig.SetSetting(from, ServerConfig.SettingName.ServerMaxPlayer, "42");
        ServerConfig.SetSetting(from, ServerConfig.SettingName.RconPassword, "rcon-secret");
        ServerConfig.SetSetting(from, "savewait", "25");
        string toParamBefore = ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerParam);
        string fromParamBefore = ServerConfig.GetSetting(from, ServerConfig.SettingName.ServerParam);
        ServerConfig.SetSetting(from, ServerConfig.SettingName.ServerParam, $"-port={fromPort} -log");
        string cfg = ServerPath.GetServersServerFiles(from, "server.cfg");
        File.WriteAllText(cfg, $"hostname \"Friends PvE\"\nport {fromPort}\nmaxplayers 42\nversion 1.27015\n");
        try
        {
            var res = await _f.Owner.PostAsync(_f.ServerUrl(from, "/template"), new { name = "PvE setup", description = "friends", includeFiles = true });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var t = await ApiClient.Read<JsonElement>(res);
            string id = t.GetProperty("id").GetString()!;
            Assert.Contains("server.cfg", t.GetProperty("files").EnumerateArray().Select(f => f.GetString()));

            var stored = File.ReadAllText(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "templates", id + ".json"));
            Assert.DoesNotContain("rcon-secret", stored);

            var list = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/machines/local/templates");
            Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetString() == id);

            Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PostAsync(_f.ServerUrl(to, "/apply-template"), new { template = id })).StatusCode);
            Assert.Equal("42", ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerMaxPlayer));
            Assert.Equal("25", ServerConfig.GetSetting(to, "savewait"));
            Assert.Equal($"-port={toPort} -log", ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerParam)); // ports in start parameters too
            Assert.Equal(toName, ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerName));   // its own name
            Assert.Equal(toPort, ServerConfig.GetSetting(to, ServerConfig.SettingName.ServerPort));   // and ports
            Assert.NotEqual("rcon-secret", ServerConfig.GetSetting(to, ServerConfig.SettingName.RconPassword));
            string copied = File.ReadAllText(ServerPath.GetServersServerFiles(to, "server.cfg"));
            Assert.Contains($"port {toPort}", copied);
            Assert.Contains("version 1.27015", copied); // only whole port numbers change
            Assert.Contains("Friends PvE", copied);

            // A template for another game is refused when installing.
            var bad = await _f.Owner.PostAsync("/api/v2/machines/local/servers", new { game = "Nope", name = "x", template = id });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

            // Only admins delete templates.
            var viewer = await _f.UserAsync("templateviewer", WindowsGSM.Contracts.Role.Operator);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/v2/machines/local/templates/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.DeleteAsync($"/api/v2/machines/local/templates/{id}")).StatusCode);
        }
        finally
        {
            WindowsGSM.Core.Tests.TestData.DeleteFile(cfg);
            WindowsGSM.Core.Tests.TestData.DeleteFile(ServerPath.GetServersServerFiles(to, "server.cfg"));
            ServerConfig.SetSetting(from, ServerConfig.SettingName.ServerParam, fromParamBefore);
            ServerConfig.SetSetting(to, ServerConfig.SettingName.ServerParam, toParamBefore);
            foreach (string sid in new[] { from, to })
            {
                ServerConfig.SetSetting(sid, ServerConfig.SettingName.RconPassword, "");
                ServerConfig.SetSetting(sid, "savewait", "");
                _f.Engine.Servers.Get(sid)!.ReloadConfig();
            }
        }
    }
}
