using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WindowsGSM.Desktop.Shared;

namespace WindowsGSM.Desktop;

/// <summary>
/// Another PC (or hub) this app controls: its panel's address, a name for the menu, and — when it uses a certificate
/// Windows doesn't trust (self-signed) — the fingerprint accepted the first time, the only one trusted afterwards.
/// </summary>
internal sealed class SavedPc
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Fingerprint { get; set; }
    /// <summary>Sign back in on its own when the session ends (with the key that PC gave this app).</summary>
    public bool StaySignedIn { get; set; } = true;
    /// <summary>That key, encrypted for this Windows account (DPAPI) — never stored readable.</summary>
    public string? SignInKey { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public Uri BaseUri => new(Url);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Key
    {
        get
        {
            if (string.IsNullOrEmpty(SignInKey)) { return null; }
            try { return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(SignInKey), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
            catch { return null; } // another Windows account's, or damaged: sign in again
        }
        set => SignInKey = string.IsNullOrEmpty(value) ? null
            : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("WindowsGSM.Desktop.SignInKey");

    /// <summary>
    /// An HTTP client for this PC that checks its certificate the way the window does: trusted by Windows, or the
    /// exact one accepted for this PC.
    /// </summary>
    public HttpClient Client(System.Net.CookieContainer? jar = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = jar ?? new System.Net.CookieContainer(), UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || (Fingerprint != null && cert != null && CertificateFingerprint.Same(CertificateFingerprint.Of(new X509Certificate2(cert)), Fingerprint)),
        };
        var http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Desktop/2");
        http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
        return http;
    }

    public bool Matches(Uri u) =>
        Uri.TryCreate(Url, UriKind.Absolute, out var b) && string.Equals(b.Scheme, u.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(b.Host, u.Host, StringComparison.OrdinalIgnoreCase) && b.Port == u.Port;
}

/// <summary>What answered at an address.</summary>
internal sealed record ProbeResult(Uri Url, string MachineName, string Version, bool SetupRequired, string? Fingerprint, string? CertificateProblem, bool PlainHttp, bool OnThisNetwork);

/// <summary>Finds WindowsGSM at an address someone typed: "192.168.1.20", "games.example.com", "https://host:9000".</summary>
internal static class PcProbe
{
    public const int DefaultPort = AgentLocator.DefaultPort;

    /// <summary>The addresses to try, best first: what was typed, or https then http when no scheme was given.</summary>
    public static IReadOnlyList<Uri> Candidates(string typed)
    {
        typed = typed.Trim().TrimEnd('/');
        if (typed.Length == 0) { return Array.Empty<Uri>(); }
        bool schemeGiven = typed.Contains("://", StringComparison.Ordinal);
        var list = new List<Uri>();
        foreach (string scheme in schemeGiven ? new[] { "" } : new[] { "https://", "http://" })
        {
            if (!Uri.TryCreate(scheme + typed, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) || u.Host.Length == 0) { continue; }
            // No port typed: the agent's default (an explicit :443 / :80 is kept as typed).
            bool portTyped = HasExplicitPort(scheme + typed, u);
            var b = new UriBuilder(u.Scheme, u.Host, portTyped ? u.Port : DefaultPort, "/");
            list.Add(b.Uri);
        }
        return list;
    }

    private static bool HasExplicitPort(string text, Uri u)
    {
        string afterScheme = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        string authority = afterScheme.Split('/', '?', '#')[0];
        if (authority.StartsWith('[')) { return authority.Contains("]:", StringComparison.Ordinal); } // [::1]:9000
        return authority.Contains(':');
    }

    /// <summary>
    /// Asks each candidate for /api/v2/info (nothing secret is sent). Returns what answered, or throws with a message
    /// worth showing. Certificates aren't checked here — the result says whether Windows trusts it, and its fingerprint.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string typed, CancellationToken token = default)
    {
        var candidates = Candidates(typed);
        if (candidates.Count == 0) { throw new InvalidOperationException("Enter the other PC's address — for example 192.168.1.20, or games.example.com."); }
        Exception? last = null;
        foreach (var url in candidates)
        {
            X509Certificate2? cert = null;
            SslPolicyErrors errors = SslPolicyErrors.None;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, c, _, e) => { cert = c == null ? null : new X509Certificate2(c); errors = e; return true; },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Desktop/2");
            try
            {
                using var res = await http.GetAsync(new Uri(url, "api/v2/info"), token);
                if (!res.IsSuccessStatusCode) { last = new InvalidOperationException($"Something answered at {url}, but it isn't WindowsGSM ({(int)res.StatusCode})."); continue; }
                JsonDocument doc;
                try { doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token)); }
                catch (JsonException) { last = new InvalidOperationException($"Something answered at {url}, but it isn't WindowsGSM."); continue; }
                using var owned = doc;
                var root = doc.RootElement;
                if (!root.TryGetProperty("machine", out _) || !root.TryGetProperty("version", out var v))
                {
                    last = new InvalidOperationException($"Something answered at {url}, but it isn't WindowsGSM.");
                    continue;
                }
                string name = root.TryGetProperty("machineName", out var n) && n.GetString() is { Length: > 0 } s ? s : url.Host;
                bool setup = root.TryGetProperty("setupRequired", out var sr) && sr.ValueKind == JsonValueKind.True;
                bool https = url.Scheme == Uri.UriSchemeHttps;
                string? fingerprint = https && errors != SslPolicyErrors.None && cert != null ? CertificateFingerprint.Of(cert) : null;
                string? problem = !https || errors == SslPolicyErrors.None ? null
                    : errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) && !errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)
                        ? $"its certificate is for a different name than {url.Host}"
                        : "it uses its own certificate (self-signed), which Windows can't check for you";
                return new ProbeResult(url, name, v.GetString() ?? "", setup, fingerprint, problem, !https, OnThisNetwork(url.Host));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException(Explain(candidates[0], last));
    }

    private static string Explain(Uri url, Exception? ex)
    {
        string host = url.Host;
        if (ex is InvalidOperationException ioe) { return ioe.Message; }
        if (ex?.GetBaseException() is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }) { return $"Couldn't find a PC called \"{host}\". Check the spelling, or use its IP address (e.g. 192.168.1.20)."; }
        return $"Nothing answered at {host}:{url.Port}. On that PC: Agent settings → Network → turn on \"Reachable from other computers\", restart its agent, and allow the port through its firewall. Over the internet, the port also has to be forwarded on its router.";
    }

    /// <summary>Your own network (private address or a local name) — plain HTTP there is acceptable, across the internet it isn't.</summary>
    public static bool OnThisNetwork(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) { return true; }
            if (ip.IsIPv4MappedToIPv6) { ip = ip.MapToIPv4(); }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) { return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; }
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
        return !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }
}
