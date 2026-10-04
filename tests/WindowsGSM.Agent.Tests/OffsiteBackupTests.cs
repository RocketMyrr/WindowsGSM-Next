using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Off-site backups against a small fake S3 service (path-style, like MinIO/B2/R2 are used): the connection test,
/// automatic uploads after each backup, keeping the newest N there, listing, and bringing one back.
/// </summary>
[Collection("Agent")]
public class OffsiteBackupTests
{
    private readonly AgentFixture _f;
    public OffsiteBackupTests(AgentFixture f) => _f = f;

    /// <summary>Just enough of S3 for the SDK: PUT/GET/HEAD/DELETE objects and ListObjectsV2. Auth isn't checked.</summary>
    private sealed class FakeS3 : IDisposable
    {
        public readonly ConcurrentDictionary<string, (byte[] Data, DateTime At)> Objects = new();
        private readonly HttpListener _listener = new();
        public string Url { get; }

        public FakeS3()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch { return; }
                try { Handle(c); } catch { c.Response.StatusCode = 500; }
                finally { try { c.Response.Close(); } catch { } }
            }
        }

        private void Handle(HttpListenerContext c)
        {
            string path = Uri.UnescapeDataString(c.Request.Url!.AbsolutePath.TrimStart('/'));
            int slash = path.IndexOf('/');
            string key = slash < 0 ? "" : path[(slash + 1)..];
            var res = c.Response;
            switch (c.Request.HttpMethod)
            {
                case "PUT":
                    using (var ms = new MemoryStream()) { c.Request.InputStream.CopyTo(ms); Objects[key] = (Decode(c.Request, ms.ToArray()), DateTime.UtcNow); }
                    Thread.Sleep(20); // distinct LastModified for the newest-first order
                    res.AddHeader("ETag", ETag(Objects[key].Data));
                    res.StatusCode = 200;
                    return;
                case "DELETE":
                    Objects.TryRemove(key, out _);
                    res.StatusCode = 204;
                    return;
                case "HEAD" or "GET" when key.Length > 0:
                    if (!Objects.TryGetValue(key, out var o)) { res.StatusCode = 404; return; }
                    res.StatusCode = 200;
                    res.ContentLength64 = o.Data.Length;
                    res.AddHeader("ETag", ETag(o.Data));
                    res.AddHeader("Last-Modified", o.At.ToString("R"));
                    if (c.Request.HttpMethod == "GET") { res.OutputStream.Write(o.Data); }
                    return;
                case "GET": // ListObjectsV2
                    string prefix = c.Request.QueryString["prefix"] ?? "";
                    var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated>");
                    foreach (var (k, v) in Objects.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        xml.Append($"<Contents><Key>{SecurityElement(k)}</Key><LastModified>{v.At:yyyy-MM-ddTHH:mm:ss.fffZ}</LastModified><ETag>{System.Security.SecurityElement.Escape(ETag(v.Data))}</ETag><Size>{v.Data.Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
                    }
                    xml.Append("</ListBucketResult>");
                    byte[] bytes = Encoding.UTF8.GetBytes(xml.ToString());
                    res.ContentType = "application/xml";
                    res.StatusCode = 200;
                    res.OutputStream.Write(bytes);
                    return;
                default:
                    res.StatusCode = 400;
                    return;
            }
        }

        /// <summary>As S3 sends it: the MD5 of the object, in quotes (the SDK checks it).</summary>
        private static string ETag(byte[] data) => "\"" + Convert.ToHexString(System.Security.Cryptography.MD5.HashData(data)).ToLowerInvariant() + "\"";

        private static string SecurityElement(string s) => System.Security.SecurityElement.Escape(s)!;

        /// <summary>Undoes aws-chunked encoding (signed chunks over plain HTTP).</summary>
        private static byte[] Decode(HttpListenerRequest request, byte[] body)
        {
            string sha = request.Headers["x-amz-content-sha256"] ?? "";
            if (!sha.StartsWith("STREAMING", StringComparison.Ordinal) && !(request.Headers["Content-Encoding"] ?? "").Contains("aws-chunked")) { return body; }
            var output = new MemoryStream();
            int i = 0;
            while (i < body.Length)
            {
                int lineEnd = IndexOf(body, i, "\r\n"u8);
                string header = Encoding.ASCII.GetString(body, i, lineEnd - i);
                int size = Convert.ToInt32(header.Split(';')[0], 16);
                i = lineEnd + 2;
                if (size == 0) { break; }
                output.Write(body, i, size);
                i += size + 2;
            }
            return output.ToArray();
        }

        private static int IndexOf(byte[] data, int from, ReadOnlySpan<byte> what)
        {
            int at = data.AsSpan(from).IndexOf(what);
            return at < 0 ? data.Length : from + at;
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static object Settings(FakeS3 s3, bool enabled = true, int keep = 2, string? secret = "fake-secret") => new
    {
        enabled, provider = "other", endpoint = s3.Url, region = "us-east-1", bucket = "game-backups",
        accessKeyId = "fake-key-id", secretAccessKey = secret, prefix = "windowsgsm", keepCount = keep,
    };

    [Fact]
    public async Task Backups_go_off_site_the_newest_are_kept_and_one_can_be_brought_back()
    {
        using var s3 = new FakeS3();
        const string id = "102";
        try
        {
            // Set up and test (admins only).
            var member = await _f.UserAsync("offsitemember", Role.Member, new Dictionary<string, Capability> { [$"{_f.MachineId}/{id}"] = Capability.All });
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v2/machines/local/offsite")).StatusCode);
            var test = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", Settings(s3)));
            Assert.True(test.GetProperty("ok").GetBoolean(), test.GetProperty("message").GetString());
            Assert.Empty(s3.Objects); // the test file was removed again
            var saved = await ApiClient.Read<JsonElement>(await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", Settings(s3), AgentFixture.Json));
            Assert.True(saved.GetProperty("ready").GetBoolean());
            Assert.True(saved.GetProperty("hasSecret").GetBoolean());
            Assert.DoesNotContain("fake-secret", saved.GetRawText()); // the secret never comes back
            Assert.DoesNotContain("fake-secret", File.ReadAllText(Path.Combine(_f.Context.ConfigDir, "offsite.json"))); // stored encrypted

            // The server opts in (admins only), and every backup goes up; only the newest 2 are kept there.
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            Assert.True(settingsNow.OffsiteReady);
            Assert.Equal(HttpStatusCode.Forbidden, (await member.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = true }, AgentFixture.Json)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = true, KeepCount = 10 }, AgentFixture.Json)).StatusCode);
            for (int i = 0; i < 3; i++)
            {
                int before = s3.Objects.Count;
                await _f.Owner.PostAsync(_f.ServerUrl(id, "/backups"), new { everything = false });
                await Core.Tests.EngineFixture.WaitUntil(() => _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups")).GetAwaiter().GetResult().Count > i
                    && (i < 2 ? s3.Objects.Count == before + 1 : s3.Objects.Count == 2 && _f.Context.Engine.Log.Tail(id, 10).Any(l => l.Contains("1 older off-site backup(s) removed"))), $"backup {i + 1} uploaded", 60000);
                await Task.Delay(1100); // backups are named by the second
            }
            Assert.All(s3.Objects.Keys, k => Assert.StartsWith($"windowsgsm/{_f.MachineId}/server-{id}/", k));

            // Listed newest first, the same bytes as on this PC.
            var remote = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl(id, "/backups/offsite"));
            var names = remote.GetProperty("backups").EnumerateArray().Select(b => b.GetProperty("name").GetString()!).ToList();
            Assert.Equal(2, names.Count);
            var local = await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"));
            string newest = local.OrderByDescending(b => b.Created).First().Name;
            Assert.Equal(newest, names[0]);
            string archive = _f.Context.Engine.Backups.ResolveArchive(id, newest)!;
            Assert.Equal(File.ReadAllBytes(archive), s3.Objects[$"windowsgsm/{_f.MachineId}/server-{id}/{newest}"].Data);

            // The PC loses it: bring it back, and it's in the backups list again, byte for byte.
            byte[] original = File.ReadAllBytes(archive);
            File.Delete(archive);
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.UserAsync("offsiteviewer", Role.Viewer) is var viewer ? await viewer.PostAsync(_f.ServerUrl(id, $"/backups/offsite/{newest}/download")) : null)!.StatusCode);
            var bring = await _f.Owner.PostAsync(_f.ServerUrl(id, $"/backups/offsite/{Uri.EscapeDataString(newest)}/download"));
            Assert.Equal(HttpStatusCode.Accepted, bring.StatusCode);
            await Core.Tests.EngineFixture.WaitUntil(() => File.Exists(archive), "the backup to come back", 30000);
            Assert.Equal(original, File.ReadAllBytes(archive));
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync(_f.ServerUrl(id, $"/backups/offsite/{Uri.EscapeDataString(newest)}/download"))).StatusCode); // already here
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync(_f.ServerUrl(id, "/backups/offsite/..%2F..%2Fx.zip/download"))).StatusCode);
        }
        finally
        {
            var settingsNow = await _f.Owner.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl(id, "/backups/settings"));
            await _f.Owner.Http.PutAsJsonAsync(_f.ServerUrl(id, "/backups/settings"), settingsNow with { UploadOffsite = false }, AgentFixture.Json);
            await _f.Owner.Http.PutAsJsonAsync("/api/v2/machines/local/offsite", new { enabled = false, provider = "b2", keepCount = 7 }, AgentFixture.Json);
            foreach (var b in await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl(id, "/backups"))) { await _f.Owner.DeleteAsync(_f.ServerUrl(id, $"/backups/{Uri.EscapeDataString(b.Name)}")); }
            await _f.Owner.DeleteAsync("/api/v2/users/offsitemember");
            await _f.Owner.DeleteAsync("/api/v2/users/offsiteviewer");
        }
    }

    [Fact]
    public async Task A_service_that_refuses_is_reported_plainly()
    {
        // Nothing listens there: the test says so instead of failing oddly.
        var res = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", new
        {
            enabled = true, provider = "other", endpoint = "http://127.0.0.1:9", region = "us-east-1", bucket = "nope-bucket", accessKeyId = "k", secretAccessKey = "s", prefix = "x", keepCount = 1,
        }));
        Assert.False(res.GetProperty("ok").GetBoolean());
        Assert.Contains("Couldn't reach it", res.GetProperty("message").GetString());
        // A bad bucket name never gets as far as the service.
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/machines/local/offsite/test", new { enabled = true, bucket = "Not A Bucket!", accessKeyId = "k", secretAccessKey = "s", keepCount = 1 })).StatusCode);
    }
}
