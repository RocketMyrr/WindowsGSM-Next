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

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsGSM");
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

    public static void Set(bool on, string exe, string dataRoot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        // Installed: the launcher (it always starts the current version and knows the data folder).
        string? launcher = Environment.GetEnvironmentVariable("WGSM_LAUNCHER");
        if (on) { key.SetValue(ValueName, launcher is { Length: > 0 } && File.Exists(launcher) ? $"\"{launcher}\" --minimized" : $"\"{exe}\" --minimized --data \"{dataRoot}\""); }
        else { key.DeleteValue(ValueName, throwOnMissingValue: false); }
    }
}
