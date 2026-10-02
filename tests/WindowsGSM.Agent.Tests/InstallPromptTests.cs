using System.Net;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;

namespace WindowsGSM.Agent.Tests;

/// <summary>Installing through the API, with the plugin's EULA question answered live or up front.</summary>
[Collection("Agent")]
public class InstallPromptTests
{
    private readonly AgentFixture _f;
    public InstallPromptTests(AgentFixture f) => _f = f;

    private async Task<JobDto> Finished(string jobId)
    {
        JobDto job = null!;
        await EngineFixture.WaitUntil(() =>
        {
            job = _f.Owner.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}").GetAwaiter().GetResult();
            return job.Status != "Running";
        }, "install to finish", 60000);
        return job;
    }

    [Fact]
    public async Task An_unexpected_question_waits_for_a_person_to_answer()
    {
        var res = await _f.Owner.PostAsync("/api/v2/machines/local/servers", new InstallRequest(EngineFixture.GameName, "Asked live"));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        string jobId = (await ApiClient.Read<JobAccepted>(res)).JobId;

        PromptDto prompt = null!;
        await EngineFixture.WaitUntil(() =>
        {
            var pending = _f.Owner.GetJsonAsync<List<PromptDto>>("/api/v2/machines/local/prompts").GetAwaiter().GetResult();
            prompt = pending.FirstOrDefault(p => p.JobId == jobId)!;
            return prompt != null;
        }, "the EULA question");
        Assert.Equal("eula", prompt.Key);
        Assert.Contains("Waiting for an answer", (await _f.Owner.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}")).Stage);

        // Someone who can only view servers doesn't even see an install in progress.
        var viewer = await _f.UserAsync("promptviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"/api/v2/machines/local/prompts/{prompt.Id}", new PromptAnswer(true))).StatusCode);
        Assert.Empty(await viewer.GetJsonAsync<List<PromptDto>>("/api/v2/machines/local/prompts"));

        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PostAsync($"/api/v2/machines/local/prompts/{prompt.Id}", new PromptAnswer(true))).StatusCode);
        var job = await Finished(jobId);
        Assert.Equal("Succeeded", job.Status);
        Assert.Contains(job.RecentLog, l => l.Contains("owner answered yes"));

        var server = (await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers")).Single(s => s.Name == "Asked live");
        await Cleanup(server.Id);
    }

    [Fact]
    public async Task Consent_given_up_front_means_no_question_and_declining_fails_cleanly()
    {
        var upFront = await _f.Owner.PostAsync("/api/v2/machines/local/servers", new InstallRequest(EngineFixture.GameName, "Consented", Consents: new[] { "eula" }));
        var done = await Finished((await ApiClient.Read<JobAccepted>(upFront)).JobId);
        Assert.Equal("Succeeded", done.Status);
        await Cleanup((await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers")).Single(s => s.Name == "Consented").Id);

        var declined = await _f.Owner.PostAsync("/api/v2/machines/local/servers", new InstallRequest(EngineFixture.GameName, "Declined"));
        string jobId = (await ApiClient.Read<JobAccepted>(declined)).JobId;
        PromptDto? prompt = null;
        await EngineFixture.WaitUntil(() =>
        {
            prompt = _f.Owner.GetJsonAsync<List<PromptDto>>("/api/v2/machines/local/prompts").GetAwaiter().GetResult().FirstOrDefault(p => p.JobId == jobId);
            return prompt != null;
        }, "the EULA question");
        await _f.Owner.PostAsync($"/api/v2/machines/local/prompts/{prompt!.Id}", new PromptAnswer(false));
        var failed = await Finished(jobId);
        Assert.Equal("Failed", failed.Status);
        Assert.DoesNotContain(await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers"), s => s.Name == "Declined");
    }

    [Fact]
    public async Task Only_people_allowed_to_install_can_and_unknown_games_are_refused()
    {
        var op = await _f.UserAsync("noinstall", Role.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/v2/machines/local/servers", new InstallRequest(EngineFixture.GameName, "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/machines/local/servers", new InstallRequest("Not A Game", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/v2/machines/local/servers/import", new ImportRequest(EngineFixture.GameName, "x", "C:\\"))).StatusCode);
    }

    private async Task Cleanup(string id)
    {
        var del = await _f.Owner.DeleteAsync(_f.ServerUrl(id));
        Assert.Equal(HttpStatusCode.Accepted, del.StatusCode);
        await Finished((await ApiClient.Read<JobAccepted>(del)).JobId);
    }
}
