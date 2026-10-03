#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WindowsGSM.Hosting;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: a server's game files can live on another drive. servers\&lt;id&gt;\serverfiles then stays where it always
    /// was but is a junction (a Windows folder link) to the chosen folder — so WindowsGSM, every community plugin,
    /// the games and their updaters keep using the usual path, while the files sit wherever there's room.
    /// Settings, logs, caches and the backup list stay in the WindowsGSM folder.
    /// The chosen folder is also recorded in WindowsGSM.cfg (serverfileslocation), so a disconnected drive can be
    /// recognised and named instead of failing oddly.
    /// </summary>
    public static class ServerLocation
    {
        public const string SettingKey = "serverfileslocation";

        /// <summary>The usual path (a junction when the files are elsewhere).</summary>
        public static string LinkPath(string id) => Path.GetFullPath(ServerPath.GetServersServerFiles(id)).TrimEnd('\\');

        /// <summary>True when the server's files are on another folder/drive (serverfiles is a junction).</summary>
        public static bool IsElsewhere(string id)
        {
            try { return (File.GetAttributes(LinkPath(id)) & FileAttributes.ReparsePoint) != 0; }
            catch { return Recorded(id) != null; } // the link itself may be gone with a missing drive — the record says
        }

        /// <summary>The recorded other folder, or null when the files are in the usual place.</summary>
        public static string? Recorded(string id)
        {
            string value = ServerConfig.GetSetting(id, SettingKey);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>Where the files really are (the junction's target, or the usual folder).</summary>
        public static string RealPath(string id)
        {
            string link = LinkPath(id);
            try
            {
                var info = new DirectoryInfo(link);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null) { return Path.GetFullPath(target.FullName).TrimEnd('\\'); }
                }
            }
            catch { /* unreadable link: fall back to the record */ }
            return Recorded(id) ?? link;
        }

        /// <summary>Null when the files can be reached, else why not (a drive that's disconnected, a missing folder).</summary>
        public static string? Problem(string id)
        {
            if (!IsElsewhere(id)) { return null; }
            string real = RealPath(id);
            if (Directory.Exists(real)) { return null; }
            string drive = Path.GetPathRoot(real) ?? real;
            return Directory.Exists(drive)
                ? $"The server's files folder ({real}) is missing."
                : $"The server's files are on {drive.TrimEnd('\\')}, which isn't connected right now.";
        }

        /// <summary>
        /// Checks the folder someone picked to keep server files in (e.g. E:\GameServers — each server gets its own
        /// folder inside it, see <see cref="FolderFor"/>). Null when it's fine, else what's wrong, in plain words.
        /// </summary>
        public static string? Validate(string folder, string? forServer = null)
        {
            if (string.IsNullOrWhiteSpace(folder)) { return "Choose a folder."; }
            string full;
            try { full = Path.GetFullPath(folder.Trim()).TrimEnd('\\'); } catch { return "That isn't a valid folder path."; }
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) { return "Network shares can't hold a server's files (Windows can't link to them). Pick a folder on a drive in this PC."; }
            string root = Path.GetPathRoot(full) ?? "";
            if (string.Equals(full + "\\", root, StringComparison.OrdinalIgnoreCase) || full.Length <= 3) { return "Pick a folder on the drive, not the whole drive (e.g. E:\\GameServers)."; }
            DriveInfo drive;
            try { drive = new DriveInfo(root); } catch { return "That drive isn't available."; }
            if (!drive.IsReady) { return $"Drive {root.TrimEnd('\\')} isn't ready (is it connected?)."; }
            if (drive.DriveType == DriveType.Network) { return "Mapped network drives can't hold a server's files (Windows can't link to them). Pick a drive in this PC."; }
            if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) { return "Pick a folder on a hard drive or SSD in this PC."; }

            string data = Path.GetFullPath(WgsmEnvironment.DataRoot).TrimEnd('\\');
            string servers = Path.Combine(data, "servers");
            if (Within(full, data)) { return "That's inside WindowsGSM's own folder — servers there already use the usual place. Pick a folder somewhere else."; }

            foreach (string dir in Directory.Exists(servers) ? Directory.EnumerateDirectories(servers) : Enumerable.Empty<string>())
            {
                string id = Path.GetFileName(dir);
                if (id == forServer || !IsElsewhere(id)) { continue; }
                string other = RealPath(id);
                if (Within(full, other)) { return $"That's inside server #{id}'s files — pick another folder."; }
            }
            return CanWrite(full);
        }

        /// <summary>A new folder for server <paramref name="id"/> inside the base folder the person picked ("E:\GameServers").</summary>
        public static string FolderFor(string baseFolder, string id, string? name)
        {
            string safe = new string((name ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
            if (safe.Length > 40) { safe = safe.Substring(0, 40).Trim(); }
            string folder = Path.Combine(Path.GetFullPath(baseFolder.Trim()), safe.Length > 0 ? $"{id} - {safe}" : $"server {id}");
            string candidate = folder;
            for (int n = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); n++) { candidate = folder + $" ({n})"; }
            return candidate;
        }

        /// <summary>
        /// Points the server at <paramref name="target"/>: serverfiles (which must be missing or empty) becomes a
        /// junction to it. Throws on failure.
        /// </summary>
        public static void Link(string id, string target)
        {
            string link = LinkPath(id);
            target = Path.GetFullPath(target).TrimEnd('\\');
            Directory.CreateDirectory(target);
            if (Directory.Exists(link))
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(link, recursive: false); }
                else if (Directory.EnumerateFileSystemEntries(link).Any()) { throw new IOException("The server's files folder isn't empty."); }
                else { Directory.Delete(link); }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            MakeJunction(link, target);
            ServerConfig.SetSetting(id, SettingKey, target);
        }

        /// <summary>Back to the usual place: removes the junction (only — never the files it points at) and the record.</summary>
        public static void Unlink(string id)
        {
            string link = LinkPath(id);
            try
            {
                if (Directory.Exists(link) || File.Exists(link))
                {
                    if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(link, recursive: false); }
                }
            }
            catch (DirectoryNotFoundException) { /* already gone */ }
            ServerConfig.SetSetting(id, SettingKey, "");
        }

        /// <summary>
        /// Deleting a server: its files on the other drive go too (a plain delete would only remove the link and leave
        /// them behind). Only a folder that's this server's own — never a drive root or a folder holding WindowsGSM.
        /// </summary>
        public static void DeleteElsewhereFiles(string id)
        {
            if (!IsElsewhere(id)) { return; }
            string real = RealPath(id);
            string link = LinkPath(id);
            try { if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(link, recursive: false); } } catch { /* gone */ }
            string root = Path.GetPathRoot(real) ?? "";
            string data = Path.GetFullPath(WgsmEnvironment.DataRoot).TrimEnd('\\');
            if (real.Length > root.Length + 1 && !Within(data, real) && Directory.Exists(real)) { Directory.Delete(real, recursive: true); }
        }

        /// <summary>The folder a server's own processes run from (the real one) — for matching processes and firewall rules.</summary>
        public static string ProcessFolder(string id) => RealPath(id) + Path.DirectorySeparatorChar;

        public static bool Within(string path, string folder)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\'), f = Path.GetFullPath(folder).TrimEnd('\\');
            return string.Equals(p, f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string? CanWrite(string folder)
        {
            string probe = folder;
            while (!Directory.Exists(probe)) { probe = Path.GetDirectoryName(probe)!; if (probe == null) { return "That drive isn't available."; } }
            try
            {
                string test = Path.Combine(probe, ".wgsm-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(test, "");
                File.Delete(test);
                return null;
            }
            catch (UnauthorizedAccessException) { return "WindowsGSM isn't allowed to write there. Pick a folder you own (not Program Files or Windows)."; }
            catch (IOException ex) { return ex.Message; }
        }

        /// <summary>A directory junction (no administrator rights needed, unlike symbolic links).</summary>
        private static void MakeJunction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (p.ExitCode != 0 || !Directory.Exists(link)) { throw new IOException("Couldn't link the server's files folder: " + output.Trim()); }
        }
    }
}
