using System.Net;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>The Logs page's files, and the old app's restart setting on the Schedules tab.</summary>
[Collection("Agent")]
public class LogsPageTests
{
    private readonly AgentFixture _f;
    public LogsPageTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task Log_files_are_listed_by_kind_and_only_listed_files_can_be_read()
    {
        string logs = ServerPath.Get("logs");
        Directory.CreateDirectory(Path.Combine(logs, "servers", "101"));
        Directory.CreateDirectory(Path.Combine(logs, "plugins"));
        File.WriteAllText(Path.Combine(logs, "L20260102-DiscordBot.log"), "[01/02/2026-10:00:00] /list by someone\n");
        File.WriteAllText(Path.Combine(logs, "CRASH_20260102.log"), "The agent crashed\n");
        File.WriteAllText(Path.Combine(logs, "servers", "101", "crash_20260102_101500.log"), "exit code 1\n");
        File.WriteAllText(Path.Combine(logs, "plugins", "Broken.cs.log"), "CS1002: ; expected\n");
        File.WriteAllText(Path.Combine(logs, "secret.txt"), "not a log");
        try
        {
            var viewer = await _f.UserAsync("logviewer", Role.Viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/machines/local/logs")).StatusCode);

            var list = await _f.Owner.GetJsonAsync<List<LogFileDto>>("/api/v2/machines/local/logs");
            Assert.Contains(list, l => l.Kind == "discord" && l.Path == "L20260102-DiscordBot.log");
            Assert.Contains(list, l => l.Kind == "crash" && l.Path == "CRASH_20260102.log");
            Assert.Contains(list, l => l.Kind == "crash" && l.Path == "servers/101/crash_20260102_101500.log" && l.Server == "101");
            Assert.Contains(list, l => l.Kind == "plugins" && l.Path == "plugins/Broken.cs.log");
            Assert.Contains(list, l => l.Kind == "app"); // the engine's own L<date>.log
            Assert.DoesNotContain(list, l => l.Path.Contains("secret"));

            var text = await _f.Owner.GetJsonAsync<LogTextDto>("/api/v2/machines/local/logs/read?path=" + Uri.EscapeDataString("servers/101/crash_20260102_101500.log"));
            Assert.Equal("exit code 1\n", text.Text);
            Assert.False(text.Truncated);

            // Anything not in the listing is refused, however it's spelled.
            foreach (string path in new[] { "secret.txt", "../configs/next/users.json", "..\\secret.txt", "C:\\Windows\\win.ini" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await _f.Owner.GetAsync("/api/v2/machines/local/logs/read?path=" + Uri.EscapeDataString(path))).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await _f.Owner.GetAsync("/api/v2/machines/local/logs/download?path=" + Uri.EscapeDataString(path))).StatusCode);
            }
            var download = await _f.Owner.GetAsync("/api/v2/machines/local/logs/download?path=CRASH_20260102.log");
            Assert.Equal("The agent crashed\n", await download.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (string f in new[] { "L20260102-DiscordBot.log", "CRASH_20260102.log", "secret.txt", "servers/101/crash_20260102_101500.log", "plugins/Broken.cs.log" })
            {
                File.Delete(Path.Combine(logs, f));
            }
        }
    }

    [Fact]
    public void A_big_log_is_read_from_its_end()
    {
        string logs = ServerPath.Get("logs");
        string file = Path.Combine(logs, "L20250101.log");
        File.WriteAllText(file, string.Concat(Enumerable.Range(0, 60_000).Select(i => $"line {i:D6} ........................\n")));
        try
        {
            var text = LogFiles.Read(ServerPath.Get(), "L20250101.log")!;
            Assert.True(text.Truncated);
            Assert.True(text.Text.Length <= LogFiles.MaxRead);
            Assert.StartsWith("line ", text.Text); // starts on a whole line
            Assert.EndsWith("line 059999 ........................\n", text.Text);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task The_old_restart_setting_shows_only_when_on_and_can_be_edited_or_removed()
    {
        string id = "102";
        try
        {
            // Legacy fills in "0 6 * * *" with the switch off: listed as not enabled, with no next run.
            ServerConfig.SetSetting(id, ServerConfig.SettingName.CrontabFormat, "0 6 * * *");
            ServerConfig.SetSetting(id, ServerConfig.SettingName.RestartCrontab, "0");
            var list = await _f.Owner.GetJsonAsync<List<ScheduleDto>>(_f.ServerUrl(id, "/schedules"));
            var setting = Assert.Single(list, s => s.Source == "RestartSetting");
            Assert.False(setting.Enabled);
            Assert.Null(setting.Next);

            var viewer = await _f.UserAsync("schedviewer", Role.Viewer);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(_f.ServerUrl(id, "/schedules/restart-setting"), new RestartSettingRequest("0 5 * * *", true))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl(id, "/schedules/restart-setting"), new RestartSettingRequest("at five", true))).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl(id, "/schedules/restart-setting"), new RestartSettingRequest("30 5 * * *", true))).StatusCode);
            setting = Assert.Single(await _f.Owner.GetJsonAsync<List<ScheduleDto>>(_f.ServerUrl(id, "/schedules")), s => s.Source == "RestartSetting");
            Assert.Equal("30 5 * * *", setting.Cron);
            Assert.True(setting.Enabled);
            Assert.NotNull(setting.Next);

            // Deleting it switches it off (the cron stays, like legacy).
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl(id, "/schedules/restart-setting"), new RestartSettingRequest(null, false))).StatusCode);
            Assert.False(Assert.Single(await _f.Owner.GetJsonAsync<List<ScheduleDto>>(_f.ServerUrl(id, "/schedules")), s => s.Source == "RestartSetting").Enabled);
        }
        finally
        {
            ServerConfig.SetSetting(id, ServerConfig.SettingName.RestartCrontab, "0");
            ServerConfig.SetSetting(id, ServerConfig.SettingName.CrontabFormat, "");
        }
    }
}
