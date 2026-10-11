using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Hub;

namespace WindowsGSM.Agent.Tests;

/// <summary>Machines on different WindowsGSM versions: who can update, and when "not found" means "too old".</summary>
public class VersionsTests
{
    private static AppRelease Release(string v) => new(v, v, null, "https://feed/x.zip", null, 1, null, v.Contains('-'));

    [Fact]
    public void A_machine_can_update_only_to_something_newer_than_it_runs()
    {
        Assert.Equal("2.0.0-beta.3", Versions.UpdateFor("v2.0.0-beta.2", Release("2.0.0-beta.3")));
        Assert.Equal("2.0.0", Versions.UpdateFor("2.0.0-beta.9", Release("2.0.0")));
        Assert.Null(Versions.UpdateFor("v2.0.0-beta.3", Release("2.0.0-beta.3")));
        Assert.Null(Versions.UpdateFor("2.1.0", Release("2.0.5")));
        Assert.Null(Versions.UpdateFor("v2.0.0", null));
        // Not known (a machine that never said, or an odd value): no claim either way.
        Assert.Null(Versions.UpdateFor("?", Release("9.0.0")));
        Assert.Null(Versions.UpdateFor(null, Release("9.0.0")));
        Assert.Null(Versions.UpdateFor("v", Release("9.0.0")));
    }

    [Fact]
    public void Not_found_from_an_older_machine_means_the_feature_is_newer()
    {
        // No route on an older machine (an empty 404/405): the feature is newer than it.
        Assert.True(Versions.MissingOnOlder(404, null, "v2.0.0-beta.1", "v2.0.0-beta.3"));
        Assert.True(Versions.MissingOnOlder(405, "text/plain", "2.0.0-beta.1", "v2.0.0"));
        // The API's own "not found" (a JSON error) is passed on as it is: that server or file really isn't there.
        Assert.False(Versions.MissingOnOlder(404, "application/json; charset=utf-8", "v2.0.0-beta.1", "v2.0.0-beta.3"));
        // Same, newer or unknown version: not the reason.
        Assert.False(Versions.MissingOnOlder(404, null, "v2.0.0-beta.3", "v2.0.0-beta.3"));
        Assert.False(Versions.MissingOnOlder(404, null, "v2.1.0", "v2.0.0"));
        Assert.False(Versions.MissingOnOlder(404, null, null, "v2.0.0"));
        Assert.False(Versions.MissingOnOlder(500, null, "v1.0.0", "v2.0.0"));
    }
}
