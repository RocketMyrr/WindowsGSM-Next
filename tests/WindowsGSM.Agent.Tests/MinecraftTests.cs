using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Tests;

/// <summary>Minecraft: Paper installed from a fake PaperMC API; plugins from a fake Modrinth (with a dependency), updated and removed.</summary>
[Collection("Agent")]
public class MinecraftTests
{
    private readonly AgentFixture _f;
    public MinecraftTests(AgentFixture f) => _f = f;

    private static readonly byte[] Jar = Encoding.ASCII.GetBytes("PK\u0003\u0004 fake jar");
    private static string Hex(byte[] data, HashAlgorithm a) { using (a) { return Convert.ToHexString(a.ComputeHash(data)).ToLowerInvariant(); } }

    private sealed class Fake : HttpMessageHandler
    {
        public string LuckPermsVersion = "v1";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            string? json = null;
            byte[]? bytes = null;
            if (url == "https://fill.papermc.io/v3/projects/paper") { json = """{"versions":{"1.21":["1.21.4","1.21.4-rc1","1.21.3"]}}"""; }
            else if (url.StartsWith("https://fill.papermc.io/v3/projects/paper/versions/1.21.4/builds/latest"))
            {
                json = """{"id":232,"downloads":{"server:default":{"name":"paper-1.21.4-232.jar","checksums":{"sha256":"SHA"},"url":"https://fill-data.papermc.io/paper.jar"}}}"""
                    .Replace("SHA", Hex(Jar, SHA256.Create()));
            }
            else if (url == "https://fill-data.papermc.io/paper.jar") { bytes = Jar; }
            else if (url.StartsWith("https://api.modrinth.com/v2/search")) { json = """{"hits":[{"project_id":"LP","slug":"luckperms","title":"LuckPerms","description":"Permissions","downloads":123,"icon_url":null}]}"""; }
            else if (url.StartsWith("https://api.modrinth.com/v2/project/LP/version"))
            {
                Assert.Contains("\"paper\"", url);
                Assert.Contains("\"1.21.4\"", url);
                json = ("""[{"id":"lp-VER","version_number":"VER","version_type":"release","files":[{"primary":true,"filename":"LuckPerms-VER.jar","url":"https://cdn.modrinth.com/lp.jar","hashes":{"sha512":"SHA"}}],"""
                    + "\"dependencies\":[{\"project_id\":\"VAULT\",\"dependency_type\":\"required\"},{\"project_id\":\"OPT\",\"dependency_type\":\"optional\"}]}]")
                    .Replace("VER", LuckPermsVersion).Replace("SHA", Hex(Jar, SHA512.Create()));
            }
            else if (url.StartsWith("https://api.modrinth.com/v2/project/VAULT/version"))
            {
                json = """[{"id":"vault-1","version_number":"1.7","version_type":"release","files":[{"primary":true,"filename":"Vault.jar","url":"https://cdn.modrinth.com/vault.jar","hashes":{"sha512":"SHA"}}],"dependencies":[]}]"""
                    .Replace("SHA", Hex(Jar, SHA512.Create()));
            }
            else if (url == "https://api.modrinth.com/v2/project/LP") { json = """{"id":"LP","slug":"luckperms","title":"LuckPerms","icon_url":null}"""; }
            else if (url == "https://api.modrinth.com/v2/project/VAULT") { json = """{"id":"VAULT","slug":"vault","title":"Vault","icon_url":null}"""; }
            else if (url.StartsWith("https://cdn.modrinth.com/")) { bytes = Jar; }
            if (json == null && bytes == null) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = bytes != null ? new ByteArrayContent(bytes) : new StringContent(json!, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Paper_is_installed_and_plugins_come_from_Modrinth_with_their_dependencies()
    {
        const string id = "101";
        var fake = new Fake();
        MinecraftSoftware.HttpOverride = new HttpClient(fake);
        string dir = ServerPath.GetServersServerFiles(id);
        try
        {
            Assert.Equal(new[] { "1.21.4", "1.21.3" }, await MinecraftSoftware.VersionsAsync("paper")); // no release candidates

            File.WriteAllText(Path.Combine(dir, "server.jar"), "old vanilla");
            var info = await MinecraftSoftware.InstallAsync(id, "paper", "latest");
            Assert.Equal(("paper", "1.21.4", "232", true), (info.Flavor, info.Version, info.Build, info.FollowLatest));
            Assert.Equal(Jar, File.ReadAllBytes(Path.Combine(dir, "server.jar")));
            Assert.Equal("old vanilla", File.ReadAllText(Path.Combine(dir, "server.jar.previous")));
            Assert.Contains("eula=true", File.ReadAllText(Path.Combine(dir, "eula.txt")));
            Assert.Equal("paper 1.21.4 #232", MinecraftSoftware.Label(MinecraftSoftware.Read(id)!));

            var modrinth = new Modrinth(new HttpClient(fake));
            var c = Modrinth.ContextFor(id)!;
            Assert.Equal("plugins", c.Folder);
            Assert.Single(await modrinth.SearchAsync(id, c, "perm", default));

            File.WriteAllBytes(Path.Combine(dir, "plugins", "MyOwn.jar"), Jar);
            var done = await modrinth.InstallAsync(id, c, "LP", default);
            Assert.Equal(new[] { "LuckPerms", "Vault" }, done.Select(d => d.Title)); // the required dependency, not the optional one
            Assert.True(File.Exists(Path.Combine(dir, "plugins", "LuckPerms-v1.jar")));
            Assert.Equal(new[] { "MyOwn.jar" }, modrinth.Others(id, c));

            fake.LuckPermsVersion = "v2";
            var changes = await modrinth.UpdateAllAsync(id, c, default);
            Assert.Equal(new[] { "LuckPerms: v1 → v2" }, changes);
            Assert.False(File.Exists(Path.Combine(dir, "plugins", "LuckPerms-v1.jar")));
            Assert.True(File.Exists(Path.Combine(dir, "plugins", "LuckPerms-v2.jar")));

            Assert.True(modrinth.Remove(id, c, "LP"));
            Assert.False(File.Exists(Path.Combine(dir, "plugins", "LuckPerms-v2.jar")));
            Assert.True(File.Exists(Path.Combine(dir, "plugins", "MyOwn.jar"))); // never touched
        }
        finally
        {
            MinecraftSoftware.HttpOverride = null;
            foreach (string f in new[] { "server.jar", "server.jar.previous", "eula.txt", "wgsm-minecraft.json", "wgsm-modrinth.json" }) { File.Delete(Path.Combine(dir, f)); }
            if (Directory.Exists(Path.Combine(dir, "plugins"))) { Directory.Delete(Path.Combine(dir, "plugins"), true); }
        }
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_refused_and_server_jar_kept()
    {
        const string id = "102";
        MinecraftSoftware.HttpOverride = new HttpClient(new BadJar());
        string dir = ServerPath.GetServersServerFiles(id);
        File.WriteAllText(Path.Combine(dir, "server.jar"), "keep me");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => MinecraftSoftware.InstallAsync(id, "paper", "1.21.4"));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(dir, "server.jar")));
            Assert.Null(MinecraftSoftware.Read(id));
        }
        finally { MinecraftSoftware.HttpOverride = null; File.Delete(Path.Combine(dir, "server.jar")); }
    }

    private sealed class BadJar : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri!.ToString();
            var content = url.Contains("builds/latest")
                ? new StringContent("""{"id":1,"downloads":{"server:default":{"name":"p.jar","checksums":{"sha256":"00"},"url":"https://fill-data.papermc.io/p.jar"}}}""")
                : (HttpContent)new ByteArrayContent(Jar);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
