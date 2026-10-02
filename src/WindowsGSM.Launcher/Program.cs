using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WindowsGSM.Launcher
{
    // WindowsGSM.exe (launcher)
    //   (no arguments)              open WindowsGSM (the desktop app of the current version); setup if not installed
    //   --minimized                 same, straight to the tray (start with Windows)
    //   --agent [args]              run just the agent (the "start at sign-in" task uses this)
    //   --switch <ver> --wait-pid <pid> [--start-agent]
    //                               after an update: wait for the old agent to exit, make <ver> current, start it again
    //   --rollback [--wait-pid <pid>] [--start-agent]
    //                               go back to the previous version
    //   --setup [--data <folder>]   run setup even if installed (install another copy / repair); --data pre-selects
    //                               an existing WindowsGSM folder
    //   --uninstall                 remove the app (never the game servers' data folder)
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            var install = Install.Load(root);
            bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            string Arg(string a) { int i = Array.FindIndex(args, x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

            try
            {
                if (Has("--uninstall")) { return Uninstall(install); }

                if (install == null || Has("--setup"))
                {
                    string version = Install.NewestVersionIn(root);
                    if (version == null)
                    {
                        // Usually: opened straight from inside the zip (Windows runs just this file from a temp folder).
                        bool fromZip = root.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase) >= 0 || root.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0;
                        MessageBox.Show(fromZip
                                ? "WindowsGSM needs to be extracted before it can install.\n\nRight-click the zip you downloaded → Extract All…, then open the extracted folder and run WindowsGSM.exe from there."
                                : "This copy of WindowsGSM is incomplete — the versions folder next to WindowsGSM.exe is missing.\n\nDownload the zip again, right-click it → Extract All…, and run WindowsGSM.exe from the extracted folder.",
                            "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 1;
                    }
                    Application.Run(new SetupForm(root, version, Arg("--data")));
                    return 0;
                }

                if (Arg("--switch") is string target || Has("--rollback"))
                {
                    WaitFor(Arg("--wait-pid"));
                    string from = install.VersionDir(install.Current) + "\\";
                    if (Has("--rollback"))
                    {
                        if (string.IsNullOrWhiteSpace(install.Previous) || !Directory.Exists(install.VersionDir(install.Previous))) { return Fail("There's no previous version to go back to."); }
                        install.SwitchTo(install.Previous);
                    }
                    else { install.SwitchTo(Arg("--switch")); }
                    if (Has("--start-agent")) { install.Start(install.AgentExe, Array.Empty<string>(), hidden: true); }
                    // NEXT: the desktop window, if open, moves to the new version too (it was still the old one until
                    // closed). Game servers aren't involved — they kept running throughout.
                    bool reopen = false;
                    foreach (var p in Process.GetProcessesByName("WindowsGSM"))
                    {
                        try
                        {
                            if (p.Id != Process.GetCurrentProcess().Id && p.MainModule.FileName.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                            {
                                p.CloseMainWindow();
                                if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); }
                                reopen = true;
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                    if (reopen && File.Exists(install.DesktopExe)) { Thread.Sleep(1500); install.Start(install.DesktopExe, Array.Empty<string>(), hidden: false); }
                    return 0;
                }

                if (!File.Exists(install.DesktopExe))
                {
                    return Fail($"WindowsGSM {install.Current} is missing from {install.VersionDir(install.Current)}. Run setup again to repair it.");
                }

                // Can't sign in: WindowsGSM.exe --reset-password <user> [--disable-2fa] — the agent does it; the
                // answer (with the new password) is shown here, since the launcher has no console.
                if (Arg("--reset-password") is string resetUser)
                {
                    var psi = new ProcessStartInfo(install.AgentExe, Install.Quote(new[] { "--reset-password", resetUser, "--data", install.Data }
                        .Concat(Has("--disable-2fa") ? new[] { "--disable-2fa" } : new string[0])))
                    {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(install.AgentExe),
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit();
                        MessageBox.Show(output + (p.ExitCode == 0 ? "\n\n(Ctrl+C copies this message.)" : string.Empty), "WindowsGSM — reset password",
                            MessageBoxButtons.OK, p.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        return p.ExitCode;
                    }
                }

                // Start menu: start / stop / restart the agent (game servers keep running either way).
                if (Has("--agent-start") || Has("--agent-stop") || Has("--agent-restart"))
                {
                    string action = Has("--agent-start") ? "start" : Has("--agent-stop") ? "stop" : "restart";
                    return AgentControl.Run(install, action, Has("--quiet"), Arg("--wait-pid"));
                }

                if (Has("--agent"))
                {
                    var rest = args.SkipWhile(a => !string.Equals(a, "--agent", StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
                    var agent = install.Start(install.AgentExe, rest, hidden: true);
                    // The sign-in task watches this process: stay alive as long as the agent does.
                    agent.WaitForExit();
                    return agent.ExitCode;
                }

                Shell.EnsureStartMenuTools(install); // copies installed before these shortcuts existed get them now
                install.Start(install.DesktopExe, args.Where(a => a != "--setup").ToArray(), hidden: false);
                return 0;
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private static void WaitFor(string pid)
        {
            if (!int.TryParse(pid, out int id)) { return; }
            try { using (var p = Process.GetProcessById(id)) { p.WaitForExit(60000); } }
            catch (ArgumentException) { /* already gone */ }
            Thread.Sleep(500); // let file handles close
        }

        private static int Uninstall(Install install)
        {
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            string data = install?.Data;
            var answer = MessageBox.Show(
                "Remove WindowsGSM from this computer?\n\nOnly the app, its shortcuts and start-up entries are removed. Your game servers, backups and settings stay where they are"
                + (data != null ? $" ({data})" : "") + " — reinstall any time to pick them up again.\n\nGame servers that are running keep running; stop them first if you're removing them for good.",
                "Uninstall WindowsGSM", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) { return 1; }

            // Stop the app and agent running from this install.
            foreach (string name in new[] { "WindowsGSM", "wgsm-agent" })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id != Process.GetCurrentProcess().Id && p.MainModule.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(10000); }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            Shell.Unregister();
            // This exe is running, so its files go a moment after we exit. NEXT: only what setup put there — never
            // the whole folder (someone may have installed into a folder with other things in it) — and the folder
            // itself only if that leaves it empty.
            var steps = new System.Collections.Generic.List<string> { "ping -n 3 127.0.0.1 >nul" };
            foreach (string entry in Install.OwnEntries)
            {
                string path = Path.Combine(root, entry);
                if (Directory.Exists(path)) { steps.Add($"rmdir /s /q \"{path}\""); }
                else { steps.Add($"del /f /q \"{path}\" 2>nul"); }
            }
            steps.Add($"rmdir \"{root}\" 2>nul"); // only if nothing else is left in it
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", steps)) { CreateNoWindow = true, UseShellExecute = false });
            MessageBox.Show("WindowsGSM has been removed." + (data != null ? $"\n\nYour game servers are still in {data}." : ""), "Uninstall WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static int Fail(string message)
        {
            MessageBox.Show(message, "WindowsGSM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        /// <summary>
        /// The logo as a picture. NEXT: the icon's big frames are stored as PNG, which .NET Framework's
        /// Icon.ToBitmap turns into noise — so the largest frame is decoded directly.
        /// </summary>
        public static Image AppLogo()
        {
            try
            {
                byte[] ico;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("WindowsGSM.ico"))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); ico = ms.ToArray(); }
                int count = BitConverter.ToUInt16(ico, 4), best = -1, bestSize = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + i * 16, w = ico[e] == 0 ? 256 : ico[e];
                    if (w > bestSize) { bestSize = w; best = e; }
                }
                int size = BitConverter.ToInt32(ico, best + 8), offset = BitConverter.ToInt32(ico, best + 12);
                bool png = ico[offset] == 0x89 && ico[offset + 1] == (byte)'P' && ico[offset + 2] == (byte)'N' && ico[offset + 3] == (byte)'G';
                if (png) { return new Bitmap(new MemoryStream(ico, offset, size)); }
            }
            catch { /* fall back to the icon */ }
            return AppIcon().ToBitmap();
        }

        public static Icon AppIcon()
        {
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("WindowsGSM.ico")) { return new Icon(s, 48, 48); }
        }
    }
}
