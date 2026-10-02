#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>How a command reached the server.</summary>
    public enum CommandRoute { Embedded, Window, Rcon }

    /// <summary>The outcome of sending a console command. <see cref="Reply"/> is only available over RCON.</summary>
    public sealed record CommandResult(bool Sent, CommandRoute? Route, string? Reply, string? Error);

    /// <summary>
    /// Sending console commands to a game server, the best way available:
    ///  • Embedded console (redirected stdin) — output then arrives in the live console buffer;
    ///  • the game's own console window (keystrokes, as legacy did for window-based servers);
    ///  • RCON — Source RCON, or Rust's WebSocket RCON, picked by game — when configured.
    /// Port of legacy WebSendConsoleCommand / SendCommandAsync / the web RCON fallback.
    /// </summary>
    public sealed class ConsoleService
    {
        private readonly ServerRegistry _servers;
        private readonly ServerLog _log;

        public ConsoleService(ServerRegistry servers, ServerLog log)
        {
            _servers = servers;
            _log = log;
        }

        /// <summary>
        /// Sends <paramref name="command"/>. With <paramref name="preferRcon"/> RCON is tried first when
        /// configured (you get a reply); otherwise the server's own console is used when it's running.
        /// </summary>
        public async Task<CommandResult> SendAsync(string id, string command, string? sentBy = null, bool preferRcon = false)
        {
            var s = _servers.Get(id);
            if (s == null) { return new CommandResult(false, null, null, $"Server {id} doesn't exist."); }
            command = command?.Trim() ?? string.Empty;
            if (command.Length == 0) { return new CommandResult(false, null, null, "Empty command."); }
            s.ReloadConfig();

            bool rconConfigured = RconConfigured(s.Config, out _, out _);
            if (preferRcon && rconConfigured) { return await ViaRconAsync(s, command, sentBy).ConfigureAwait(false); }

            var p = s.Process;
            if (p != null && !p.HasExited)
            {
                bool embedded;
                try { embedded = p.StartInfo.RedirectStandardInput; } catch { embedded = false; }
                if (embedded || s.ConsoleWindow != IntPtr.Zero)
                {
                    // ServerConsole.Input: stdin when redirected, otherwise keystrokes into the game's window.
                    s.Console.Input(p, command, s.ConsoleWindow);
                    Audit(s, command, sentBy, embedded ? "Console" : "Window");
                    return new CommandResult(true, embedded ? CommandRoute.Embedded : CommandRoute.Window, null, null);
                }
            }

            if (rconConfigured) { return await ViaRconAsync(s, command, sentBy).ConfigureAwait(false); }
            return new CommandResult(false, null, null, p == null
                ? $"{s.Name} isn't running, and RCON isn't set up for it."
                : $"{s.Name} has no console WindowsGSM can type into, and RCON isn't set up for it.");
        }

        /// <summary>True when RCON details are saved for the server (port set).</summary>
        public static bool RconConfigured(ServerConfig cfg, out string ip, out int port)
        {
            ip = string.IsNullOrWhiteSpace(cfg.RconIp) ? (cfg.ServerIP ?? "127.0.0.1") : cfg.RconIp;
            if (ip == "0.0.0.0") { ip = "127.0.0.1"; }
            return int.TryParse(cfg.RconPort, out port) && port > 0;
        }

        /// <summary>
        /// Asks the game something over RCON without logging it (WindowsGSM's own polling, e.g. server FPS each
        /// minute). Null when RCON isn't set up or didn't answer.
        /// </summary>
        public async Task<string?> QueryRconAsync(string id, string command)
        {
            var s = _servers.Get(id);
            if (s == null || !RconConfigured(s.Config, out string ip, out int port)) { return null; }
            string password = s.Config.RconPassword ?? string.Empty;
            try
            {
                var ask = UsesRustRcon(s.Game) ? RustOneShotAsync(ip, port, password, command) : RconClient.SendCommandAsync(ip, port, password, command);
                var done = await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
                return done == ask ? await ask.ConfigureAwait(false) : null;
            }
            catch { return null; }
        }

        /// <summary>Rust speaks WebSocket RCON; everything else Source RCON.</summary>
        public static bool UsesRustRcon(string game) => game?.IndexOf("Rust", StringComparison.OrdinalIgnoreCase) >= 0;

        private async Task<CommandResult> ViaRconAsync(ServerInstance s, string command, string? sentBy)
        {
            RconConfigured(s.Config, out string ip, out int port);
            string password = s.Config.RconPassword ?? string.Empty;
            string reply;
            try
            {
                reply = UsesRustRcon(s.Game)
                    ? await RustOneShotAsync(ip, port, password, command).ConfigureAwait(false)
                    : await RconClient.SendCommandAsync(ip, port, password, command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new CommandResult(false, CommandRoute.Rcon, null, "RCON error: " + ex.Message);
            }
            Audit(s, command, sentBy, "RCON");
            return new CommandResult(true, CommandRoute.Rcon, reply ?? string.Empty, null);
        }

        /// <summary>Connect, send, collect the asynchronous replies briefly, disconnect (Rust WebRCON).</summary>
        private static async Task<string> RustOneShotAsync(string ip, int port, string password, string command)
        {
            var client = new RustRconClient();
            var sb = new StringBuilder();
            client.MessageReceived += m => { lock (sb) { sb.AppendLine(m); } };
            string error = await client.Connect(ip, port, password).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(error)) { client.Disconnect(); throw new InvalidOperationException(error); }
            await client.Send(command).ConfigureAwait(false);
            await Task.Delay(700).ConfigureAwait(false); // replies arrive asynchronously
            client.Disconnect();
            lock (sb) { return sb.ToString().TrimEnd(); }
        }

        private void Audit(ServerInstance s, string command, string? sentBy, string via) =>
            _log.Write(s.Id, $"[{via}] {(string.IsNullOrWhiteSpace(sentBy) ? "WindowsGSM" : sentBy)} sent: {command}");
    }
}
