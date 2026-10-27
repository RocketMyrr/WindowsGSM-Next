using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Off-site backups to a file server — SFTP, plain FTP and FTPS — against a real one (SFTPGo, see
/// <see cref="TestTools"/>): the connection test, a wrong password said plainly, a backup uploaded, listed and brought
/// back byte for byte, and the server's identity (SFTP host key, self-signed FTPS certificate) pinned on first use.
/// </summary>
[Collection("Agent")]
public class OffsiteFileServerTests
{
    private const string User = "wgsm", Password = "wgsm-test-pw";
    private readonly AgentFixture _f;
    public OffsiteFileServerTests(AgentFixture f) => _f = f;

    /// <summary>SFTPGo's portable mode on loopback: SFTP and FTP (with explicit TLS, from a certificate made here) on one folder.</summary>
    private sealed class FileServer : IDisposable
    {
        private readonly Process _process;
        public string Dir { get; }
        public int SftpPort { get; }
        public int FtpPort { get; }

        private FileServer(Process p, string dir, int sftp, int ftp) { _process = p; Dir = dir; SftpPort = sftp; FtpPort = ftp; }

        /// <summary>Started; tried again on fresh ports if one was taken between choosing it and SFTPGo binding it.</summary>
        public static async Task<FileServer?> StartAsync()
        {
            for (int attempt = 1; ; attempt++)
            {
                try { return await TryStartAsync(); }
                catch (InvalidOperationException) when (attempt < 3) { /* another try on other ports */ }
            }
        }

        private static async Task<FileServer?> TryStartAsync()
        {
            string? exe = TestTools.Find("sftpgo", "sftpgo.exe");
            if (exe == null) { return null; }
            string dir = Path.Combine(Path.GetTempPath(), "wgsm-sftpgo-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            using (var key = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                File.WriteAllText(Path.Combine(dir, "cert.pem"), cert.ExportCertificatePem());
                File.WriteAllText(Path.Combine(dir, "key.pem"), key.ExportPkcs8PrivateKeyPem());
            }
            int[] ports = TestTools.FreePorts(2);
            // FTP's data connections: SFTPGo's default 50000-50100 sits in Windows' range for outgoing connections (and
            // in FreePorts'), so a port already taken there stalled a listing or upload until it timed out.
            int passive = TestTools.FreeRange(20, 30000, 45000);
            var p = TestTools.Start(exe, new[]
            {
                "portable", "-d", Path.Combine(dir, "data"), "-u", User, "-p", Password, "-g", "*",
                "-s", ports[0].ToString(), "--ftpd-port", ports[1].ToString(),
                "--ftpd-cert", Path.Combine(dir, "cert.pem"), "--ftpd-key", Path.Combine(dir, "key.pem"),
            }, dir, new Dictionary<string, string>
            {
                ["SFTPGO_SFTPD__BINDINGS__0__ADDRESS"] = "127.0.0.1", ["SFTPGO_FTPD__BINDINGS__0__ADDRESS"] = "127.0.0.1", // this PC only
                ["SFTPGO_FTPD__PASSIVE_PORT_RANGE__START"] = passive.ToString(), ["SFTPGO_FTPD__PASSIVE_PORT_RANGE__END"] = (passive + 19).ToString(),
            });
            for (int i = 0; i < 60 && !p.HasExited; i++)
            {
                if (Listening(ports[0]) && Listening(ports[1])) { return new FileServer(p, dir, ports[0], ports[1]); }
                await Task.Delay(500);
            }
            string tail = TestTools.Tail(dir, exe);
            TestTools.Stop(p);
            throw new InvalidOperationException("SFTPGo didn't start:" + Environment.NewLine + tail);
        }

        private static bool Listening(int port)
        {
            try { using var c = new System.Net.Sockets.TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
            catch (System.Net.Sockets.SocketException) { return false; }
        }

        public void Dispose()
        {
            TestTools.Stop(_process);
            Core.Tests.TestData.DeleteDirectory(Dir);
        }
    }

    private static object Settings(string provider, int port, string password = Password, int keep = 2) => new
    {
        enabled = true, provider, endpoint = $"127.0.0.1:{port}", accessKeyId = User, secretAccessKey = password, prefix = "backups/wgsm", keepCount = keep,
    };

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftp")]
    [InlineData("ftps")]
    public async Task Backups_go_to_a_file_server_and_come_back(string provider)
    {
        using var server = await FileServer.StartAsync();
        if (server == null) { return; } // SFTPGo isn't installed here (tools\Get-TestTools.ps1)
        int port = provider == "sftp" ? server.SftpPort : server.FtpPort;
        const string id = "103";
        string world = Path.Combine(WindowsGSM.Functions.ServerPath.GetServersServerFiles(id), "world-ftp.dat");
        try
        {
            var wrong = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(provider, port, password: "not-it")));
            Assert.False(wrong.GetProperty("ok").GetBoolean());
            Assert.Contains("refused the username or password", wrong.GetProperty("message").GetString());

            var test = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(provider, port)));
            Assert.True(test.GetProperty("ok").GetBoolean(), test.GetProperty("message").GetString());
            Assert.Equal(provider == "ftp", test.GetProperty("message").GetString()!.Contains("unencrypted")); // plain FTP is called out
            var saved = await ApiClient.Read<JsonElement>(await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", Settings(provider, port), AgentFixture.Json));
            Assert.True(saved.GetProperty("ready").GetBoolean());
            Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_f.Context.ConfigDir, "offsite.json"))); // stored encrypted

            var bytes = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(world, bytes);
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = true, KeepCount = 10 }, AgentFixture.Json);
            int uploadsBefore = _f.Context.Engine.Log.Tail(id, 500).Count(l => l.Contains("Off-site: uploaded"));
            await _f.Owner.PostAsync(_f.ServerUrl(id, "/backups"), new { everything = false });
            await Core.Tests.EngineFixture.WaitUntil(() => _f.Context.Engine.Log.Tail(id, 500).Count(l => l.Contains("Off-site: uploaded")) > uploadsBefore, "this backup to be uploaded", 120000);

            string name = (await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"))).OrderByDescending(b => b.Created).First().Name;
            string archive = _f.Context.Engine.Backups.ResolveArchive(id, name)!;
            Assert.True(File.Exists(Path.Combine(server.Dir, "data", "backups", "wgsm", _f.MachineId, $"server-{id}", name)), "the file is in the folder asked for");
            var listed = (await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl(id, "/backups/offsite"))).GetProperty("backups").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);
            Assert.Equal(new FileInfo(archive).Length, listed.GetProperty("size").GetInt64());

            // An encrypted connection's identity was pinned the first time (plain FTP has none to pin).
            var offsite = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<OffsiteBackups>(_f.App.Services);
            Assert.Equal(provider != "ftp", offsite.Settings.Pin.StartsWith("SHA256:", StringComparison.Ordinal));

            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
            Core.Tests.TestData.DeleteFile(archive);
            Assert.Equal(HttpStatusCode.Accepted, (await _f.Owner.PostAsync(_f.ServerUrl(id, $"/backups/offsite/{Uri.EscapeDataString(name)}/download"))).StatusCode);
            await Core.Tests.EngineFixture.WaitUntil(() => File.Exists(archive), "the backup to come back", 60000);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))));

            // Someone else answering at that address (a different key): refused, nothing sent.
            if (provider != "ftp")
            {
                offsite.Settings.Pin = "SHA256:not-the-servers";
                var refused = await _f.Owner.GetAsync(_f.ServerUrl(id, "/backups/offsite"));
                Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
                Assert.Contains("identity changed", await refused.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            Core.Tests.TestData.DeleteFile(world);
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = false }, AgentFixture.Json);
            await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", new { enabled = false, provider = "b2", keepCount = 7 }, AgentFixture.Json);
            foreach (var b in await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"))) { await _f.Owner.DeleteAsync(_f.ServerUrl(id, $"/backups/{Uri.EscapeDataString(b.Name)}")); }
        }
    }
}
