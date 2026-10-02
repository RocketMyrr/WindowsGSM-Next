using System.Diagnostics;
using System.Text.Json;

namespace WindowsGSM.Desktop;

/// <summary>
/// Finds this machine's agent — the panel's address comes from the data folder's configs/next/agent.json —
/// checks whether it's running, and starts wgsm-agent.exe (shipped next to this app) when it isn't.
/// </summary>
internal static class AgentLocator
{
    public const int DefaultPort = 8971;

    /// <summary>--data &lt;folder&gt;, or this app's own folder (where the agent keeps its data by default).</summary>
    public static string DataRoot(string[] args, string baseDir)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--data", StringComparison.OrdinalIgnoreCase));
        // No trailing slash: a folder ending in \ inside quotes on a command line would escape the closing quote.
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(i >= 0 && i + 1 < args.Length ? args[i + 1] : baseDir));
    }

    /// <summary>The local address of the agent that uses <paramref name="dataRoot"/> (always localhost: it's this machine).</summary>
    public static Uri UrlFor(string dataRoot)
    {
        int port = DefaultPort;
        bool https = false;
        try
        {
            string file = Path.Combine(dataRoot, "configs", "next", "agent.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("Port", out var p) && p.TryGetInt32(out int n) && n is > 0 and < 65536) { port = n; }
                https = Bool(root, "UseHttps") || Bool(root, "AcmeEnabled");
            }
        }
        catch { /* unreadable: the defaults are what a fresh agent uses */ }
        return new Uri($"{(https ? "https" : "http")}://localhost:{port}/");
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>An HTTP client for talking to our own agent (its certificate may be self-signed).</summary>
    public static HttpClient LocalClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri is { } u && IsLocal(u),
    }) { Timeout = TimeSpan.FromSeconds(3) };

    public static bool IsLocal(Uri u) =>
        u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> IsRunningAsync(HttpClient http, Uri url)
    {
        try
        {
            using var res = await http.GetAsync(new Uri(url, "api/v2/info"));
            return (int)res.StatusCode < 500;
        }
        catch { return false; }
    }

    /// <summary>wgsm-agent.exe next to this app, if it's there.</summary>
    public static string? AgentExe(string baseDir)
    {
        string exe = Path.Combine(baseDir, "wgsm-agent.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Starts the agent on its own (it keeps running when this app closes).</summary>
    public static Process? StartAgent(string exe, string dataRoot) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "--data", dataRoot },
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });

    /// <summary>Waits for the agent to answer, up to <paramref name="timeout"/>.</summary>
    public static async Task<bool> WaitAsync(HttpClient http, Uri url, TimeSpan timeout, CancellationToken token = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !token.IsCancellationRequested)
        {
            if (await IsRunningAsync(http, url)) { return true; }
            try { await Task.Delay(400, token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }
}
