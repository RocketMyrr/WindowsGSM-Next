using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Core.Tests;

/// <summary>NeoForge: its version numbers, the Java it needs, and (opt-in) a real install that starts from its arguments file.</summary>
[Collection("Lifecycle")]
public class NeoForgeTests
{
    [Theory]
    [InlineData("21.1.209", "1.21.1")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("26.3.0.90", "26.3")]
    [InlineData("26.1.1.4", "26.1.1")]
    [InlineData("26.3.0.73-beta", "26.3")]
    [InlineData("nonsense", null)]
    public void NeoForge_versions_say_which_Minecraft_they_are_for(string neo, string? minecraft) => Assert.Equal(minecraft, MinecraftSoftware.NeoForgeMinecraft(neo));

    [Theory]
    [InlineData("26.3", 25)]
    [InlineData("1.21.1", 21)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.16.5", 8)]
    public void The_installer_runs_on_the_Java_that_Minecraft_version_needs(string minecraft, int java) => Assert.Equal(java, MinecraftSoftware.JavaFor(minecraft));

    [Fact]
    public async Task Opt_in_a_real_NeoForge_install_starts_from_its_arguments_file()
    {
        if (Environment.GetEnvironmentVariable("WGSM_NETWORK_TESTS") != "1") { return; }
        EngineFixture.CreateServer("148");
        var versions = await MinecraftSoftware.VersionsAsync("neoforge");
        Assert.Contains("1.21.1", versions);
        var installed = await MinecraftSoftware.InstallAsync("148", "neoforge", "latest");
        Assert.Equal("neoforge", installed.Flavor);
        string files = ServerPath.GetServersServerFiles("148");
        Assert.True(File.Exists(Path.Combine(files, MinecraftSoftware.NeoForgeArgs(installed.Build!))));
        Assert.True(File.Exists(Path.Combine(files, MinecraftSoftware.NeoForgeMinecraftJar(installed.Version))));
        Assert.True(File.Exists(Path.Combine(files, "user_jvm_args.txt")));
        Assert.Empty(Directory.EnumerateFiles(files, "*installer*")); // cleaned up
    }
}
