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
