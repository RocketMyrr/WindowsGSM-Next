using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Also compiled into the desktop app (its own copy, in its own namespace).
#if WGSM_DESKTOP
namespace WindowsGSM.Desktop.Shared;
#else
namespace WindowsGSM.Agent.Hosting;
#endif

/// <summary>
/// A certificate's SHA-256 fingerprint as people compare it ("3F:A2:…"). The agent shows its own under Agent
/// settings → HTTPS; the WindowsGSM app on another PC shows the one it was given the first time it connects, and
/// afterwards trusts only that one. The desktop project compiles this file in.
/// </summary>
public static class CertificateFingerprint
{
    public static string Of(X509Certificate2 cert) => Format(SHA256.HashData(cert.RawData));

    public static string Format(byte[] hash) => string.Join(":", hash.Select(b => b.ToString("X2")));

    /// <summary>Same fingerprint, however it was written (case, colons, spaces).</summary>
    public static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase) && Normalize(a).Length > 0;

    private static string Normalize(string s) => new(s.Where(Uri.IsHexDigit).ToArray());
}
