using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Agent.Security;
using WindowsGSM.Engine;

namespace WindowsGSM.Agent;

/// <summary>How the web app is hosted. Tests swap in an in-memory server; the real agent listens with Kestrel.</summary>
public sealed class AgentAppOptions
{
    /// <summary>Configures the web host (tests call UseTestServer here). Null = Kestrel on the configured port.</summary>
    public Action<IWebHostBuilder>? ConfigureHost { get; init; }

    /// <summary>Where agent messages go (the console window, and the structured log).</summary>
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Write the JSON-lines log file (off in tests).</summary>
    public bool FileLog { get; init; } = true;

    /// <summary>Tests: how the hub link reaches this agent's own API (default: the internal loopback listener).</summary>
    public Func<HttpMessageHandler>? LocalApiHandler { get; init; }

    /// <summary>Tests: how the member link opens its WebSocket to the hub.</summary>
    public Hub.MemberLink.Connector? LinkConnector { get; init; }
}

/// <summary>
/// Builds the agent's web app around a running engine: auth, security headers, the v2 API, the event stream
/// and the embedded web UI.
/// </summary>
public static class AgentApp
{
    public const string CsrfHeader = "X-WGSM-CSRF";
    public const string CookieName = "wgsm_session";

    public static async Task<WebApplication> BuildAsync(WgsmEngine engine, AgentAppOptions? options = null)
    {
        options ??= new AgentAppOptions();
        string configDir = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next");
        var settings = AgentSettings.Load(configDir);

        var users = new UserStore(configDir);
        if (!users.Exists || users.Count == 0)
        {
            string legacy = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "webdashboard", "users.json");
            int imported = users.ImportLegacy(legacy, settings.MachineId);
            if (imported > 0) { options.Log($"Adopted {imported} account(s) from the legacy web dashboard — same passwords and two-factor."); }
        }
        try { global::WindowsGSM.Installer.SteamAccount.ImportLegacy(); } catch (Exception ex) { options.Log($"Couldn't read the Steam account: {ex.Message}"); }
        var sessions = new SessionStore(configDir, TimeSpan.FromHours(settings.SessionHours));
        var audit = new AuditLog(configDir, settings.MachineId);

        // ── TLS ──
        CertificateService? certs = null;
        bool https = settings.UseHttps || (settings.AcmeEnabled && !string.IsNullOrWhiteSpace(settings.AcmeDomain));
        if (https && options.ConfigureHost == null)
        {
            certs = new CertificateService(settings, configDir, options.Log);
            if (!await certs.InitializeAsync()) { https = false; }
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        if (options.FileLog) { builder.Logging.AddProvider(new JsonFileLoggerProvider(Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "logs", "agent"))); }

        if (options.ConfigureHost != null)
        {
            options.ConfigureHost(builder.WebHost);
        }
        else
        {
            // Let's Encrypt is only for internet-facing machines, so it implies network exposure.
            var address = settings.ExposeToNetwork || certs?.UsingAcme == true ? IPAddress.Any : IPAddress.Loopback;
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Listen(address, settings.Port, listen =>
                {
                    // A selector (not a fixed certificate) so renewals swap in without a restart.
                    if (https && certs != null) { listen.UseHttps(h => h.ServerCertificateSelector = (_, _) => certs.Current); }
                });
                // Loopback only, random port, plain HTTP: requests relayed by this machine's hub are replayed here.
                k.Listen(IPAddress.Loopback, 0);
            });
        }

        // Cookies survive restarts: the data-protection keys live with the agent's settings.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDir, "keys")))
            .SetApplicationName("WindowsGSM.Agent");

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict; // never sent on cross-site requests
            o.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = sessions.Lifetime;
            o.SlidingExpiration = true;
            // API semantics: status codes, never redirects to a login page.
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            // Every request: the account must still be enabled and the session not revoked or expired.
            o.Events.OnValidatePrincipal = async c =>
            {
                string? name = c.Principal?.Identity?.Name;
                var u = users.Get(name);
                string? sid = c.Principal?.FindFirst(AgentContext.SessionClaim)?.Value;
                if (u is not { Enabled: true } || !sessions.Touch(sid, name, c.HttpContext.Connection.RemoteIpAddress?.ToString()))
                {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
        builder.Services.AddAuthorization();

        // The panel's own files (scripts, styles, icons) go compressed — a phone on mobile data loads it about four
        // times faster. Only those: API answers mix secrets with what a visitor sends, which compression can leak
        // over HTTPS (BREACH), so they stay as they are.
        builder.Services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.MimeTypes = new[] { "text/css", "text/javascript", "application/javascript", "image/svg+xml", "application/manifest+json", "text/html" };
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
        });
        builder.Services.AddRateLimiter(rl =>
        {
            rl.RejectionStatusCode = 429;
            rl.AddPolicy(AuthEndpoints.LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            j.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        var context = new AgentContext(engine, settings, configDir, users, sessions, audit, new MachineMetrics());
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton<SetupTokens>();
        builder.Services.AddSingleton(new GameArt(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, options.Log));
        builder.Services.AddSingleton<EventStream>();
        builder.Services.AddSingleton(new Hub.MachineRegistry(configDir));
        builder.Services.AddSingleton<Hub.PairingCodes>();
        builder.Services.AddSingleton(sp => new Hub.HubLinks(context, sp.GetRequiredService<Hub.MachineRegistry>(), sp.GetRequiredService<EventStream>(), options.Log));
        builder.Services.AddSingleton(_ => new MetricsHistory(context));
        builder.Services.AddSingleton(sp => new GamePerformance(context, sp.GetRequiredService<MetricsHistory>()));
        builder.Services.AddSingleton(_ => new PluginStore(context));
        builder.Services.AddSingleton(sp => new Notifications.NotificationCentre(context, sp.GetRequiredService<EventStream>(), sp.GetRequiredService<Hub.MachineRegistry>()));
        builder.Services.AddSingleton(sp => new Notifications.NotificationChannels(context, sp.GetRequiredService<Notifications.NotificationCentre>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, options.Log));
        builder.Services.AddSingleton(_ => new RestartWarnings(context));
        builder.Services.AddSingleton(sp => new SelfUpdate(context, sp.GetRequiredService<Notifications.NotificationCentre>(), options.Log));
        builder.Services.AddSingleton(_ => new PortForwarding(context));
        builder.Services.AddSingleton(_ => new ServerTemplates(context));
        builder.Services.AddSingleton(_ => new Modrinth());
        builder.Services.AddSingleton(_ => new ArkTools(context));
        builder.Services.AddSingleton(sp => new Reachability(context) { Forwarding = sp.GetRequiredService<PortForwarding>() });
        builder.Services.AddSingleton<ConfigHistory>();
        builder.Services.AddSingleton(_ => new Workshop(context));
        builder.Services.AddSingleton(sp => new Automations(context, sp.GetRequiredService<Notifications.NotificationCentre>()));
        builder.Services.AddSingleton(sp => new DiskSpace(context, sp.GetRequiredService<SelfUpdate>()));
        builder.Services.AddSingleton(sp => new UpdateWatch(context, sp.GetRequiredService<Notifications.NotificationCentre>()));
        HttpMessageHandler? sharedLocal = null;
        Uri localBase = new("http://127.0.0.1/");
        // This agent's own API over the internal loopback listener (the address is known once it's listening).
        Func<HttpClient> localClient = () => options.LocalApiHandler != null
            ? new HttpClient(options.LocalApiHandler(), disposeHandler: true) { BaseAddress = new Uri("http://localhost/") }
            : new HttpClient(sharedLocal ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: false) { BaseAddress = localBase, Timeout = Timeout.InfiniteTimeSpan };
        builder.Services.AddSingleton(_ => new Hub.MemberLink(context, localClient, options.LinkConnector, options.Log));
        builder.Services.AddSingleton(_ => new LocalApi(context, localClient));
        builder.Services.AddSingleton(sp => new Discord.DiscordBotService(context, sp.GetRequiredService<LocalApi>(), options.Log));
        if (certs != null) { builder.Services.AddSingleton(certs); }

        var app = builder.Build();

        app.UseRateLimiter();
        app.Use(SecurityHeaders(() => https && certs?.Trusted == true));
        app.Use(CsrfGuard);
        app.UseWebSockets();

        // The web UI, read straight out of this assembly (never a temp folder). For UI development only,
        // WGSM_WEB_ROOT points at a wwwroot folder on disk so edits show on refresh without a rebuild.
        string? devRoot = Environment.GetEnvironmentVariable("WGSM_WEB_ROOT");
        IFileProvider ui = !string.IsNullOrWhiteSpace(devRoot) && Directory.Exists(devRoot)
            ? new PhysicalFileProvider(Path.GetFullPath(devRoot))
            : new ManifestEmbeddedFileProvider(typeof(AgentApp).Assembly, "wwwroot");
        if (ui is PhysicalFileProvider) { options.Log($"UI served from {devRoot} (WGSM_WEB_ROOT) — development mode."); }
        app.UseResponseCompression();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
        // Always revalidate (cheap with ETags): after an upgrade nobody keeps running yesterday's UI code.
        static void NoCache(Microsoft.AspNetCore.StaticFiles.StaticFileResponseContext c) =>
            c.Context.Response.Headers.CacheControl = "no-cache";
        app.UseStaticFiles(new StaticFileOptions { FileProvider = ui, OnPrepareResponse = NoCache });

        app.UseAuthentication();
        app.UseAuthorization();
        // On a hub: calls for other machines go down their links.
        app.Use(Hub.Forwarder.InvokeAsync);

        var api = app.MapGroup("/api/v2");
        AuthEndpoints.Map(api);
        MachineEndpoints.Map(api);
        var server = ServerEndpoints.Map(api);
        ServerDataEndpoints.Map(server);
        GameConfigEndpoints.Map(server);
        HistoryEndpoints.Map(api, server);
        WarningEndpoints.Map(server);
        FirewallEndpoints.Map(api, server);
        DiskEndpoints.Map(api);
        ConfigHistoryEndpoints.Map(server);
        TransferEndpoints.Map(api, server);
        AutomationEndpoints.Map(api);
        PasskeyEndpoints.Map(api);
        WorkshopEndpoints.Map(server);
        SteamEndpoints.Map(api);
        TemplateEndpoints.Map(api, server);
        MinecraftEndpoints.Map(server);
        ArkEndpoints.Map(api, server);
        PluginEndpoints.Map(api);
        AdminEndpoints.Map(api);
        HubEndpoints.Map(api);
        NotificationEndpoints.Map(api);
        DiscordEndpoints.Map(api);
        UpdateEndpoints.Map(api);
        LocalEndpoints.Map(api, LocalEndpoints.WriteKey(configDir));
        api.Map("/events", (HttpContext http, EventStream stream) => stream.HandleAsync(http));
        api.MapFallback((HttpContext _) => ApiResults.NotFound("No such API endpoint."));

        // Client-side routes fall back to the UI's index.html.
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = ui, OnPrepareResponse = NoCache });

        // Live answers to plugin questions now that there's a UI to ask.
        engine.Prompts.Enable();

        _ = app.Services.GetRequiredService<EventStream>(); // start recording events before anyone connects
        _ = app.Services.GetRequiredService<Hub.HubLinks>();
        _ = app.Services.GetRequiredService<Notifications.NotificationChannels>(); // and with it the notification centre
        _ = app.Services.GetRequiredService<MetricsHistory>();
        _ = app.Services.GetRequiredService<GamePerformance>(); // asks games for server FPS / TPS each minute
        app.Services.GetRequiredService<UpdateWatch>().Start();
        _ = app.Services.GetRequiredService<RestartWarnings>();
        _ = app.Services.GetRequiredService<SelfUpdate>();
        _ = app.Services.GetRequiredService<Automations>(); // starts watching
        _ = app.Services.GetRequiredService<PortForwarding>(); // forwards router ports as servers start
        engine.Lifecycle.PreStartSteps.Add(new WorkshopBeforeStart(app.Services.GetRequiredService<Workshop>()));
        // Once listening: find the internal loopback address, then connect to our hub (if this machine joined one).
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
            string? internalAddress = addresses?.FirstOrDefault(a => a.StartsWith("http://127.0.0.1:") && !a.EndsWith(":" + settings.Port));
            if (internalAddress != null) { localBase = new Uri(internalAddress + "/"); }
            app.Services.GetRequiredService<Hub.MemberLink>().Start();
            _ = app.Services.GetRequiredService<Discord.DiscordBotService>().StartAsync();
        });
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<Hub.MemberLink>().StopAsync().GetAwaiter().GetResult());
        if (users.Count == 0)
        {
            options.Log($"First run: open the agent on this machine to create the owner account, or use setup code {app.Services.GetRequiredService<SetupTokens>().Current} from another computer.");
        }
        return app;
    }

    /// <summary>
    /// Defensive headers. HSTS only with a CA-trusted certificate: sent with a self-signed one it permanently
    /// blocks the browser's "proceed anyway" and can't be undone from the server.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> SecurityHeaders(Func<bool> trustedHttps) => async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: https://cdn.modrinth.com; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (trustedHttps()) { h["Strict-Transport-Security"] = "max-age=31536000"; }
        await next(ctx);
    };

    /// <summary>
    /// CSRF: state-changing API calls must carry a custom header. A page on another site can't add one without
    /// a CORS preflight, which is never granted. (The cookie is SameSite=Strict as well.)
    /// </summary>
    private static async Task CsrfGuard(HttpContext ctx, RequestDelegate next)
    {
        string m = ctx.Request.Method;
        bool unsafeMethod = HttpMethods.IsPost(m) || HttpMethods.IsPut(m) || HttpMethods.IsPatch(m) || HttpMethods.IsDelete(m);
        if (unsafeMethod && ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Headers.ContainsKey(CsrfHeader))
        {
            await ApiResults.Error(403, "csrf", $"Missing the {CsrfHeader} header.").ExecuteAsync(ctx);
            return;
        }
        await next(ctx);
    }
}
