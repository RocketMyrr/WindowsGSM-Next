using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Real servers some tests talk to (tools\Get-TestTools.ps1 fetches them): Pebble, Let's Encrypt's test ACME
/// server, and SeaweedFS, an S3-compatible store. Found in WGSM_TEST_TOOLS or %LOCALAPPDATA%\WindowsGSM-TestTools.
/// Without them those tests pass without checking anything — except where WGSM_REQUIRE_TEST_TOOLS is set (CI),
/// where a missing tool fails the test, so they can't quietly stop running there.
/// </summary>
internal static class TestTools
{
    public static string Root => Environment.GetEnvironmentVariable("WGSM_TEST_TOOLS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsGSM-TestTools");

    /// <summary>The tool's program, or null when it isn't there (and that's allowed here).</summary>
    public static string? Find(string folder, string exe)
    {
        string path = Path.Combine(Root, folder, exe);
        if (File.Exists(path)) { return path; }
        if (Environment.GetEnvironmentVariable("WGSM_REQUIRE_TEST_TOOLS") is { Length: > 0 })
        {
            throw new InvalidOperationException($"{path} is missing. Run tools\\Get-TestTools.ps1 first.");
        }
        return null;
    }

    /// <summary>
    /// Free loopback ports below 55000 (SeaweedFS derives gRPC ports by adding 10000), none of them taken.
    /// </summary>
    public static int[] FreePorts(int count)
    {
        var listeners = new List<TcpListener>();
        try
        {
            while (listeners.Count < count)
            {
                var l = new TcpListener(IPAddress.Loopback, Random.Shared.Next(20000, 55000));
                try { l.Start(); listeners.Add(l); } catch (SocketException) { }
            }
            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally { foreach (var l in listeners) { l.Stop(); } }
    }

    /// <summary>Starts a tool hidden, with its output kept for the failure message.</summary>
    public static Process Start(string exe, IEnumerable<string> args, string workDir, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) { psi.ArgumentList.Add(a); }
        if (env != null) { foreach (var (k, v) in env) { psi.Environment[k] = v; } }
        var p = Process.Start(psi)!;
        string log = Path.Combine(workDir, Path.GetFileNameWithoutExtension(exe) + ".log");
        void Keep(string? line) { if (line != null) { lock (p) { File.AppendAllText(log, line + Environment.NewLine); } } }
        p.OutputDataReceived += (_, e) => Keep(e.Data);
        p.ErrorDataReceived += (_, e) => Keep(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>The last lines a tool printed (for a failure message).</summary>
    public static string Tail(string workDir, string exe)
    {
        string log = Path.Combine(workDir, Path.GetFileNameWithoutExtension(exe) + ".log");
        try { return string.Join(Environment.NewLine, File.ReadLines(log).TakeLast(15)); } catch { return "(no output)"; }
    }

    public static void Stop(Process? p)
    {
        if (p == null) { return; }
        try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(15000); } } catch { }
        p.Dispose();
    }
}

/// <summary>SeaweedFS in one process ("weed mini"): an S3 endpoint on loopback that checks signatures for real.</summary>
internal sealed class SeaweedS3 : IDisposable
{
    public const string AccessKey = "wgsm-test-key", SecretKey = "wgsm-test-secret", Bucket = "wgsm-test";
    private readonly Process _process;
    private readonly string _dir;
    public string Url { get; }

    private SeaweedS3(Process process, string dir, string url) { _process = process; _dir = dir; Url = url; }

    /// <summary>Started and answering, or null when SeaweedFS isn't installed for the tests.</summary>
    public static async Task<SeaweedS3?> StartAsync()
    {
        string? weed = TestTools.Find("seaweedfs", "weed.exe");
        if (weed == null) { return null; }
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-s3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "s3.json"),
            $$"""{"identities":[{"name":"wgsm","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}""");
        string[] names = { "master.port", "master.port.grpc", "volume.port", "volume.port.grpc", "filer.port", "filer.port.grpc", "s3.port", "s3.port.grpc", "s3.port.iceberg", "admin.port", "admin.port.grpc", "webdav.port" };
        int[] ports = TestTools.FreePorts(names.Length);
        var args = new List<string> { "mini", "-dir=" + Path.Combine(dir, "data"), "-ip=127.0.0.1", "-ip.bind=127.0.0.1", "-s3.config=" + Path.Combine(dir, "s3.json"), "-s3.autoCreateBucket=false", "-webdav=false", "-admin.ui=false" }; // like a real service: no buckets made on the fly
        args.AddRange(names.Select((n, i) => $"-{n}={ports[i]}"));
        var p = TestTools.Start(weed, args, dir);
        string url = $"http://127.0.0.1:{ports[Array.IndexOf(names, "s3.port")]}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 0; i < 90; i++)
        {
            if (p.HasExited) { break; }
            try { await http.GetAsync(url); } // any answer (403 for no signature) means it's listening
            catch (HttpRequestException) { await Task.Delay(1000); continue; }
            catch (TaskCanceledException) { continue; }
            // The bucket, made as you'd make one at a real provider (and only once the store is ready for it).
            try
            {
                using var client = new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials(AccessKey, SecretKey),
                    new Amazon.S3.AmazonS3Config { ServiceURL = url, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
                await client.PutBucketAsync(Bucket);
                return new SeaweedS3(p, dir, url);
            }
            catch (Amazon.S3.AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { return new SeaweedS3(p, dir, url); }
            catch (Exception) { await Task.Delay(1000); }
        }
        string tail = TestTools.Tail(dir, weed);
        TestTools.Stop(p);
        throw new InvalidOperationException("SeaweedFS didn't start:" + Environment.NewLine + tail);
    }

    public void Dispose()
    {
        TestTools.Stop(_process);
        Core.Tests.TestData.DeleteDirectory(_dir);
    }
}
