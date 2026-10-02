using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowsGSM.Launcher
{
    /// <summary>Windows integration for a per-user install: shortcuts, "Apps &amp; features", start with Windows.</summary>
    internal static class Shell
    {
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsGSM-Next";
        private const string UninstallKey = UninstallKeyPath;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "WindowsGSM Desktop";

        public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WindowsGSM.lnk");
        public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "WindowsGSM.lnk");

        /// <summary>The Start menu folder with the agent's controls (Start / Stop / Restart agent).</summary>
        public static string StartMenuFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WindowsGSM");

        private static readonly string[][] Tools =
        {
            new[] { "Start WindowsGSM agent", "--agent-start", "Start the WindowsGSM agent (the part that runs your servers and the web panel)" },
            new[] { "Stop WindowsGSM agent", "--agent-stop", "Stop the WindowsGSM agent — your game servers keep running" },
            new[] { "Restart WindowsGSM agent", "--agent-restart", "Restart the WindowsGSM agent — your game servers keep running" },
            new[] { "WindowsGSM setup (repair or change options)", "--setup", "Change shortcuts and start-with-Windows, or repair this install" },
            new[] { "Uninstall WindowsGSM", "--uninstall", "Remove the app — your game servers, backups and settings stay" },
        };

        /// <summary>Adds the agent's Start menu controls (when the app has a Start menu shortcut). Quiet on failure.</summary>
        public static void EnsureStartMenuTools(Install install)
        {
            try
            {
                if (!File.Exists(StartMenuShortcut)) { return; }
                string launcher = Path.Combine(install.Root, "WindowsGSM.exe");
                Directory.CreateDirectory(StartMenuFolder);
                foreach (var t in Tools)
                {
                    string lnk = Path.Combine(StartMenuFolder, t[0] + ".lnk");
                    if (!File.Exists(lnk)) { CreateShortcut(lnk, launcher, t[2], t[1]); }
                }
            }
            catch { /* shortcuts are a convenience */ }
        }

        public static void CreateShortcut(string lnk, string target, string description, string arguments = null)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = target;
            if (!string.IsNullOrEmpty(arguments)) { s.Arguments = arguments; }
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.IconLocation = target + ",0";
            s.Description = description;
            s.Save();
        }

        public static void RegisterUninstall(Install install, string version)
        {
            string launcher = Path.Combine(install.Root, "WindowsGSM.exe");
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "WindowsGSM");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "WindowsGSM");
                key.SetValue("DisplayIcon", launcher + ",0");
                key.SetValue("InstallLocation", install.Root);
                key.SetValue("UninstallString", "\"" + launcher + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void SetStartWithWindows(bool on, string launcher)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) { key.SetValue(RunValue, "\"" + launcher + "\" --minimized"); }
                else { key.DeleteValue(RunValue, false); }
            }
        }

        public static bool TrayAtSignIn()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) { return key?.GetValue(RunValue) != null; } }
            catch { return false; }
        }

        private const string AgentTask = "WindowsGSM Agent"; // the agent's own StartupTask.TaskName

        public static bool AgentAtSignIn()
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{AgentTask}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// "Start the agent when I sign in": the agent registers its own task (through the launcher, so it always
        /// starts the current version). Returns null, or what went wrong.
        /// </summary>
        public static string SetAgentAtSignIn(Install install, bool on)
        {
            try
            {
                var psi = new ProcessStartInfo(install.AgentExe, Install.Quote(new[] { "--data", install.Data, on ? "--register-startup" : "--unregister-startup" }))
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(install.AgentExe),
                };
                psi.EnvironmentVariables["WGSM_LAUNCHER"] = Path.Combine(install.Root, "WindowsGSM.exe");
                psi.EnvironmentVariables["WGSM_INSTALL_ROOT"] = install.Root;
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return "It didn't answer in time."; }
                    return p.ExitCode == 0 || !on ? null : output; // turning off something that was never on is fine
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Removes everything setup added. The data folder (servers, backups) is never touched.</summary>
        public static void Unregister()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) { key.DeleteValue(RunValue, false); } } catch { }
            foreach (string lnk in new[] { StartMenuShortcut, DesktopShortcut })
            {
                try { if (File.Exists(lnk)) { File.Delete(lnk); } } catch { }
            }
            try { if (Directory.Exists(StartMenuFolder)) { Directory.Delete(StartMenuFolder, true); } } catch { }
            // The agent's own "start at sign-in" task, if it was turned on.
            try
            {
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN \"WindowsGSM Agent\" /F") { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(5000);
            }
            catch { }
        }
    }
}
