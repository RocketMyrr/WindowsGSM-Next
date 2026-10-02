using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// The agent's TLS certificate: a supplied PFX/PEM, one from Let's Encrypt (renewed in the background and
/// hot-swapped with no restart), or a persisted self-signed one. Ported from the legacy dashboard, keeping
/// its hard-won rules: keys go in the user key store (machine store needs admin, and fails silently as a
/// broken TLS listener), the key is test-signed before use, and HSTS is only ever sent for a CA-trusted
/// certificate (with a self-signed one it permanently locks browsers out).
/// </summary>
public sealed class CertificateService : IAsyncDisposable
{
    private const X509KeyStorageFlags KeyFlags = X509KeyStorageFlags.Exportable;
    private static readonly TimeSpan RenewCheckEvery = TimeSpan.FromHours(12);

    private readonly AgentSettings _settings;
    private readonly string _configDir;
    private readonly Action<string> _log;
    private readonly AcmeCertificateManager _acme = new();
    private readonly ConcurrentDictionary<string, string> _challenges = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _challengeHost;
    private CancellationTokenSource? _renewLoop;

    public CertificateService(AgentSettings settings, string configDir, Action<string> log)
    {
        _settings = settings;
        _configDir = configDir;
        _log = log;
    }

    /// <summary>The certificate every TLS handshake uses (swapped in place on renewal).</summary>
    public X509Certificate2? Current { get; private set; }

    /// <summary>True for a CA-trusted certificate — the only case where HSTS may be sent.</summary>
    public bool Trusted { get; private set; }

    public bool UsingAcme => _settings.AcmeEnabled && !string.IsNullOrWhiteSpace(_settings.AcmeDomain);

    /// <summary>
    /// Picks and loads the certificate. Returns false if HTTPS can't be served (the caller then serves plain
    /// HTTP and says so, rather than standing up a listener that fails every handshake).
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        X509Certificate2? cert = null;
        bool trusted = false;

        if (UsingAcme)
        {
            if (await EnsureChallengeListenerAsync())
            {
                cert = await _acme.EnsureCertificateAsync(_settings.AcmeDomain, _settings.AcmeEmail, _settings.AcmeStaging, _configDir, _challenges);
                if (cert == null) { _log($"Let's Encrypt certificate unavailable ({_acme.LastError}) — using a self-signed certificate for now."); }
                else { trusted = !_settings.AcmeStaging; }
            }
            else
            {
                _log("Skipping Let's Encrypt — the port-80 challenge listener isn't reachable. Using a self-signed certificate for now.");
            }
            StartRenewLoop();
        }
        else if (!string.IsNullOrWhiteSpace(_settings.CertPath))
        {
            try
            {
                cert = LoadSupplied();
                trusted = true; // a supplied certificate is assumed to be CA-issued
            }
            catch (Exception ex) { _log($"Couldn't load the certificate at {_settings.CertPath}: {ex.Message}. Using a self-signed certificate instead."); }
        }

        cert ??= LoadOrCreateSelfSigned(out trusted);
        if (!CanSign(cert))
        {
            _log("HTTPS was requested but the certificate's private key can't be used by this process (wrong key file, or a key that needs administrator rights). Serving plain HTTP so the agent stays reachable.");
            cert.Dispose();
            return false;
        }
        Current = cert;
        Trusted = trusted;
        return true;
    }

    private X509Certificate2 LoadSupplied()
    {
        string certPath = _settings.CertPath;
        bool hasKey = !string.IsNullOrWhiteSpace(_settings.KeyPath) && File.Exists(_settings.KeyPath);
        if (hasKey || IsPem(certPath))
        {
            string keyPath = hasKey ? _settings.KeyPath : certPath;
            using var fromPem = string.IsNullOrEmpty(_settings.CertPassword)
                ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(certPath, _settings.CertPassword, keyPath);
            // SChannel needs the key re-imported through PFX to use it for TLS.
            return X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pfx), null, KeyFlags);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(certPath, _settings.CertPassword, KeyFlags);
    }

    /// <summary>Self-signed, persisted so browsers only need to accept it once.</summary>
    private X509Certificate2 LoadOrCreateSelfSigned(out bool trusted)
    {
        trusted = false;
        string pfx = Path.Combine(_configDir, "selfsigned.pfx");
        const string password = "wgsm";
        if (File.Exists(pfx))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(pfx, password, KeyFlags); }
            catch { /* corrupt — make a new one */ }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=WindowsGSM Agent ({_settings.MachineName})", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        byte[] bytes = generated.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(pfx, bytes);
        return X509CertificateLoader.LoadPkcs12(bytes, password, KeyFlags);
    }

    private static bool IsPem(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".pem" or ".crt" or ".cer" or ".key") { return true; }
        try
        {
            using var reader = new StreamReader(path);
            char[] buf = new char[64];
            int n = reader.Read(buf, 0, buf.Length);
            return new string(buf, 0, n).Contains("-----BEGIN");
        }
        catch { return false; }
    }

    /// <summary>The key must really sign — a loaded-but-inaccessible key otherwise fails every handshake later.</summary>
    private static bool CanSign(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey) { return false; }
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa != null) { rsa.SignData(new byte[] { 0 }, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); return true; }
            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa != null) { ecdsa.SignData(new byte[] { 0 }, HashAlgorithmName.SHA256); return true; }
            return false;
        }
        catch { return false; }
    }

    // ── Let's Encrypt ──

    /// <summary>
    /// Port-80 host answering HTTP-01 challenges (everything else redirects to the agent). It stays up so
    /// renewals don't need a restart, and it's self-checked before Let's Encrypt is asked — a failed
    /// validation counts against Let's Encrypt's rate limits.
    /// </summary>
    private async Task<bool> EnsureChallengeListenerAsync()
    {
        if (_challengeHost == null)
        {
            string? hint = await Firewall.EnsureAsync(Firewall.AcmeRule, 80);
            if (hint != null) { _log(hint); }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, 80));
            var app = builder.Build();
            app.MapGet("/.well-known/acme-challenge/{token}", (string token) =>
                _challenges.TryGetValue(token, out var keyAuthz) ? Results.Text(keyAuthz, "text/plain") : Results.NotFound());
            app.MapFallback(ctx =>
            {
                ctx.Response.Redirect($"https://{_settings.AcmeDomain}:{_settings.Port}{ctx.Request.Path}{ctx.Request.QueryString}");
                return Task.CompletedTask;
            });
            try
            {
                await app.StartAsync();
                _challengeHost = app;
            }
            catch (Exception ex)
            {
                _log($"Couldn't listen on port 80 for Let's Encrypt: {ex.Message} (IIS or another web server may already be using it).");
                await app.DisposeAsync();
                return false;
            }
        }

        string probe = "wgsm-selfcheck-" + Guid.NewGuid().ToString("N");
        _challenges[probe] = "ok";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var res = await http.GetAsync($"http://127.0.0.1:80/.well-known/acme-challenge/{probe}");
            if (res.IsSuccessStatusCode) { return true; }
            _log($"The Let's Encrypt challenge listener answered {(int)res.StatusCode} instead of 200 — something is intercepting port 80.");
            return false;
        }
        catch (Exception ex)
        {
            _log($"The Let's Encrypt challenge listener didn't answer a local test ({ex.Message}).");
            return false;
        }
        finally { _challenges.TryRemove(probe, out _); }
    }

    private void StartRenewLoop()
    {
        if (_renewLoop != null) { return; }
        _renewLoop = new CancellationTokenSource();
        var token = _renewLoop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(RenewCheckEvery, token); } catch (OperationCanceledException) { return; }
                try { await RenewIfDueAsync(); } catch (Exception ex) { _log($"Certificate renewal check failed: {ex.Message}"); }
            }
        });
    }

    /// <summary>Renews within 30 days of expiry and swaps the new certificate in live.</summary>
    public async Task RenewIfDueAsync()
    {
        if (!UsingAcme || !_acme.NeedsRenewal(_configDir, _settings.AcmeDomain)) { return; }
        await _gate.WaitAsync();
        try
        {
            if (!_acme.NeedsRenewal(_configDir, _settings.AcmeDomain)) { return; }
            _log($"Renewing the Let's Encrypt certificate for {_settings.AcmeDomain}.");
            if (!await EnsureChallengeListenerAsync()) { _log("Renewal skipped — keeping the current certificate."); return; }
            var fresh = await _acme.EnsureCertificateAsync(_settings.AcmeDomain, _settings.AcmeEmail, _settings.AcmeStaging, _configDir, _challenges);
            if (fresh == null || !CanSign(fresh))
            {
                _log($"Renewal failed ({_acme.LastError ?? "the new key isn't usable"}) — keeping the current certificate.");
                fresh?.Dispose();
                return;
            }
            var old = Current;
            Current = fresh;
            Trusted = !_settings.AcmeStaging;
            _log($"Certificate renewed; valid until {fresh.NotAfter:yyyy-MM-dd}.");
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => old?.Dispose()); // let in-flight handshakes finish
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _renewLoop?.Cancel();
        if (_challengeHost != null)
        {
            try { await _challengeHost.StopAsync(); await _challengeHost.DisposeAsync(); } catch { /* shutting down */ }
            _challengeHost = null;
        }
    }
}
