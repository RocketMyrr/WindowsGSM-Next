using System.Net;

namespace WindowsGSM.Agent.Tests;

/// <summary>The web UI is served from the agent's own assembly, never a temp folder, and never stale.</summary>
[Collection("Agent")]
public class WebUiTests
{
    private readonly AgentFixture _f;
    public WebUiTests(AgentFixture f) => _f = f;

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/app/main.js", "text/javascript")]
    [InlineData("/app/views/server/console.js", "text/javascript")]
    [InlineData("/css/tokens.css", "text/css")]
    [InlineData("/img/logo-192.png", "image/png")]
    [InlineData("/img/favicon.ico", "image/x-icon")]
    [InlineData("/img/games/rust.png", "image/png")]
    public async Task Ui_files_are_served_from_the_embedded_copy_and_always_revalidated(string path, string type)
    {
        var res = await _f.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(type, res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/machines/local/servers/101/console")]
    [InlineData("/install")]
    [InlineData("/account")]
    public async Task App_routes_load_the_single_page_app(string path)
    {
        var res = await _f.NewClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("/app/main.js", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_script_the_app_imports_exists()
    {
        // Walk the ES-module graph from main.js the way a browser would, so a typo'd import path fails here
        // instead of as a blank page.
        var client = _f.NewClient();
        var seen = new HashSet<string>();
        var queue = new Queue<string>(new[] { "/app/main.js" });
        var import = new System.Text.RegularExpressions.Regex(@"(?:from\s+|import\s*\(\s*)[""'](\.{1,2}/[^""']+)[""']");
        while (queue.Count > 0)
        {
            string path = queue.Dequeue();
            if (!seen.Add(path)) { continue; }
            var res = await client.GetAsync(path);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path} → {(int)res.StatusCode}");
            string js = await res.Content.ReadAsStringAsync();
            foreach (System.Text.RegularExpressions.Match m in import.Matches(js))
            {
                var resolved = new Uri(new Uri("http://x" + path), m.Groups[1].Value).AbsolutePath;
                queue.Enqueue(resolved);
            }
        }
        Assert.True(seen.Count > 20, $"only found {seen.Count} modules");
    }
}
