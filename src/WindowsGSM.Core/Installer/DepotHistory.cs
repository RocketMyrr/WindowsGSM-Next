#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using WindowsGSM.Functions;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// Which builds of a server's game DepotDownloader has installed, for "roll back to an earlier build".
    ///
    /// DepotDownloader keeps, in serverfiles\.DepotDownloader, depot.config (what's installed now: depot → manifest)
    /// and a copy of every manifest it ever downloaded. Downloads that happened together form one build; since
    /// an update only fetches the depots that changed, each build carries the others forward. WindowsGSM also
    /// records each build it installs (servers\&lt;id&gt;\configs\builds.json) to name them with Steam's build number.
    /// </summary>
    public static class DepotHistory
    {
        public sealed record Build(string Key, DateTimeOffset InstalledAt, IReadOnlyDictionary<uint, ulong> Depots, string? BuildId, bool Current);

        private sealed record Recorded(DateTimeOffset At, string? BuildId, Dictionary<uint, ulong> Depots);

        /// <summary>Manifests downloaded within this long of each other belong to one update.</summary>
        private static readonly TimeSpan SameUpdate = TimeSpan.FromMinutes(15);

        public static string ToolFolder(string serverId) => Path.Combine(ServerPath.GetServersServerFiles(serverId), ".DepotDownloader");
        private static string RecordFile(string serverId) => ServerPath.GetServersConfigs(serverId, "builds.json");

        /// <summary>What's installed now (depot → manifest), from depot.config; empty if DepotDownloader never installed this server.</summary>
        public static Dictionary<uint, ulong> Installed(string serverId)
        {
            var result = new Dictionary<uint, ulong>();
            string file = Path.Combine(ToolFolder(serverId), "depot.config");
            if (!File.Exists(file)) { return result; }
            try
            {
                byte[] raw = File.ReadAllBytes(file);
                byte[] data;
                try
                {
                    using var input = new MemoryStream(raw);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    deflate.CopyTo(output);
                    data = output.ToArray();
                }
                catch (InvalidDataException) { data = raw; } // older DepotDownloader wrote it uncompressed
                ParseConfig(data, result);
            }
            catch { /* unreadable: no history */ }
            return result;
        }

        /// <summary>DepotConfigStore (protobuf): repeated field 1 = map entry { 1: depot id, 2: manifest id }.</summary>
        internal static void ParseConfig(byte[] data, Dictionary<uint, ulong> into)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                ulong tag = Varint(data, ref pos);
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (wire == 2)
                {
                    int len = (int)Varint(data, ref pos);
                    int end = pos + len;
                    if (end > data.Length) { return; }
                    if (field == 1)
                    {
                        ulong depot = 0, manifest = 0;
                        while (pos < end)
                        {
                            ulong t = Varint(data, ref pos);
                            if ((t & 7) != 0) { pos = end; break; }
                            ulong v = Varint(data, ref pos);
                            if (t >> 3 == 1) { depot = v; } else if (t >> 3 == 2) { manifest = v; }
                        }
                        if (depot != 0 && manifest != 0) { into[(uint)depot] = manifest; }
                    }
                    pos = end;
                }
                else if (wire == 0) { Varint(data, ref pos); }
                else if (wire == 1) { pos += 8; }
                else if (wire == 5) { pos += 4; }
                else { return; }
            }
        }

        private static ulong Varint(byte[] data, ref int pos)
        {
            ulong value = 0;
            for (int shift = 0; pos < data.Length && shift < 64; shift += 7)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) { break; }
            }
            return value;
        }

        /// <summary>Every build this server has had, newest first; the installed one is marked <see cref="Build.Current"/>.</summary>
        public static List<Build> Builds(string serverId)
        {
            var installed = Installed(serverId);
            var builds = new List<Build>();
            string folder = ToolFolder(serverId);
            if (!Directory.Exists(folder)) { return builds; }

            // Manifest files: <depot>_<manifest>.manifest, written when downloaded.
            var files = Directory.EnumerateFiles(folder, "*.manifest")
                .Select(f => (Name: Path.GetFileNameWithoutExtension(f), At: new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
                .Select(f => (Parts: f.Name.Split('_'), f.At))
                .Where(f => f.Parts.Length == 2 && uint.TryParse(f.Parts[0], out _) && ulong.TryParse(f.Parts[1], out _))
                .Select(f => (Depot: uint.Parse(f.Parts[0]), Manifest: ulong.Parse(f.Parts[1]), f.At))
                .OrderBy(f => f.At).ToList();

            var state = new Dictionary<uint, ulong>();
            DateTimeOffset groupLast = DateTimeOffset.MinValue;
            var snapshots = new List<(DateTimeOffset At, Dictionary<uint, ulong> Depots)>();
            foreach (var f in files)
            {
                if (snapshots.Count > 0 && f.At - groupLast <= SameUpdate)
                {
                    snapshots[^1].Depots[f.Depot] = f.Manifest;
                }
                else
                {
                    var next = new Dictionary<uint, ulong>(snapshots.Count > 0 ? snapshots[^1].Depots : state) { [f.Depot] = f.Manifest };
                    snapshots.Add((f.At, next));
                }
                groupLast = f.At;
            }

            // Depots only ever downloaded once (e.g. shared redistributables) stay in every later build; but a build
            // only counts the depots that are installed now (or were then) — drop ones the game no longer uses.
            var recorded = ReadRecords(serverId);
            var seen = new HashSet<string>();
            for (int i = snapshots.Count - 1; i >= 0; i--)
            {
                var depots = installed.Count > 0
                    ? snapshots[i].Depots.Where(d => installed.ContainsKey(d.Key)).ToDictionary(d => d.Key, d => d.Value)
                    : snapshots[i].Depots;
                if (depots.Count == 0) { continue; }
                string key = Key(depots);
                if (!seen.Add(key)) { continue; }
                string? buildId = recorded.FirstOrDefault(r => Key(r.Depots) == key)?.BuildId;
                builds.Add(new Build(key, snapshots[i].At, depots, buildId, installed.Count > 0 && Key(installed) == key));
            }
            // If depot.config names a build whose files we can't date, still show it as current.
            if (installed.Count > 0 && !builds.Any(b => b.Current))
            {
                string key = Key(installed);
                builds.Insert(0, new Build(key, DateTimeOffset.UtcNow, installed, recorded.FirstOrDefault(r => Key(r.Depots) == key)?.BuildId ?? BuildCache.Read(serverId), true));
            }
            return builds;
        }

        /// <summary>Stable id for a set of depot manifests (what the panel sends back to roll back to it).</summary>
        public static string Key(IReadOnlyDictionary<uint, ulong> depots) =>
            string.Join("-", depots.OrderBy(d => d.Key).Select(d => $"{d.Key}_{d.Value}"));

        /// <summary>Remembers the build that's installed now with Steam's build number (after an update or install).</summary>
        public static void Record(string serverId, string? buildId)
        {
            var installed = Installed(serverId);
            if (installed.Count == 0) { return; }
            var records = ReadRecords(serverId);
            string key = Key(installed);
            // Keep the build number we already know when this call doesn't (e.g. nothing in the build cache yet).
            string? known = records.LastOrDefault(r => Key(r.Depots) == key)?.BuildId;
            records.RemoveAll(r => Key(r.Depots) == key);
            records.Add(new Recorded(DateTimeOffset.UtcNow, string.IsNullOrWhiteSpace(buildId) ? known : buildId.Trim(), installed));
            if (records.Count > 50) { records.RemoveRange(0, records.Count - 50); }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecordFile(serverId))!);
                File.WriteAllText(RecordFile(serverId), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* history is a nicety */ }
        }

        private static List<Recorded> ReadRecords(string serverId)
        {
            try
            {
                string file = RecordFile(serverId);
                return File.Exists(file) ? JsonSerializer.Deserialize<List<Recorded>>(File.ReadAllText(file)) ?? new() : new();
            }
            catch { return new(); }
        }
    }
}
