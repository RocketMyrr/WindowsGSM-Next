#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// NEXT: which version of a built-in add-on a server has, and the newest on GitHub — so the Add-ons tab can say
    /// "Installed 2.0.7807 · Latest 2.0.7830". Installed is read from the add-on's own file (right even when it was put
    /// there by hand); latest is asked of GitHub at most once an hour per add-on (GitHub limits unsigned requests).
    /// </summary>
    public static class AddonVersions
    {
        /// <summary>Where each add-on's version lives on disk, and where its releases are.</summary>
        private static readonly Dictionary<string, (string[] Dll, string Api, Func<JObject, string?> Pick)> Known = new()
        {
            ["oxide"] = (new[] { "RustDedicated_Data", "Managed", "Oxide.Rust.dll" },
                "https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest", r => (string?)r["tag_name"]),
            // Carbon publishes one rolling "production_build" release; its version is in the release's name ("… — v2.0.262").
            ["carbon"] = (new[] { "carbon", "managed", "Carbon.dll" },
                "https://api.github.com/repos/CarbonCommunity/Carbon/releases/tags/production_build", r => (string?)r["name"]),
        };

        private static readonly ConcurrentDictionary<string, (string? Version, DateTime At)> Latest = new();
        private static readonly TimeSpan Fresh = TimeSpan.FromHours(1), Retry = TimeSpan.FromMinutes(10);

        /// <summary>Swappable for tests: fetches a GitHub API address.</summary>
        public static Func<string, Task<string>> Fetch { get; set; } = url => Http.DownloadStringAsync(url);

        public static bool Tracks(string key) => Known.ContainsKey(key);

        /// <summary>The installed version ("2.0.7807"), whether it's a staging/dev build, or nulls when it can't be told.</summary>
        public static (string? Version, bool Staging) Installed(string key, string serverId)
        {
            if (!Known.TryGetValue(key, out var k)) { return (null, false); }
            string dll = ServerPath.GetServersServerFiles(serverId, Path.Combine(k.Dll));
            if (!File.Exists(dll)) { return (null, false); }
            try
            {
                var info = FileVersionInfo.GetVersionInfo(dll);
                string product = info.ProductVersion ?? info.FileVersion ?? "";
                return (Number(product), Regex.IsMatch(product, @"staging|develop|preview|beta|alpha", RegexOptions.IgnoreCase));
            }
            catch (Exception) { return (null, false); }
        }

        /// <summary>The newest release's version from the cache (null until asked, or when GitHub couldn't be reached).</summary>
        public static string? LatestKnown(string key) => Latest.TryGetValue(key, out var v) ? v.Version : null;

        /// <summary>Asks GitHub for any of <paramref name="keys"/> not asked recently; waits at most <paramref name="wait"/>.</summary>
        public static async Task RefreshAsync(IEnumerable<string> keys, TimeSpan wait)
        {
            var due = keys.Where(Tracks).Where(key => !Latest.TryGetValue(key, out var v) || DateTime.UtcNow - v.At > (v.Version == null ? Retry : Fresh)).ToList();
            if (due.Count == 0) { return; }
            var all = Task.WhenAll(due.Select(async key =>
            {
                string? version = null;
                try { version = Number(Known[key].Pick(JObject.Parse(await Fetch(Known[key].Api).ConfigureAwait(false))) ?? ""); }
                catch (Exception) { /* offline or rate-limited: try again later */ }
                Latest[key] = (version, DateTime.UtcNow);
            }));
            await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false);
        }

        /// <summary>Forgets what GitHub said (after an install, so the tab shows the new state straight away).</summary>
        public static void Forget(string key) => Latest.TryRemove(key, out _);

        /// <summary>"v2.0.262", "2.0.7807-staging+feaed4f" → "2.0.262", "2.0.7807".</summary>
        public static string? Number(string text)
        {
            var m = Regex.Match(text ?? "", @"(\d+(?:\.\d+){1,3})");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Whether <paramref name="latest"/> is newer than <paramref name="installed"/> (by number, part by part).</summary>
        public static bool Newer(string? latest, string? installed)
        {
            if (latest == null || installed == null) { return false; }
            int[] a = latest.Split('.').Select(int.Parse).ToArray(), b = installed.Split('.').Select(int.Parse).ToArray();
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) { return x > y; }
            }
            return false;
        }
    }
}
