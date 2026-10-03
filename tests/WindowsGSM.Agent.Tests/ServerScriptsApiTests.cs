using System.Net;
using System.Text.Json;
using WindowsGSM.Contracts;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>A server's scripts run a program on the PC: only admins choose them, and only .bat / .ps1 files.</summary>
[Collection("Agent")]
public class ServerScriptsApiTests
{
    private readonly AgentFixture _f;
    public ServerScriptsApiTests(AgentFixture f) => _f = f;

    private static string Script(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-scripts-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "@echo off");
        return path;
    }

    private static SettingsUpdateRequest Set(string key, string value) => new(new Dictionary<string, string?> { [key] = value });

    [Fact]
    public async Task Only_admins_choose_scripts_and_only_bat_or_ps1()
    {
        string bat = Script("rotate.bat"), exe = Script("tool.exe");
        var editor = await _f.UserAsync("scripteditor", Role.Member, new Dictionary<string, Capability> { [$"{_f.MachineId}/103"] = Capability.View | Capability.EditConfig });
        try
        {
            // Someone who may edit the settings, but isn't an admin: other settings yes, scripts no.
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.PatchAsync(_f.ServerUrl("103", "/settings"), Set("batchfile", bat))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.PatchAsync(_f.ServerUrl("103", "/settings"), Set("afterstopscript", bat))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await editor.PatchAsync(_f.ServerUrl("103", "/settings"), Set("scripttimeout", "120"))).StatusCode);

            // An admin: .bat yes; .exe and missing files no.
            var bad = await _f.Owner.PatchAsync(_f.ServerUrl("103", "/settings"), Set("batchfile", exe));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains((await ApiClient.Read<ApiError>(bad)).Details!, d => d.Contains("Only .bat and .ps1"));
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PatchAsync(_f.ServerUrl("103", "/settings"), Set("batchfile", bat + ".missing.bat"))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PatchAsync(_f.ServerUrl("103", "/settings"), Set("batchfile", $"\"{bat}\""))).StatusCode);
            Assert.Equal(bat, (await _f.Owner.GetJsonAsync<ServerSettingsDto>(_f.ServerUrl("103", "/settings"))).Values["batchfile"]);

            // Templates never carry a script to another server.
            var tpl = await _f.Owner.PostAsync(_f.ServerUrl("103", "/template"), new { name = "with-script", includeFiles = false });
            Assert.Equal(HttpStatusCode.OK, tpl.StatusCode);
            string tplId = (await ApiClient.Read<JsonElement>(tpl)).GetProperty("id").GetString()!;
            var templates = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<WindowsGSM.Agent.Hosting.ServerTemplates>(_f.Server.Services);
            Assert.DoesNotContain("batchfile", templates.Get(tplId)!.Settings.Keys);
            await _f.Owner.DeleteAsync($"/api/v2/machines/local/templates/{tplId}");

            // Putting back older settings doesn't let a non-admin bring a script back: an admin removes it…
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PatchAsync(_f.ServerUrl("103", "/settings"), Set("batchfile", ""))).StatusCode);
            var versions = await _f.Owner.GetJsonAsync<List<JsonElement>>(_f.ServerUrl("103", "/config-history?path=settings"));
            // …the version saved just before that removal still has it…
            string withScript = versions.First().GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.NoContent, (await editor.PostAsync(_f.ServerUrl("103", $"/config-history/{withScript}/restore"))).StatusCode);
            // …but for the non-admin the scripts stay as they were (none).
            Assert.Equal(string.Empty, new ServerConfig("103").BatchFile);
        }
        finally
        {
            await _f.Owner.PatchAsync(_f.ServerUrl("103", "/settings"), new SettingsUpdateRequest(new Dictionary<string, string?> { ["batchfile"] = "", ["scripttimeout"] = "" }));
            await _f.Owner.DeleteAsync("/api/v2/users/scripteditor");
        }
    }
}
