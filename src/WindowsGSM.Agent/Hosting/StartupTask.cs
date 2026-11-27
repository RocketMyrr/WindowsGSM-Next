using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Starts the agent when the user signs in, and restarts it if it stops unexpectedly — a Task Scheduler task
/// with a logon trigger and restart-on-failure. A task rather than a Windows service on purpose: the agent
/// must run in the user's session so plugins that type into console windows keep working. (Pair it with
/// Windows auto-logon on a dedicated game machine so servers come back after a reboot.)
/// </summary>
public static class StartupTask
{
    public const string TaskName = "WindowsGSM Agent";

    /// <summary>Creates or replaces the task for the current user. Returns an error message, or null.</summary>
    public static string? Register(string exePath, string dataRoot)
    {
        // Installed: go through the launcher, which always starts the current version (an update moves the
        // agent to a new versions\ folder, and a task pointing at the old one would start yesterday's agent).
        string command = exePath, arguments = $"--data \"{Escape(dataRoot)}\"";
        if (Environment.GetEnvironmentVariable("WGSM_LAUNCHER") is { Length: > 0 } launcher && File.Exists(launcher))
        {
            command = launcher;
            arguments = "--agent";
        }
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Runs the WindowsGSM agent (game servers, web panel) when {Escape(user)} signs in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(user)}</UserId>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(command)}</Command>
                  <Arguments>{arguments}</Arguments>
                  <WorkingDirectory>{Escape(Path.GetDirectoryName(command) ?? string.Empty)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), $"wgsm-agent-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        finally { try { File.Delete(file); } catch { /* temp */ } }
    }

    public static string? Unregister() => Run($"/Delete /TN \"{TaskName}\" /F");

    public static bool IsRegistered() => Run($"/Query /TN \"{TaskName}\"") == null;

    /// <summary>The old app's "Start on boot": a task of its own, made as administrator (schtasks … /rl highest).</summary>
    public const string LegacyTaskName = "WGSM";

    /// <summary>
    /// Whether the old app's task is on and starts the WindowsGSM.exe in <paramref name="dataRoot"/> (the old app uses
    /// its own folder as its data folder). Then at sign-in the old app opens this folder first, and this agent can't.
    /// </summary>
    public static bool LegacyTaskOn(string dataRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{LegacyTaskName}\" /XML")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string xml = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 && LegacyTaskStarts(xml, dataRoot);
        }
        catch { return false; }
    }

    /// <summary>From the task's XML: enabled, and its program is a file in <paramref name="dataRoot"/>.</summary>
    internal static bool LegacyTaskStarts(string xml, string dataRoot)
    {
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        if (doc.Descendants().Any(e => e.Name.LocalName == "Enabled" && e.Parent?.Name.LocalName == "Settings" && e.Value.Trim() == "false")) { return false; }
        string? command = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        string? dir = command == null ? null : Path.GetDirectoryName(command)?.TrimEnd('\\');
        return dir != null && string.Equals(dir, Path.GetFullPath(dataRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns the old app's task off (not deleted, so going back to the old app is one switch). It was made as
    /// administrator, so when a plain try is refused Windows asks. Returns an error message, or null.</summary>
    public static string? DisableLegacyTask()
    {
        if (Run($"/Change /TN \"{LegacyTaskName}\" /Disable") == null) { return null; }
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Change /TN \"{LegacyTaskName}\" /Disable")
            {
                UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(60000);
            return p.ExitCode == 0 ? null : $"schtasks exited with {p.ExitCode}";
        }
        catch (System.ComponentModel.Win32Exception) { return "Windows' permission prompt was declined."; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? null : (string.IsNullOrWhiteSpace(err) ? $"schtasks exited with {p.ExitCode}" : err.Trim());
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string Escape(string s) => SecurityElement.Escape(s) ?? string.Empty;
}
