#nullable disable
// Ported unchanged from the legacy dashboard (Functions/Web/AcmeCertificateManager.cs).
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;

namespace WindowsGSM.Agent.Hosting
{
    /// <summary>
    /// Requests and renews a domain certificate from Let's Encrypt via ACME (RFC 8555), using the
    /// HTTP-01 challenge. The PFX is cached on disk and reused until it's within 30 days of expiry,
    /// so normal app restarts never touch Let's Encrypt's servers (which rate-limit aggressively —
    /// re-issuing on every start would exhaust the weekly-per-domain quota within days).
    /// </summary>
    public class AcmeCertificateManager
    {
        private const string PfxPassword = "wgsm-acme";
        private const int RenewWithinDays = 30;

        // Load into the *user's* key store, not the machine store — MachineKeySet needs admin rights,
        // and when WindowsGSM isn't elevated the private key ends up inaccessible, which surfaces as a
        // silently-broken TLS listener (ERR_INVALID_RESPONSE in the browser).
        private const X509KeyStorageFlags KeyFlags = X509KeyStorageFlags.Exportable;

        /// <summary>Human-readable reason the last obtain/renew attempt failed, or null if it succeeded.</summary>
        public string LastError { get; private set; }

        /// <summary>NEXT: tests only — another ACME server (Pebble) and the client that trusts its certificate.</summary>
        internal Uri DirectoryOverride { get; set; }
        internal HttpClient HttpOverride { get; set; }

        private static string AccountKeyPath(string configDir) => Path.Combine(configDir, "acme-account.pem");
        private static string PfxPath(string configDir, string domain) => Path.Combine(configDir, $"acme-{SanitizeForFilename(domain)}.pfx");

        private static string SanitizeForFilename(string domain)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) { domain = domain.Replace(c, '_'); }
            return domain;
        }

        /// <summary>
        /// Returns a certificate for <paramref name="domain"/>, from the on-disk cache if it's still
        /// valid for a while, otherwise by requesting/renewing one from Let's Encrypt. Returns null
        /// (with <see cref="LastError"/> set) if neither the cache nor a fresh request works.
        /// </summary>
        public async Task<X509Certificate2> EnsureCertificateAsync(
            string domain, string email, bool staging, string configDir, ConcurrentDictionary<string, string> challengeStore)
        {
            LastError = null;
            string pfxPath = PfxPath(configDir, domain);

            if (File.Exists(pfxPath))
            {
                try
                {
                    var cached = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, PfxPassword,
                        KeyFlags);
                    if (cached.NotAfter > DateTime.Now.AddDays(RenewWithinDays))
                    {
                        return cached; // still good for a while — don't touch Let's Encrypt
                    }
                    cached.Dispose();
                }
                catch { /* corrupt/unreadable cache — fall through and re-issue */ }
            }

            return await ObtainAsync(domain, email, staging, configDir, challengeStore);
        }

        /// <summary>True when the cached cert (if any) is within the renewal window or already expired.</summary>
        public bool NeedsRenewal(string configDir, string domain)
        {
            string pfxPath = PfxPath(configDir, domain);
            if (!File.Exists(pfxPath)) { return true; }
            try
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, PfxPassword,
                    KeyFlags);
                return cert.NotAfter <= DateTime.Now.AddDays(RenewWithinDays);
            }
            catch { return true; }
        }

        /// <summary>Certificate expiry, or null if there is no cached cert for this domain.</summary>
        public DateTime? GetExpiry(string configDir, string domain)
        {
            string pfxPath = PfxPath(configDir, domain);
            if (!File.Exists(pfxPath)) { return null; }
            try
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, PfxPassword,
                    KeyFlags);
                return cert.NotAfter;
            }
            catch { return null; }
        }

        private async Task<X509Certificate2> ObtainAsync(
            string domain, string email, bool staging, string configDir, ConcurrentDictionary<string, string> challengeStore)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(domain)) { LastError = "No domain configured."; return null; }
                if (string.IsNullOrWhiteSpace(email)) { LastError = "An email address is required for the Let's Encrypt account."; return null; }

                Uri serverUri = DirectoryOverride ?? (staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2);
                IAcmeHttpClient http = HttpOverride != null ? new AcmeHttpClient(serverUri, HttpOverride) : null;
                AcmeContext acme;
                string accountKeyPath = AccountKeyPath(configDir);
                if (File.Exists(accountKeyPath))
                {
                    var key = KeyFactory.FromPem(File.ReadAllText(accountKeyPath));
                    acme = new AcmeContext(serverUri, key, http);
                }
                else
                {
                    acme = new AcmeContext(serverUri, null, http);
                    await acme.NewAccount(email, true);
                    System.IO.Directory.CreateDirectory(configDir);
                    File.WriteAllText(accountKeyPath, acme.AccountKey.ToPem());
                }

                var order = await acme.NewOrder(new[] { domain });
                var authorizations = await order.Authorizations();
                var authz = authorizations.First();
                // NEXT: the CA can reuse a recent successful validation for the domain (Let's Encrypt does, for a
                // while after one): the authorization is already valid, and asking to validate it again is refused
                // ("Cannot update challenge with status valid") — which failed renewals and re-issues.
                if ((await authz.Resource()).Status != AuthorizationStatus.Valid && await ValidateAsync(authz, domain, challengeStore) is { } failure)
                {
                    LastError = failure;
                    return null;
                }

                var certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                var certChain = await order.Generate(new CsrInfo { CommonName = domain }, certKey);

                // NEXT: the full chain up to a root Certes knows (Let's Encrypt's); for any other CA (a test one) the
                // certificate and the intermediates the ACME server sent.
                var pfx = certChain.ToPfx(certKey);
                byte[] pfxBytes;
                try { pfxBytes = pfx.Build(domain, PfxPassword); }
                catch (Exception) when (DirectoryOverride != null) { pfx.FullChain = false; pfxBytes = pfx.Build(domain, PfxPassword); }
                System.IO.Directory.CreateDirectory(configDir);
                File.WriteAllBytes(PfxPath(configDir, domain), pfxBytes);

                return X509CertificateLoader.LoadPkcs12(pfxBytes, PfxPassword,
                    KeyFlags);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        /// <summary>Answers the HTTP-01 challenge and waits for the CA's verdict. Null when valid, or why not.</summary>
        private static async Task<string> ValidateAsync(IAuthorizationContext authz, string domain, ConcurrentDictionary<string, string> challengeStore)
        {
            var httpChallenge = await authz.Http();
            if (httpChallenge == null)
            {
                return "Let's Encrypt didn't offer an HTTP-01 challenge for this domain.";
            }

            challengeStore[httpChallenge.Token] = httpChallenge.KeyAuthz;
            Challenge status;
            try
            {
                await httpChallenge.Validate();

                status = null;
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(2000);
                    status = await httpChallenge.Resource();
                    if (status.Status == ChallengeStatus.Valid || status.Status == ChallengeStatus.Invalid) { break; }
                }
            }
            finally
            {
                challengeStore.TryRemove(httpChallenge.Token, out _);
            }

            if (status == null || status.Status != ChallengeStatus.Valid)
            {
                string detail = status?.Error?.Detail;
                return "Domain validation failed" + (string.IsNullOrEmpty(detail) ? "." : $": {detail}") +
                    " Check that port 80 is forwarded to this machine from the internet and that " +
                    $"{domain}'s DNS A record points at this server's public IP.";
            }
            return null;
        }
    }
}
