using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Shared fixtures: one throwaway data root per test run, and locations of real plugin sources.</summary>
internal static class TestData
{
    private static readonly Lazy<string> _root = new(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-next-tests-" + Environment.ProcessId);
        WgsmEnvironment.Initialize(dir);
        return dir;
    });

    /// <summary>
    /// Deletes a folder (and everything in it), trying again for a few seconds: on build machines antivirus briefly
    /// opens freshly written files, and a process killed a moment ago can still hold its folder. Read-only files are
    /// cleared first. Throws only if it's still there after that.
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        for (int attempt = 0; Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50)
            {
                try { foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { File.SetAttributes(f, FileAttributes.Normal); } } catch { /* going anyway */ }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Deletes a file, trying again for a few seconds (see <see cref="DeleteDirectory"/>).</summary>
    public static void DeleteFile(string path)
    {
        for (int attempt = 0; File.Exists(path); attempt++)
        {
            try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 50) { Thread.Sleep(100); }
        }
    }

    /// <summary>Initializes (once) and returns the temp data root every test shares.</summary>
    public static string DataRoot => _root.Value;

    /// <summary>
    /// Runs when the test assembly loads — before any test — so nothing can touch a server path first and
    /// pin the data root to the default (the test binaries' folder).
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InitializeDataRoot() => _ = DataRoot;

    /// <summary>
    /// The legacy repo's plugin development kit — real third-party plugin sources — or null when it isn't next to
    /// this repository (a clone of WindowsGSM-Next on its own). Set WGSM_PLUGIN_KIT to point somewhere else.
    /// </summary>
    public static string? PluginKitDir
    {
        get
        {
            if (Environment.GetEnvironmentVariable("WGSM_PLUGIN_KIT") is { Length: > 0 } custom && Directory.Exists(custom)) { return custom; }
            // tests/WindowsGSM.Core.Tests/bin/<cfg>/<tfm>/ → up to the folder that holds WindowsGSM-Remaster
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "WindowsGSM-Remaster", "WindowsGSM-Plugin-Development")))
            {
                dir = dir.Parent;
            }
            return dir == null ? null : Path.Combine(dir.FullName, "WindowsGSM-Remaster", "WindowsGSM-Plugin-Development");
        }
    }
}
