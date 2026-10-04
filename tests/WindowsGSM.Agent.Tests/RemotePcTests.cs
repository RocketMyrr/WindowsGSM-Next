using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Desktop;

namespace WindowsGSM.Agent.Tests;

/// <summary>The app controlling another PC: reading a typed address, finding WindowsGSM there, certificates.</summary>
public class RemotePcTests
{
    [Theory]
    [InlineData("192.168.1.20", "https://192.168.1.20:8971/", "http://192.168.1.20:8971/")]
    [InlineData("  games.example.com/ ", "https://games.example.com:8971/", "http://games.example.com:8971/")]
    [InlineData("games.example.com:443", "https://games.example.com/", "http://games.example.com:443/")]
    [InlineData("https://games.example.com", "https://games.example.com:8971/", null)]
    [InlineData("http://10.0.0.5:9000/panel", "http://10.0.0.5:9000/", null)]
    [InlineData("[fd00::5]:9000", "https://[fd00::5]:9000/", "http://[fd00::5]:9000/")]
    public void A_typed_address_becomes_the_panels_address(string typed, string first, string? second)
    {
        var list = PcProbe.Candidates(typed).Select(u => u.ToString()).ToList();
        Assert.Equal(first, list[0]);
        if (second == null) { Assert.Single(list); } else { Assert.Equal(second, list[1]); }
    }

    [Fact]
    public void Nothing_typed_or_not_a_web_address_gives_nothing_to_try()
    {
        Assert.Empty(PcProbe.Candidates("   "));
        Assert.Empty(PcProbe.Candidates("ftp://files.example.com"));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.4", true)]
    [InlineData("100.101.102.103", true)] // Tailscale and other private overlays
    [InlineData("gamebox", true)]
    [InlineData("gamebox.local", true)]
    [InlineData("fd12::1", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("172.40.0.4", false)]
    [InlineData("games.example.com", false)]
    public void Plain_http_is_acceptable_only_on_your_own_network(string host, bool local) => Assert.Equal(local, PcProbe.OnThisNetwork(host));

    [Fact]
    public void Fingerprints_compare_however_they_are_written()
    {
        Assert.True(CertificateFingerprint.Same("AB:CD:01", "abcd01"));
        Assert.True(CertificateFingerprint.Same("ab cd 01", "AB:CD:01"));
        Assert.False(CertificateFingerprint.Same("AB:CD:01", "AB:CD:02"));
        Assert.False(CertificateFingerprint.Same(null, "AB"));
        Assert.False(CertificateFingerprint.Same("", ""));
    }

    [Fact]
    public async Task Finds_WindowsGSM_over_https_with_its_own_certificate_and_shows_the_fingerprint()
    {
        using var cert = SelfSigned();
        await using var app = await PanelAsync(cert);
        var r = await PcProbe.ProbeAsync($"127.0.0.1:{Port(app)}");
        Assert.Equal("https", r.Url.Scheme);
        Assert.Equal("Game box", r.MachineName);
        Assert.Equal("2.0.0-test", r.Version);
        Assert.False(r.PlainHttp);
        Assert.Equal(CertificateFingerprint.Of(cert), r.Fingerprint);
        Assert.NotNull(r.CertificateProblem);
    }

    [Fact]
    public async Task Falls_back_to_plain_http_and_says_so()
    {
        await using var app = await PanelAsync(null);
        var r = await PcProbe.ProbeAsync($"127.0.0.1:{Port(app)}");
        Assert.Equal("http", r.Url.Scheme);
        Assert.True(r.PlainHttp);
        Assert.True(r.OnThisNetwork);
        Assert.Null(r.Fingerprint);
    }

    [Fact]
    public async Task Something_else_answering_is_not_mistaken_for_WindowsGSM()
    {
        await using var app = await PanelAsync(null, info: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PcProbe.ProbeAsync($"http://127.0.0.1:{Port(app)}"));
        Assert.Contains("isn't WindowsGSM", ex.Message);
    }

    [Fact]
    public async Task Nothing_answering_says_what_to_turn_on_over_there()
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PcProbe.ProbeAsync($"127.0.0.1:{port}"));
        Assert.Contains("Reachable from other computers", ex.Message);
    }

    [Fact]
    public void A_saved_pc_matches_only_its_own_address()
    {
        var pc = new SavedPc { Name = "Box", Url = "https://192.168.1.20:8971/" };
        Assert.True(pc.Matches(new Uri("https://192.168.1.20:8971/api/v2/events")));
        Assert.False(pc.Matches(new Uri("https://192.168.1.20:9000/")));
        Assert.False(pc.Matches(new Uri("http://192.168.1.20:8971/")));
        Assert.False(pc.Matches(new Uri("https://192.168.1.21:8971/")));
    }

    [Fact]
    public void The_sign_in_key_is_stored_encrypted_for_this_windows_account()
    {
        var pc = new SavedPc { Name = "Box", Url = "https://192.168.1.20:8971/" };
        pc.Key = "abc123.0011223344";
        Assert.NotNull(pc.SignInKey);
        Assert.DoesNotContain("0011223344", pc.SignInKey);
        Assert.Equal("abc123.0011223344", pc.Key);
        string json = System.Text.Json.JsonSerializer.Serialize(pc);
        Assert.DoesNotContain("0011223344", json);
        Assert.Equal("abc123.0011223344", System.Text.Json.JsonSerializer.Deserialize<SavedPc>(json)!.Key);
        pc.SignInKey = "bm90IGVuY3J5cHRlZA=="; // tampered or another account's: just signs in again
        Assert.Null(pc.Key);
        pc.Key = null;
        Assert.Null(pc.SignInKey);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=wgsm-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A stand-in agent on a real port: /api/v2/info, over https with <paramref name="cert"/> or plain http.</summary>
    private static async Task<WebApplication> PanelAsync(X509Certificate2? cert, bool info = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => { if (cert != null) { l.UseHttps(cert); } }));
        var app = builder.Build();
        if (info) { app.MapGet("/api/v2/info", () => Results.Json(new AgentInfoDto("m1", "Game box", "2.0.0-test", DateTimeOffset.UtcNow, false), AgentFixture.Json)); }
        else { app.MapGet("/api/v2/info", () => Results.Text("hello")); }
        await app.StartAsync();
        return app;
    }

    private static int Port(WebApplication app) =>
        new Uri(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
}

/// <summary>The agent's side: the app stays signed in; the certificate's fingerprint for owners.</summary>
[Collection("Agent")]
public class RemotePcAgentTests
{
    private readonly AgentFixture _f;
    public RemotePcAgentTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task The_app_keeps_its_sign_in_and_a_browser_does_not()
    {
        async Task<string> SetCookie(string userAgent)
        {
            using var http = new HttpClient(_f.Server.CreateHandler()) { BaseAddress = _f.Server.BaseAddress };
            http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            using var res = await http.PostAsJsonAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, AgentFixture.OwnerPassword), AgentFixture.Json);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return string.Join("; ", res.Headers.GetValues("Set-Cookie"));
        }
        Assert.Contains("expires=", await SetCookie("Mozilla/5.0 (Windows NT 10.0) Edg/130.0 WindowsGSM-Desktop/2"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", await SetCookie("Mozilla/5.0 (Windows NT 10.0) Edg/130.0"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Owners_see_whether_the_panel_uses_a_certificate()
    {
        var res = await _f.Owner.GetAsync("/api/v2/agent/certificate");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(body.GetProperty("https").GetBoolean()); // the test agent runs plain http
        var viewer = await _f.UserAsync("certviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/agent/certificate")).StatusCode);
    }
}
