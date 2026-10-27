using System.Text;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>The file editor saves a file the way it was: its encoding, byte-order mark and line endings.</summary>
[Collection("Lifecycle")]
public class TextFileTests
{
    private static string Write(string id, string name, byte[] bytes)
    {
        EngineFixture.CreateServer(id);
        string path = ServerPath.GetServersServerFiles(id, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task An_older_windows_file_keeps_its_accents_and_line_endings()
    {
        var files = new ServerFiles();
        string path = Write("196", "server.cfg", Encoding.Latin1.GetBytes("hostname \"Café ©\"\r\nsv_lan 0\r\n"));
        var opened = files.Read("196", "server.cfg");
        Assert.Contains("Café ©", opened.Content);
        await files.WriteAsync("196", "server.cfg", opened.Content!.Replace("\r\n", "\n").Replace("sv_lan 0", "sv_lan 1"));
        Assert.Equal(Encoding.Latin1.GetBytes("hostname \"Café ©\"\r\nsv_lan 1\r\n"), File.ReadAllBytes(path)); // byte for byte, CRLF kept
    }

    [Fact]
    public async Task Utf16_and_utf8_with_a_mark_open_and_save_as_they_were()
    {
        var files = new ServerFiles();
        string utf16 = Write("197", "Game.ini", new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("[Server]\nName=Ünreal\n")).ToArray());
        var opened = files.Read("197", "Game.ini");
        Assert.False(opened.Binary);
        await files.WriteAsync("197", "Game.ini", opened.Content!.Replace("Ünreal", "Ünreal 2"));
        Assert.Equal(new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes("[Server]\nName=Ünreal 2\n")), File.ReadAllBytes(utf16));

        string bom = Write("197", "cfg.json", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"a\":\"é\"}")).ToArray());
        await files.WriteAsync("197", "cfg.json", files.Read("197", "cfg.json").Content!.Replace("é", "è"));
        Assert.Equal(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"a\":\"è\"}")), File.ReadAllBytes(bom));
    }
}
