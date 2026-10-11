using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Hub;

/// <summary>
/// Machines on different WindowsGSM versions. The link between them hasn't changed since the first release, so a
/// hub and its machines keep working across versions; what differs is which features each one has. The hub works
/// out from the versions alone (nothing new is asked of the other machines, so older ones are covered too) which
/// machine can update, and says so plainly when a feature isn't there because a machine is older.
/// </summary>
public static class Versions
{
    /// <summary>The release a machine on <paramref name="version"/> could update to, or null (up to date, or unknown).</summary>
    public static string? UpdateFor(string? version, AppRelease? latest) =>
        latest != null && Known(version) && AppReleases.Compare(latest.Version, Bare(version!)) > 0 ? latest.Version : null;

    /// <summary>Whether <paramref name="theirs"/> is an older WindowsGSM than <paramref name="ours"/>.</summary>
    public static bool Older(string? theirs, string ours) => Known(theirs) && AppReleases.Compare(Bare(theirs!), Bare(ours)) < 0;

    /// <summary>
    /// A forwarded request the other machine had no route for (404/405 without the API's own JSON error) while it runs
    /// an older version than this hub: the feature is newer than that machine.
    /// </summary>
    public static bool MissingOnOlder(int status, string? contentType, string? theirs, string ours) =>
        status is 404 or 405 && (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) && Older(theirs, ours);

    public static string Bare(string version) => version.Trim().TrimStart('v', 'V');

    private static bool Known(string? version) => !string.IsNullOrWhiteSpace(version) && Bare(version) is { Length: > 0 } bare && char.IsDigit(bare[0]);
}
