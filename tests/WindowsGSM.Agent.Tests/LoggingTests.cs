using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Notifications;

namespace WindowsGSM.Agent.Tests;

/// <summary>Crash logs like the legacy app's, reported on the next start.</summary>
[Collection("Agent")]
public class LoggingTests
{
    private readonly AgentFixture _f;
    public LoggingTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task A_crash_is_written_and_reported_on_the_next_start()
    {
        string root = Path.Combine(Path.GetTempPath(), "wgsm-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashLog.Configure(root, "agent");
            CrashLog.Write(new InvalidOperationException("kaboom"), fatal: true);
            CrashLog.Write(new TimeoutException("background"), fatal: false);

            string log = File.ReadAllText(Path.Combine(root, "logs", $"CRASH_{DateTime.Now:yyyyMMdd}.log"));
            Assert.Contains("The agent crashed", log);
            Assert.Contains("kaboom", log);
            Assert.Contains("A background task in the agent failed", log);
            Assert.True(File.Exists(Path.Combine(root, "logs", "crash-pending.txt")));

            CrashLog.ReportPrevious(root, _f.App.Services.GetRequiredService<NotificationCentre>(), _f.MachineId);
            Assert.False(File.Exists(Path.Combine(root, "logs", "crash-pending.txt")));
            var items = (await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications")).GetProperty("items").EnumerateArray();
            Assert.Contains(items, i => i.GetProperty("kind").GetString() == "appCrash" && i.GetProperty("text").GetString()!.Contains("kaboom"));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
