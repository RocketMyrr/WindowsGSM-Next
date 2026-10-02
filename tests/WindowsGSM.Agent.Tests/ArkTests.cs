using System.Net;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>ARK: -mods= and cluster flags edited in the start parameters without disturbing the rest.</summary>
[Collection("Agent")]
public class ArkTests
{
    private readonly AgentFixture _f;
    public ArkTests(AgentFixture f) => _f = f;

    [Theory]
    [InlineData("", "mods", "1,2", "-mods=1,2")]
    [InlineData("?ServerPassword=x -NoBattlEye", "mods", "123", "?ServerPassword=x -NoBattlEye -mods=123")]
    [InlineData("-mods=1 -NoBattlEye", "mods", "4,5", "-NoBattlEye -mods=4,5")]
    [InlineData("-clusterid=a -ClusterDirOverride=\"D:\\My Cluster\" -log", "ClusterDirOverride", null, "-clusterid=a -log")]
    [InlineData("-MODS=9", "mods", null, "")]
    public void Flags_are_set_and_removed(string before, string key, string? value, string after) =>
        Assert.Equal(after, ArkTools.SetFlag(before, key, value).Trim());

    [Fact]
    public void Flags_are_read_with_quotes_removed()
    {
        Assert.Equal("D:\\My Cluster", ArkTools.GetFlag("-clusterid=a -ClusterDirOverride=\"D:\\My Cluster\"", "clusterdiroverride"));
        Assert.Null(ArkTools.GetFlag("?Port=7777", "mods"));
        Assert.Equal("D:\\x y", ArkTools.GetFlag(ArkTools.SetFlag("", "ClusterDirOverride", "D:\\x y"), "ClusterDirOverride"));
    }

    [Fact]
    public async Task The_mod_list_keeps_order_and_names_and_ARK_only_endpoints_refuse_other_games()
    {
        var ark = new ArkTools(_f.Context);
        var s = _f.Engine.Servers.Get("101")!;
        string before = ServerConfig.GetSetting("101", ServerConfig.SettingName.ServerParam);
        try
        {
            Assert.Null(ark.SetMods(s, new[] { new ArkTools.ModEntry("928793", "Cybers Structures"), new ArkTools.ModEntry("900062", null) }));
            Assert.EndsWith("-mods=928793,900062", ServerConfig.GetSetting("101", ServerConfig.SettingName.ServerParam));
            Assert.Equal(new[] { "Cybers Structures", null }, ark.Mods(s).Select(m => m.Name));
            Assert.NotNull(ark.SetMods(s, new[] { new ArkTools.ModEntry("abc", null) }));

            Assert.Null(ark.Kind(s));
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.GetAsync(_f.ServerUrl("101", "/ark"))).StatusCode);
            Assert.NotNull(ark.SetCluster("c1", new[] { "101" }, null)); // not an ARK server
        }
        finally
        {
            ServerConfig.SetSetting("101", ServerConfig.SettingName.ServerParam, before);
            s.ReloadConfig();
        }
    }
}
