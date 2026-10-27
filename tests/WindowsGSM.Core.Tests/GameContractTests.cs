using System.Diagnostics;
using System.Reflection;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>Every built-in game honours the basics the engine relies on — checked without installing any of them.</summary>
[Collection("Lifecycle")]
public class GameContractTests
{
    public static TheoryData<string> Games()
    {
        var data = new TheoryData<string>();
        foreach (var t in typeof(ServerConfig).Assembly.GetTypes()
                     .Where(t => t.Namespace == "WindowsGSM.GameServer" && t.GetField("FullName", BindingFlags.Public | BindingFlags.Static) != null)
                     .OrderBy(t => t.Name))
        {
            data.Add(t.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Games))]
    public async Task A_game_is_found_by_name_and_copes_with_no_install(string typeName)
    {
        EngineFixture.CreateServer("180");
        var type = typeof(ServerConfig).Assembly.GetType("WindowsGSM.GameServer." + typeName)!;
        string fullName = (string)type.GetField("FullName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var config = new ServerConfig("180");

        dynamic game = GameServer.Data.Class.Get(fullName, config)!;
        Assert.True(game != null, $"{fullName}: not found by its name");
        Assert.Equal(type, (Type)game.GetType());

        foreach (string field in new[] { "Port", "QueryPort" })
        {
            string? value = (string?)type.GetField(field)?.GetValue(game);
            if (value == null) { continue; }
            Assert.True(int.TryParse(value, out int port) && port is > 0 and < 65536, $"{fullName}: {field} \"{value}\"");
        }
        if (type.GetField("AppId")?.GetValue(game) is string appId) { Assert.True(appId.Length == 0 || appId.All(char.IsDigit), $"{fullName}: AppId \"{appId}\""); }

        Assert.False((bool)game.IsInstallValid(), $"{fullName}: an empty folder counts as installed");
        string empty = Path.Combine(Path.GetTempPath(), "wgsm-contract-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try { Assert.False((bool)game.IsImportValid(empty), $"{fullName}: an empty folder can be imported"); }
        finally { Directory.Delete(empty, true); }

        // Starting with nothing installed: no process, and a reason.
        Process? p = null;
        try { p = await game.Start(); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) { return; } // the engine words this one itself
        catch (Exception ex) { Assert.Fail($"{fullName}: Start threw {ex.GetType().Name}: {ex.Message}"); }
        if (p != null)
        {
            try { if (!p.HasExited) { p.Kill(true); } } catch { }
            Assert.Fail($"{fullName}: started a process ({SafeName(p)}) with nothing installed");
        }
        Assert.False(string.IsNullOrWhiteSpace((string?)game.Error), $"{fullName}: Start failed without saying why");
    }

    [Fact]
    public void Games_that_stop_by_killing_get_Ctrl_C_first_unless_turned_off()
    {
        EngineFixture.CreateServer("183");
        var cfg = new ServerConfig("183");
        Assert.True(Engine.Services.GameCatalog.StopsByKilling(GameServer.ECO.FullName, null));       // ends the process when captured
        Assert.False(Engine.Services.GameCatalog.StopsByKilling(GameServer.MC.FullName, null));
        Assert.True(Engine.Services.LifecycleService.StopWithCtrlCFirst(cfg, stopsByKilling: true));  // unset: on for those
        Assert.False(Engine.Services.LifecycleService.StopWithCtrlCFirst(cfg, stopsByKilling: false));
        cfg.CustomSettings[Engine.Services.LifecycleService.CtrlCFirstKey] = "0";
        Assert.False(Engine.Services.LifecycleService.StopWithCtrlCFirst(cfg, stopsByKilling: true)); // turned off stays off
    }

    private static string SafeName(Process p) { try { return p.StartInfo.FileName; } catch { return "?"; } }
}
