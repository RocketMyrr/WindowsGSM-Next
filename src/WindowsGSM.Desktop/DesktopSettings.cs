using System.Text.Json;
using Microsoft.Win32;

namespace WindowsGSM.Desktop;

/// <summary>This app's own preferences (%LOCALAPPDATA%\WindowsGSM\desktop.json) — not the agent's.</summary>
internal sealed class DesktopSettings
{
    public bool Notifications { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public int[]? Bounds { get; set; } // x, y, width, height
    public bool Maximized { get; set; }
    /// <summary>Sign back in on its own (as whoever last signed in here) when the session ends.</summary>
    public bool StaySignedIn { get; set; } = true;
    /// <summary>Other PCs (or hubs) this app has connected to.</summary>
    public List<SavedPc> Pcs { get; set; } = new();
    /// <summary>The address of the PC the window shows; null = this PC's own agent.</summary>
    public string? CurrentPc { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public SavedPc? Current => CurrentPc == null ? null : Pcs.FirstOrDefault(p => string.Equals(p.Url, CurrentPc, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a PC (or updates the one at the same address) and makes it current.</summary>
    public void Remember(SavedPc pc)
    {
        Pcs.RemoveAll(p => string.Equals(p.Url, pc.Url, StringComparison.OrdinalIgnoreCase));
        Pcs.Add(pc);
        CurrentPc = pc.Url;
        Save();
    }

    /// <summary>%LOCALAPPDATA%\WindowsGSM — or WGSM_DESKTOP_HOME, so a test copy keeps its own PCs and sign-ins.</summary>
    public static string Folder => Environment.GetEnvironmentVariable("WGSM_DESKTOP_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsGSM");
    private static string FilePath => Path.Combine(Folder, "desktop.json");

    public static DesktopSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* preferences are best effort */ }
    }
}

/// <summary>"Start with Windows": this app in the current user's Run key (it then starts the agent if needed).</summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowsGSM Desktop";

    public static bool IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool on, string exe, string? dataRoot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        // Installed: the launcher (it always starts the current version and knows the data folder).
        string? launcher = Environment.GetEnvironmentVariable("WGSM_LAUNCHER");
        string own = dataRoot == null ? $"\"{exe}\" --minimized --remote" : $"\"{exe}\" --minimized --data \"{dataRoot}\"";
        if (on) { key.SetValue(ValueName, launcher is { Length: > 0 } && File.Exists(launcher) ? $"\"{launcher}\" --minimized" : own); }
        else { key.DeleteValue(ValueName, throwOnMissingValue: false); }
    }
}
