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

            string? legacy = LegacyAppUsing(root);
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

        /// <summary>
        /// The legacy app running from <paramref name="root"/> ("PID 1234"), or null. NEXT: it runs as administrator, and
        /// Process.MainModule can't read an elevated program's path from a normal one — the check silently passed. The
        /// limited query used here works across that.
        /// </summary>
        public static string? LegacyAppUsing(string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var p in Process.GetProcessesByName("WindowsGSM"))
            {
                try
                {
                    if (p.Id == Environment.ProcessId) { continue; }
                    string? exe = ImagePath(p.Id);
                    string? dir = exe == null ? null : Path.GetDirectoryName(exe)?.TrimEnd(Path.DirectorySeparatorChar);
                    if (dir != null && string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) { return $"PID {p.Id}"; }
                }
                catch { /* gone meanwhile */ }
                finally { p.Dispose(); }
            }
            return null;
        }

        /// <summary>A process's program file, readable even for one running as administrator (or null).</summary>
        public static string? ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) { return null; }
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>
        /// Whether this process may end <paramref name="pid"/> and wait for it — false for one running as administrator
        /// while WindowsGSM isn't (Windows allows only a look at it then).
        /// </summary>
        public static bool CanControl(int pid)
        {
            IntPtr h = OpenProcess(0x0001 /* TERMINATE */ | 0x00100000 /* SYNCHRONIZE */ | 0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) { return false; }
            CloseHandle(h);
            return true;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

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
