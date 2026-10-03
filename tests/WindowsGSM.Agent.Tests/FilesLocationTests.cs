using System.Net;
using System.Text.Json;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Where a server's files are, and "Open folder" — which only someone at the server's own PC gets.</summary>
[Collection("Agent")]
public class FilesLocationTests
{
    private readonly AgentFixture _f;
    public FilesLocationTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task The_location_is_shown_but_Explorer_is_never_opened_for_someone_on_another_PC()
    {
        // Test clients appear to come from another computer (X-Test-Ip), like a phone on the network.
        var where = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("101", "/files-location"));
        Assert.False(where.GetProperty("elsewhere").GetBoolean());
        Assert.Equal(ServerLocation.LinkPath("101"), where.GetProperty("path").GetString());
        Assert.False(where.GetProperty("canOpen").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Owner.PostAsync(_f.ServerUrl("101", "/open-folder"))).StatusCode);
    }

    [Fact]
    public async Task Choosing_drives_and_moving_files_are_for_admins()
    {
        var op = await _f.UserAsync("placesoperator", WindowsGSM.Contracts.Role.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v2/machines/local/file-places")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync(_f.ServerUrl("101", "/move-files"), new { folder = @"E:\GameServers" })).StatusCode);

        var places = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/machines/local/file-places?folder=" + Uri.EscapeDataString(@"\\nas\games"));
        Assert.NotEmpty(places.GetProperty("drives").EnumerateArray());
        Assert.Contains("Network shares", places.GetProperty("problem").GetString());
    }
}
