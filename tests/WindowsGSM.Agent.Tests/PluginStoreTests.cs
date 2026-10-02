using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Installing community plugins from a (fake) GitHub archive: found, compiled, listed as a game, removable.</summary>
[Collection("Agent")]
public class PluginStoreTests
{
    private readonly AgentFixture _f;
    public PluginStoreTests(AgentFixture f) => _f = f;

    private PluginStore Store => _f.App.Services.GetRequiredService<PluginStore>();

    private const string Source = """
        using System.Diagnostics;
        using System.Threading.Tasks;
        using WindowsGSM.Functions;

        namespace WindowsGSM.Plugins
        {
            public class HelloGame
            {
                public Plugin Plugin = new Plugin { name = "WindowsGSM.HelloGame", author = "someone", description = "A tiny test game", version = "1.2", url = "https://github.com/someone/WindowsGSM.HelloGame", color = "#ffffff" };
                private readonly ServerConfig _serverData;
                public string Error, Notice;
                public string FullName = "Hello Game Dedicated Server";
                public string StartPath = "";
                public bool AllowsEmbedConsole = true;
                public int PortIncrements = 1;
                public object QueryMethod = null;
                public string Port = "41000", QueryPort = "41001", Defaultmap = "", Maxplayers = "4", Additional = "";
                public HelloGame(ServerConfig serverData) { _serverData = serverData; }
                public async Task<Process> Start() => null;
                public async Task Stop(Process p) { }
                public async Task<Process> Install() => null;
                public bool IsInstallValid() => true;
                public bool IsImportValid(string path) => true;
                public async Task<Process> Update(bool validate = false, string custom = null) => null;
            }
        }
        """;

    /// <summary>A zip shaped like GitHub's: everything under "owner-repo-sha/".</summary>
    private static byte[] Archive(params (string Path, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry("someone-WindowsGSM.HelloGame-1a2b3c/" + path).Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task A_plugin_is_installed_compiled_and_offered_as_a_game_then_removed()
    {
        Store.DownloadOverride = (repo, _) => Task.FromResult(Archive(("README.md", "# Hello"), ("HelloGame.cs/HelloGame.cs", Source)));
        try
        {
            var admin = await _f.UserAsync("pluginadmin", Role.Admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/machines/local/plugins", new { repo = "someone/WindowsGSM.HelloGame" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/machines/local/plugins", new { repo = "not a repo" })).StatusCode);

            var res = await _f.Owner.PostAsync("/api/v2/machines/local/plugins", new { repo = "someone/WindowsGSM.HelloGame" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var installed = await ApiClient.Read<JsonElement>(res);
            Assert.Equal("HelloGame.cs", installed.GetProperty("file").GetString());
            Assert.True(installed.GetProperty("loaded").GetBoolean(), installed.GetProperty("error").ToString());
            Assert.Equal("someone/WindowsGSM.HelloGame", installed.GetProperty("repo").GetString());

            var games = await _f.Owner.GetJsonAsync<List<GameDto>>("/api/v2/machines/local/games");
            Assert.Contains(games, g => g.Name == "Hello Game Dedicated Server [HelloGame.cs]");
            var list = await admin.GetJsonAsync<List<JsonElement>>("/api/v2/machines/local/plugins");
            Assert.Contains(list, p => p.GetProperty("file").GetString() == "HelloGame.cs" && p.GetProperty("version").GetString() == "1.2");

            // A plugin that servers use can't be removed; an unused one can.
            var inUse = await _f.Owner.DeleteAsync("/api/v2/machines/local/plugins/WgsmTestGame.cs");
            Assert.Equal(HttpStatusCode.BadRequest, inUse.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.DeleteAsync("/api/v2/machines/local/plugins/HelloGame.cs")).StatusCode);
            Assert.DoesNotContain(await _f.Owner.GetJsonAsync<List<GameDto>>("/api/v2/machines/local/games"), g => g.Name.Contains("HelloGame.cs"));
            Assert.False(Directory.Exists(ServerPath.GetPlugins("HelloGame.cs")));
        }
        finally
        {
            Store.DownloadOverride = null;
            if (Directory.Exists(ServerPath.GetPlugins("HelloGame.cs"))) { Directory.Delete(ServerPath.GetPlugins("HelloGame.cs"), true); await _f.Engine.Plugins.LoadAsync(); }
        }
    }

    [Fact]
    public void Archives_without_a_plugin_or_with_unsafe_paths_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => PluginStore.Extract(Archive(("README.md", "nothing here"))));
        Assert.Throws<InvalidOperationException>(() => PluginStore.Extract(Archive(("Evil.cs/Evil.cs", "x"), ("Evil.cs/../../../escaped.txt", "x"))));
        Assert.False(File.Exists(Path.Combine(ServerPath.GetPlugins(), "..", "escaped.txt")));
        if (Directory.Exists(ServerPath.GetPlugins("Evil.cs.new"))) { Directory.Delete(ServerPath.GetPlugins("Evil.cs.new"), true); }
    }

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    private async Task<HttpResponseMessage> Upload(ApiClient client, string name, byte[] content, byte[]? logo = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(content), "file", name);
        if (logo != null) { form.Add(new ByteArrayContent(logo), "logo", "logo.png"); }
        return await client.Http.PostAsync("/api/v2/machines/local/plugins/upload", form);
    }

    [Fact]
    public async Task Owners_can_add_their_own_plugin_file_with_a_logo()
    {
        try
        {
            var admin = await _f.UserAsync("pluginadmin2", Role.Admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await Upload(admin, "HelloGame.cs", Encoding.UTF8.GetBytes(Source))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(_f.Owner, "Hello Game.cs", Encoding.UTF8.GetBytes(Source))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(_f.Owner, "HelloGame.cs", Encoding.UTF8.GetBytes(Source), "not a png"u8.ToArray())).StatusCode);

            var res = await Upload(_f.Owner, "HelloGame.cs", Encoding.UTF8.GetBytes(Source), Png);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var installed = await ApiClient.Read<JsonElement>(res);
            Assert.True(installed.GetProperty("loaded").GetBoolean(), installed.GetProperty("error").ToString());
            Assert.True(installed.GetProperty("hasIcon").GetBoolean());
            Assert.True(File.Exists(ServerPath.GetPlugins("HelloGame.cs", "HelloGame.cs")));

            // Its logo is served to admins for the plugins page.
            var icon = await admin.Http.GetAsync("/api/v2/machines/local/plugins/HelloGame.cs/icon");
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
            Assert.Equal(Png, await icon.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync("/api/v2/machines/local/plugins/..%5Cx.cs/icon")).StatusCode);
        }
        finally
        {
            if (Directory.Exists(ServerPath.GetPlugins("HelloGame.cs"))) { Directory.Delete(ServerPath.GetPlugins("HelloGame.cs"), true); await _f.Engine.Plugins.LoadAsync(); }
        }
    }

    [Fact]
    public async Task A_zip_of_the_plugin_folder_contents_works_and_a_broken_plugin_says_why()
    {
        try
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var w = new StreamWriter(zip.CreateEntry("HelloGame.cs").Open())) { w.Write(Source); }
                    zip.CreateEntry("HelloGame.png").Open().Write(Png);
                }
                var res = await Upload(_f.Owner, "my-plugin.zip", ms.ToArray());
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var installed = await ApiClient.Read<JsonElement>(res);
                Assert.Equal("HelloGame.cs", installed.GetProperty("file").GetString());
                Assert.True(installed.GetProperty("hasIcon").GetBoolean());
            }

            // Doesn't compile: listed with the compiler's reason, and the error log goes to the data folder's logs\.
            var broken = await Upload(_f.Owner, "HelloGame.cs", Encoding.UTF8.GetBytes(Source.Replace("public bool IsInstallValid()", "public bool IsInstallValid(")));
            var p = await ApiClient.Read<JsonElement>(broken);
            Assert.False(p.GetProperty("loaded").GetBoolean());
            Assert.Contains("Compilation failed", p.GetProperty("error").GetString());
            Assert.Equal("HelloGame.cs", p.GetProperty("file").GetString());
            Assert.True(File.Exists(ServerPath.Get("logs", "pluginsImportError.log")));
        }
        finally
        {
            if (Directory.Exists(ServerPath.GetPlugins("HelloGame.cs"))) { Directory.Delete(ServerPath.GetPlugins("HelloGame.cs"), true); await _f.Engine.Plugins.LoadAsync(); }
        }
    }

    [Fact]
    public async Task A_plugin_that_only_forgot_a_using_still_loads()
    {
        // Like ohmcodes/WindowsGSM.Windrose: Regex used without "using System.Text.RegularExpressions;".
        string source = Source.Replace("public bool IsInstallValid() => true;", "public bool IsInstallValid() => Regex.IsMatch(\"a\", \"a\");");
        try
        {
            var p = await ApiClient.Read<JsonElement>(await Upload(_f.Owner, "HelloGame.cs", Encoding.UTF8.GetBytes(source)));
            Assert.True(p.GetProperty("loaded").GetBoolean(), p.GetProperty("error").ToString());

            // A real mistake still fails, with the compiler's reason.
            var broken = await ApiClient.Read<JsonElement>(await Upload(_f.Owner, "HelloGame.cs", Encoding.UTF8.GetBytes(source.Replace("Regex.IsMatch", "NoSuchThing.IsMatch"))));
            Assert.False(broken.GetProperty("loaded").GetBoolean());
            Assert.Contains("NoSuchThing", broken.GetProperty("error").GetString());
        }
        finally
        {
            if (Directory.Exists(ServerPath.GetPlugins("HelloGame.cs"))) { Directory.Delete(ServerPath.GetPlugins("HelloGame.cs"), true); await _f.Engine.Plugins.LoadAsync(); }
        }
    }

    [Fact]
    public void Repository_urls_are_recognised()
    {
        Assert.Equal("owner/WindowsGSM.Palworld", PluginStore.RepoOf("https://github.com/owner/WindowsGSM.Palworld"));
        Assert.Equal("owner/WindowsGSM.Palworld", PluginStore.RepoOf("https://github.com/owner/WindowsGSM.Palworld.git"));
        Assert.Equal("owner/repo", PluginStore.RepoOf("https://www.github.com/owner/repo/tree/main"));
        Assert.Null(PluginStore.RepoOf("https://gitlab.com/owner/repo"));
        Assert.Null(PluginStore.RepoOf(""));
    }
}
