using System.Formats.Cbor;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Passkeys end to end against a simulated authenticator (a real P-256 key, real WebAuthn data and signatures):
/// add one while signed in, sign in with it, and the things that must be refused.
/// </summary>
[Collection("Agent")]
public class PasskeyTests
{
    private readonly AgentFixture _f;
    public PasskeyTests(AgentFixture f) => _f = f;

    private const string Origin = "http://localhost", RpId = "localhost";
    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s) => System.Buffers.Text.Base64Url.DecodeFromChars(s);

    /// <summary>A pretend phone: one key pair, one credential id, a counter.</summary>
    private sealed class Authenticator
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly byte[] CredentialId = RandomNumberGenerator.GetBytes(16);
        public byte[] UserHandle = Array.Empty<byte>();
        public uint Counter;

        private static byte[] AuthData(byte flags, uint counter, byte[]? attested = null)
        {
            var b = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(RpId))) { flags };
            b.AddRange(new[] { (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter });
            if (attested != null) { b.AddRange(attested); }
            return b.ToArray();
        }

        public object Create(JsonElement options)
        {
            UserHandle = UnB64(options.GetProperty("user").GetProperty("id").GetString()!);
            var p = Key.ExportParameters(false);
            var cose = new CborWriter();
            cose.WriteStartMap(5);
            cose.WriteInt32(1); cose.WriteInt32(2);     // kty: EC2
            cose.WriteInt32(3); cose.WriteInt32(-7);    // alg: ES256
            cose.WriteInt32(-1); cose.WriteInt32(1);    // crv: P-256
            cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!);
            cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!);
            cose.WriteEndMap();
            var attested = new List<byte>(new byte[16]) { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length };
            attested.AddRange(CredentialId);
            attested.AddRange(cose.Encode());
            byte[] authData = AuthData(0x45, Counter, attested.ToArray()); // user present + verified + attested data

            var att = new CborWriter();
            att.WriteStartMap(3);
            att.WriteTextString("fmt"); att.WriteTextString("none");
            att.WriteTextString("attStmt"); att.WriteStartMap(0); att.WriteEndMap();
            att.WriteTextString("authData"); att.WriteByteString(authData);
            att.WriteEndMap();
            string clientData = JsonSerializer.Serialize(new { type = "webauthn.create", challenge = options.GetProperty("challenge").GetString(), origin = Origin, crossOrigin = false });
            return new
            {
                id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key",
                response = new { attestationObject = B64(att.Encode()), clientDataJSON = B64(Encoding.UTF8.GetBytes(clientData)), transports = new[] { "internal" } },
                clientExtensionResults = new { },
            };
        }

        public object Get(JsonElement options, ECDsa? signWith = null)
        {
            Counter++;
            byte[] authData = AuthData(0x05, Counter);
            byte[] clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "webauthn.get", challenge = options.GetProperty("challenge").GetString(), origin = Origin, crossOrigin = false }));
            byte[] signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
            byte[] signature = (signWith ?? Key).SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new
            {
                id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key",
                response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = B64(UserHandle) },
                clientExtensionResults = new { },
            };
        }
    }

    [Fact]
    public async Task A_passkey_is_added_then_signs_in_and_forgeries_are_refused()
    {
        var phone = new Authenticator();
        var me = _f.NewClient("203.0.113.40");
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsync("/api/v2/auth/login", new LoginRequest(AgentFixture.OwnerName, AgentFixture.OwnerPassword))).StatusCode);

        // Add: options, the device answers, the agent checks and keeps the public key.
        var options = await ApiClient.Read<JsonElement>(await me.PostAsync("/api/v2/auth/passkeys/options"));
        Assert.Equal(RpId, options.GetProperty("rp").GetProperty("id").GetString());
        var added = await me.PostAsync("/api/v2/auth/passkeys", new { name = "Test phone", response = phone.Create(options) });
        Assert.True(added.StatusCode == HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var list = await me.GetJsonAsync<List<JsonElement>>("/api/v2/auth/passkeys");
        var key = Assert.Single(list, k => k.GetProperty("name").GetString() == "Test phone");

        try
        {
            // Sign in with it — no password, no code.
            var anon = _f.NewClient("203.0.113.41");
            var start = await ApiClient.Read<JsonElement>(await anon.PostAsync("/api/v2/auth/passkey/options"));
            var login = await anon.PostAsync("/api/v2/auth/passkey/login", new { token = start.GetProperty("token").GetString(), response = phone.Get(start.GetProperty("options")) });
            Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
            Assert.Equal(AgentFixture.OwnerName, (await anon.GetJsonAsync<MeDto>("/api/v2/auth/me")).Username);

            // A signature from a different key is refused.
            var thief = _f.NewClient("203.0.113.42");
            var s2 = await ApiClient.Read<JsonElement>(await thief.PostAsync("/api/v2/auth/passkey/options"));
            var forged = await thief.PostAsync("/api/v2/auth/passkey/login", new { token = s2.GetProperty("token").GetString(), response = phone.Get(s2.GetProperty("options"), ECDsa.Create(ECCurve.NamedCurves.nistP256)) });
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await thief.GetAsync("/api/v2/auth/me")).StatusCode);

            // A challenge can't be used twice.
            var s3 = await ApiClient.Read<JsonElement>(await thief.PostAsync("/api/v2/auth/passkey/options"));
            var token = s3.GetProperty("token").GetString();
            var answer = phone.Get(s3.GetProperty("options"));
            Assert.Equal(HttpStatusCode.OK, (await thief.PostAsync("/api/v2/auth/passkey/login", new { token, response = answer })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient("203.0.113.43").PostAsync("/api/v2/auth/passkey/login", new { token, response = answer })).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/v2/auth/passkeys/{key.GetProperty("id").GetString()}")).StatusCode);
        }

        // Removed: it no longer signs in.
        var later = _f.NewClient("203.0.113.44");
        var s4 = await ApiClient.Read<JsonElement>(await later.PostAsync("/api/v2/auth/passkey/options"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await later.PostAsync("/api/v2/auth/passkey/login", new { token = s4.GetProperty("token").GetString(), response = phone.Get(s4.GetProperty("options")) })).StatusCode);
    }
}
