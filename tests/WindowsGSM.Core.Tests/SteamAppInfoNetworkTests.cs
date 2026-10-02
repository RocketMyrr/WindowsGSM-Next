using WindowsGSM.Installer;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Talks to real Steam, so it's opt-in: set WGSM_NETWORK_TESTS=1 to run it. Without the variable these
/// tests return immediately (and say so in their name) so CI never depends on Steam being reachable.
/// </summary>
public class SteamAppInfoNetworkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public SteamAppInfoNetworkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("WGSM_NETWORK_TESTS") == "1";

    [Fact]
    public async Task Opt_in_Rust_public_branch_has_a_numeric_build_id()
    {
        if (!Enabled) { _output.WriteLine("skipped: WGSM_NETWORK_TESTS not set"); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? build = await SteamAppInfo.GetBuildIdAsync("258550"); // Rust Dedicated Server
        _output.WriteLine($"Rust public build = {build} ({sw.ElapsedMilliseconds} ms)");

        Assert.False(string.IsNullOrWhiteSpace(build));
        Assert.True(ulong.TryParse(build, out _), $"expected a numeric build id, got '{build}'");
    }

    [Fact]
    public async Task Opt_in_unknown_branch_returns_null_not_an_error()
    {
        if (!Enabled) { return; }

        Assert.Null(await SteamAppInfo.GetBuildIdAsync("258550", "no-such-branch-wgsm"));
    }

    [Fact]
    public async Task Opt_in_branch_list_starts_with_public()
    {
        if (!Enabled) { return; }

        var branches = await SteamAppInfo.GetBranchesAsync("258550");
        _output.WriteLine(string.Join(", ", branches.Select(b => $"{b.Name}{(b.PasswordRequired ? " (pwd)" : "")}={b.BuildId}")));
        Assert.Equal("public", branches[0].Name);
        Assert.True(branches.Count > 1, "Rust has more than one branch");
    }
}
