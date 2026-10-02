#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsGSM.Hosting
{
    /// <summary>Thrown when another WindowsGSM already manages the data folder.</summary>
    public sealed class DataRootInUseException : Exception
    {
        public DataRootInUseException(string message) : base(message) { }
    }

    /// <summary>
    /// Ensures only one WindowsGSM manages a data folder at a time. Two managers would fight over the same
    /// servers — both auto-restarting a crash, both running scheduled restarts, both rewriting configs.
    ///
    /// Next-vs-Next is prevented by an exclusively held wgsm.lock file. The legacy app never takes that lock,
    /// but it always uses its own install folder as its data folder, so a running WindowsGSM.exe from this
    /// folder is detected directly.
    /// </summary>
    public sealed class DataRootLock : IDisposable
    {
        public const string LockFileName = "wgsm.lock";
        private FileStream? _stream;

        private DataRootLock(FileStream stream) => _stream = stream;

        public static DataRootLock Acquire(string dataRoot)
        {
            string root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);

            string? legacy = FindLegacyAppUsing(root);
            if (legacy != null)
            {
                throw new DataRootInUseException(
                    $"The current WindowsGSM app ({legacy}) is running from this data folder. Close it first, " +
                    "or point WindowsGSM Next at a copy of the folder while you try it out.");
            }

            string path = Path.Combine(root, LockFileName);
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
                stream.SetLength(0);
                byte[] info = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId}\nstarted={DateTimeOffset.Now:o}\n");
                stream.Write(info, 0, info.Length);
                stream.Flush();
                return new DataRootLock(stream);
            }
            catch (IOException)
            {
                string owner = TryReadOwner(path);
                throw new DataRootInUseException($"Another WindowsGSM is already managing this data folder{owner}.");
            }
        }

        private static string? FindLegacyAppUsing(string root)
        {
            foreach (var p in Process.GetProcessesByName("WindowsGSM"))
            {
                try
                {
                    if (p.Id == Environment.ProcessId) { continue; }
                    string? exe = p.MainModule?.FileName;
                    string? dir = exe == null ? null : Path.GetDirectoryName(exe)?.TrimEnd(Path.DirectorySeparatorChar);
                    if (dir != null && string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) { return $"PID {p.Id}"; }
                }
                catch { /* can't inspect (e.g. elevated) — the lock file still covers Next instances */ }
                finally { p.Dispose(); }
            }
            return null;
        }

        private static string TryReadOwner(string path)
        {
            try
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                string text = reader.ReadToEnd();
                int i = text.IndexOf("pid=", StringComparison.Ordinal);
                if (i >= 0) { return $" (PID {text.Substring(i + 4).Split('\n')[0].Trim()})"; }
            }
            catch { /* unreadable */ }
            return string.Empty;
        }

        public void Dispose()
        {
            _stream?.Dispose(); // DeleteOnClose removes the file
            _stream = null;
        }
    }
}
