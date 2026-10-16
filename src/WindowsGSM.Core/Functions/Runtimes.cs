#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: the Windows runtimes a game server needs and whether this PC has them — Visual C++, DirectX's extra
    /// DLLs, .NET. Read from the server program itself (the DLLs it imports, its .runtimeconfig.json), so it covers
    /// every game and plugin without a list to keep up to date. Legacy never checked: a server missing one exited at
    /// once with a bare exit code. Steam installs these on a player's PC; DepotDownloader and SteamCMD don't run those
    /// install scripts, so on a fresh Windows (Server) they're often missing.
    /// </summary>
    public static class Runtimes
    {
        /// <summary>What a server needs. <paramref name="Key"/> is what <see cref="InstallAsync"/> takes.</summary>
        public sealed record Need(string Key, string Name, bool Installed, string Detail);

        private sealed record Kind(string Key, string Name, Regex Dlls);

        // Only Windows' own runtimes: the DLLs games ship themselves (Unreal's third-party folder, steam_api64…) are
        // found by the game at run time, so they're never reported.
        private static readonly Kind[] Kinds =
        {
            new("vc2015", "Microsoft Visual C++ 2015–2022", new Regex(@"^(vcruntime140(_1)?|msvcp140(_1|_2|_atomic_wait|_codecvt_ids)?|concrt140|vcomp140)\.dll$", RegexOptions.IgnoreCase)),
            new("vc2013", "Microsoft Visual C++ 2013", new Regex(@"^(msvcr120|msvcp120|vcomp120)\.dll$", RegexOptions.IgnoreCase)),
            new("vc2012", "Microsoft Visual C++ 2012", new Regex(@"^(msvcr110|msvcp110|vcomp110)\.dll$", RegexOptions.IgnoreCase)),
            new("vc2010", "Microsoft Visual C++ 2010", new Regex(@"^(msvcr100|msvcp100|vcomp100)\.dll$", RegexOptions.IgnoreCase)),
            new("directx", "DirectX End-User Runtime (June 2010)", new Regex(@"^(d3dx9_\d+|d3dx10(_\d+)?|d3dx11_\d+|d3dcompiler_(3\d|4[0-3])|xinput1_[123]|xaudio2_[0-7]|x3daudio1_\d|xactengine\d_\d|xapofx1_\d)\.dll$", RegexOptions.IgnoreCase)),
        };

        /// <summary>Where WindowsGSM keeps its own .NET runtimes (data\dotnet — the layout "dotnet" itself uses).</summary>
        public static string OwnDotnet => Path.Combine(Hosting.WgsmEnvironment.DataRoot, "dotnet");
        private static string SystemDotnet => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        /// <summary>The folders on PATH (Windows looks there for DLLs too). Swappable for tests.</summary>
        public static Func<IEnumerable<string>> PathFolders { get; set; } = () => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');

        /// <summary>Swappable for tests and for machines without Windows' folders.</summary>
        public static Func<bool, string> SystemFolder { get; set; } = x86 =>
            Environment.GetFolderPath(x86 && Environment.Is64BitOperatingSystem ? Environment.SpecialFolder.SystemX86 : Environment.SpecialFolder.System);

        // ─────────────────────────────── What's needed ───────────────────────────────

        /// <summary>
        /// The runtimes <paramref name="exe"/> needs (from it and the DLLs beside it), each with whether it's here.
        /// Empty when the program can't be read.
        /// </summary>
        public static IReadOnlyList<Need> For(string exe)
        {
            var result = new List<Need>();
            if (!File.Exists(exe)) { return result; }
            string dir = Path.GetDirectoryName(exe)!;

            // Native: every runtime DLL the program and the DLLs next to it import.
            var files = new List<string> { exe };
            try { files.AddRange(Directory.EnumerateFiles(dir, "*.dll").Take(300)); } catch { /* unreadable folder */ }
            var needed = new Dictionary<string, (Kind Kind, bool X86, SortedSet<string> Missing, bool Any)>();
            foreach (string file in files)
            {
                var info = PeImports.Read(file);
                if (info == null) { continue; }
                foreach (string dll in info.Imports)
                {
                    var kind = Kinds.FirstOrDefault(k => k.Dlls.IsMatch(dll));
                    if (kind == null) { continue; }
                    string key = $"{kind.Key}-{(info.X86 ? "x86" : "x64")}";
                    if (!needed.TryGetValue(key, out var n)) { needed[key] = n = (kind, info.X86, new SortedSet<string>(StringComparer.OrdinalIgnoreCase), true); }
                    if (!Findable(dir, dll, info.X86)) { n.Missing.Add(dll); }
                }
            }
            foreach (var (key, n) in needed.OrderBy(k => k.Key))
            {
                string name = $"{n.Kind.Name} ({(n.X86 ? "x86" : "x64")})";
                result.Add(new Need(key, name, n.Missing.Count == 0, n.Missing.Count == 0
                    ? $"{name} is installed."
                    : $"The server needs {name}, which isn't installed (missing {string.Join(", ", n.Missing)})."));
            }

            // .NET: a framework-dependent program says which runtime in <name>.runtimeconfig.json.
            foreach (var (framework, version) in DotnetFrameworks(exe))
            {
                string label = framework switch
                {
                    "Microsoft.AspNetCore.App" => "ASP.NET Core Runtime",
                    "Microsoft.WindowsDesktop.App" => ".NET Desktop Runtime",
                    _ => ".NET Runtime",
                };
                var v = Version.Parse(version);
                bool here = DotnetRoot(framework, v) != null;
                string name = $"{label} {v.Major}";
                result.Add(new Need($"dotnet-{v.Major}", name, here,
                    here ? $"{name} is available." : $"The server needs {name} ({version} or newer {v.Major}.x), which isn't installed."));
            }
            return result;
        }

        /// <summary>Where Windows would look for it: beside the program, the system folder, then PATH.</summary>
        private static bool Findable(string dir, string dll, bool x86)
        {
            if (File.Exists(Path.Combine(dir, dll)) || File.Exists(Path.Combine(SystemFolder(x86), dll))) { return true; }
            foreach (string p in PathFolders())
            {
                try { if (p.Length > 0 && File.Exists(Path.Combine(p.Trim('"'), dll))) { return true; } } catch { /* bad entry */ }
            }
            return false;
        }

        /// <summary>The shared frameworks a .NET program asks for ("Microsoft.NETCore.App", "10.0.0").</summary>
        public static IEnumerable<(string Framework, string Version)> DotnetFrameworks(string exe)
        {
            string config = Path.ChangeExtension(exe, ".runtimeconfig.json");
            if (!File.Exists(config)) { yield break; }
            JObject? json = null;
            try { json = JObject.Parse(File.ReadAllText(config)); } catch { /* not ours to fix */ }
            var options = json?["runtimeOptions"];
            if (options == null || options["includedFrameworks"] != null) { yield break; } // self-contained
            var list = options["frameworks"] is JArray many ? many.ToList() : options["framework"] is JToken one ? new List<JToken> { one } : new List<JToken>();
            foreach (var f in list)
            {
                string? name = (string?)f["name"], version = (string?)f["version"];
                if (name != null && version != null && Version.TryParse(Regex.Match(version, @"^\d+(\.\d+){1,3}").Value, out _)) { yield return (name, Regex.Match(version, @"^\d+(\.\d+){1,3}").Value); }
            }
        }

        /// <summary>
        /// The dotnet folder that has <paramref name="framework"/> at <paramref name="atLeast"/> or a newer patch of the
        /// same major (how .NET rolls forward): Program Files\dotnet, then WindowsGSM's own. Null when neither does.
        /// </summary>
        public static string? DotnetRoot(string framework, Version atLeast)
        {
            foreach (string root in new[] { SystemDotnet, OwnDotnet })
            {
                string dir = Path.Combine(root, "shared", framework);
                if (!Directory.Exists(dir)) { continue; }
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    if (Version.TryParse(Regex.Match(Path.GetFileName(d), @"^\d+\.\d+\.\d+").Value, out var have) && have.Major == atLeast.Major && have >= atLeast) { return root; }
                }
            }
            return null;
        }

        /// <summary>
        /// For a .NET server: points it at WindowsGSM's own runtime when Windows doesn't have the one it needs, getting
        /// that runtime first if it isn't here yet (like Java for Minecraft — nothing is installed on the PC).
        /// </summary>
        public static async Task PrepareDotnetAsync(ProcessStartInfo start, string exe, Action<string> note)
        {
            foreach (var (framework, version) in DotnetFrameworks(exe))
            {
                var v = Version.Parse(version);
                string? root = DotnetRoot(framework, v);
                if (root == null)
                {
                    note($"This server needs .NET {v.Major}, which isn't installed. Downloading it for WindowsGSM's own use (about 30 MB)…");
                    await DownloadDotnetAsync(v.Major, framework).ConfigureAwait(false);
                    root = DotnetRoot(framework, v) ?? throw new InvalidOperationException($".NET {v.Major} was downloaded but isn't usable.");
                    note($".NET {v.Major} is ready.");
                }
                if (string.Equals(root, OwnDotnet, StringComparison.OrdinalIgnoreCase))
                {
                    start.Environment["DOTNET_ROOT"] = root;
                    start.Environment["DOTNET_ROOT_X64"] = root;
                }
            }
        }

        // ─────────────────────────────── Installing ───────────────────────────────

        // Microsoft's own download links (aka.ms → download.visualstudio.microsoft.com / download.microsoft.com).
        private static readonly Dictionary<string, string> Official = new(StringComparer.OrdinalIgnoreCase)
        {
            ["vc2015-x64"] = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            ["vc2015-x86"] = "https://aka.ms/vs/17/release/vc_redist.x86.exe",
            ["vc2013-x64"] = "https://aka.ms/highdpimfc2013x64enu",
            ["vc2013-x86"] = "https://aka.ms/highdpimfc2013x86enu",
            ["directx-x64"] = "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
            ["directx-x86"] = "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
        };

        public static Func<string, string, Task> Download { get; set; } = (url, path) => Http.DownloadFileAsync(url, path);
        public static Func<string, Task<string>> FetchJson { get; set; } = url => Http.DownloadStringAsync(url);

        /// <summary>
        /// Installs <paramref name="key"/> (from <see cref="For"/>) for <paramref name="serverId"/>: the installer the game
        /// shipped (Steam's _CommonRedist) when it has one, otherwise Microsoft's — either way only when it's signed by
        /// Microsoft. .NET goes into WindowsGSM's own folder. Returns null, or what went wrong.
        /// </summary>
        public static async Task<string?> InstallAsync(string serverId, string key, Action<string> log)
        {
            if (key.StartsWith("dotnet-", StringComparison.OrdinalIgnoreCase) && int.TryParse(key.Substring(7), out int major))
            {
                log($"Downloading .NET {major} into WindowsGSM's folder…");
                await DownloadDotnetAsync(major, "Microsoft.NETCore.App").ConfigureAwait(false);
                return null;
            }
            if (!Official.ContainsKey(key) && !key.StartsWith("vc", StringComparison.OrdinalIgnoreCase)) { return $"WindowsGSM doesn't know how to install '{key}'."; }
            if (!IsAdministrator()) { return "WindowsGSM isn't running as administrator, so it can't install this for Windows. Run the installer yourself, or restart WindowsGSM as administrator."; }

            bool x86 = key.EndsWith("-x86", StringComparison.OrdinalIgnoreCase);
            string? shipped = Shipped(serverId, key, x86);
            string temp = Path.Combine(Path.GetTempPath(), "wgsm-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string installer;
                if (shipped != null) { installer = shipped; log($"Using the installer that came with the game: {shipped}"); }
                else if (Official.TryGetValue(key, out string? url))
                {
                    installer = Path.Combine(temp, key.StartsWith("directx", StringComparison.OrdinalIgnoreCase) ? "directx_Jun2010_redist.exe" : $"{key}.exe");
                    log($"Downloading from Microsoft: {url}");
                    await Download(url, installer).ConfigureAwait(false);
                }
                else { return $"The game didn't come with an installer for this, and WindowsGSM has no Microsoft download for it. Get it from https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist."; }

                if (!SignedByMicrosoft(installer)) { return $"{Path.GetFileName(installer)} isn't signed by Microsoft, so it wasn't run."; }

                if (key.StartsWith("directx", StringComparison.OrdinalIgnoreCase))
                {
                    string setup = installer;
                    if (!Path.GetFileName(installer).Equals("DXSETUP.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string unpacked = Path.Combine(temp, "dx");
                        int unpack = await RunAsync(installer, $"/Q /T:\"{unpacked}\"").ConfigureAwait(false);
                        setup = Path.Combine(unpacked, "DXSETUP.exe");
                        if (unpack != 0 || !File.Exists(setup)) { return $"Unpacking the DirectX installer failed (exit code {unpack})."; }
                        if (!SignedByMicrosoft(setup)) { return "DXSETUP.exe isn't signed by Microsoft, so it wasn't run."; }
                    }
                    log("Installing DirectX…");
                    int code = await RunAsync(setup, "/silent").ConfigureAwait(false);
                    return code == 0 ? null : $"The DirectX installer finished with code {code}.";
                }

                log($"Installing {Path.GetFileName(installer)}…");
                bool old = key.StartsWith("vc2010", StringComparison.OrdinalIgnoreCase);
                int exit = await RunAsync(installer, old ? "/q /norestart" : "/install /quiet /norestart").ConfigureAwait(false);
                return exit switch
                {
                    0 or 1638 => null,                                              // done, or a newer one is already there
                    3010 => null,                                                   // done; Windows wants a restart some time
                    1618 => "Another installation is running on Windows. Try again when it's finished.",
                    _ => $"The installer finished with code {exit}.",
                };
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { /* temp */ }
            }
        }

        /// <summary>
        /// The redistributable installer the game came with, if any: Steam's _CommonRedist first, then (Visual C++ only)
        /// the one Unreal games carry in Engine\Extras\Redist.
        /// </summary>
        private static string? Shipped(string serverId, string key, bool x86)
        {
            string root = ServerPath.GetServersServerFiles(serverId);
            string arch = x86 ? "x86" : "x64";
            IEnumerable<string> Find(string dir, string pattern)
            {
                try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories) : Enumerable.Empty<string>(); }
                catch { return Enumerable.Empty<string>(); }
            }
            string redist = Path.Combine(root, "_CommonRedist");
            if (key.StartsWith("directx", StringComparison.OrdinalIgnoreCase)) { return Find(redist, "DXSETUP.exe").FirstOrDefault(); }

            string[] years = key.StartsWith("vc2015", StringComparison.OrdinalIgnoreCase) ? new[] { "2022", "2019", "2017", "2015" }
                : new[] { Regex.Match(key, @"\d{4}").Value };
            var installers = Find(redist, "*.exe").Where(f => Regex.IsMatch(Path.GetFileName(f), $@"^vc_?redist[._]{arch}\.exe$", RegexOptions.IgnoreCase)).ToList();
            foreach (string year in years)
            {
                string? hit = installers.FirstOrDefault(f => f.Contains($"{Path.DirectorySeparatorChar}{year}{Path.DirectorySeparatorChar}"));
                if (hit != null) { return hit; }
            }
            return key.StartsWith("vc2015", StringComparison.OrdinalIgnoreCase)
                ? Find(Path.Combine(root, "Engine", "Extras", "Redist"), $"vc_redist.{arch}.exe").FirstOrDefault()
                : null;
        }

        /// <summary>Downloads Microsoft's .NET runtime zip for <paramref name="major"/> into WindowsGSM's dotnet folder (SHA-512 checked).</summary>
        public static async Task DownloadDotnetAsync(int major, string framework)
        {
            string product = framework switch
            {
                "Microsoft.AspNetCore.App" => "aspnetcore-runtime",
                "Microsoft.WindowsDesktop.App" => "windowsdesktop-runtime",
                _ => "dotnet-runtime",
            };
            var meta = JObject.Parse(await FetchJson($"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{major}.0/releases.json").ConfigureAwait(false));
            string? latest = (string?)meta["latest-runtime"];
            var release = meta["releases"]?.FirstOrDefault(r => (string?)r["runtime"]?["version"] == latest) ?? throw new InvalidOperationException($"Microsoft lists no .NET {major} runtime.");
            var files = framework == "Microsoft.AspNetCore.App" ? release["aspnetcore-runtime"]?["files"]
                : framework == "Microsoft.WindowsDesktop.App" ? release["windowsdesktop"]?["files"] : release["runtime"]?["files"];
            var file = files?.FirstOrDefault(f => (string?)f["name"] == $"{product}-win-x64.zip") ?? throw new InvalidOperationException($"Microsoft has no {product} {latest} zip for Windows x64.");
            string url = (string?)file["url"] ?? "", hash = (string?)file["hash"] ?? "";
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || hash.Length != 128) { throw new InvalidOperationException("Microsoft's .NET release list didn't look right; nothing was downloaded."); }

            Directory.CreateDirectory(OwnDotnet);
            string zip = Path.Combine(OwnDotnet, $"{product}-{latest}.zip.part");
            try
            {
                await Download(url, zip).ConfigureAwait(false);
                string actual;
                using (var s = File.OpenRead(zip)) { actual = Convert.ToHexString(SHA512.HashData(s)); }
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The .NET download didn't match Microsoft's checksum, so it wasn't used."); }
                Engine.Services.AddonService.ExtractSafely(zip, OwnDotnet); // dotnet.exe, host\, shared\<framework>\<version>\
            }
            finally { try { File.Delete(zip); } catch { /* temp */ } }
        }

        private static async Task<int> RunAsync(string exe, string arguments)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(15));
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return -1; }
            return p.ExitCode;
        }

        private static bool IsAdministrator()
        {
            try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ─────────────────────────────── Signature ───────────────────────────────

        /// <summary>Whether Windows trusts the file's Authenticode signature and it's Microsoft's.</summary>
        public static bool SignedByMicrosoft(string file)
        {
            if (!Trusted(file)) { return false; }
            try
            {
#pragma warning disable SYSLIB0057 // the signer of a signed file; there's no replacement API for this yet
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
                // Windows trusts the signature (above), and the signer is Microsoft ("CN=Microsoft Corporation", "CN=.NET"…).
                return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool Trusted(string file)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */,
                    pFile = pFile, dwStateAction = 0, dwProvFlags = 0x00000080 /* revocation check: chain, cached */,
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public uint dwUIChoice; public uint fdwRevocationChecks;
            public uint dwUnionChoice; public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
            public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    }

    /// <summary>The DLLs a Windows program loads (import and delay-load tables) and whether it's 32-bit.</summary>
    public static class PeImports
    {
        public sealed record Info(bool X86, IReadOnlyList<string> Imports);

        // Read once per file version: an Unreal server program is hundreds of MB, and only its headers are read.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Info?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static Info? Read(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                string key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                return Cache.GetOrAdd(key, _ => ReadNow(fi.FullName));
            }
            catch { return null; }
        }

        private static Info? ReadNow(string path)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] Bytes(long at, int count)
                {
                    if (at < 0 || at + count > f.Length) { throw new InvalidDataException(); }
                    var b = new byte[count];
                    f.Position = at;
                    f.ReadExactly(b);
                    return b;
                }
                uint U32(long at) => BitConverter.ToUInt32(Bytes(at, 4), 0);
                ushort U16(long at) => BitConverter.ToUInt16(Bytes(at, 2), 0);

                if (f.Length < 0x40 || U16(0) != 0x5A4D) { return null; }            // "MZ"
                long pe = U32(0x3C);
                if (U32(pe) != 0x00004550) { return null; }                          // "PE" and two zero bytes
                ushort machine = U16(pe + 4);
                int sections = U16(pe + 6), optSize = U16(pe + 20);
                long opt = pe + 24;
                bool pe32Plus = U16(opt) == 0x20b;
                long dirs = opt + (pe32Plus ? 112 : 96);
                uint importRva = U32(dirs + 8), delayRva = U32(dirs + 13 * 8);
                var table = new List<(uint Va, uint Size, uint Raw)>();
                byte[] heads = Bytes(opt + optSize, sections * 40);
                for (int i = 0; i < sections; i++)
                {
                    int s = i * 40;
                    table.Add((BitConverter.ToUInt32(heads, s + 12), Math.Max(BitConverter.ToUInt32(heads, s + 8), BitConverter.ToUInt32(heads, s + 16)), BitConverter.ToUInt32(heads, s + 20)));
                }
                long Offset(uint rva) { foreach (var t in table) { if (rva >= t.Va && rva < t.Va + t.Size) { return rva - t.Va + t.Raw; } } return -1; }
                string? Name(uint rva)
                {
                    long o = Offset(rva);
                    if (o < 0 || o >= f.Length) { return null; }
                    var b = Bytes(o, (int)Math.Min(260, f.Length - o));
                    int end = Array.IndexOf(b, (byte)0);
                    return end <= 0 ? null : System.Text.Encoding.ASCII.GetString(b, 0, end);
                }

                var names = new List<string>();
                long at = importRva == 0 ? -1 : Offset(importRva);
                for (int i = 0; at >= 0 && i < 512; i++, at += 20)
                {
                    uint nameRva = U32(at + 12);
                    if (nameRva == 0) { break; }
                    if (Name(nameRva) is string n) { names.Add(n); }
                }
                at = delayRva == 0 ? -1 : Offset(delayRva);
                for (int i = 0; at >= 0 && i < 512; i++, at += 32)
                {
                    uint nameRva = U32(at + 4);
                    if (nameRva == 0) { break; }
                    if (Name(nameRva) is string n) { names.Add(n); }
                }
                return new Info(machine == 0x14c, names);
            }
            catch { return null; }
        }
    }
}
