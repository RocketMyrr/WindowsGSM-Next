using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>WindowsGSM's own settings files: a file that can't be read must never turn into lost settings.</summary>
public class SafeJsonTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgsm-safejson-" + Guid.NewGuid().ToString("N")[..8]);
    private string F(string name = "settings.json") => Path.Combine(_dir, name);

    public SafeJsonTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => TestData.DeleteDirectory(_dir);

    public enum Colour { None, Red, Green }

    public sealed class Settings
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public Colour Colour { get; set; }
        public List<string> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Strings = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void A_save_keeps_the_previous_copy()
    {
        SafeJson.Write(F(), new Settings { Name = "one" });
        SafeJson.Write(F(), new Settings { Name = "two" });
        var got = SafeJson.Read<Settings>(F());
        Assert.True(got != null, string.Join(" | ", SafeJson.Problems.Select(p => p.Message)));
        Assert.Equal("two", got!.Name);
        Assert.Contains("one", File.ReadAllText(F() + ".bak"));
        Assert.False(File.Exists(F() + ".tmp"));
    }

    [Fact]
    public void A_damaged_file_is_read_from_its_previous_copy_and_kept_aside()
    {
        SafeJson.Write(F(), new Settings { Name = "good", Items = { "a", "b" } });
        SafeJson.Write(F(), new Settings { Name = "newer" });
        File.WriteAllText(F(), "{ \"Name\": \"newer\", \"Ite"); // cut off mid-write

        var s = SafeJson.Read<Settings>(F());
        Assert.Equal("good", s!.Name);
        Assert.False(SafeJson.Lost(F()));
        var p = Assert.Single(SafeJson.Problems, x => x.File == F());
        Assert.True(p.Recovered);
        Assert.Contains("{ \"Name\": \"newer\", \"Ite", File.ReadAllText(p.KeptAs!));
    }

    [Fact]
    public void A_damaged_file_with_no_previous_copy_is_kept_and_the_next_save_cannot_destroy_it()
    {
        File.WriteAllText(F(), "this isn't json { at all");
        Assert.Null(SafeJson.Read<Settings>(F()));
        Assert.True(SafeJson.Lost(F()));
        string kept = SafeJson.Problems.Single(x => x.File == F()).KeptAs!;

        SafeJson.Write(F(), new Settings { Name = "fresh" });                 // the store saves its empty state
        Assert.Equal("this isn't json { at all", File.ReadAllText(kept));     // the original is still there
        Assert.False(File.Exists(F() + ".bak"));                              // and the damaged file never becomes "the previous copy"
        Assert.False(SafeJson.Lost(F()));                                     // a good save clears the problem

        // Reading the same damaged file again (agent restarted) doesn't pile up copies.
        File.WriteAllText(F("again.json"), "nope");
        SafeJson.Read<Settings>(F("again.json"));
        SafeJson.Read<Settings>(F("again.json"));
        Assert.Single(Directory.GetFiles(_dir, "again.json.unreadable-*"));
    }

    [Fact]
    public void An_empty_file_counts_as_damaged()
    {
        SafeJson.Write(F(), new Settings { Name = "before" });
        SafeJson.Write(F(), new Settings { Name = "after" });
        File.WriteAllText(F(), "");
        Assert.Equal("before", SafeJson.Read<Settings>(F())!.Name);
    }

    [Fact]
    public void A_save_cut_off_before_the_swap_reads_the_previous_copy()
    {
        File.WriteAllText(F() + ".bak", "{ \"Name\": \"previous\" }");
        Assert.Equal("previous", SafeJson.Read<Settings>(F())!.Name);
        Assert.Null(SafeJson.Read<Settings>(F("missing.json")));
    }

    [Fact]
    public void Settings_from_a_newer_version_still_read()
    {
        // Going back to an older version: fields and option values it doesn't know are skipped, not fatal.
        File.WriteAllText(F(), """{ "Name": "x", "Count": 3, "Colour": "Purple", "NewThing": { "deep": [1, 2] }, "Items": ["a"] }""");
        var s = SafeJson.Read<Settings>(F(), Strings)!;
        Assert.Equal(("x", 3, Colour.None, "a"), (s.Name, s.Count, s.Colour, s.Items.Single()));
        File.WriteAllText(F("n.json"), """{ "Colour": 7 }""");
        Assert.Equal(Colour.None, SafeJson.Read<Settings>(F("n.json"))!.Colour);
        File.WriteAllText(F("ok.json"), """{ "Colour": "green" }""");
        Assert.Equal(Colour.Green, SafeJson.Read<Settings>(F("ok.json"), Strings)!.Colour);
    }

    [Fact]
    public void Hand_edits_with_comments_trailing_commas_and_quoted_numbers_read()
    {
        File.WriteAllText(F(), """
            {
              // my server
              "Name": "x",
              "Count": "5",
              "Items": ["a", "b",],
            }
            """);
        var s = SafeJson.Read<Settings>(F())!;
        Assert.Equal((5, 2), (s.Count, s.Items.Count));
    }

    [Fact]
    public void Writing_keeps_the_options_format()
    {
        SafeJson.Write(F(), new Settings { Colour = Colour.Red }, Strings);
        Assert.Contains("\"Red\"", File.ReadAllText(F()));
        SafeJson.Write(F("n.json"), new Settings { Colour = Colour.Red });
        Assert.Contains("\"Colour\":1", File.ReadAllText(F("n.json")));
    }

    [Fact]
    public void Newtonsoft_files_skip_values_they_cannot_read_but_not_broken_json()
    {
        File.WriteAllText(F(), """[{ "Name": "a", "Colour": "Purple" }, { "Name": "b", "Colour": "Red" }]""");
        var list = SafeJson.ReadWith(F(), t => JsonConvert.DeserializeObject<List<Settings>>(t, SafeJson.LenientNewtonsoft()))!;
        Assert.Equal(new[] { "a", "b" }, list.Select(x => x.Name));
        Assert.Equal(Colour.Red, list[1].Colour);

        SafeJson.WriteText(F("cut.json"), """[{ "Name": "whole" }]""");
        SafeJson.WriteText(F("cut.json"), """[{ "Name": "newer" }, { "Name": "also" }]""");
        File.WriteAllText(F("cut.json"), """[{ "Name": "newer" }, { "Na""");
        var recovered = SafeJson.ReadWith(F("cut.json"), t => JsonConvert.DeserializeObject<List<Settings>>(t, SafeJson.LenientNewtonsoft()))!;
        Assert.Equal("whole", recovered.Single().Name); // half a file is never used
    }
}

/// <summary>The data layout stamp: recorded, never lowered, upgrade steps backed up first.</summary>
// Shares the engines' process-wide data-format state (every engine start runs Prepare): never alongside them.
[Collection("Lifecycle")]
public class DataFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wgsm-dataformat-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();

    public DataFormatTests() => Directory.CreateDirectory(Path.Combine(_root, "configs", "next"));
    public void Dispose() { DataFormat.Steps = Array.Empty<DataFormat.Step>(); TestData.DeleteDirectory(_root); }

    private DataFormat.Stamp Stamp() => System.Text.Json.JsonSerializer.Deserialize<DataFormat.Stamp>(File.ReadAllText(DataFormat.StampFile(_root)))!;

    [Fact]
    public void A_folder_is_stamped_with_the_format_and_the_newest_version_that_used_it()
    {
        DataFormat.Prepare(_root, "v2.0.0-beta.1", _log.Add);
        Assert.Equal((DataFormat.Current, "2.0.0-beta.1"), (Stamp().Format, Stamp().LastVersion));
        DataFormat.Prepare(_root, "v2.0.0-beta.2", _log.Add);
        Assert.Equal("2.0.0-beta.2", Stamp().LastVersion);
        DataFormat.Prepare(_root, "v2.0.0-beta.1", _log.Add); // went back: still says beta.2 used it
        Assert.Equal("2.0.0-beta.2", Stamp().LastVersion);
        Assert.Null(DataFormat.NewerDataWarning);
    }

    [Fact]
    public void Data_from_a_newer_layout_is_left_alone_and_flagged()
    {
        File.WriteAllText(DataFormat.StampFile(_root), $$"""{ "Format": {{DataFormat.Current + 1}}, "LastVersion": "2.3.0" }""");
        DataFormat.Prepare(_root, "v2.0.0", _log.Add);
        Assert.Contains("2.3.0", DataFormat.NewerDataWarning);
        Assert.Equal(DataFormat.Current + 1, Stamp().Format); // never stamped down
    }

    [Fact]
    public void Upgrade_steps_run_once_in_order_after_backing_up_the_settings()
    {
        File.WriteAllText(Path.Combine(_root, "configs", "next", "automations.json"), "[]");
        Directory.CreateDirectory(Path.Combine(_root, "servers", "1", "configs"));
        File.WriteAllText(Path.Combine(_root, "servers", "1", "configs", "backup.json"), "{}");
        File.WriteAllText(DataFormat.StampFile(_root), """{ "Format": 1, "LastVersion": "2.0.0" }""");
        var ran = new List<string>();
        int was = DataFormat.Current;
        try
        {
            // A version two layouts newer than the folder.
            DataFormat.Current = 3;
            DataFormat.Steps = new[]
            {
                new DataFormat.Step(3, "second", _ => ran.Add("second")),
                new DataFormat.Step(2, "first", _ => ran.Add("first")),
            };
            DataFormat.Prepare(_root, "v2.1.0", _log.Add);

            Assert.Equal(new[] { "first", "second" }, ran);
            Assert.Equal(3, Stamp().Format);
            string zip = Assert.Single(Directory.GetFiles(Path.Combine(_root, "backups"), "wgsm-settings-before-format-2-*.zip"));
            using (var a = ZipFile.OpenRead(zip))
            {
                Assert.Contains(a.Entries, e => e.FullName == "configs/next/automations.json");
                Assert.Contains(a.Entries, e => e.FullName == "servers/1/configs/backup.json");
            }
            ran.Clear();
            DataFormat.Prepare(_root, "v2.1.0", _log.Add);
            Assert.Empty(ran); // done is done
        }
        finally { DataFormat.Current = was; }
    }
}
