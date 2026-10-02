using System.Net;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Server tags: set with EditConfig, cleaned up, shown in the server list.</summary>
[Collection("Agent")]
public class TagTests
{
    private readonly AgentFixture _f;
    public TagTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task Tags_are_saved_cleaned_and_listed()
    {
        var res = await _f.Owner.PutAsync(_f.ServerUrl("103", "/tags"), new { tags = new[] { " EU ", "friends", "eu", "" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(new[] { "EU", "friends" }, await ApiClient.Read<string[]>(res));

        var servers = await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers");
        Assert.Equal(new[] { "EU", "friends" }, servers.Single(s => s.Id == "103").Tags);

        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl("103", "/tags"), new { tags = Enumerable.Range(0, 9).Select(i => "t" + i) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl("103", "/tags"), new { tags = new[] { new string('x', 30) } })).StatusCode);

        var viewer = await _f.UserAsync("tagviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(_f.ServerUrl("103", "/tags"), new { tags = new[] { "nope" } })).StatusCode);

        await _f.Owner.PutAsync(_f.ServerUrl("103", "/tags"), new { tags = Array.Empty<string>() });
        Assert.Empty((await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers")).Single(s => s.Id == "103").Tags ?? new List<string>());
    }
}
