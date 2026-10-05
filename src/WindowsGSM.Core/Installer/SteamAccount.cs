#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// The Steam account used for games (and Workshop items) that don't allow anonymous downloads. Kept in
    /// configs\next\steam.json with the password encrypted (<see cref="Secret"/>) — the legacy app kept it as plain
    /// text in bin\steamcmd\userData.txt, which is still read when nothing is saved here (and imported on start).
    /// </summary>
    public static class SteamAccount
    {
        private sealed class Stored
        {
            public string Username { get; set; } = string.Empty;
            [JsonConverter(typeof(SecretJsonConverter))] public string Password { get; set; } = string.Empty;
            public DateTimeOffset? SignedInAt { get; set; }
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private static readonly object Gate = new object();

        private static string FilePath => Path.Combine(WgsmEnvironment.DataRoot, "configs", "next", "steam.json");
        public static string LegacyFile => Path.Combine(ServerPath.GetBin("steamcmd"), "userData.txt");

        /// <summary>Username and password to use (saved here first, then the legacy file), or nulls.</summary>
        public static (string? Username, string? Password) Get()
        {
            var s = Load();
            if (s != null && s.Username.Length > 0) { return (s.Username, s.Password); }
            return ReadLegacy();
        }

        /// <summary>The account's state for the panel: who, whether a password is saved, when it last signed in.</summary>
        public static (string? Username, bool HasPassword, DateTimeOffset? SignedInAt, bool FromLegacyFile) Status()
        {
            var s = Load();
            if (s != null && s.Username.Length > 0) { return (s.Username, s.Password.Length > 0, s.SignedInAt, false); }
            var (u, p) = ReadLegacy();
            return (u, !string.IsNullOrEmpty(p), null, !string.IsNullOrEmpty(u));
        }

        public static void Save(string username, string password, bool signedIn)
        {
            lock (Gate)
            {
                var s = new Stored { Username = username.Trim(), Password = password, SignedInAt = signedIn ? DateTimeOffset.UtcNow : null };
                global::WindowsGSM.Hosting.SafeJson.Write(FilePath, s, Json);
            }
        }

        public static void Clear()
        {
            lock (Gate) { try { File.Delete(FilePath); } catch { /* gone */ } }
        }

        /// <summary>On start: an account only in the legacy plain-text file is copied here (encrypted). The file stays (the old app reads it).</summary>
        public static void ImportLegacy()
        {
            var s = Load();
            if (s != null && s.Username.Length > 0) { return; }
            var (u, p) = ReadLegacy();
            if (!string.IsNullOrWhiteSpace(u) && !string.IsNullOrEmpty(p)) { Save(u!, p!, signedIn: false); }
        }

        /// <summary>The legacy file still holds a plain-text password.</summary>
        public static bool LegacyHasPassword() => !string.IsNullOrEmpty(ReadLegacy().Password);

        /// <summary>Blanks the password in the legacy file (the old app then can't sign in to Steam by itself).</summary>
        public static void RemoveLegacyPassword()
        {
            if (!File.Exists(LegacyFile)) { return; }
            var lines = File.ReadAllLines(LegacyFile).Select(l => l.TrimStart().StartsWith("steamPass", StringComparison.Ordinal) ? "steamPass=\"\"" : l).ToArray();
            File.WriteAllLines(LegacyFile, lines);
        }

        private static Stored? Load()
        {
            lock (Gate)
            {
                return global::WindowsGSM.Hosting.SafeJson.Read<Stored>(FilePath, Json);
            }
        }

        private static (string? Username, string? Password) ReadLegacy()
        {
            if (!File.Exists(LegacyFile)) { return (null, null); }
            string? user = null, pass = null;
            foreach (string raw in File.ReadAllLines(LegacyFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) { continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim().Trim('"');
                if (key == "steamUser") { user = value; }
                else if (key == "steamPass") { pass = value; }
            }
            return (string.IsNullOrWhiteSpace(user) ? null : user, string.IsNullOrEmpty(pass) ? null : pass);
        }
    }
}
