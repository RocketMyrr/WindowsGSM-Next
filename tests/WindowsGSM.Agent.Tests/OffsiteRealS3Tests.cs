using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Off-site backups against a real S3-compatible server (SeaweedFS, see <see cref="TestTools"/>) rather than the
/// small fake one: request signing is checked, a large backup goes up in parts (multipart upload), and one comes
/// back byte for byte. Wrong credentials and a missing bucket are reported plainly.
/// </summary>
[Collection("Agent")]
public class OffsiteRealS3Tests
{
    private readonly AgentFixture _f;
    public OffsiteRealS3Tests(AgentFixture f) => _f = f;

    private static object Settings(string url, string secret = SeaweedS3.SecretKey, string bucket = SeaweedS3.Bucket, int keep = 2) => new
    {
        enabled = true, provider = "other", endpoint = url, region = "us-east-1", bucket,
        accessKeyId = SeaweedS3.AccessKey, secretAccessKey = secret, prefix = "windowsgsm", keepCount = keep,
    };

    [Fact]
    public async Task A_large_backup_goes_up_in_parts_and_comes_back_byte_for_byte()
    {
        using var s3 = await SeaweedS3.StartAsync();
        if (s3 == null) { return; } // SeaweedFS isn't installed here (tools\Get-TestTools.ps1)
        const string id = "103";
        string big = Path.Combine(WindowsGSM.Functions.ServerPath.GetServersServerFiles(id), "world-big.dat");
        try
        {
            // The real service checks the signature: a wrong secret is refused, and said so.
            var wrong = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(s3.Url, secret: "not-the-secret")));
            Assert.False(wrong.GetProperty("ok").GetBoolean());
            Assert.Contains("The storage service said", wrong.GetProperty("message").GetString());
            var noBucket = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(s3.Url, bucket: "no-such-bucket")));
            Assert.False(noBucket.GetProperty("ok").GetBoolean());

            var test = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(s3.Url)));
            Assert.True(test.GetProperty("ok").GetBoolean(), test.GetProperty("message").GetString());
            Assert.Equal(HttpStatusCode.OK, (await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", Settings(s3.Url), AgentFixture.Json)).StatusCode);

            // ~40 MB that won't compress: above the 16 MB where uploads switch to parts.
            var bytes = new byte[40 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(big, bytes);
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = true, KeepCount = 10 }, AgentFixture.Json)).StatusCode);
            await _f.Owner.PostAsync(_f.ServerUrl(id, "/backups"), new { everything = false });
            await Core.Tests.EngineFixture.WaitUntil(() => _f.Context.Engine.Log.Tail(id, 20).Any(l => l.Contains("Off-site: uploaded")), "the backup to be uploaded", 180000);

            var local = await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"));
            string name = local.OrderByDescending(b => b.Created).First().Name;
            string archive = _f.Context.Engine.Backups.ResolveArchive(id, name)!;
            long size = new FileInfo(archive).Length;
            Assert.True(size > 20 * 1024 * 1024, $"the backup should be big enough to go up in parts ({size} bytes)");

            // Listed with its size, then brought back after the PC loses it — the same bytes.
            var remote = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl(id, "/backups/offsite"));
            var listed = remote.GetProperty("backups").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);
            Assert.Equal(size, listed.GetProperty("size").GetInt64());
            string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive)));
            Core.Tests.TestData.DeleteFile(archive);
            Assert.Equal(HttpStatusCode.Accepted, (await _f.Owner.PostAsync(_f.ServerUrl(id, $"/backups/offsite/{Uri.EscapeDataString(name)}/download"))).StatusCode);
            await Core.Tests.EngineFixture.WaitUntil(() => File.Exists(archive), "the backup to come back", 120000);
            Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive))));
        }
        finally
        {
            Core.Tests.TestData.DeleteFile(big);
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = false }, AgentFixture.Json);
            await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", new { enabled = false, provider = "b2", keepCount = 7 }, AgentFixture.Json);
            foreach (var b in await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"))) { await _f.Owner.DeleteAsync(_f.ServerUrl(id, $"/backups/{Uri.EscapeDataString(b.Name)}")); }
        }
    }
}
