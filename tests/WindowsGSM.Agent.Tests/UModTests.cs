using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Rust plugins from a fake uMod: install with requirements, keep hand-added ones up to date, update, edits left alone, remove.</summary>
[Collection("Agent")]
public class UModTests
{
    private readonly AgentFixture _f;
    public UModTests(AgentFixture f) => _f = f;

    private static string Plugin(string title, string version, string? requires = null) =>
        (requires != null ? $"// Requires: {requires}\n" : "") +
        $"namespace Oxide.Plugins\n{{\n    [Info(\"{title}\", \"Someone\", \"{version}\")]\n    public class {title.Replace(" ", "")} : RustPlugin {{ }}\n}}\n";

    private static string Sha1(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>umod.org's search.json and downloads, for a few plugins.</summary>
    private sealed class FakeUMod : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Sources = new()
        {
            ["Kits"] = Plugin("Kits", "1.0.0", requires: "ImageLibrary"),
            ["ImageLibrary"] = Plugin("Image Library", "2.0.0"),
            ["GatherManager"] = Plugin("Gather Manager", "2.2.78"),
        };
        public bool WrongChecksum;
        public string Distribution = "download";

        private string Entry(string name) => $$"""
            {"name":"{{name}}","title":"{{name}}","description":"{{name}} for Rust","author":"Someone","distribution":"{{Distribution}}",
             "latest_release_version":"{{System.Text.RegularExpressions.Regex.Match(Sources[name], "\"([0-9.]+)\"\\)").Groups[1].Value}}",
             "latest_release_version_checksum":"{{(WrongChecksum ? new string('0', 40) : Sha1(Sources[name]))}}",
             "download_url":"https://umod.org/plugins/{{name}}.cs","url":"https://umod.org/plugins/{{name.ToLowerInvariant()}}","icon_url":null,"downloads":1000}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            string? body = null;
            if (url.StartsWith("https://umod.org/plugins/search.json"))
            {
                Assert.Contains("categories[]=rust", url);
                string query = System.Text.RegularExpressions.Regex.Match(url, @"query=([^&]*)").Groups[1].Value;
                var names = Sources.Keys.Where(n => query.Length == 0 || n.Contains(query, StringComparison.OrdinalIgnoreCase));
                body = $"{{\"data\":[{string.Join(",", names.Select(Entry))}],\"total\":{names.Count()}}}";
            }
            else if (url.StartsWith("https://umod.org/plugins/") && url.EndsWith(".cs"))
            {
                string name = url["https://umod.org/plugins/".Length..^3];
                if (Sources.TryGetValue(name, out var source)) { body = source; }
            }
            if (body == null) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Plugins_come_from_uMod_with_what_they_require_and_hand_added_ones_are_left_alone()
    {
        const string id = "103";
        string files = ServerPath.GetServersServerFiles(id);
        string managed = Path.Combine(files, "RustDedicated_Data", "Managed");
        string plugins = Path.Combine(files, "oxide", "plugins");
        var fake = new FakeUMod();
        var umod = new UMod(new HttpClient(fake));
        try
        {
            Assert.Null(UMod.ContextFor(id)); // no Oxide or Carbon: nothing can load plugins
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(managed, "Oxide.Core.dll"), "fake");
            var c = UMod.ContextFor(id)!;
            Assert.Equal(("Oxide", Path.Combine("oxide", "plugins")), (c.Framework, c.Folder));

            Assert.Equal(3, (await umod.SearchAsync(id, "", default)).Count);

            // Install: Kits brings ImageLibrary, which it requires.
            Directory.CreateDirectory(plugins);
            File.WriteAllText(Path.Combine(plugins, "MyOwn.cs"), Plugin("My Own", "0.1"));
            var done = await umod.InstallAsync(id, c, "Kits", default);
            Assert.Equal(new[] { "Kits", "ImageLibrary" }, done.Select(d => d.Name));
            Assert.True(done[1].Dependency);
            Assert.Equal(fake.Sources["Kits"], File.ReadAllText(Path.Combine(plugins, "Kits.cs")));
            var present = umod.Plugins(id, c);
            Assert.Contains(present, p => p.Name == "MyOwn" && !p.Tracked && p.Version == "0.1" && p.Title == "My Own");
            Assert.Contains(present, p => p.Name == "Kits" && p.Tracked && !p.Edited);

            // A plugin added by hand isn't installed over…
            File.WriteAllText(Path.Combine(plugins, "GatherManager.cs"), Plugin("Gather Manager", "2.2.70"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => umod.InstallAsync(id, c, "GatherManager", default));
            // …but can be kept up to date from uMod when asked; MyOwn isn't on uMod.
            Assert.Equal("2.2.70", (await umod.TrackAsync(id, c, "GatherManager", default)).Version);
            await Assert.ThrowsAsync<InvalidOperationException>(() => umod.TrackAsync(id, c, "MyOwn", default));

            // New versions; a tracked plugin edited since is left alone.
            fake.Sources["Kits"] = Plugin("Kits", "1.0.1", requires: "ImageLibrary");
            Assert.Equal(new[] { "Kits", "GatherManager" }, (await umod.UpdatesAvailableAsync(id, default)).OrderByDescending(n => n));
            File.AppendAllText(Path.Combine(plugins, "ImageLibrary.cs"), "// my tweak\n");
            fake.Sources["ImageLibrary"] = Plugin("Image Library", "2.0.1");
            var (changes, skipped) = await umod.UpdateAllAsync(id, c, default);
            Assert.Contains("Kits: 1.0.0 → 1.0.1", changes);
            Assert.Contains("GatherManager: 2.2.70 → 2.2.78", changes);
            Assert.Contains(skipped, s => s.StartsWith("ImageLibrary") && s.Contains("changed by hand"));
            Assert.EndsWith("// my tweak\n", File.ReadAllText(Path.Combine(plugins, "ImageLibrary.cs")));
            Assert.Equal(Plugin("My Own", "0.1"), File.ReadAllText(Path.Combine(plugins, "MyOwn.cs"))); // never touched
            Assert.Equal(new[] { "ImageLibrary" }, await umod.UpdatesAvailableAsync(id, default)); // only the edited one is behind

            // A download that doesn't match uMod's checksum is refused and nothing is written.
            fake.Sources["Kits"] = Plugin("Kits", "1.0.2", requires: "ImageLibrary");
            fake.WrongChecksum = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => umod.UpdateAllAsync(id, c, default));
            Assert.Contains("1.0.1", File.ReadAllText(Path.Combine(plugins, "Kits.cs")));
            fake.WrongChecksum = false;

            // Only downloadable plugins (not paid or external ones).
            fake.Distribution = "external";
            Assert.Empty(await umod.SearchAsync(id, "", default));
            fake.Distribution = "download";

            // Remove: the file goes, its config stays; hand-added files stay.
            Assert.True(umod.Remove(id, c, "Kits"));
            Assert.False(File.Exists(Path.Combine(plugins, "Kits.cs")));
            Assert.False(umod.Remove(id, c, "MyOwn"));
            Assert.True(File.Exists(Path.Combine(plugins, "MyOwn.cs")));
            Assert.True(umod.Untrack(id, "GatherManager"));
            Assert.True(File.Exists(Path.Combine(plugins, "GatherManager.cs")));
        }
        finally
        {
            WindowsGSM.Core.Tests.TestData.DeleteFile(Path.Combine(files, "wgsm-umod.json"));
            if (Directory.Exists(Path.Combine(files, "oxide"))) { WindowsGSM.Core.Tests.TestData.DeleteDirectory(Path.Combine(files, "oxide")); }
            if (Directory.Exists(Path.Combine(files, "RustDedicated_Data"))) { WindowsGSM.Core.Tests.TestData.DeleteDirectory(Path.Combine(files, "RustDedicated_Data")); }
        }
    }

    [Fact]
    public async Task The_Rust_tab_is_only_for_Rust_servers()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.GetAsync(_f.ServerUrl("103", "/rust"))).StatusCode);
    }
}
