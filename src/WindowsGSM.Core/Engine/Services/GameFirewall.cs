#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>What Windows Firewall does with a server's program.</summary>
    /// <param name="Program">The game's program file (null when the game doesn't name one).</param>
    /// <param name="State">allowed, blocked (a rule blocks it — often someone pressed Cancel on Windows' own
    /// prompt), missing (no rule: players on other computers can't connect), unknown (couldn't read the rules).</param>
    public sealed record FirewallStatus(string? Program, string State, string Message);

    /// <summary>
    /// Windows Firewall rules for game servers. The legacy app added an "authorized application" on every
    /// start, which only works as administrator — it always ran elevated. Next runs as the signed-in user, so:
    /// rules are read (no rights needed), added silently when the agent happens to be elevated, and otherwise
    /// added on request through ONE Windows administrator prompt for any number of servers. Rules are inbound
    /// "allow this program" (all protocols and ports — games use UDP and change ports), named
    /// "WindowsGSM - &lt;server&gt; (#id)" in the "WindowsGSM" group, and removed when a server is deleted.
    /// </summary>
    public static class GameFirewall
    {
        public const string Group = "WindowsGSM";
        private const int In = 1, Allow = 1, Block = 0;

        public static bool IsElevated
        {
            get
            {
                try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
                catch { return false; }
            }
        }

        /// <summary>The server's program file (from its game's StartPath), or null.</summary>
        public static string? ProgramOf(ServerInstance s, PluginCatalog plugins)
        {
            try
            {
                dynamic? game = plugins.Create(s.Game, s.Config);
                if (game == null) { return null; }
                string startPath = Dyn.Get((object)game, "StartPath")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(startPath)) { return null; }
                return Path.GetFullPath(ServerPath.GetServersServerFiles(s.Id, startPath));
            }
            catch { return null; }
        }

        public static string RuleName(ServerInstance s)
        {
            var clean = new StringBuilder();
            foreach (char c in s.Name) { if (char.IsLetterOrDigit(c) || " -_.()[]'".IndexOf(c) >= 0) { clean.Append(c); } }
            string name = clean.ToString().Trim();
            if (name.Length > 60) { name = name.Substring(0, 60); }
            return $"WindowsGSM - {(name.Length == 0 ? "server" : name)} (#{s.Id})";
        }

        /// <summary>
        /// Every enabled inbound program rule (program, allows?) — read once and passed to <see cref="Status"/> when
        /// checking several servers: Windows has hundreds of rules. Null when they can't be read.
        /// </summary>
        public static IReadOnlyList<(string Program, bool Allows)>? ReadRules()
        {
            try
            {
                var list = new List<(string, bool)>();
                foreach (dynamic rule in Rules())
                {
                    try
                    {
                        if (!(bool)rule.Enabled || (int)rule.Direction != In) { continue; }
                        if (!(rule.ApplicationName is string app) || app.Length == 0) { continue; }
                        int action = (int)rule.Action;
                        if (action == Block || action == Allow) { list.Add((Environment.ExpandEnvironmentVariables(app), action == Allow)); }
                    }
                    catch { /* odd rule */ }
                }
                return list;
            }
            catch { return null; }
        }

        public static FirewallStatus Status(ServerInstance s, PluginCatalog plugins, IReadOnlyList<(string Program, bool Allows)>? rules = null)
        {
            string? program = ProgramOf(s, plugins);
            if (program == null) { return new FirewallStatus(null, "unknown", "This game doesn't name its program, so its firewall rule can't be checked."); }
            rules ??= ReadRules();
            bool? allowed = null, blocked = null;
            if (rules != null)
            {
                var mine = rules.Where(r => string.Equals(r.Program, program, StringComparison.OrdinalIgnoreCase)).ToList();
                allowed = mine.Any(r => r.Allows);
                blocked = mine.Any(r => !r.Allows);
                // The legacy app's rules (Windows' older "allowed programs" list).
                if (allowed == false && LegacyAllowed(program)) { allowed = true; }
            }

            string file = Path.GetFileName(program);
            if (allowed == null) { return new FirewallStatus(program, "unknown", "Couldn't read the Windows Firewall rules."); }
            if (blocked == true) { return new FirewallStatus(program, "blocked", $"A Windows Firewall rule blocks {file} — players on other computers can't connect. (Pressing Cancel on Windows' \"allow access\" prompt creates one.) Allow it through the firewall to replace it."); }
            if (allowed == true) { return new FirewallStatus(program, "allowed", $"Windows Firewall lets {file} in."); }
            return new FirewallStatus(program, "missing", $"Windows Firewall has no rule for {file} — players on other computers probably can't connect. Allow it through the firewall (one Windows prompt).");
        }

        /// <summary>Adds the rule directly — only works when the agent is elevated. Returns an error, or null.</summary>
        public static string? TryAdd(ServerInstance s, string program)
        {
            if (!IsElevated) { return "needs administrator rights"; }
            try
            {
                dynamic policy = Policy();
                try { policy.Rules.Remove(RuleName(s)); } catch { /* none yet */ }
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                rule.Name = RuleName(s);
                rule.Description = $"Lets players reach {s.Name} (added by WindowsGSM).";
                rule.ApplicationName = program;
                rule.Direction = In;
                rule.Action = Allow;
                rule.Enabled = true;
                rule.Profiles = 0x7FFFFFFF; // domain, private and public
                rule.Grouping = Group;
                policy.Rules.Add(rule);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Adds (or replaces) the rules for <paramref name="servers"/> — and removes rules that block them — with
        /// one Windows administrator prompt (none when already elevated). Returns an error, or null.
        /// The prompt appears on this computer's screen: someone has to be there to answer it.
        /// </summary>
        public static async Task<string?> AllowAsync(IReadOnlyList<(ServerInstance Server, string Program)> servers)
        {
            if (servers.Count == 0) { return null; }
            var script = new StringBuilder("@echo off\r\nchcp 65001 >nul\r\n");
            foreach (var (s, program) in servers)
            {
                if (program.IndexOf('"') >= 0) { continue; }
                string name = RuleName(s);
                script.Append($"netsh advfirewall firewall delete rule name=\"{name}\" >nul 2>&1\r\n");
                // Any other inbound rule for this program goes: block rules win over allow rules (that's what
                // Cancel on Windows' own prompt leaves behind), and Windows' own allow rules are replaced by ours.
                script.Append($"netsh advfirewall firewall delete rule name=all dir=in program=\"{program}\" >nul 2>&1\r\n");
                script.Append($"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow program=\"{program}\" enable=yes profile=any description=\"Lets players reach this game server (added by WindowsGSM).\" >nul\r\n");
                script.Append("if errorlevel 1 exit /b 1\r\n");
            }
            script.Append("exit /b 0\r\n");

            string file = Path.Combine(Path.GetTempPath(), $"wgsm-firewall-{Guid.NewGuid():N}.cmd");
            try
            {
                File.WriteAllText(file, script.ToString(), new UTF8Encoding(false));
                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{file}\"")
                {
                    UseShellExecute = true,
                    Verb = IsElevated ? string.Empty : "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                // Starting it waits for the administrator prompt to be answered, so the time limit covers both —
                // and stays under the hub's 2-minute wait when the request came through one.
                var deadline = DateTime.UtcNow.AddSeconds(100);
                var run = Task.Run(() =>
                {
                    using var p = Process.Start(psi);
                    if (p == null) { return (int?)-1; }
                    var left = deadline - DateTime.UtcNow;
                    return left > TimeSpan.Zero && p.WaitForExit((int)left.TotalMilliseconds) ? p.ExitCode : null;
                });
                var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(100))).ConfigureAwait(false);
                if (finished != run)
                {
                    _ = run.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // a late "No" isn't a crash
                    return "Nobody answered the Windows administrator prompt on that computer in time.";
                }
                int? exit = await run.ConfigureAwait(false);
                if (exit == null) { return "Nobody answered the Windows administrator prompt on that computer in time."; }
                return exit == 0 ? null : "Windows couldn't add the firewall rule.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows administrator prompt was cancelled."; }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(file); } catch { /* temp */ } }
        }

        /// <summary>Removes this server's rules (when it's deleted). Silent; only possible when elevated.</summary>
        public static void RemoveFor(string id, string serverFolder)
        {
            if (!IsElevated) { return; }
            try
            {
                dynamic policy = Policy();
                string folder = Path.GetFullPath(serverFolder).TrimEnd('\\') + "\\";
                var names = new List<string>();
                foreach (dynamic rule in Rules())
                {
                    try
                    {
                        string? app = rule.ApplicationName as string;
                        string name = rule.Name as string ?? string.Empty;
                        bool ours = name.StartsWith("WindowsGSM - ", StringComparison.Ordinal) && name.EndsWith($"(#{id})", StringComparison.Ordinal);
                        if (ours || (app != null && Environment.ExpandEnvironmentVariables(app).StartsWith(folder, StringComparison.OrdinalIgnoreCase))) { names.Add(name); }
                    }
                    catch { /* odd rule */ }
                }
                foreach (string name in names.Distinct()) { try { policy.Rules.Remove(name); } catch { /* gone */ } }
            }
            catch { /* best effort */ }
        }

        private static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;

        private static IEnumerable Rules() => (IEnumerable)Policy().Rules;

        private static bool LegacyAllowed(string program)
        {
            try
            {
                dynamic manager = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwMgr")!)!;
                foreach (dynamic app in (IEnumerable)manager.LocalPolicy.CurrentProfile.AuthorizedApplications)
                {
                    if (string.Equals((string)app.ProcessImageFileName, program, StringComparison.OrdinalIgnoreCase) && (bool)app.Enabled) { return true; }
                }
            }
            catch { /* not available */ }
            return false;
        }
    }
}
