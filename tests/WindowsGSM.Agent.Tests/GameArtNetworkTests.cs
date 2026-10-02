using System.Text.Json;
using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Tests;

/// <summary>
/// Talks to Steam, so opt-in (WGSM_NETWORK_TESTS=1). Checks every curated store app id really is the game it's
/// listed for — a wrong id would show another game's art.
/// </summary>
public class GameArtNetworkTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    public GameArtNetworkTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;
    private static bool Enabled => Environment.GetEnvironmentVariable("WGSM_NETWORK_TESTS") == "1";

    [Fact]
    public async Task Opt_in_curated_app_ids_are_the_right_games()
    {
        if (!Enabled) { _out.WriteLine("skipped: WGSM_NETWORK_TESTS not set"); return; }
        using var http = new HttpClient();
        var wrong = new List<string>();
        foreach (var (game, id) in GameArt.Curated.Where(c => c.Value != null))
        {
            string json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={id}&filters=basic&l=english");
            using var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty(id!.Value.ToString());
            string name = entry.GetProperty("success").GetBoolean() ? entry.GetProperty("data").GetProperty("name").GetString()! : "(not found)";
            string want = GameArt.CleanName(game);
            // Share the first significant word (Counter-Strike 2 is fine for CS:GO; "Rust" for Rust…).
            string firstWord = want.Split(' ', ':', '-')[0].ToLowerInvariant();
            // Known renames on Steam: CS:GO became Counter-Strike 2; Post Scriptum became Squad 44.
            bool ok = name.ToLowerInvariant().Contains(firstWord) || (id == 730 && name.Contains("Counter-Strike")) || (id == 736220 && name == "Squad 44");
            _out.WriteLine($"{(ok ? "ok " : "BAD")} {id,8}  {want}  →  {name}");
            if (!ok) { wrong.Add($"{game}: {id} is '{name}'"); }
            await Task.Delay(250); // be polite to the store API
        }
        Assert.Empty(wrong);
    }
}
