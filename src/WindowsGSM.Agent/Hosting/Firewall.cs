using System.Diagnostics;
using System.Security.Principal;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Windows Firewall rules for the agent's port (and port 80 for Let's Encrypt). Silent: if the agent isn't
/// elevated it never pops a UAC prompt on its own — it says what to run instead. (The first-run wizard and
/// settings page can ask for elevation when a person is there to answer.)
/// </summary>
public static class Firewall
{
    public const string AgentRule = "WindowsGSM Agent";
    public const string AcmeRule = "WindowsGSM Agent (Let's Encrypt)";

    public static bool IsElevated
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    /// <summary>Makes sure an inbound TCP rule named <paramref name="rule"/> allows <paramref name="port"/>. Returns a hint on failure.</summary>
    public static async Task<string?> EnsureAsync(string rule, int port, bool elevateIfNeeded = false)
    {
        if (await QueryPortAsync(rule) == port) { return null; }
        string command = $"netsh advfirewall firewall delete rule name=\"{rule}\" >nul 2>&1 & " +
                         $"netsh advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={port}";
        bool elevated = IsElevated;
        if (!elevated && !elevateIfNeeded)
        {
            return $"Windows Firewall may block port {port}. To allow it, run this in an administrator command prompt:  " +
                   $"netsh advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={port}";
        }
        return await RunAsync(command, elevate: !elevated) ? null : $"Couldn't add the firewall rule for port {port}.";
    }

    private static async Task<int?> QueryPortAsync(string rule)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{rule}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) { return null; }
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(new CancellationTokenSource(8000).Token);
            if (p.ExitCode != 0) { return null; } // "No rules match the specified criteria."
            foreach (string line in output.Split('\n'))
            {
                string t = line.TrimStart();
                if (!t.StartsWith("LocalPort", StringComparison.OrdinalIgnoreCase)) { continue; }
                int colon = t.IndexOf(':');
                if (colon >= 0 && int.TryParse(t[(colon + 1)..].Trim(), out int port)) { return port; }
            }
            return 0; // rule exists with a non-numeric port (e.g. Any)
        }
        catch { return null; }
    }

    private static async Task<bool> RunAsync(string command, bool elevate)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                UseShellExecute = elevate, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevate) { psi.Verb = "runas"; }
            using var p = Process.Start(psi);
            if (p == null) { return false; }
            await p.WaitForExitAsync(new CancellationTokenSource(12000).Token);
            return p.ExitCode == 0;
        }
        catch { return false; } // declined UAC, netsh missing…
    }
}
