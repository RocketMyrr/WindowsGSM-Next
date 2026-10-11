using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// HTTPS with Let's Encrypt, against Pebble — Let's Encrypt's own test ACME server (see <see cref="TestTools"/>).
/// The real code runs end to end: account, order, the HTTP-01 challenge answered by the agent's challenge
/// listener and checked by Pebble, the certificate saved and reused on the next start, renewal near expiry, and
/// a failed validation falling back to a self-signed certificate with the reason logged.
/// </summary>
public class AcmePebbleTests
{
    private const string Domain = "localhost";

    /// <summary>Pebble on free loopback ports, serving its API with a certificate made here (so the test can trust it).</summary>
    private sealed class Pebble : IDisposable
    {
        private readonly System.Diagnostics.Process _process;
        public string Dir { get; }
        public Uri Directory { get; }
        public int HttpPort { get; }
        public HttpClient Http { get; }

        private Pebble(System.Diagnostics.Process p, string dir, Uri directory, int httpPort, HttpClient http) { _process = p; Dir = dir; Directory = directory; HttpPort = httpPort; Http = http; }

        public static async Task<Pebble?> StartAsync()
        {
            string? exe = TestTools.Find("pebble", "pebble.exe");
            if (exe == null) { return null; }
            string dir = Path.Combine(Path.GetTempPath(), "wgsm-pebble-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(dir);

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Pebble test API", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(Path.Combine(dir, "cert.pem"), cert.ExportCertificatePem());
            File.WriteAllText(Path.Combine(dir, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            string thumbprint = cert.Thumbprint;

            int[] ports = TestTools.FreePorts(4);
            File.WriteAllText(Path.Combine(dir, "pebble.json"), $$"""
                { "pebble": { "listenAddress": "127.0.0.1:{{ports[0]}}", "managementListenAddress": "127.0.0.1:{{ports[1]}}",
                  "certificate": "cert.pem", "privateKey": "key.pem", "httpPort": {{ports[2]}}, "tlsPort": {{ports[3]}},
                  "ocspResponderURL": "", "externalAccountBindingRequired": false } }
                """);
            // No random delays or refused nonces: the test checks the flow, not Certes' patience.
            var p = TestTools.Start(exe, new[] { "-config", "pebble.json" }, dir,
                new Dictionary<string, string> { ["PEBBLE_VA_NOSLEEP"] = "1", ["PEBBLE_WFE_NONCEREJECT"] = "0" });

            var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == thumbprint }) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-tests/1"); // ACME servers turn away requests without one
            var directory = new Uri($"https://127.0.0.1:{ports[0]}/dir");
            string? last = null;
            for (int i = 0; i < 60 && !p.HasExited; i++)
            {
                try
                {
                    var res = await http.GetAsync(directory);
                    if (res.IsSuccessStatusCode) { return new Pebble(p, dir, directory, ports[2], http); }
                    last = $"{(int)res.StatusCode} {res.ReasonPhrase}";
                }
                catch (HttpRequestException ex) { last = ex.GetType().Name + ": " + ex.Message + " / " + ex.InnerException; }
                await Task.Delay(500);
            }
            string tail = TestTools.Tail(dir, exe);
            TestTools.Stop(p);
            throw new InvalidOperationException($"Pebble didn't answer ({last}):" + Environment.NewLine + tail);
        }

        public void Dispose()
        {
            Http.Dispose();
            TestTools.Stop(_process);
            Core.Tests.TestData.DeleteDirectory(Dir);
        }
    }

    private static CertificateService Service(Pebble pebble, string configDir, List<string> log, int? challengePort = null) =>
        new(new AgentSettings { AcmeEnabled = true, AcmeDomain = Domain, AcmeEmail = "owner@example.com", MachineName = "ACME test" }, configDir, log.Add)
        {
            TestServer = new CertificateService.AcmeTestServer(pebble.Directory, pebble.Http, challengePort ?? pebble.HttpPort),
        };

    [Fact]
    public async Task A_certificate_is_issued_reused_and_renewed()
    {
        using var pebble = await Pebble.StartAsync();
        if (pebble == null) { return; } // Pebble isn't installed here (tools\Get-TestTools.ps1)
        string configDir = Path.Combine(pebble.Dir, "agent-config");
        var log = new List<string>();

        // Issued: Pebble checked the challenge the agent answered, and signed a certificate for the domain.
        string first;
        await using (var service = Service(pebble, configDir, log))
        {
            Assert.True(await service.InitializeAsync(), string.Join(Environment.NewLine, log));
            Assert.NotNull(service.Current);
            Assert.True(service.Current!.Issuer.Contains("Pebble"), "Not issued by Pebble: " + string.Join(Environment.NewLine, log));
            Assert.Contains(Domain, service.Current.GetNameInfo(X509NameType.DnsName, false));
            Assert.True(service.Current.HasPrivateKey);
            Assert.True(service.Trusted);
            first = service.Current.Thumbprint;
        }

        // The next start uses the saved certificate: no new order (rate limits at the real Let's Encrypt).
        await using (var service = Service(pebble, configDir, log))
        {
            Assert.True(await service.InitializeAsync());
            Assert.Equal(first, service.Current!.Thumbprint);

            // Within 30 days of expiry: renewed, and the new one swapped in without a restart.
            string pfx = Path.Combine(configDir, $"acme-{Domain}.pfx");
            using (var rsa = RSA.Create(2048))
            {
                var soon = new CertificateRequest($"CN={Domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(5));
                File.WriteAllBytes(pfx, soon.Export(X509ContentType.Pfx, "wgsm-acme"));
            }
            await service.RenewIfDueAsync();
            Assert.True(log.Any(l => l.StartsWith("Certificate renewed", StringComparison.Ordinal)), string.Join(Environment.NewLine, log));
            Assert.NotEqual(first, service.Current!.Thumbprint);
            Assert.Contains("Pebble", service.Current.Issuer);
            Assert.True(service.Current.NotAfter > DateTime.Now.AddDays(30));
        }
    }

    [Fact]
    public async Task A_failed_validation_falls_back_to_a_self_signed_certificate_and_says_why()
    {
        using var pebble = await Pebble.StartAsync();
        if (pebble == null) { return; }
        var log = new List<string>();
        // The challenge listener answers on a port Pebble doesn't check: as if port 80 weren't forwarded.
        int elsewhere = TestTools.FreePorts(1)[0];
        await using var service = Service(pebble, Path.Combine(pebble.Dir, "agent-config"), log, elsewhere);
        Assert.True(await service.InitializeAsync());
        Assert.False(service.Trusted);
        Assert.DoesNotContain("Pebble", service.Current!.Issuer);
        Assert.Contains(log, l => l.Contains("Let's Encrypt certificate unavailable") && l.Contains("Domain validation failed"));
    }
}
