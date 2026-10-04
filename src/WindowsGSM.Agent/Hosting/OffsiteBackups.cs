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

/// <summary>Where off-site backups go: one S3-compatible bucket for the machine (configs/next/offsite.json).</summary>
public sealed class OffsiteSettings
{
    public bool Enabled { get; set; }
    /// <summary>"b2", "r2", "wasabi", "s3" or "other" — only for the panel's hints; the endpoint decides.</summary>
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

    [JsonIgnore] public bool Ready => Enabled && Bucket.Length > 0 && AccessKeyId.Length > 0 && SecretAccessKey.Length > 0;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string File(string configDir) => Path.Combine(configDir, "offsite.json");

    public static OffsiteSettings Load(string configDir)
    {
        try { return System.IO.File.Exists(File(configDir)) ? JsonSerializer.Deserialize<OffsiteSettings>(System.IO.File.ReadAllText(File(configDir)), Json) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(string configDir)
    {
        string file = File(configDir);
        System.IO.File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, Json));
        System.IO.File.Move(file + ".tmp", file, overwrite: true);
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
    public string ServerPrefix(string serverId) => $"{Settings.Prefix.Trim().Trim('/')}/{_ctx.MachineId}/server-{serverId}/".TrimStart('/');

    /// <summary>Writes, reads back and deletes a small file: whether the settings work. Null, or what's wrong.</summary>
    public async Task<string?> TestAsync(OffsiteSettings s, CancellationToken token)
    {
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

    /// <summary>Keeps the newest <see cref="OffsiteSettings.KeepCount"/> backups of the server off-site.</summary>
    private async Task<int> PruneAsync(IAmazonS3 client, OffsiteSettings settings, string serverId, CancellationToken token)
    {
        if (settings.KeepCount <= 0) { return 0; }
        var all = await ListAsync(client, settings, serverId, token);
        int removed = 0;
        foreach (var old in all.Skip(settings.KeepCount))
        {
            await client.DeleteObjectAsync(settings.Bucket, old.Key, token);
            removed++;
        }
        return removed;
    }

    /// <summary>The server's off-site backups, newest first.</summary>
    public async Task<List<RemoteBackup>> ListAsync(string serverId, CancellationToken token)
    {
        using var client = Client(Settings);
        return await ListAsync(client, Settings, serverId, token);
    }

    private async Task<List<RemoteBackup>> ListAsync(IAmazonS3 client, OffsiteSettings settings, string serverId, CancellationToken token)
    {
        var list = new List<RemoteBackup>();
        string prefix = ServerPrefix(serverId);
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
    public (Job? Job, string? Problem) Download(string serverId, string name)
    {
        if (!Settings.Ready) { return (null, "Off-site backups aren't set up (Agent settings → Off-site backups)."); }
        if (!SafeName.IsMatch(name)) { return (null, "That isn't a backup name."); }
        string folder = BackupSettings.Load(serverId).ResolveLocation();
        string target = Path.Combine(folder, name);
        if (File.Exists(target)) { return (null, $"{name} is already on this PC — restore it from the list above."); }
        var s = _ctx.Engine.Servers.Get(serverId);
        var job = _ctx.Engine.Jobs.Start("offsite", serverId, $"Bring back {name} for {s?.Name ?? serverId}", async job =>
        {
            var settings = Settings;
            string partial = target + ".download";
            try
            {
                using var client = Client(settings);
                using var transfer = new TransferUtility(client);
                var request = new TransferUtilityDownloadRequest { BucketName = settings.Bucket, Key = ServerPrefix(serverId) + name, FilePath = partial };
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
                return $"Couldn't bring back {name}: {ex.Message}";
            }
        });
        return (job, null);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}
