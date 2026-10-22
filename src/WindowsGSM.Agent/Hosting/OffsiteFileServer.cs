using System.Security.Cryptography;
using FluentFTP;
using Renci.SshNet;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Off-site backups on a plain file server instead of an S3 bucket: FTP, FTPS (explicit or implicit TLS) or SFTP.
/// The settings reuse the S3 ones: Endpoint is the host (with an optional :port), AccessKeyId/SecretAccessKey the
/// username and password, Prefix the folder. A self-signed FTPS certificate or an SFTP host key is trusted the first
/// time and pinned (<see cref="OffsiteSettings.Pin"/>): a different one later is refused, as SSH does.
/// </summary>
internal sealed class OffsiteFileServer : IAsyncDisposable
{
    public static readonly string[] Providers = { "ftp", "ftps", "ftps-implicit", "sftp" };
    public static bool Handles(OffsiteSettings s) => Providers.Contains(s.Provider);

    private readonly AsyncFtpClient? _ftp;
    private readonly SftpClient? _sftp;
    private string? _seen; // the certificate / host key fingerprint met on this connection
    private bool _mismatch;

    /// <summary>The fingerprint this connection trusted while nothing was pinned yet (to pin), or null.</summary>
    public string? NewPin { get; private set; }

    private OffsiteFileServer(AsyncFtpClient? ftp, SftpClient? sftp) { _ftp = ftp; _sftp = sftp; }

    /// <summary>"host" or "host:port" → the parts (the protocol's usual port when none is given).</summary>
    public static (string Host, int Port) Address(OffsiteSettings s)
    {
        string a = s.Endpoint.Trim();
        int defaultPort = s.Provider switch { "sftp" => 22, "ftps-implicit" => 990, _ => 21 };
        int colon = a.LastIndexOf(':');
        return colon > 0 && int.TryParse(a[(colon + 1)..], out int port) && port is > 0 and < 65536 ? (a[..colon], port) : (a, defaultPort);
    }

    public static async Task<OffsiteFileServer> ConnectAsync(OffsiteSettings s, CancellationToken token)
    {
        var (host, port) = Address(s);
        if (s.Provider == "sftp")
        {
            var client = new SftpClient(host, port, s.AccessKeyId, s.SecretAccessKey);
            var server = new OffsiteFileServer(null, client);
            client.HostKeyReceived += (_, e) => e.CanTrust = server.Trust("SHA256:" + e.FingerPrintSHA256, s.Pin, valid: false);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
            try { await client.ConnectAsync(token); }
            catch (Exception) when (server._mismatch) { client.Dispose(); throw server.Refused(); }
            return server;
        }
        var ftp = new AsyncFtpClient(host, s.AccessKeyId, s.SecretAccessKey, port);
        ftp.Config.EncryptionMode = s.Provider switch { "ftps" => FtpEncryptionMode.Explicit, "ftps-implicit" => FtpEncryptionMode.Implicit, _ => FtpEncryptionMode.None };
        ftp.Config.ConnectTimeout = 30000;
        ftp.Config.DataConnectionEncryption = true;
        var self = new OffsiteFileServer(ftp, null);
        ftp.ValidateCertificate += (_, e) =>
        {
            string hash = "SHA256:" + Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData()));
            e.Accept = self.Trust(hash, s.Pin, valid: e.PolicyErrors == System.Net.Security.SslPolicyErrors.None);
        };
        try { await ftp.Connect(token); }
        catch (Exception) when (self._mismatch) { await ftp.DisposeAsync(); throw self.Refused(); }
        return self;
    }

    /// <summary>A certificate a trusted authority signed is fine; otherwise the pinned one, or the first one met.</summary>
    private bool Trust(string fingerprint, string pinned, bool valid)
    {
        _seen = fingerprint;
        if (valid) { return true; }
        if (pinned.Length == 0) { NewPin = fingerprint; return true; }
        if (string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase)) { return true; }
        _mismatch = true;
        return false;
    }

    private Exception Refused() => new InvalidOperationException(
        $"The server's identity changed (now {_seen}), so nothing was sent. If you replaced the server or its certificate, save the off-site settings again to trust the new one.");

    private static string Dir(string path) => path.TrimEnd('/');

    public async Task UploadAsync(string localFile, string remoteFile, Action<double> progress, CancellationToken token)
    {
        string dir = remoteFile[..remoteFile.LastIndexOf('/')];
        if (_ftp != null)
        {
            await _ftp.UploadFile(localFile, remoteFile, FtpRemoteExists.Overwrite, createRemoteDir: true, FtpVerify.None,
                new Progress<FtpProgress>(p => progress(p.Progress)), token);
            return;
        }
        await Task.Run(() =>
        {
            EnsureSftpDir(dir);
            long size = new FileInfo(localFile).Length;
            using var stream = File.OpenRead(localFile);
            using var cancel = token.Register(() => stream.Dispose()); // stops a transfer in progress
            _sftp!.UploadFile(stream, remoteFile, true, sent => progress(size == 0 ? 100 : 100.0 * sent / size));
        }, token);
    }

    private void EnsureSftpDir(string dir)
    {
        string path = "";
        foreach (string part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            if (!_sftp!.Exists(path)) { _sftp.CreateDirectory(path); }
        }
    }

    /// <summary>The folders in a folder (an absent folder has none).</summary>
    public async Task<List<string>> ListFoldersAsync(string dir, CancellationToken token)
    {
        dir = Dir(dir);
        if (_ftp != null)
        {
            if (!await _ftp.DirectoryExists(dir, token)) { return new(); }
            return (await _ftp.GetListing(dir, token)).Where(i => i.Type == FtpObjectType.Directory).Select(i => i.Name).ToList();
        }
        return await Task.Run(() => _sftp!.Exists(dir)
            ? _sftp.ListDirectory(dir).Where(f => f.IsDirectory && f.Name is not ("." or "..")).Select(f => f.Name).ToList()
            : new List<string>(), token);
    }

    /// <summary>The files in a folder (an absent folder is simply empty).</summary>
    public async Task<List<(string Name, long Size, DateTimeOffset Modified)>> ListAsync(string dir, CancellationToken token)
    {
        dir = Dir(dir);
        if (_ftp != null)
        {
            if (!await _ftp.DirectoryExists(dir, token)) { return new(); }
            return (await _ftp.GetListing(dir, token)).Where(i => i.Type == FtpObjectType.File)
                .Select(i => (i.Name, i.Size, new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc)))).ToList();
        }
        return await Task.Run(() =>
        {
            if (!_sftp!.Exists(dir)) { return new List<(string, long, DateTimeOffset)>(); }
            return _sftp.ListDirectory(dir).Where(f => f.IsRegularFile)
                .Select(f => (f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).ToList();
        }, token);
    }

    public async Task RenameAsync(string from, string to, CancellationToken token)
    {
        if (_ftp != null) { await _ftp.Rename(from, to, token); return; }
        await Task.Run(() => _sftp!.RenameFile(from, to), token);
    }

    public async Task DeleteAsync(string remoteFile, CancellationToken token)
    {
        if (_ftp != null) { await _ftp.DeleteFile(remoteFile, token); return; }
        await Task.Run(() => _sftp!.DeleteFile(remoteFile), token);
    }

    public async Task DownloadAsync(string remoteFile, string localFile, Action<double> progress, CancellationToken token)
    {
        if (_ftp != null)
        {
            var status = await _ftp.DownloadFile(localFile, remoteFile, FtpLocalExists.Overwrite, FtpVerify.None, new Progress<FtpProgress>(p => progress(p.Progress)), token);
            if (status == FtpStatus.Failed) { throw new IOException($"{remoteFile} couldn't be downloaded."); }
            return;
        }
        await Task.Run(() =>
        {
            long size = _sftp!.GetAttributes(remoteFile).Size;
            using var stream = File.Create(localFile);
            using var cancel = token.Register(() => stream.Dispose());
            _sftp.DownloadFile(remoteFile, stream, got => progress(size == 0 ? 100 : 100.0 * got / size));
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ftp != null) { try { await _ftp.Disconnect(); } catch { /* closing anyway */ } await _ftp.DisposeAsync(); }
        if (_sftp != null) { try { _sftp.Disconnect(); } catch { /* closing anyway */ } _sftp.Dispose(); }
    }
}
