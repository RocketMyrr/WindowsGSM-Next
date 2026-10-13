using WindowsGSM.Engine.Services;

namespace WindowsGSM.Core.Tests;

/// <summary>Built-in add-ons' installed and latest versions (Oxide, Carbon), as the Add-ons tab shows them.</summary>
[Collection("Lifecycle")]
public class AddonVersionTests
{
    [Theory]
    [InlineData("2.0.7807-staging+feaed4f9349bd87e80e0389b5419b4465c0521c6", "2.0.7807")]
    [InlineData("Production Build — v2.0.262", "2.0.262")]
    [InlineData("2.0.7830", "2.0.7830")]
    [InlineData("no version here", null)]
    public void Versions_are_read_out_of_what_the_projects_publish(string text, string? number) => Assert.Equal(number, AddonVersions.Number(text));

    [Fact]
    public void Newer_compares_by_number_not_by_text()
    {
        Assert.True(AddonVersions.Newer("2.0.7830", "2.0.7807"));
        Assert.True(AddonVersions.Newer("2.0.10", "2.0.9"));
        Assert.False(AddonVersions.Newer("2.0.262", "2.0.262"));
        Assert.False(AddonVersions.Newer("2.0.261", "2.0.262"));
        Assert.False(AddonVersions.Newer(null, "2.0.1"));
    }

    [Fact]
    public async Task The_latest_comes_from_GitHub_once_an_hour_at_most()
    {
        var before = AddonVersions.Fetch;
        int asked = 0;
        AddonVersions.Fetch = url =>
        {
            asked++;
            return Task.FromResult(url.Contains("Oxide")
                ? """{"tag_name":"2.0.7830","name":"2.0.7830"}"""
                : """{"tag_name":"production_build","name":"Production Build — v2.0.262"}""");
        };
        try
        {
            AddonVersions.Forget("oxide");
            AddonVersions.Forget("carbon");
            await AddonVersions.RefreshAsync(new[] { "oxide", "carbon", "sourcemod" }, TimeSpan.FromSeconds(5));
            Assert.Equal("2.0.7830", AddonVersions.LatestKnown("oxide"));
            Assert.Equal("2.0.262", AddonVersions.LatestKnown("carbon"));
            Assert.Equal(2, asked); // add-ons without a release feed aren't asked about

            await AddonVersions.RefreshAsync(new[] { "oxide", "carbon" }, TimeSpan.FromSeconds(5));
            Assert.Equal(2, asked); // cached
        }
        finally
        {
            AddonVersions.Fetch = before;
            AddonVersions.Forget("oxide");
            AddonVersions.Forget("carbon");
        }
    }

    [Fact]
    public void The_installed_version_is_read_from_the_add_ons_own_file()
    {
        EngineFixture.CreateServer("155");
        string dll = WindowsGSM.Functions.ServerPath.GetServersServerFiles("155", Path.Combine("RustDedicated_Data", "Managed", "Oxide.Rust.dll"));
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.Copy(typeof(AddonVersions).Assembly.Location, dll, true); // any file with version info will do
        var (version, _) = AddonVersions.Installed("oxide", "155");
        Assert.Equal(AddonVersions.Number(System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion!), version);
        Assert.Equal((null, false), AddonVersions.Installed("carbon", "155")); // not there
    }
}
