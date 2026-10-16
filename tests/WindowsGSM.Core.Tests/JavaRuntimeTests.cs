using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>Minecraft gets the Java its server.jar needs: read from the jar, picked from what's installed, or downloaded.</summary>
[Collection("Lifecycle")]
public class JavaRuntimeTests
{
    private static string Jar(params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), "wgsm-jar-" + Guid.NewGuid().ToString("N") + ".jar");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    [Theory]
    [InlineData("1.8.0_491", 8)]
    [InlineData("25.0.4", 25)]
    [InlineData("17", 17)]
    [InlineData("21.0.2+13-LTS", 21)]
    [InlineData("nonsense", null)]
    public void Java_versions_read_as_their_major_number(string text, int? major) => Assert.Equal(major, JavaRuntimes.Major(text));

    [Fact]
    public void The_jar_says_which_Java_it_needs()
    {
        // Vanilla: version.json.
        string vanilla = Jar(("version.json", Encoding.UTF8.GetBytes("""{"id":"26.3","java_version":25}""")));
        // Others: the main class's file version (69 = Java 25, 52 = Java 8).
        string other = Jar(("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\r\nMain-Class: io.example.Main\r\n")),
                           ("io/example/Main.class", new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 65 }));
        string unknown = Jar(("readme.txt", Encoding.UTF8.GetBytes("hi")));
        try
        {
            Assert.Equal(25, JavaRuntimes.RequiredMajor(vanilla));
            Assert.Equal(21, JavaRuntimes.RequiredMajor(other));
            Assert.Null(JavaRuntimes.RequiredMajor(unknown));
            Assert.Null(JavaRuntimes.RequiredMajor(Path.Combine(Path.GetTempPath(), "no-such.jar")));
        }
        finally { foreach (var f in new[] { vanilla, other, unknown }) { TestData.DeleteFile(f); } }
    }

    [Fact]
    public void The_oldest_Java_that_is_new_enough_is_used()
    {
        var installed = new[] { new JavaRuntimes.Runtime("j8", 8), new JavaRuntimes.Runtime("j25", 25), new JavaRuntimes.Runtime("j21", 21) };
        Assert.Equal("j21", JavaRuntimes.Best(installed, 17)!.Exe); // old Minecraft and mods dislike much newer Java
        Assert.Equal("j25", JavaRuntimes.Best(installed, 25)!.Exe);
        Assert.Null(JavaRuntimes.Best(installed, 26)); // Java 8 alone never runs a Java 25 jar
        Assert.Equal("j25", JavaRuntimes.Best(installed, null)!.Exe); // jar doesn't say: the newest
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_checksum_is_not_used()
    {
        var (fetch, download) = (JavaRuntimes.FetchJson, JavaRuntimes.Download);
        byte[] zipBytes = File.ReadAllBytes(Jar(("jdk-99-jre/bin/java.exe", new byte[] { 1, 2, 3 })));
        string Asset(string sum) => "[{\"binary\":{\"package\":{\"name\":\"OpenJDK99U-jre_x64_windows.zip\",\"link\":\"https://example.com/j.zip\",\"checksum\":\"" + sum + "\"}}}]";
        try
        {
            JavaRuntimes.Download = (url, path) => File.WriteAllBytesAsync(path, zipBytes);
            JavaRuntimes.FetchJson = _ => Task.FromResult(Asset(new string('0', 64)));
            await Assert.ThrowsAsync<InvalidDataException>(() => JavaRuntimes.DownloadTemurinAsync(99));
            Assert.False(Directory.Exists(Path.Combine(JavaRuntimes.OwnFolder, "temurin-99")));

            JavaRuntimes.FetchJson = _ => Task.FromResult(Asset(Convert.ToHexString(SHA256.HashData(zipBytes))));
            string exe = await JavaRuntimes.DownloadTemurinAsync(99);
            Assert.Equal(Path.Combine(JavaRuntimes.OwnFolder, "temurin-99", "bin", "java.exe"), exe);
            Assert.True(File.Exists(exe));
        }
        finally
        {
            (JavaRuntimes.FetchJson, JavaRuntimes.Download) = (fetch, download);
            string dir = Path.Combine(JavaRuntimes.OwnFolder, "temurin-99");
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }

    [Fact]
    public async Task Opt_in_a_real_Temurin_download_runs()
    {
        if (Environment.GetEnvironmentVariable("WGSM_NETWORK_TESTS") != "1") { return; }
        string exe = await JavaRuntimes.DownloadTemurinAsync(25);
        var found = JavaRuntimes.Installed().Single(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(25, found.Major);
    }
}
