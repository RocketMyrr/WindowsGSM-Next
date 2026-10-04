using System.Net;
using System.Text;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Workshop mods against a fake Steam: a collection is expanded, DayZ-style install (@Mod, keys, -mod=), removal.</summary>
[Collection("Agent")]
public class WorkshopTests
{
    private readonly AgentFixture _f;
    public WorkshopTests(AgentFixture f) => _f = f;

    /// <summary>Steam's Web API with a collection (900900) of two mods (101010, 202020) for DayZ (221100).</summary>
    private sealed class FakeSteam : HttpMessageHandler
    {
        public long Updated = 1700000000;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            var ids = System.Text.RegularExpressions.Regex.Matches(Uri.UnescapeDataString(body), @"publishedfileids\[\d+\]=(\d+)").Select(m => m.Groups[1].Value).ToList();
            string json;
            if (request.RequestUri!.AbsolutePath.Contains("GetCollectionDetails"))
            {
                json = "{\"response\":{\"collectiondetails\":[" + string.Join(",", ids.Select(id => id == "900900"
                    ? "{\"publishedfileid\":\"900900\",\"result\":1,\"children\":[{\"publishedfileid\":\"101010\",\"filetype\":0},{\"publishedfileid\":\"202020\",\"filetype\":0}]}"
                    : $"{{\"publishedfileid\":\"{id}\",\"result\":9}}")) + "]}}";
            }
            else
            {
                json = "{\"response\":{\"publishedfiledetails\":[" + string.Join(",", ids.Select(id => id switch
                {
                    "900900" => "{\"publishedfileid\":\"900900\",\"result\":1,\"title\":\"My collection\",\"consumer_app_id\":221100,\"file_size\":0,\"time_updated\":1}",
                    "101010" => $"{{\"publishedfileid\":\"101010\",\"result\":1,\"title\":\"Community Framework\",\"consumer_app_id\":221100,\"file_size\":\"1000\",\"time_updated\":{Updated}}}",
                    "202020" => $"{{\"publishedfileid\":\"202020\",\"result\":1,\"title\":\"Base Building+\",\"consumer_app_id\":221100,\"file_size\":\"2000\",\"time_updated\":{Updated}}}",
                    "303030" => "{\"publishedfileid\":\"303030\",\"result\":1,\"title\":\"Other game mod\",\"consumer_app_id\":4000,\"file_size\":\"5\",\"time_updated\":1}",
                    _ => $"{{\"publishedfileid\":\"{id}\",\"result\":9}}",
                })) + "]}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_collection_installs_DayZ_style_and_a_mod_comes_out_cleanly()
    {
        const string id = "103";
        await _f.Owner.PostAsync(_f.ServerUrl(id, "/kill"));
        await EngineFixture.WaitUntil(() => _f.Engine.Servers.Get(id)!.State == WindowsGSM.Engine.Servers.ServerState.Stopped, "stopped");
        ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerParam, "-config=serverDZ.cfg -mod=\"@MyOwn\"");
        var steam = new FakeSteam();
        var workshop = new Workshop(_f.Context, new HttpClient(steam));
        int downloads = 0;
        workshop.DownloadOverride = (app, file, dir) =>
        {
            Assert.Equal("221100", app);
            downloads++;
            Directory.CreateDirectory(Path.Combine(dir, "addons"));
            Directory.CreateDirectory(Path.Combine(dir, "keys"));
            File.WriteAllText(Path.Combine(dir, "addons", $"{file}.pbo"), "pbo");
            File.WriteAllText(Path.Combine(dir, "keys", $"author{file}.bikey"), "key");
            return Task.FromResult<string?>(null);
        };
        try
        {
            // The collection link brings both mods; the app id comes from them; a mod for another game is refused.
            var (added, problems) = await workshop.AddAsync(id, "https://steamcommunity.com/sharedfiles/filedetails/?id=900900", default);
            Assert.Equal(new[] { "101010", "202020" }, added.Select(a => a.Id));
            Assert.Empty(problems);
            var (_, other) = await workshop.AddAsync(id, "303030", default);
            Assert.Contains("another game", Assert.Single(other));
            var s = workshop.Load(id);
            Assert.Equal("221100", s.AppId);
            s.Style = "arma";
            workshop.Save(id, s);

            // Download & place.
            var job = workshop.Update(id);
            Assert.True(job.Accepted, job.Error);
            var result = await job.Job!.Completion;
            Assert.True(result.Status == WindowsGSM.Engine.Operations.JobStatus.Succeeded, result.Error);
            string files = ServerPath.GetServersServerFiles(id);
            Assert.True(File.Exists(Path.Combine(files, "@Community_Framework", "addons", "101010.pbo")));
            Assert.True(File.Exists(Path.Combine(files, "keys", "author202020.bikey")));
            string param = new ServerConfig(id).ServerParam;
            Assert.Contains("-mod=\"@MyOwn;@Community_Framework;@Base_Building\"", param);
            Assert.StartsWith("-config=serverDZ.cfg", param);

            // Nothing changed on Steam: nothing downloads again. A newer version: both do.
            Assert.True((await workshop.Update(id).Job!.Completion).Status == WindowsGSM.Engine.Operations.JobStatus.Succeeded);
            Assert.Equal(2, downloads);
            steam.Updated += 100;
            await workshop.Update(id).Job!.Completion;
            Assert.Equal(4, downloads);

            // Removing one takes its folder, its key and its -mod= entry — and leaves the server's own.
            Assert.Null(workshop.Remove(id, "101010"));
            Assert.False(Directory.Exists(Path.Combine(files, "@Community_Framework")));
            Assert.False(File.Exists(Path.Combine(files, "keys", "author101010.bikey")));
            Assert.Contains("-mod=\"@MyOwn;@Base_Building\"", new ServerConfig(id).ServerParam);
        }
        finally
        {
            foreach (var i in workshop.Load(id).Items.ToList()) { workshop.Remove(id, i.Id); }
            WindowsGSM.Core.Tests.TestData.DeleteFile(ServerPath.GetServersConfigs(id, "workshop.json"));
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerParam, "");
        }
    }

    [Fact]
    public void Workshop_links_and_numbers_are_understood()
    {
        Assert.Equal(new[] { "1559212036" }, Workshop.IdsIn("https://steamcommunity.com/sharedfiles/filedetails/?id=1559212036&searchtext="));
        Assert.Equal(new[] { "1559212036", "2116151222" }, Workshop.IdsIn("1559212036, 2116151222"));
        Assert.Empty(Workshop.IdsIn("not a link"));
    }
}
