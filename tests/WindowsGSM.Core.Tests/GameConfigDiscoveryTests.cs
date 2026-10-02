using WindowsGSM.Engine.GameConfig;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>Finding a server's config files, and editing them through the service (jail, conflicts, encoding).</summary>
[Collection("Lifecycle")]
public class GameConfigDiscoveryTests
{
    private static void Write(string id, string rel, string text)
    {
        string full = ServerPath.GetServersServerFiles(id, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public async Task Known_files_come_first_then_likely_configs_and_noise_is_skipped()
    {
        EngineFixture.CreateServer("181");
        Write("181", "csgo/cfg/server.cfg", "hostname \"x\"\n");                           // not known for the test game…
        Write("181", "ShooterGame/Saved/Config/WindowsServer/GameUserSettings.ini", "[S]\nA=1\n");
        Write("181", "settings.json", "{ \"a\": 1 }");
        Write("181", "game.deps.json", "{}");                                                  // build noise
        Write("181", "steamapps/appmanifest_1.acf", "x");                                      // skipped folder
        Write("181", "logs/server.cfg", "x");                                                  // skipped folder
        Write("181", "Mods/SomeMod/config.ini", "[a]\nb=1\n");                                 // mods folder skipped
        using var engine = await EngineFixture.StartEngineAsync();

        var files = engine.GameConfigs.Discover("181");
        var paths = files.Select(f => f.Path).ToList();
        Assert.Equal("ShooterGame/Saved/Config/WindowsServer/GameUserSettings.ini", paths[0]); // Unreal server config ranks top
        Assert.Contains("csgo/cfg/server.cfg", paths);
        Assert.Contains("settings.json", paths);
        Assert.DoesNotContain("game.deps.json", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("steamapps") || p.StartsWith("logs") || p.StartsWith("Mods"));
    }

    [Fact]
    public async Task Editing_through_the_service_is_jailed_conflict_safe_and_keeps_a_bom()
    {
        EngineFixture.CreateServer("182");
        string full = ServerPath.GetServersServerFiles("182", "server.properties");
        File.WriteAllBytes(full, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("# comment\nmax-players=10\n")).ToArray());
        using var engine = await EngineFixture.StartEngineAsync();

        var parsed = engine.GameConfigs.Read("182", "server.properties");
        var entry = Assert.Single(parsed.Entries);
        var saved = engine.GameConfigs.Write("182", "server.properties", new[] { new ConfigChange(entry.Id, "12") }, parsed.Modified);
        Assert.Equal("12", saved.Entries[0].Value);
        byte[] bytes = File.ReadAllBytes(full);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal("# comment\nmax-players=12\n", System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        // The game rewrote the file after we read it: refused rather than clobbering its version.
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(5));
        var ex = Assert.Throws<FileOperationException>(() => engine.GameConfigs.Write("182", "server.properties", new[] { new ConfigChange(entry.Id, "14") }, saved.Modified));
        Assert.Equal(FileProblem.Conflict, ex.Problem);

        Assert.Throws<FileOperationException>(() => engine.GameConfigs.Read("182", "../configs/WindowsGSM.cfg"));
    }
}
