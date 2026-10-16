using System.Text;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>The Windows runtimes a server program needs (Visual C++, DirectX, .NET), read from the program itself.</summary>
[Collection("Lifecycle")]
public class RuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wgsm-runtime-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _system;
    private readonly Func<bool, string> _realSystem = Runtimes.SystemFolder;
    private readonly Func<IEnumerable<string>> _realPath = Runtimes.PathFolders;

    public RuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _system = Path.Combine(_dir, "system32");
        Directory.CreateDirectory(_system);
        Runtimes.SystemFolder = _ => _system; // a Windows without any runtimes
        Runtimes.PathFolders = () => Array.Empty<string>();
    }

    public void Dispose()
    {
        Runtimes.SystemFolder = _realSystem;
        Runtimes.PathFolders = _realPath;
        TestData.DeleteDirectory(_dir);
    }

    /// <summary>The smallest 64-bit Windows program that imports <paramref name="dlls"/>.</summary>
    private static byte[] Program(params string[] dlls)
    {
        var b = new byte[0x400];
        void U16(int at, int v) => BitConverter.GetBytes((ushort)v).CopyTo(b, at);
        void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(b, at);
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        U32(0x3C, 0x40);
        b[0x40] = (byte)'P'; b[0x41] = (byte)'E';
        U16(0x44, 0x8664); U16(0x46, 1); U16(0x54, 0xF0);       // x64, one section, optional header size
        U16(0x58, 0x20B);                                         // PE32+
        U32(0x58 + 112 + 8, 0x1000); U32(0x58 + 112 + 12, 20 * (uint)(dlls.Length + 1)); // import directory
        int section = 0x58 + 0xF0;
        Encoding.ASCII.GetBytes(".idata").CopyTo(b, section);
        U32(section + 8, 0x200); U32(section + 12, 0x1000); U32(section + 16, 0x200); U32(section + 20, 0x200);
        int names = 0x200 + 20 * (dlls.Length + 1);
        for (int i = 0; i < dlls.Length; i++)
        {
            U32(0x200 + i * 20 + 12, (uint)(0x1000 + names - 0x200));
            Encoding.ASCII.GetBytes(dlls[i] + "\0").CopyTo(b, names);
            names += dlls[i].Length + 1;
        }
        return b;
    }

    [Fact]
    public void A_program_says_which_runtime_DLLs_it_loads()
    {
        string exe = Path.Combine(_dir, "game.exe");
        File.WriteAllBytes(exe, Program("KERNEL32.dll", "VCRUNTIME140.dll", "steam_api64.dll", "d3dx9_43.dll"));
        var info = PeImports.Read(exe)!;
        Assert.False(info.X86);
        Assert.Equal(new[] { "KERNEL32.dll", "VCRUNTIME140.dll", "steam_api64.dll", "d3dx9_43.dll" }, info.Imports);
        Assert.Null(PeImports.Read(Path.Combine(_dir, "nope.exe")));
    }

    [Fact]
    public void Missing_runtimes_are_named_and_the_games_own_DLLs_are_not()
    {
        string exe = Path.Combine(_dir, "game.exe");
        File.WriteAllBytes(exe, Program("KERNEL32.dll", "VCRUNTIME140.dll", "MSVCP140.dll", "steam_api64.dll", "d3dx9_43.dll"));

        var needs = Runtimes.For(exe);
        Assert.Equal(new[] { "directx-x64", "vc2015-x64" }, needs.Select(n => n.Key));
        Assert.All(needs, n => Assert.False(n.Installed));
        Assert.Contains("MSVCP140.dll, VCRUNTIME140.dll", needs.Single(n => n.Key == "vc2015-x64").Detail);

        // Installed (in Windows' system folder) or shipped beside the program: fine.
        File.WriteAllText(Path.Combine(_system, "VCRUNTIME140.dll"), "");
        File.WriteAllText(Path.Combine(_system, "MSVCP140.dll"), "");
        File.WriteAllText(Path.Combine(_dir, "d3dx9_43.dll"), "");
        Assert.All(Runtimes.For(exe), n => Assert.True(n.Installed));
    }

    [Fact]
    public void A_dotnet_program_says_which_runtime_it_needs()
    {
        string exe = Path.Combine(_dir, "VintagestoryServer.exe");
        File.WriteAllBytes(exe, Program("KERNEL32.dll"));
        File.WriteAllText(Path.Combine(_dir, "VintagestoryServer.runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net99.0","framework":{"name":"Microsoft.NETCore.App","version":"99.0.0"}}}""");
        var need = Assert.Single(Runtimes.For(exe));
        Assert.Equal(("dotnet-99", ".NET Runtime 99", false), (need.Key, need.Name, need.Installed));

        // Self-contained (brings its own): nothing needed.
        File.WriteAllText(Path.Combine(_dir, "VintagestoryServer.runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net99.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"99.0.0"}]}}""");
        Assert.Empty(Runtimes.For(exe));
    }

    [Fact]
    public void Only_installers_signed_by_Microsoft_are_run()
    {
        Assert.True(Runtimes.SignedByMicrosoft(typeof(object).Assembly.Location)); // .NET's own files are
        string exe = Path.Combine(_dir, "vc_redist.x64.exe");
        File.WriteAllBytes(exe, Program("KERNEL32.dll"));
        Assert.False(Runtimes.SignedByMicrosoft(exe));
    }

    [Fact]
    public void Crash_exit_codes_that_mean_a_missing_DLL_say_so()
    {
        Assert.Contains("DLL the server needs wasn't found", CrashReport.Meaning(-1073741515)); // 0xC0000135
        Assert.Contains(".NET runtime", CrashReport.Meaning(unchecked((int)0x80008083)));
        Assert.Null(CrashReport.Meaning(1));
    }

    [Fact]
    public async Task Opt_in_the_real_sources_answer()
    {
        if (Environment.GetEnvironmentVariable("WGSM_NETWORK_TESTS") != "1") { return; }
        // Vintage Story's own feed, and PocketMine's build info on GitHub.
        var vts = new WindowsGSM.GameServer.VTS(new ServerConfig("9999"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", await vts.GetRemoteBuild());
        var mcpe = new WindowsGSM.GameServer.MCPE(new ServerConfig("9999"));
        Assert.Matches(@"^\d+$", await mcpe.GetRemoteBuild());
        // FiveM's own version feed: "35245-6efb47df…".
        var fivem = new WindowsGSM.GameServer.GTA5(new ServerConfig("9999"));
        Assert.Matches(@"^\d{4,}-[0-9a-f]{40}$", await fivem.GetRemoteBuild());
        // .NET 10 into WindowsGSM's folder, checksum-verified.
        await Runtimes.DownloadDotnetAsync(10, "Microsoft.NETCore.App");
        Assert.Contains(Directory.EnumerateDirectories(Path.Combine(Runtimes.OwnDotnet, "shared", "Microsoft.NETCore.App")), d => Path.GetFileName(d).StartsWith("10."));
        Assert.True(File.Exists(Path.Combine(Runtimes.OwnDotnet, "dotnet.exe")));
    }
}
