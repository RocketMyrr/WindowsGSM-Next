using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// The plugin contract is the thing the new engine must never break: community plugins are C# source
/// compiled at run time against WindowsGSM.Core and bound via `dynamic`. These tests push real plugin
/// sources through the exact loader the app uses, and construct every built-in game server, with no UI.
/// </summary>
public class PluginCompatibilityTests
{
    public static IEnumerable<object[]> ThirdPartyPlugins()
    {
        // Without the plugin kit (a clone of this repository on its own) there's nothing to compile: one
        // placeholder case passes and says so, instead of the whole theory failing for lack of data.
        if (TestData.PluginKitDir == null) { yield return new object[] { NoKit }; yield break; }
        foreach (string file in Directory.GetFiles(Path.Combine(TestData.PluginKitDir, "Plugins"), "*.cs").OrderBy(f => f))
        {
            yield return new object[] { Path.GetFileName(file) };
        }
    }

    private const string NoKit = "(plugin kit not found — set WGSM_PLUGIN_KIT)";

    [Theory]
    [MemberData(nameof(ThirdPartyPlugins))]
    public async Task Third_party_plugin_compiles_and_loads(string fileName)
    {
        _ = TestData.DataRoot;
        if (fileName == NoKit) { return; }
        string path = Path.Combine(TestData.PluginKitDir!, "Plugins", fileName);

        var metadata = await new PluginManagement().LoadPlugin(path);

        Assert.True(metadata.IsLoaded, $"{fileName} failed to load: {metadata.Error}");
        Assert.False(string.IsNullOrWhiteSpace(metadata.FullName));
    }

    [Fact]
    public void Every_builtin_game_server_constructs_without_a_UI()
    {
        _ = TestData.DataRoot;
        var names = GameServer.Data.Icon.ResourceManager
            .GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true)!
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (string)e.Key)
            .ToList();
        Assert.NotEmpty(names);

        var failures = new List<string>();
        foreach (string fullName in names)
        {
            try
            {
                dynamic? server = GameServer.Data.Class.Get(fullName, new ServerConfig("1"));
                if (server == null) { failures.Add(fullName + ": no class"); }
            }
            catch (Exception ex) { failures.Add(fullName + ": " + ex.Message); }
        }
        Assert.True(failures.Count == 0, "Built-in servers that failed:\n" + string.Join("\n", failures));
    }
}
