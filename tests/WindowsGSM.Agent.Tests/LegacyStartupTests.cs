using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>The old app's own start-on-boot (task WGSM) still on for this folder: noticed, and only owners turn it off.</summary>
[Collection("Agent")]
public class LegacyStartupTests
{
    private readonly AgentFixture _f;
    public LegacyStartupTests(AgentFixture f) => _f = f;

    // What `schtasks /create /tn WGSM /tr "<exe>" /sc onlogon /rl highest` (the old app) leaves behind, trimmed.
    private static string Task(string command, bool enabled = true) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Settings><Enabled>{(enabled ? "true" : "false")}</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{command}</Command></Exec></Actions>
        </Task>
        """;

    [Fact]
    public void The_old_apps_task_counts_only_when_on_and_starting_the_app_in_this_folder()
    {
        string root = @"C:\Users\admin\Desktop\WindowsGSM";
        Assert.True(StartupTask.LegacyTaskStarts(Task(@"C:\Users\admin\Desktop\WindowsGSM\WindowsGSM.exe"), root));
        Assert.True(StartupTask.LegacyTaskStarts(Task(@"""C:\Users\admin\Desktop\WindowsGSM\WindowsGSM.exe"""), root + @"\"));
        Assert.False(StartupTask.LegacyTaskStarts(Task(@"C:\Users\admin\Desktop\WindowsGSM\WindowsGSM.exe", enabled: false), root));
        Assert.False(StartupTask.LegacyTaskStarts(Task(@"D:\Other\WindowsGSM\WindowsGSM.exe"), root)); // the old app on another folder: not ours
    }

    [Fact]
    public async Task Agent_settings_report_it_and_only_owners_can_turn_it_off()
    {
        var startup = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/agent/startup");
        Assert.True(startup.TryGetProperty("legacyTask", out var on));
        Assert.Equal(JsonValueKind.False, on.ValueKind); // the test machine has no WGSM task for the test folder

        var admin = await _f.UserAsync("legacy-admin", Role.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/agent/startup/legacy-off")).StatusCode);
    }
}
