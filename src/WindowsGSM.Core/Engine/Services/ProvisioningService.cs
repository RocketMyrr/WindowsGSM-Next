#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;
using WindowsGSM.Installer;

namespace WindowsGSM.Engine.Services
{
    /// <summary>What to install.</summary>
    /// <param name="Consents">Answers given up front to the plugin's questions (e.g. <see cref="UserPrompt.Keys.Eula"/>).</param>
    public sealed record InstallRequest(string Game, string Name, string? SteamBranch = null, string? SteamBranchPassword = null, IReadOnlyList<string>? Consents = null);

    /// <summary>
    /// Creating, importing and deleting servers. Port of the legacy install/import/delete flows.
    ///
    /// Differences from legacy:
    ///  • server ids are reserved atomically, so installs can run side by side (legacy picked "the first free
    ///    folder" and relied on installs being globally serial to avoid two getting the same id);
    ///  • a failed install removes its half-downloaded folder (legacy left it, and the next install then
    ///    complained the folder existed) and writes a failure report to logs/servers/{id}/;
    ///  • installer output is always drained into the job log — an undrained pipe can hang the installer;
    ///  • plugin questions (EULA, Java) are answered by the consents in the request, never by a popup.
    /// </summary>
    public sealed class ProvisioningService
    {
        private static readonly object _idLock = new object();
        private static readonly HashSet<string> _reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly PluginCatalog _plugins;
        private readonly ServerLog _log;

        public ProvisioningService(ServerRegistry servers, OperationGate gate, JobManager jobs, PluginCatalog plugins, ServerLog log)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _plugins = plugins;
            _log = log;
        }

        // ─────────────────────────────── Install ───────────────────────────────

        public OperationRequest Install(InstallRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name)) { return OperationRequest.Rejected("Give the server a name."); }
            if (_plugins.Create(request.Game, new ServerConfig("0")) == null) { return OperationRequest.Rejected($"Unknown game \"{request.Game}\"."); }

            string? id = ReserveId();
            if (id == null) { return OperationRequest.Rejected($"No free server slots (the limit is {WgsmEnvironment.MaxServers})."); }
            if (!_gate.TryBegin(id, OperationKind.Install, "Install", out var lease, out var blockedBy))
            {
                Release(id);
                return OperationRequest.Rejected($"Busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("install", id, $"Install {request.Name}", async ctx =>
            {
                using (lease)
                {
                    try { return await InstallCoreAsync(id, request, ctx).ConfigureAwait(false); }
                    finally { Release(id); }
                }
            }));
        }

        private async Task<string?> InstallCoreAsync(string id, InstallRequest request, JobContext job)
        {
            var config = new ServerConfig(id);
            config.CreateServerDirectory();
            _log.Write(id, $"Action: Install {request.Game} \"{request.Name}\"");

            dynamic? game = _plugins.Create(request.Game, config);
            if (game == null) { return Fail(id, request, "The game's plugin couldn't be loaded.", null); }

            string appId = Dyn.Get((object)game, "AppId")?.ToString() ?? string.Empty;
            bool steam = !string.IsNullOrWhiteSpace(appId);
            string branch = request.SteamBranch?.Trim() ?? string.Empty;
            if (steam) { SteamCMD.SetPendingSteamBranch(id, branch, request.SteamBranchPassword?.Trim() ?? string.Empty); }

            int? exitCode = null;
            try
            {
                job.Report(0, "Waiting for a download slot");
                await _gate.Downloads.WaitAsync(job.Cancellation).ConfigureAwait(false);
                try
                {
                    var tool = steam ? SteamContentPolicy.Choose(id) : SteamContentTool.DepotDownloader;
                    string stage = steam ? $"Downloading with {tool}" : "Installing";
                    var output = new StringBuilder();

                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        job.Report(0, stage);
                        Process? p;
                        using (UserPrompt.WithConsents(request.Consents))
                        using (DownloadContext.Use(line => { job.Log(line); lock (output) { output.AppendLine(line); } }, pct => job.Report(pct, stage)))
                        {
                            p = await game.Install();
                            if (p != null)
                            {
                                Drain(p, job, output);
                                await p.WaitForExitAsync(job.Cancellation).ConfigureAwait(false);
                                exitCode = p.ExitCode;
                            }
                        }

                        // SteamCMD sometimes needs a first-run configuration pass before it installs (legacy retry).
                        bool retry = attempt == 1 && !(bool)game.IsInstallValid() && output.ToString().IndexOf("Missing configuration", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!retry) { break; }
                        _log.Write(id, "[NOTICE] SteamCMD missing configuration on first install attempt; retrying once.");
                    }
                }
                finally { _gate.Downloads.Release(); }

                job.Report(100, "Checking the installed files");
                if (!(bool)game.IsInstallValid())
                {
                    string pluginError = Dyn.Get((object)game, "Error") as string ?? string.Empty;
                    string reason = exitCode is int code && code != 0
                        ? $"The installer exited with code {code}."
                        : !string.IsNullOrWhiteSpace(pluginError) ? pluginError
                        : "The download finished, but the server files the game needs weren't found.";
                    return Fail(id, request, reason, exitCode);
                }

                // Same config creation as legacy: ports allocated against every other configured server.
                config.SetData(request.Game, request.Name, game);
                config.SteamBranch = branch;
                config.SteamBranchPassword = request.SteamBranchPassword?.Trim() ?? string.Empty;
                config.SteamBranchLastInstalled = branch;
                config.CreateWindowsGSMConfig();

                try
                {
                    dynamic? configured = _plugins.Create(request.Game, new ServerConfig(id));
                    configured?.CreateServerCFG();
                }
                catch { /* optional per plugin */ }

                var instance = _servers.Add(id);
                _log.Write(id, "Install: Success");
                job.Log($"Installed as server #{id} on port {instance.Config.ServerPort}.");
                return null;
            }
            catch (OperationCanceledException)
            {
                Cleanup(id);
                _log.Write(id, "Install: Cancelled");
                throw;
            }
            catch (Exception ex)
            {
                return Fail(id, request, ex.Message, exitCode);
            }
            finally
            {
                if (steam) { SteamCMD.SetPendingSteamBranch(id, string.Empty, string.Empty); }
            }
        }

        // ─────────────────────────────── Import ───────────────────────────────

        /// <summary>Copies an existing server installation from <paramref name="sourceDir"/> into a new server.</summary>
        public OperationRequest Import(string game, string name, string sourceDir)
        {
            if (string.IsNullOrWhiteSpace(name)) { return OperationRequest.Rejected("Give the server a name."); }
            if (!Directory.Exists(sourceDir)) { return OperationRequest.Rejected("That folder doesn't exist."); }

            string? id = ReserveId();
            if (id == null) { return OperationRequest.Rejected($"No free server slots (the limit is {WgsmEnvironment.MaxServers})."); }
            var config = new ServerConfig(id);
            dynamic? plugin = _plugins.Create(game, config);
            if (plugin == null) { Release(id); return OperationRequest.Rejected($"Unknown game \"{game}\"."); }
            if (!(bool)plugin.IsImportValid(sourceDir))
            {
                Release(id);
                return OperationRequest.Rejected(Dyn.Get((object)plugin, "Error") as string ?? "That folder doesn't look like a server for this game.");
            }
            if (!_gate.TryBegin(id, OperationKind.Import, "Import", out var lease, out var blockedBy))
            {
                Release(id);
                return OperationRequest.Rejected($"Busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("import", id, $"Import {name}", async ctx =>
            {
                using (lease)
                {
                    try
                    {
                        config.CreateServerDirectory();
                        string target = ServerPath.GetServersServerFiles(id);
                        _log.Write(id, $"Action: Import {game} \"{name}\" from {sourceDir}");
                        await Task.Run(() => CopyTree(sourceDir, target, ctx), ctx.Cancellation).ConfigureAwait(false);

                        config.SetData(game, name, plugin);
                        config.CreateWindowsGSMConfig();
                        _servers.Add(id);
                        _log.Write(id, "Import: Success");
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Cleanup(id);
                        _log.Write(id, "[ERROR] Import failed: " + ex.Message);
                        return "Couldn't copy the server files: " + ex.Message;
                    }
                    finally { Release(id); }
                }
            }));
        }

        // ─────────────────────────────── Clone, export, import ───────────────────────────────

        /// <summary>Config files that belong to one server and don't travel with a copy.</summary>
        private static readonly string[] NotCopied = { "history", "cache" };

        /// <summary>
        /// A copy of a stopped server — files and settings — as a new server here, named <paramref name="name"/>,
        /// on the next free ports, with auto-start off (two copies starting together would clash).
        /// </summary>
        public OperationRequest Clone(string sourceId, string name)
        {
            var src = _servers.Get(sourceId);
            if (src == null) { return OperationRequest.Rejected($"Server {sourceId} doesn't exist."); }
            if (src.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {src.Name} first — copying a running server copies files mid-write."); }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) { return OperationRequest.Rejected("Give the copy a name (up to 100 characters)."); }
            if (!_gate.TryBegin(sourceId, OperationKind.Import, "Being copied", out var sourceLease, out var busy)) { return OperationRequest.Rejected($"{src.Name} is busy: {busy}."); }

            string? id = ReserveId();
            if (id == null) { sourceLease?.Dispose(); return OperationRequest.Rejected($"No free server slots (the limit is {WgsmEnvironment.MaxServers})."); }
            if (!_gate.TryBegin(id, OperationKind.Import, "Copy", out var lease, out var blockedBy)) { sourceLease?.Dispose(); Release(id); return OperationRequest.Rejected($"Busy: {blockedBy}."); }

            return OperationRequest.Running(_jobs.Start("clone", id, $"Copy {src.Name} as {name}", async ctx =>
            {
                using (sourceLease)
                using (lease)
                {
                    try
                    {
                        _log.Write(id, $"Action: Copy of #{sourceId} {src.Name} as \"{name}\"");
                        await Task.Run(() =>
                        {
                            CopyConfigs(ServerPath.GetServersConfigs(sourceId), ServerPath.GetServersConfigs(id));
                            string files = ServerPath.GetServersServerFiles(sourceId);
                            Directory.CreateDirectory(ServerPath.GetServersServerFiles(id));
                            if (Directory.Exists(files)) { CopyTree(files, ServerPath.GetServersServerFiles(id), ctx); }
                        }, ctx.Cancellation).ConfigureAwait(false);
                        string? problem = Adopt(id, name, fromAnotherMachine: false);
                        if (problem != null) { Cleanup(id); return problem; }
                        ctx.Log($"Copied as server #{id} on port {_servers.Get(id)?.Config.ServerPort}.");
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Cleanup(id);
                        _log.Write(id, "[ERROR] Copy failed: " + ex.Message);
                        return "Couldn't copy the server: " + ex.Message;
                    }
                    finally { Release(id); }
                }
            }));
        }

        /// <summary>
        /// Packs a stopped server (files + settings) into <paramref name="zipPath"/> — the first half of moving it
        /// to another machine. The server is held (can't start) while it's being packed.
        /// </summary>
        public OperationRequest Export(string id, string zipPath)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} first — moving a running server copies files mid-write."); }
            if (!_gate.TryBegin(id, OperationKind.Import, "Being packed to move", out var lease, out var busy)) { return OperationRequest.Rejected($"{s.Name} is busy: {busy}."); }
            return OperationRequest.Running(_jobs.Start("export", id, $"Pack {s.Name} to move", async ctx =>
            {
                using (lease)
                {
                    string partial = zipPath + ".partial";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                        await Task.Run(() =>
                        {
                            using var zip = System.IO.Compression.ZipFile.Open(partial, System.IO.Compression.ZipArchiveMode.Create);
                            var items = new List<(string Source, string Entry)>();
                            string configs = ServerPath.GetServersConfigs(id), files = ServerPath.GetServersServerFiles(id);
                            foreach (string f in Directory.Exists(configs) ? Directory.EnumerateFiles(configs, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                            {
                                string rel = Path.GetRelativePath(configs, f);
                                if (NotCopied.Any(n => rel.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { continue; }
                                items.Add((f, "configs/" + rel.Replace('\\', '/')));
                            }
                            foreach (string f in Directory.Exists(files) ? Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                            {
                                items.Add((f, "serverfiles/" + Path.GetRelativePath(files, f).Replace('\\', '/')));
                            }
                            long total = Math.Max(1, items.Sum(i => new FileInfo(i.Source).Length)), done = 0;
                            ctx.Report(0, "Packing files");
                            foreach (var (source, entry) in items)
                            {
                                ctx.Cancellation.ThrowIfCancellationRequested();
                                // Already-compressed game data barely shrinks; fastest keeps a big move quick.
                                var e = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Fastest);
                                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                using (var dst = e.Open()) { src.CopyTo(dst); }
                                done += new FileInfo(source).Length;
                                ctx.Report((int)(done * 100 / total));
                            }
                        }, ctx.Cancellation).ConfigureAwait(false);
                        File.Move(partial, zipPath, overwrite: true);
                        _log.Write(id, $"Packed to move ({new FileInfo(zipPath).Length / 1048576.0:0.#} MB)");
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        try { File.Delete(partial); } catch { }
                        return "Couldn't pack the server: " + ex.Message;
                    }
                }
            }));
        }

        /// <summary>
        /// The other half of a move: a new server here from a package made by <see cref="Export"/> — on the next
        /// free ports, auto-start off. The game's plugin must be on this machine too.
        /// </summary>
        public OperationRequest ImportPackage(string zipPath, string name)
        {
            if (!File.Exists(zipPath)) { return OperationRequest.Rejected("The package isn't here."); }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) { return OperationRequest.Rejected("Give the server a name (up to 100 characters)."); }
            string? id = ReserveId();
            if (id == null) { return OperationRequest.Rejected($"No free server slots (the limit is {WgsmEnvironment.MaxServers})."); }
            if (!_gate.TryBegin(id, OperationKind.Import, "Unpacking a moved server", out var lease, out var blockedBy)) { Release(id); return OperationRequest.Rejected($"Busy: {blockedBy}."); }
            return OperationRequest.Running(_jobs.Start("import", id, $"Unpack {name}", async ctx =>
            {
                using (lease)
                {
                    try
                    {
                        string root = ServerPath.GetServers(id);
                        await Task.Run(() =>
                        {
                            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                            long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                            ctx.Report(0, "Unpacking files");
                            string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                            foreach (var e in zip.Entries)
                            {
                                ctx.Cancellation.ThrowIfCancellationRequested();
                                string entryName = e.FullName.Replace('\\', '/');
                                if (!(entryName.StartsWith("configs/") || entryName.StartsWith("serverfiles/")) || entryName.EndsWith("/")) { continue; }
                                string target = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
                                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The package contains an unsafe path."); }
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                System.IO.Compression.ZipFileExtensions.ExtractToFile(e, target, overwrite: true);
                                done += e.Length;
                                ctx.Report((int)(done * 100 / total));
                            }
                        }, ctx.Cancellation).ConfigureAwait(false);
                        Directory.CreateDirectory(ServerPath.GetServersServerFiles(id));
                        string? problem = Adopt(id, name, fromAnotherMachine: true);
                        if (problem != null) { Cleanup(id); return problem; }
                        ctx.Log($"Unpacked as server #{id} on port {_servers.Get(id)?.Config.ServerPort}.");
                        return null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Cleanup(id);
                        return "Couldn't unpack the server: " + ex.Message;
                    }
                    finally { Release(id); }
                }
            }));
        }

        /// <summary>
        /// Makes copied files a server of their own: its name, the next free ports (game, query and RCON keep their
        /// spacing), auto-start off, then registered. Returns a problem, or null.
        /// </summary>
        private string? Adopt(string id, string name, bool fromAnotherMachine)
        {
            if (!File.Exists(ServerPath.GetServersConfigs(id, "WindowsGSM.cfg"))) { return "The copy has no server settings (WindowsGSM.cfg)."; }
            var cfg = new ServerConfig(id);
            dynamic? game = _plugins.Create(cfg.ServerGame, cfg);
            if (game == null) { return $"The game \"{cfg.ServerGame}\" isn't available here — install its plugin first."; }

            int increments = 1;
            try { increments = Math.Max(1, (int)game.PortIncrements); } catch { /* default */ }
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerName, name.Trim());
            if (int.TryParse(cfg.ServerPort, out int oldPort))
            {
                int newPort = int.Parse(cfg.GetAvailablePort(cfg.ServerPort, increments));
                int delta = newPort - oldPort;
                ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerPort, newPort.ToString());
                if (int.TryParse(cfg.ServerQueryPort, out int q)) { ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerQueryPort, (q + delta).ToString()); }
                if (int.TryParse(cfg.RconPort, out int r) && r > 0 && delta != 0) { ServerConfig.SetSetting(id, ServerConfig.SettingName.RconPort, (r + delta).ToString()); }
            }
            ServerConfig.SetSetting(id, ServerConfig.SettingName.AutoStart, "0");
            if (fromAnotherMachine)
            {
                // The old machine's address means nothing here.
                ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerIP, cfg.GetIPAddress());
                if (!string.IsNullOrWhiteSpace(cfg.RconIp)) { ServerConfig.SetSetting(id, ServerConfig.SettingName.RconIp, cfg.GetIPAddress()); }
            }
            _servers.Add(id);
            _log.Write(id, $"Copy ready: \"{name}\" on port {new ServerConfig(id).ServerPort} (auto-start off)");
            return null;
        }

        private static void CopyConfigs(string from, string to)
        {
            if (!Directory.Exists(from)) { return; }
            foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(from, f);
                if (NotCopied.Any(n => rel.StartsWith(n + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { continue; }
                string dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }

        // ─────────────────────────────── Delete ───────────────────────────────

        /// <summary>Deletes a stopped server's folder. Its backups are kept.</summary>
        public OperationRequest Delete(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} before deleting it."); }
            if (!_gate.TryBegin(id, OperationKind.Delete, "Delete", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }

            return OperationRequest.Running(_jobs.Start("delete", id, $"Delete {s.Name}", async ctx =>
            {
                using (lease)
                {
                    s.SetState(ServerState.Deleting);
                    _log.Write(id, "Action: Delete");
                    try { new WindowsFirewall(null, ServerPath.GetServers(id)).RemoveRuleEx(); } catch { /* best effort */ }
                    GameFirewall.RemoveFor(id, ServerPath.GetServers(id));
                    await LifecycleService.EndLeftoverProcessesAsync(id).ConfigureAwait(false);
                    await Task.Delay(500).ConfigureAwait(false);

                    string dir = ServerPath.GetServers(id);
                    string? error = null;
                    await Task.Run(() =>
                    {
                        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
                        catch (Exception ex) { error = ex.Message; }
                    }).ConfigureAwait(false);

                    if (error != null && File.Exists(ServerPath.GetServersConfigs(id, "WindowsGSM.cfg")))
                    {
                        s.SetState(ServerState.Stopped);
                        _log.Write(id, "Server: Fail to delete server");
                        _log.Write(id, "[ERROR] " + error);
                        return "Couldn't delete the server folder (something has files open): " + error;
                    }

                    _servers.Remove(id);
                    _log.Write(id, "Server: Deleted server");
                    return null;
                }
            }));
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        /// <summary>Claims the lowest free server id and creates its folder so no one else can take it.</summary>
        private static string? ReserveId()
        {
            lock (_idLock)
            {
                for (int n = 1; n <= WgsmEnvironment.MaxServers; n++)
                {
                    string id = n.ToString();
                    string dir = Path.Combine(WgsmEnvironment.DataRoot, "servers", id);
                    if (_reserved.Contains(id) || Directory.Exists(dir)) { continue; }
                    Directory.CreateDirectory(dir);
                    _reserved.Add(id);
                    return id;
                }
                return null;
            }
        }

        private static void Release(string id)
        {
            lock (_idLock) { _reserved.Remove(id); }
        }

        /// <summary>Records why an install failed and removes the half-installed server.</summary>
        private string Fail(string id, InstallRequest request, string reason, int? exitCode)
        {
            try
            {
                string dir = ServerPath.GetLogs("servers", id);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"install-failure-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                    "WindowsGSM install did not complete.\r\n" +
                    $"Server ID: {id}\r\nGame: {request.Game}\r\nName: {request.Name}\r\n" +
                    $"Steam branch: {(string.IsNullOrWhiteSpace(request.SteamBranch) ? "public" : request.SteamBranch)}\r\n" +
                    $"Installer exit code: {(exitCode?.ToString() ?? "not available")}\r\nReason: {reason}\r\n");
            }
            catch { /* the job error still says why */ }

            Cleanup(id);
            _log.Write(id, "Install: Failed");
            _log.Write(id, "[ERROR] " + reason);
            return reason;
        }

        private static void Cleanup(string id)
        {
            try
            {
                string dir = Path.Combine(WgsmEnvironment.DataRoot, "servers", id);
                if (Directory.Exists(dir) && !File.Exists(Path.Combine(dir, "configs", "WindowsGSM.cfg"))) { Directory.Delete(dir, recursive: true); }
            }
            catch { /* in use — a later install skips the folder */ }
        }

        /// <summary>Reads an installer's redirected output if nobody else is (DepotDownloader already reads its own).</summary>
        private static void Drain(Process p, JobContext job, StringBuilder output)
        {
            void OnData(object _, DataReceivedEventArgs e)
            {
                if (e.Data == null) { return; }
                job.Log(e.Data);
                lock (output) { output.AppendLine(e.Data); }
            }

            try
            {
                if (p.StartInfo.RedirectStandardOutput)
                {
                    p.OutputDataReceived += OnData;
                    try { p.BeginOutputReadLine(); } catch (InvalidOperationException) { p.OutputDataReceived -= OnData; } // already being read
                }
                if (p.StartInfo.RedirectStandardError)
                {
                    p.ErrorDataReceived += OnData;
                    try { p.BeginErrorReadLine(); } catch (InvalidOperationException) { p.ErrorDataReceived -= OnData; }
                }
            }
            catch { /* process already gone */ }
        }

        private static void CopyTree(string source, string target, JobContext job)
        {
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
            long total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
            long done = 0;
            job.Report(0, "Copying server files");
            foreach (string file in files)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                string dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                done += new FileInfo(file).Length;
                job.Report((int)(done * 100 / total));
            }
            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir))); // keep empty folders
            }
        }
    }
}
