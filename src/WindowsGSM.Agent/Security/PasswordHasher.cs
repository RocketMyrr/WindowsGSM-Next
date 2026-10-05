#nullable disable
// Ported unchanged from the legacy dashboard (Functions/Web) — same hash and secret formats, so accounts import as-is.
using System;
using System.Security.Cryptography;
using System.Text;

namespace WindowsGSM.Agent.Security
{
    /// <summary>
    /// PBKDF2 (SHA-256) password hashing — built into .NET, no external dependency.
    /// Stored format: pbkdf2.sha256$&lt;iterations&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;.
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        // OWASP's current figure for PBKDF2-HMAC-SHA256. Older hashes (100 000, and those adopted from the legacy
        // dashboard) still verify, and are upgraded at the next successful sign-in (NeedsRehash).
        private const int Iterations = 600_000;

        /// <summary>
        /// Checked against when the account doesn't exist, so a wrong username takes as long as a wrong password
        /// (otherwise timing reveals which usernames are real).
        /// </summary>
        public static string Dummy => _dummy.Value;
        private static readonly Lazy<string> _dummy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

        /// <summary>True for a hash made with fewer iterations than new ones get.</summary>
        public static bool NeedsRehash(string encoded)
        {
            string[] parts = (encoded ?? string.Empty).Split('$');
            return parts.Length != 4 || !int.TryParse(parts[1], out int n) || n < Iterations;
        }

        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"pbkdf2.sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string encoded)
        {
            try
            {
                string[] parts = encoded.Split('$');
                if (parts.Length != 4) { return false; }

                int iterations = int.Parse(parts[1]);
                byte[] salt = Convert.FromBase64String(parts[2]);
                byte[] expected = Convert.FromBase64String(parts[3]);

                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Generates a readable random password (no ambiguous chars) for the first-run admin.</summary>
        public static string GenerateReadable(int length = 16)
        {
            const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] bytes = RandomNumberGenerator.GetBytes(length);
            var sb = new StringBuilder(length);
            foreach (byte b in bytes)
            {
                sb.Append(chars[b % chars.Length]);
            }
            return sb.ToString();
        }
    }
}
