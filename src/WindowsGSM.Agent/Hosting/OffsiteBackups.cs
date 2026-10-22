using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Operations;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Where off-site backups go (configs/next/offsite.json): one S3-compatible bucket for the machine, or a file server
/// (FTP, FTPS, SFTP — see <see cref="OffsiteFileServer"/>, which reuses these fields).
/// </summary>
public sealed class OffsiteSettings
{
    public bool Enabled { get; set; }
    /// <summary>"b2", "r2", "wasabi", "s3" or "other" (S3: only for the panel's hints; the endpoint decides), or "ftp", "ftps", "ftps-implicit", "sftp".</summary>
    public string Provider { get; set; } = "b2";
    /// <summary>The service's S3 address (https://s3.us-west-004.backblazeb2.com…). Blank: Amazon S3.</summary>
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    [JsonConverter(typeof(global::WindowsGSM.Hosting.SecretJsonConverter))] public string SecretAccessKey { get; set; } = "";
    /// <summary>A folder in the bucket for this WindowsGSM ("windowsgsm" by default); each machine and server get their own inside.</summary>
    public string Prefix { get; set; } = "windowsgsm";
    /// <summary>Off-site copies kept per server (the newest; 0 = all).</summary>
    public int KeepCount { get; set; } = 7;

    /// <summary>File servers: the certificate / host key trusted the first time (a different one is refused).</summary>
    public string Pin { get; set; } = "";

    [JsonIgnore] public bool FileServer => OffsiteFileServer.Handles(this);
    [JsonIgnore] public bool Ready => Enabled && (FileServer ? Endpoint.Length > 0 : Bucket.Length > 0) && AccessKeyId.Length > 0 && SecretAccessKey.Length > 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string File(string configDir) => Path.Combine(configDir, "offsite.json");

    public static OffsiteSettings Load(string configDir)
    {
        return global::WindowsGSM.Hosting.SafeJson.Read<OffsiteSettings>(File(configDir), Json) ?? new();
    }

    public void Save(string configDir)
    {
        global::WindowsGSM.Hosting.SafeJson.Write(File(configDir), this, Json);
    }
}

/// <summary>A backup in the off-site bucket.</summary>
public sealed record RemoteBackup(string Key, string Name, long Size, DateTimeOffset Uploaded);

/// <summary>
/// Off-site backups: each new backup of a server that has "Also upload off-site" on is uploaded to the machine's
/// S3-compatible bucket (Backblaze B2, Cloudflare R2, Wasabi, Amazon S3, MinIO…) — as a job of its own, so a big
/// upload never keeps the server busy — and the newest few are kept there. A backup can be brought back from the
/// bucket into the server's backups folder, to restore it as usual (the PC's disk died, say).
/// Objects: {prefix}/{machine id}/server-{id}/{backup file name}.
/// </summary>
public sealed class OffsiteBackups
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9 _.()\-]{1,200}\.zip$", RegexOptions.Compiled);
    /// <summary>Another PC's (or server's) folder in the storage: "{machine id}/server-{id}".</summary>
    public static readonly Regex Source = new(@"^[A-Za-z0-9_.\-]{1,64}/server-[A-Za-z0-9_\-]{1,32}$", RegexOptions.Compiled);
    private readonly AgentContext _ctx;

    /// <summary>For tests: the S3 client to use instead of one made from the settings.</summary>
    internal Func<OffsiteSettings, IAmazonS3>? ClientOverride { get; set; }

    public OffsiteBackups(AgentContext ctx)
    {
        _ctx = ctx;
        Settings = OffsiteSettings.Load(ctx.ConfigDir);
    }

    public OffsiteSettings Settings { get; private set; }

    public void Apply(OffsiteSettings next)
    {
        next.Save(_ctx.ConfigDir);
        Settings = next;
    }

    public IAmazonS3 Client(OffsiteSettings s)
    {
        if (ClientOverride != null) { return ClientOverride(s); }
        var config = new AmazonS3Config
        {
            // Many S3-compatible services (B2, R2) don't accept the newest checksum headers: only when required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(10),
        };
        if (s.Endpoint.Length > 0)
        {
            config.ServiceURL = s.Endpoint;
            config.ForcePathStyle = true;
            config.AuthenticationRegion = s.Region.Length > 0 ? s.Region : "auto";
        }
        else { config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s.Region.Length > 0 ? s.Region : "us-east-1"); }
        return new AmazonS3Client(new BasicAWSCredentials(s.AccessKeyId, s.SecretAccessKey), config);
    }

    /// <summary>Where a server's backups go in the bucket.</summary>
    public string ServerPrefix(string serverId) => SourcePrefix($"{_ctx.MachineId}/server-{serverId}");

    /// <summary>A "{machine id}/server-{id}" folder's place in the storage.</summary>
    private string SourcePrefix(string source) => $"{Settings.Prefix.Trim().Trim('/')}/{source}/".TrimStart('/');

    /// <summary>
    /// Other folders in the storage with backups — other PCs, or this one before it was set up again (a new machine
    /// id): after a disk dies, the old backups are there to bring back. "{machine id}/server-{id}", sorted.
    /// </summary>
    public async Task<List<string>> SourcesAsync(string serverId, CancellationToken token)
    {
        string root = $"{Settings.Prefix.Trim().Trim('/')}/".TrimStart('/');
        string mine = $"{_ctx.MachineId}/server-{serverId}";
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Settings.FileServer)
        {
            await using var server = await OffsiteFileServer.ConnectAsync(Settings, token);
            Pin(server);
            foreach (string machine in await server.ListFoldersAsync(root, token))
            {
                foreach (string folder in await server.ListFoldersAsync(root + machine, token)) { found.Add($"{machine}/{folder}"); }
            }
        }
        else
        {
            using var client = Client(Settings);
            string? next = null;
            do
            {
                var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Settings.Bucket, Prefix = root, ContinuationToken = next }, token);
                foreach (var o in page.S3Objects ?? new List<S3Object>())
                {
                    var parts = o.Key[root.Length..].Split('/');
                    if (parts.Length == 3 && SafeName.IsMatch(parts[2])) { found.Add($"{parts[0]}/{parts[1]}"); }
                }
                next = page.IsTruncated == true ? page.NextContinuationToken : null;
            } while (next != null);
        }
        found.RemoveWhere(f => !Source.IsMatch(f) || string.Equals(f, mine, StringComparison.OrdinalIgnoreCase));
        return found.ToList();
    }

    /// <summary>Writes, reads back and deletes a small file: whether the settings work. Null, or what's wrong.</summary>
    public async Task<string?> TestAsync(OffsiteSettings s, CancellationToken token)
    {
        if (s.FileServer) { return await TestFileServerAsync(s, token); }
        if (s.Bucket.Length == 0 || s.AccessKeyId.Length == 0 || s.SecretAccessKey.Length == 0) { return "Fill in the bucket and the key."; }
        try
        {
            using var client = Client(s);
            string key = $"{s.Prefix.Trim().Trim('/')}/{_ctx.MachineId}/.wgsm-test".TrimStart('/');
            await client.PutObjectAsync(new PutObjectRequest { BucketName = s.Bucket, Key = key, ContentBody = "WindowsGSM off-site backup test", DisablePayloadSigning = s.Endpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase) }, token);
            using (var got = await client.GetObjectAsync(s.Bucket, key, token)) { using var reader = new StreamReader(got.ResponseStream); await reader.ReadToEndAsync(token); }
            await client.DeleteObjectAsync(s.Bucket, key, token);
            return null;
        }
        catch (AmazonS3Exception ex) { return $"The storage service said: {ex.Message} ({(int)ex.StatusCode} {ex.ErrorCode})"; }
        catch (Exception ex) when (ex is AmazonClientException or HttpRequestException or TaskCanceledException or UriFormatException) { return "Couldn't reach it: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "It didn't work: " + ex.Message; } // the SDK has more ways to fail than it documents
    }

    private async Task<string?> TestFileServerAsync(OffsiteSettings s, CancellationToken token)
    {
        if (s.Endpoint.Length == 0 || s.AccessKeyId.Length == 0 || s.SecretAccessKey.Length == 0) { return "Fill in the server, username and password."; }
        string probe = Path.Combine(Path.GetTempPath(), "wgsm-offsite-test-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(probe, "WindowsGSM off-site backup test");
            await using var server = await OffsiteFileServer.ConnectAsync(s, token);
            string remote = $"{s.Prefix.Trim().Trim('/')}/{_ctx.MachineId}/.wgsm-test".TrimStart('/');
            await server.UploadAsync(probe, remote, _ => { }, token);
            string back = probe + ".back";
            await server.DownloadAsync(remote, back, _ => { }, token);
            bool same = File.ReadAllText(back) == File.ReadAllText(probe);
            TryDelete(back);
            await server.DeleteAsync(remote, token);
            if (!same) { return "The test file came back different — check the server's transfer settings."; }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return FileServerProblem(ex); }
        finally { TryDelete(probe); }
    }

    /// <summary>What went wrong with a file server, in words.</summary>
    private static string FileServerProblem(Exception ex) => ex switch
    {
        Renci.SshNet.Common.SshAuthenticationException or FluentFTP.Exceptions.FtpAuthenticationException => "The server refused the username or password.",
        System.Net.Sockets.SocketException or TimeoutException or Renci.SshNet.Common.SshOperationTimeoutException => "Couldn't reach it: " + ex.Message,
        System.Security.Authentication.AuthenticationException => "The encrypted connection failed: " + ex.Message + " (does the server support this kind of FTPS?)",
        _ => ex.InnerException is System.Net.Sockets.SocketException inner ? "Couldn't reach it: " + inner.Message : "It didn't work: " + ex.Message,
    };

    /// <summary>The first connection to a file server trusted its certificate / host key: remember it.</summary>
    private void Pin(OffsiteFileServer server)
    {
        if (server.NewPin == null || Settings.Pin.Length > 0) { return; }
        Settings.Pin = server.NewPin;
        Settings.Save(_ctx.ConfigDir);
    }

    /// <summary>A backup just finished: uploads it if this server has "Also upload off-site" on.</summary>
    public void BackupFinished(string serverId, string archive)
    {
        if (!Settings.Ready || !BackupSettings.Load(serverId).UploadOffsite) { return; }
        Upload(serverId, archive);
    }

    /// <summary>Uploads a backup archive as a job of its own; returns it.</summary>
    public Job Upload(string serverId, string archive)
    {
        string name = Path.GetFileName(archive);
        var s = _ctx.Engine.Servers.Get(serverId);
        return _ctx.Engine.Jobs.Start("offsite", serverId, $"Upload backup of {s?.Name ?? serverId} off-site", job => UploadAsync(serverId, archive, job));
    }

    private async Task<string?> UploadAsync(string serverId, string archive, JobContext job)
    {
        var settings = Settings;
        string name = Path.GetFileName(archive);
        if (!File.Exists(archive)) { return $"{name} isn't there any more."; }
        if (settings.FileServer) { return await UploadToFileServerAsync(serverId, archive, settings, job); }
        try
        {
            using var client = Client(settings);
            using var transfer = new TransferUtility(client);
            var request = new TransferUtilityUploadRequest
            {
                BucketName = settings.Bucket,
                Key = ServerPrefix(serverId) + name,
                FilePath = archive,
                DisablePayloadSigning = settings.Endpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase) || settings.Endpoint.Length == 0,
            };
            request.UploadProgressEvent += (_, e) => job.Report(e.PercentDone, $"Uploading {name}");
            job.Report(0, $"Uploading {name}");
            await transfer.UploadAsync(request, job.Cancellation);
            long size = new FileInfo(archive).Length;
            int removed = await PruneAsync(client, settings, serverId, job.Cancellation);
            _ctx.Engine.Log.Write(serverId, $"Off-site: uploaded {name} ({size / 1048576.0:0.#} MB) to {settings.Bucket}{(removed > 0 ? $", {removed} older off-site backup(s) removed" : "")}");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ctx.Engine.Log.Write(serverId, $"[NOTICE] Off-site upload of {name} failed: {ex.Message} — the backup on this PC is fine.");
            return $"Couldn't upload {name} off-site: {ex.Message}";
        }
    }

    private async Task<string?> UploadToFileServerAsync(string serverId, string archive, OffsiteSettings settings, JobContext job)
    {
        string name = Path.GetFileName(archive);
        try
        {
            job.Report(0, $"Uploading {name}");
            await using var server = await OffsiteFileServer.ConnectAsync(settings, job.Cancellation);
            Pin(server);
            // Under a temporary name until it's all there, so a cut-off upload never looks like a backup.
            string remote = ServerPrefix(serverId) + name, partial = remote + ".part";
            await server.UploadAsync(archive, partial, p => job.Report((int)p, $"Uploading {name}"), job.Cancellation);
            if ((await server.ListAsync(ServerPrefix(serverId), job.Cancellation)).Any(f => f.Name == name)) { await server.DeleteAsync(remote, job.Cancellation); }
            await RenameAsync(server, partial, remote, archive, job);
            long size = new FileInfo(archive).Length;
            int removed = 0;
            if (settings.KeepCount > 0)
            {
                foreach (var old in (await ListFileServerAsync(server, ServerPrefix(serverId), job.Cancellation)).Skip(settings.KeepCount))
                {
                    await server.DeleteAsync(old.Key, job.Cancellation);
                    removed++;
                }
            }
            _ctx.Engine.Log.Write(serverId, $"Off-site: uploaded {name} ({size / 1048576.0:0.#} MB) to {OffsiteFileServer.Address(settings).Host}{(removed > 0 ? $", {removed} older off-site backup(s) removed" : "")}");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string why = FileServerProblem(ex);
            _ctx.Engine.Log.Write(serverId, $"[NOTICE] Off-site upload of {name} failed: {why} — the backup on this PC is fine.");
            return $"Couldn't upload {name} off-site: {why}";
        }
    }

    /// <summary>Renames by uploading again under the final name when the server can't rename (rare): the partial file then goes.</summary>
    private static async Task RenameAsync(OffsiteFileServer server, string from, string to, string archive, JobContext job)
    {
        try { await server.RenameAsync(from, to, job.Cancellation); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await server.UploadAsync(archive, to, p => job.Report((int)p, $"Uploading {Path.GetFileName(archive)}"), job.Cancellation);
            try { await server.DeleteAsync(from, job.Cancellation); } catch (Exception e) when (e is not OperationCanceledException) { /* left behind; ignored by the list */ }
        }
    }

    private async Task<List<RemoteBackup>> ListFileServerAsync(OffsiteFileServer server, string dir, CancellationToken token)
    {
        return (await server.ListAsync(dir, token)).Where(f => SafeName.IsMatch(f.Name))
            .Select(f => new RemoteBackup(dir + f.Name, f.Name, f.Size, f.Modified))
            .OrderByDescending(b => b.Uploaded).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Keeps the newest <see cref="OffsiteSettings.KeepCount"/> backups of the server off-site.</summary>
    private async Task<int> PruneAsync(IAmazonS3 client, OffsiteSettings settings, string serverId, CancellationToken token)
    {
        if (settings.KeepCount <= 0) { return 0; }
        var all = await ListAsync(client, settings, ServerPrefix(serverId), token);
        int removed = 0;
        foreach (var old in all.Skip(settings.KeepCount))
        {
            await client.DeleteObjectAsync(settings.Bucket, old.Key, token);
            removed++;
        }
        return removed;
    }

    /// <summary>The server's off-site backups, newest first — or those in another folder (<see cref="SourcesAsync"/>).</summary>
    public async Task<List<RemoteBackup>> ListAsync(string serverId, CancellationToken token, string? from = null)
    {
        string prefix = from == null ? ServerPrefix(serverId) : SourcePrefix(from);
        if (Settings.FileServer)
        {
            await using var server = await OffsiteFileServer.ConnectAsync(Settings, token);
            Pin(server);
            return await ListFileServerAsync(server, prefix, token);
        }
        using var client = Client(Settings);
        return await ListAsync(client, Settings, prefix, token);
    }

    private async Task<List<RemoteBackup>> ListAsync(IAmazonS3 client, OffsiteSettings settings, string prefix, CancellationToken token)
    {
        var list = new List<RemoteBackup>();
        string? next = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = settings.Bucket, Prefix = prefix, ContinuationToken = next }, token);
            foreach (var o in page.S3Objects ?? new List<S3Object>())
            {
                string name = o.Key[prefix.Length..];
                if (!SafeName.IsMatch(name)) { continue; }
                list.Add(new RemoteBackup(o.Key, name, o.Size ?? 0, o.LastModified is { } at ? new DateTimeOffset(at.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue));
            }
            next = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (next != null);
        return list.OrderByDescending(b => b.Uploaded).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Brings an off-site backup back into the server's backups folder (a job), where it can be restored as usual.
    /// Null and the job, or why not.
    /// </summary>
    public (Job? Job, string? Problem) Download(string serverId, string name, string? from = null)
    {
        if (!Settings.Ready) { return (null, "Off-site backups aren't set up (Agent settings → Off-site backups)."); }
        if (!SafeName.IsMatch(name)) { return (null, "That isn't a backup name."); }
        if (from != null && !Source.IsMatch(from)) { return (null, "That isn't an off-site folder."); }
        string folder = BackupSettings.Load(serverId).ResolveLocation();
        string key = (from == null ? ServerPrefix(serverId) : SourcePrefix(from)) + name;
        string target = Path.Combine(folder, LocalName(name, serverId));
        if (File.Exists(target)) { return (null, $"{name} is already on this PC — restore it from the list above."); }
        var s = _ctx.Engine.Servers.Get(serverId);
        var job = _ctx.Engine.Jobs.Start("offsite", serverId, $"Bring back {name} for {s?.Name ?? serverId}", async job =>
        {
            var settings = Settings;
            string partial = target + ".download";
            try
            {
                if (settings.FileServer)
                {
                    await using var server = await OffsiteFileServer.ConnectAsync(settings, job.Cancellation);
                    Pin(server);
                    await server.DownloadAsync(key, partial, p => job.Report((int)p, $"Downloading {name}"), job.Cancellation);
                    File.Move(partial, target);
                    _ctx.Engine.Log.Write(serverId, $"Off-site: brought back {name} — it's in the backups list now");
                    return null;
                }
                using var client = Client(settings);
                using var transfer = new TransferUtility(client);
                var request = new TransferUtilityDownloadRequest { BucketName = settings.Bucket, Key = key, FilePath = partial };
                request.WriteObjectProgressEvent += (_, e) => job.Report(e.PercentDone, $"Downloading {name}");
                await transfer.DownloadAsync(request, job.Cancellation);
                File.Move(partial, target);
                _ctx.Engine.Log.Write(serverId, $"Off-site: brought back {name} — it's in the backups list now");
                return null;
            }
            catch (OperationCanceledException) { TryDelete(partial); throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(partial);
                return $"Couldn't bring back {name}: {(settings.FileServer ? FileServerProblem(ex) : ex.Message)}";
            }
        });
        return (job, null);
    }

    /// <summary>
    /// A backup's name on this PC: one made for another server id (another PC's, or before a reinstall) is named for
    /// this server, so it shows in this server's list.
    /// </summary>
    internal static string LocalName(string name, string serverId)
    {
        name = Regex.Replace(name, @"^wgsm-[^-]+-", $"wgsm-{serverId}-", RegexOptions.IgnoreCase);
        return Regex.Replace(name, @"^WGSM-Backup-Server-[^-]+-", $"WGSM-Backup-Server-{serverId}-", RegexOptions.IgnoreCase);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}
