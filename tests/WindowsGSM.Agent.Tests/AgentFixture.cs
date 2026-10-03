using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent;
using WindowsGSM.Agent.Api;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine;

namespace WindowsGSM.Agent.Tests;

[CollectionDefinition("Agent")]
public class AgentCollection : ICollectionFixture<AgentFixture> { }

/// <summary>
/// One real engine (with the fake game plugin) and one agent app on an in-memory test server, shared by every
/// API test. The owner account is created through the real first-run setup endpoint.
/// </summary>
public sealed class AgentFixture : IAsyncLifetime
{
    public const string OwnerName = "owner";
    public const string OwnerPassword = "correct horse battery";

    /// <summary>Test servers: 101 runs normally, 102 is for permission checks, 103 for file tests.</summary>
    public static readonly string[] ServerIds = { "101", "102", "103" };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public WgsmEngine Engine { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public TestServer Server => (TestServer)App.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    public AgentContext Context => App.Services.GetRequiredService<AgentContext>();
    public string MachineId => Context.MachineId;

    /// <summary>Status of the setup call made without the setup code (checked by a test).</summary>
    public HttpStatusCode SetupWithoutCodeStatus { get; private set; }

    public ApiClient Owner { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        foreach (string id in ServerIds) { EngineFixture.CreateServer(id, mode: id == "101" ? "players" : "long"); }
        Engine = await EngineFixture.StartEngineAsync();
        App = await AgentApp.BuildAsync(Engine, new AgentAppOptions
        {
            FileLog = false,
            // In-process calls to the agent's own API (moves, the Discord bot) go through the test server.
            LocalApiHandler = () => ((TestServer)App.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()).CreateHandler(),
            ConfigureHost = host =>
            {
                host.UseTestServer();
                host.ConfigureServices(s => s.AddSingleton<IStartupFilter, TestIpFilter>());
            },
        });
        await App.StartAsync();

        // First run, from "another computer": the setup code is required.
        var stranger = NewClient("203.0.113.9");
        SetupWithoutCodeStatus = (await stranger.PostAsync("/api/v2/setup", new SetupRequest(OwnerName, OwnerPassword))).StatusCode;

        Owner = NewClient("203.0.113.10");
        string code = App.Services.GetRequiredService<SetupTokens>().Current!;
        var res = await Owner.PostAsync("/api/v2/setup", new SetupRequest(OwnerName, OwnerPassword, "Test Box", code));
        res.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        foreach (var s in Engine.Servers.All) { EngineFixture.KillQuietly(s.Process); }
        await App.StopAsync();
        await App.DisposeAsync();
        Engine.Dispose();
    }

    /// <summary>A browser-like client: keeps cookies, sends the CSRF header, and appears to come from <paramref name="ip"/>.</summary>
    public ApiClient NewClient(string? ip = null) => new(Server, ip ?? $"198.51.100.{Random.Shared.Next(1, 250)}");

    /// <summary>Creates a user (as the owner) and returns a signed-in client for it.</summary>
    public async Task<ApiClient> UserAsync(string name, Role role, Dictionary<string, Capability>? grants = null)
    {
        var res = await Owner.PostAsync("/api/v2/users", new UserRequest(name, role, true, grants, "password-" + name));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var client = NewClient();
        var login = await client.PostAsync("/api/v2/auth/login", new LoginRequest(name, "password-" + name));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    public string ServerUrl(string id, string rest = "") => $"/api/v2/machines/local/servers/{id}{rest}";

    /// <summary>
    /// Stops a server and waits until it has. A stop asked for while the previous start or restart is still
    /// finishing is turned away as busy (on a slow machine that window is wide), so it's asked again until it takes.
    /// </summary>
    public async Task StopAndWait(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (Context.Engine.Servers.Get(id)!.State != WindowsGSM.Engine.Servers.ServerState.Stopped)
        {
            if (DateTime.UtcNow > deadline) { Assert.Fail($"Server {id} didn't stop (still {Context.Engine.Servers.Get(id)!.State})."); }
            if (Context.Engine.Servers.Get(id)!.State == WindowsGSM.Engine.Servers.ServerState.Running) { await Owner.PostAsync(ServerUrl(id, "/stop")); }
            await Task.Delay(500);
        }
    }

    /// <summary>Test-only: take the client address from a header, so each test client has its own rate-limit bucket.</summary>
    private sealed class TestIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                if (IPAddress.TryParse(ctx.Request.Headers["X-Test-Ip"].ToString(), out var ip)) { ctx.Connection.RemoteIpAddress = ip; }
                await nxt(ctx);
            });
            next(app);
        };
    }
}

/// <summary>HttpClient wrapper with a cookie jar, the CSRF header and JSON helpers.</summary>
public sealed class ApiClient
{
    private readonly TestServer _server;
    private readonly CookieContainer _cookies = new();
    public HttpClient Http { get; }
    public string Ip { get; }

    public ApiClient(TestServer server, string ip, bool csrf = true)
    {
        _server = server;
        Ip = ip;
        Http = new HttpClient(new CookieHandler(_cookies) { InnerHandler = server.CreateHandler() }) { BaseAddress = server.BaseAddress };
        Http.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        if (csrf) { Http.DefaultRequestHeaders.Add(AgentApp.CsrfHeader, "1"); }
    }

    public string CookieHeader => _cookies.GetCookieHeader(_server.BaseAddress);

    public Task<HttpResponseMessage> GetAsync(string url) => Http.GetAsync(url);
    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => Http.PostAsJsonAsync(url, body ?? new { }, AgentFixture.Json);
    public Task<HttpResponseMessage> PutAsync(string url, object body) => Http.PutAsJsonAsync(url, body, AgentFixture.Json);
    public Task<HttpResponseMessage> PatchAsync(string url, object body) => Http.PatchAsJsonAsync(url, body, AgentFixture.Json);
    public Task<HttpResponseMessage> DeleteAsync(string url) => Http.DeleteAsync(url);

    public async Task<T> GetJsonAsync<T>(string url)
    {
        var res = await Http.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"GET {url} → {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(AgentFixture.Json))!;
    }

    public static async Task<T> Read<T>(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<T>(AgentFixture.Json))!;

    private sealed class CookieHandler : DelegatingHandler
    {
        private readonly CookieContainer _jar;
        public CookieHandler(CookieContainer jar) => _jar = jar;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string header = _jar.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) { request.Headers.Add("Cookie", header); }
            var response = await base.SendAsync(request, token);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (string c in cookies) { _jar.SetCookies(request.RequestUri!, c); }
            }
            return response;
        }
    }
}
