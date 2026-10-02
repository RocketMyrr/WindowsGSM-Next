#nullable enable
using System;
using System.Threading.Tasks;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Engine.Watchdog;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine
{
    /// <summary>
    /// The WindowsGSM engine: everything needed to run game servers, with no UI. A host (the agent, a test,
    /// a console harness) creates one with <see cref="StartAsync"/> and talks to its services; everything that
    /// happens is published on <see cref="Events"/>.
    /// </summary>
    public sealed class WgsmEngine : IDisposable
    {
        private readonly Action<string, string> _consoleForwarder;
        private readonly Action<string, string> _joinCodeForwarder;

        private WgsmEngine(CrashLoopOptions? crashLoop)
        {
            Events = new EventBus();
            Servers = new ServerRegistry(Events);
            Operations = new OperationGate();
            Jobs = new JobManager(Events);
            Plugins = new PluginCatalog();
            Log = new ServerLog(Events);
            Lifecycle = new LifecycleService(Servers, Operations, Jobs, Plugins, Log, Events, crashLoop);
            Updates = new UpdateService(Servers, Operations, Jobs, Plugins, Log);
            Backups = new BackupService(Servers, Operations, Jobs, Log);
            Addons = new AddonService(Servers, Operations, Jobs, Log);
            Provisioning = new ProvisioningService(Servers, Operations, Jobs, Plugins, Log);
            Console = new ConsoleService(Servers, Log);
            Monitor = new MonitorService(Servers, Plugins, Lifecycle, Log, Events);
            Scheduler = new SchedulerService(Servers, Operations, Jobs, Lifecycle, Updates, Backups, Console, Plugins, Log, Events);
            Notifications = new NotificationService(Servers, Events);
            Prompts = new PromptBroker(Events);
            Games = new GameCatalog(Plugins);
            Lifecycle.BeforeStop = s => WorldSave.RunAsync(s, Games.Get(s.Game)?.AppId, Console, Log);
            Settings = new ServerSettingsService(Servers, Games, Events);
            Files = new ServerFiles();
            Readiness = new ReadinessService(Servers, Plugins);
            GameConfigs = new GameConfig.GameConfigService(Servers, Files);

            // Pre-start steps, in legacy order: backup → update → add-ons.
            Lifecycle.PreStartSteps.Add(new BackupBeforeStartStep(Backups));
            Lifecycle.PreStartSteps.Add(new UpdateOnStartStep(Updates));
            Lifecycle.PreStartSteps.Add(new UpdateAddonsOnStartStep(Addons));

            // Game output arrives on process-reader threads via the shared console buffers; surface it as events.
            _consoleForwarder = (id, line) => Events.Publish(new ConsoleLineAdded(id, line));
            _joinCodeForwarder = (id, line) => Events.Publish(new ServerAlert(id, AlertKind.JoinCode, "Join code found", line));
            ServerConsole.LineAdded += _consoleForwarder;
            ServerConsole.JoinCodeDetected += _joinCodeForwarder;
        }

        public EventBus Events { get; }
        public ServerRegistry Servers { get; }
        public OperationGate Operations { get; }
        public JobManager Jobs { get; }
        public PluginCatalog Plugins { get; }
        public ServerLog Log { get; }
        public LifecycleService Lifecycle { get; }
        public UpdateService Updates { get; }
        public BackupService Backups { get; }
        public AddonService Addons { get; }
        public ProvisioningService Provisioning { get; }
        public ConsoleService Console { get; }
        public MonitorService Monitor { get; }
        public SchedulerService Scheduler { get; }
        public NotificationService Notifications { get; }

        /// <summary>Live answers to plugin questions. Off until a host with a UI calls <c>Prompts.Enable()</c>.</summary>
        public PromptBroker Prompts { get; }
        public GameCatalog Games { get; }
        public ServerSettingsService Settings { get; }
        public ServerFiles Files { get; }
        public ReadinessService Readiness { get; }

        /// <summary>The game's own config files (server.cfg, server.properties…) as editable settings.</summary>
        public GameConfig.GameConfigService GameConfigs { get; }

        /// <summary>
        /// Starts the background loops (monitoring and scheduling) and auto-starts servers configured to. A host
        /// calls this once it's ready; tests leave the loops off and drive them directly.
        /// </summary>
        public void StartBackgroundServices(bool autoStartServers = true)
        {
            Monitor.Start();
            Scheduler.Start();
            if (autoStartServers) { Scheduler.StartAutoStartServers(); }
        }

        /// <summary>
        /// Opens the data folder, loads plugins and servers, and re-adopts game servers still running from a
        /// previous session. <paramref name="dataRoot"/> may be an existing legacy WindowsGSM data folder.
        /// </summary>
        /// <exception cref="DataRootInUseException">Another WindowsGSM (legacy or Next) manages this folder.</exception>
        public static async Task<WgsmEngine> StartAsync(string dataRoot, CrashLoopOptions? crashLoop = null)
        {
            WgsmEnvironment.Initialize(dataRoot);
            var dataLock = DataRootLock.Acquire(WgsmEnvironment.DataRoot);
            var engine = new WgsmEngine(crashLoop) { _dataLock = dataLock };
            try
            {
                await engine.Plugins.LoadAsync().ConfigureAwait(false);
                engine.Servers.LoadAll();
                engine.Lifecycle.ReattachRunningServers();
            }
            catch
            {
                engine.Dispose();
                throw;
            }
            engine.Log.Write("System", $"Engine {WgsmEnvironment.Version} started — {engine.Servers.All.Count} server(s), {engine.Plugins.Plugins.Count} plugin(s)");
            return engine;
        }

        private DataRootLock? _dataLock;

        public void Dispose()
        {
            Monitor.Dispose();
            Scheduler.Dispose();
            Notifications.Dispose();
            Prompts.Dispose();
            ServerConsole.LineAdded -= _consoleForwarder;
            ServerConsole.JoinCodeDetected -= _joinCodeForwarder;
            _dataLock?.Dispose();
            _dataLock = null;
        }
    }
}
