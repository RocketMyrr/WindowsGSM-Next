using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsGSM.Agent;

/// <summary>
/// The agent's own settings (configs/next/agent.json). Safe defaults: this machine only, plain HTTP on
/// port 8971 — one above the legacy dashboard's 8970, so both can run side by side during the move.
/// Exposing it to the network is opt-in.
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Stable id used in every API route and grant. Generated once; never changes.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the UI. Defaults to the computer name.</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    public int Port { get; set; } = 8971;

    /// <summary>false = 127.0.0.1 only; true = every network interface.</summary>
    public bool ExposeToNetwork { get; set; }

    public bool UseHttps { get; set; }

    /// <summary>PFX, or a PEM certificate / full chain. Blank with HTTPS on = self-signed.</summary>
    public string CertPath { get; set; } = string.Empty;

    /// <summary>PEM private key, when <see cref="CertPath"/> is a PEM certificate with a separate key.</summary>
    public string KeyPath { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonConverter(typeof(global::WindowsGSM.Hosting.SecretJsonConverter))] public string CertPassword { get; set; } = string.Empty;

    /// <summary>Get a certificate from Let's Encrypt for <see cref="AcmeDomain"/> (needs port 80 reachable from the internet).</summary>
    public bool AcmeEnabled { get; set; }
    public string AcmeDomain { get; set; } = string.Empty;
    public string AcmeEmail { get; set; } = string.Empty;
    public bool AcmeStaging { get; set; }

    /// <summary>How long a sign-in lasts without activity.</summary>
    public int SessionHours { get; set; } = 8;

    // ── Joined to a hub (this machine is a member) ──

    /// <summary>The hub this machine reports to, e.g. https://game-box-a:8971. Null = not joined.</summary>
    public string? HubUrl { get; set; }

    /// <summary>This machine's credential on the hub, issued at pairing.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(global::WindowsGSM.Hosting.SecretJsonConverter))] public string? HubCredential { get; set; }

    public string? HubName { get; set; }

    /// <summary>The hub's certificate, pinned at pairing when it isn't CA-trusted (self-signed).</summary>
    public string? HubCertThumbprint { get; set; }

    // ── Updates ──

    /// <summary>The GitHub repository whose releases carry WindowsGSM-&lt;version&gt;.zip (+ .sha256).</summary>
    public string UpdateRepo { get; set; } = DefaultUpdateRepo;

    public const string DefaultUpdateRepo = global::WindowsGSM.Agent.Hosting.AppReleases.DefaultRepo;
    /// <summary>Where releases used to be published (alpha.1 – alpha.2): moved to the new repository on load.</summary>
    private const string FormerUpdateRepo = "RocketMyrr/WindowsGSM-Remaster";

    /// <summary>Also offer pre-releases (alpha/beta).</summary>
    public bool UpdatePrerelease { get; set; } = true;

    [JsonIgnore] public string FilePath { get; private set; } = string.Empty;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AgentSettings Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        string file = Path.Combine(configDir, "agent.json");
        AgentSettings? settings = null;
        try
        {
            if (File.Exists(file)) { settings = JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(file), Json); }
        }
        catch { /* unreadable: fall back to defaults, but keep the machine id if we can */ }

        settings ??= new AgentSettings();
        settings.FilePath = file;
        bool changed = false;
        if (string.IsNullOrWhiteSpace(settings.MachineId))
        {
            settings.MachineId = NewMachineId();
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(settings.MachineName)) { settings.MachineName = Environment.MachineName; changed = true; }
        // Releases moved to their own repository; copies that still point at the old one follow.
        if (string.Equals(settings.UpdateRepo, FormerUpdateRepo, StringComparison.OrdinalIgnoreCase)) { settings.UpdateRepo = DefaultUpdateRepo; changed = true; }
        // Secrets saved before encryption existed are encrypted now.
        if ((settings.CertPassword.Length > 0 || !string.IsNullOrEmpty(settings.HubCredential)) && File.Exists(file) && !File.ReadAllText(file).Contains("dpapi:")) { changed = true; }
        if (changed || !File.Exists(file)) { settings.Save(); }
        return settings;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) { return; } // an in-memory copy (tests)
        SaveToDisk();
    }

    private void SaveToDisk()
    {
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Short, URL-safe and readable: "m-" + 10 lowercase letters/digits.</summary>
    private static string NewMachineId()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
        return "m-" + new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}
