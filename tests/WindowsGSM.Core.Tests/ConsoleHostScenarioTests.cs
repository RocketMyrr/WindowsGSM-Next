using System.Diagnostics;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Game servers in console windows of their own, end to end: the console handling only switches on in a process
/// without a visible console (as the agent runs), so this starts tests/WindowsGSM.ConsoleHarness windowless — the
/// way the launcher starts the agent — and checks every scenario it reports: a game that keeps its console, one that
/// moves to its parent's (Rust), two of those together, plugins "pressing keys", and the agent restarting.
/// </summary>
public class ConsoleHostScenarioTests
{
    /// <summary>The harness built next to these tests (same configuration, same framework).</summary>
    private static string HarnessPath()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)); // …/bin/<config>/<tfm>
        string tfm = bin.Name, config = bin.Parent!.Name;
        string tests = bin.Parent.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(tests, "WindowsGSM.ConsoleHarness", "bin", config, tfm, "wgsm-console-harness.exe");
    }

    [Fact]
    public void Game_consoles_work_end_to_end()
    {
        string harness = HarnessPath();
        Assert.True(File.Exists(harness), $"The console harness isn't built: {harness}");
        string root = Path.Combine(Path.GetTempPath(), "wgsm-console-scenarios-" + Guid.NewGuid().ToString("N")[..8]);
        string results = root + ".txt";

        var psi = new ProcessStartInfo(harness, $"run \"{root}\" \"{results}\"") { UseShellExecute = false, CreateNoWindow = true };
        using var run = Process.Start(psi)!;
        bool finished = run.WaitForExit(TimeSpan.FromMinutes(4));
        try
        {
            if (!finished) { try { run.Kill(entireProcessTree: true); } catch { } }
            string[] lines = File.Exists(results) ? File.ReadAllLines(results) : Array.Empty<string>();
            string report = string.Join(Environment.NewLine, lines);
            if (lines.Any(l => l.StartsWith("SKIP"))) { return; } // the test host has a visible console here: nothing to check
            Assert.True(finished, "The console scenarios didn't finish in 4 minutes:" + Environment.NewLine + report);
            Assert.DoesNotContain(lines, l => l.StartsWith("FAIL"));
            Assert.True(lines.Count(l => l.StartsWith("PASS")) >= 15, "Fewer scenarios ran than expected:" + Environment.NewLine + report);
            Assert.Equal(0, run.ExitCode);
        }
        finally
        {
            // Nothing left running if a scenario failed half way (the harness's own stand-in games).
            foreach (var p in Process.GetProcessesByName("wgsm-console-harness")) { try { p.Kill(); } catch { } finally { p.Dispose(); } }
        }
    }
}
