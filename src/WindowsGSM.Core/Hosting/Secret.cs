#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsGSM.Hosting
{
    /// <summary>
    /// Secrets at rest (Discord bot token, webhook URLs, the Steam password, certificate passwords…): encrypted with
    /// Windows' data protection for the account WindowsGSM runs as, so a copied settings file — or another account
    /// on the machine — can't read them. Stored as "dpapi:&lt;base64&gt;"; a plain value (older files) still reads, and is
    /// encrypted the next time the file is saved.
    /// </summary>
    public static class Secret
    {
        private const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsGSM secret v1");

        public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        public static string Protect(string? value)
        {
            if (string.IsNullOrEmpty(value) || IsProtected(value) || !OperatingSystem.IsWindows()) { return value ?? string.Empty; }
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        /// <summary>The plain value. A value encrypted by another Windows account (or machine) reads as empty.</summary>
        public static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value) || !IsProtected(value) || !OperatingSystem.IsWindows()) { return value ?? string.Empty; }
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(value.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { return string.Empty; }
        }
    }

    /// <summary>[JsonConverter(typeof(SecretJsonConverter))] on a string property: encrypted in the file, plain in memory.</summary>
    public sealed class SecretJsonConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : Secret.Unprotect(reader.GetString());

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null) { writer.WriteNullValue(); return; }
            writer.WriteStringValue(Secret.Protect(value));
        }

        public override bool HandleNull => true;
    }
}
