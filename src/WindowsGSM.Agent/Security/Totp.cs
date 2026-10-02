#nullable disable
// Ported unchanged from the legacy dashboard (Functions/Web) — same hash and secret formats, so accounts import as-is.
using System;
using System.Security.Cryptography;
using System.Text;

namespace WindowsGSM.Agent.Security
{
    /// <summary>
    /// RFC 6238 TOTP (SHA-1, 30s period, 6 digits) — compatible with Google Authenticator,
    /// Authy, etc. Implemented locally so there's no external dependency.
    /// </summary>
    public static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string GenerateSecret(int byteLength = 20)
        {
            return Base32Encode(RandomNumberGenerator.GetBytes(byteLength));
        }

        public static bool Verify(string secretBase32, string code, int window = 1) => Verify(secretBase32, code, out _, window);

        // NEXT: also reports which 30-second step matched, so a caller can refuse the same code twice (replay).
        public static bool Verify(string secretBase32, string code, out long matchedStep, int window = 1)
        {
            matchedStep = 0;
            if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code)) { return false; }
            code = code.Trim().Replace(" ", "");

            byte[] key;
            try { key = Base32Decode(secretBase32); }
            catch { return false; }

            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            for (long i = -window; i <= window; i++)
            {
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(Compute(key, step + i)),
                        Encoding.ASCII.GetBytes(code)))
                {
                    matchedStep = step + i;
                    return true;
                }
            }
            return false;
        }

        /// <summary>The current code for a secret (tests and tooling).</summary>
        public static string CurrentCode(string secretBase32) =>
            Compute(Base32Decode(secretBase32), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

        public static string GetUri(string account, string secret, string issuer = "WindowsGSM")
        {
            return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
                   $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
        }

        private static string Compute(byte[] key, long counter)
        {
            byte[] msg = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian) { Array.Reverse(msg); }

            using var hmac = new HMACSHA1(key);
            byte[] hash = hmac.ComputeHash(msg);

            int offset = hash[^1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24)
                       | ((hash[offset + 1] & 0xFF) << 16)
                       | ((hash[offset + 2] & 0xFF) << 8)
                       | (hash[offset + 3] & 0xFF);
            return (binary % 1_000_000).ToString("D6");
        }

        private static string Base32Encode(byte[] data)
        {
            var sb = new StringBuilder();
            int buffer = 0, bitsLeft = 0;
            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;
                while (bitsLeft >= 5)
                {
                    bitsLeft -= 5;
                    sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
                }
            }
            if (bitsLeft > 0)
            {
                sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
            }
            return sb.ToString();
        }

        private static byte[] Base32Decode(string input)
        {
            input = input.Trim().TrimEnd('=').ToUpperInvariant();
            int buffer = 0, bitsLeft = 0;
            var bytes = new System.Collections.Generic.List<byte>(input.Length * 5 / 8);
            foreach (char c in input)
            {
                int val = Base32Alphabet.IndexOf(c);
                if (val < 0) { continue; }
                buffer = (buffer << 5) | val;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
                }
            }
            return bytes.ToArray();
        }
    }
}
