#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    public sealed record FileEntry(string Name, bool IsDirectory, long? Size, DateTimeOffset Modified);

    /// <summary>A text file opened for the editor. Large files come back as a read-only tail.</summary>
    public sealed record TextFile(string Path, string Name, long Size, DateTimeOffset Modified, string? Content, bool Binary, bool ReadOnly, string? Note);

    /// <summary>Thrown for anything the caller did wrong (bad path, name taken…). The message is safe to show.</summary>
    public sealed class FileOperationException : Exception
    {
        public FileOperationException(string message, FileProblem problem) : base(message) => Problem = problem;
        public FileProblem Problem { get; }
    }

    public enum FileProblem { Invalid, NotFound, Conflict, TooLarge }

    /// <summary>
    /// Browse and edit a server's files, jailed to its serverfiles folder. Paths are always relative with
    /// forward slashes. Port of the legacy dashboard's file endpoints, with one addition: links (junctions,
    /// symlinks) are only followed when they point back inside the server folder, so a link can't be used to
    /// read or overwrite files elsewhere on the machine.
    /// </summary>
    public sealed class ServerFiles
    {
        /// <summary>Largest file the editor loads (or saves) in full.</summary>
        public const long MaxEditableBytes = 2 * 1024 * 1024;

        /// <summary>How much of a larger text file (logs) is shown, read-only.</summary>
        public const long TailViewBytes = 256 * 1024;

        public string Root(string id) => Path.GetFullPath(ServerPath.GetServersServerFiles(id));

        public IReadOnlyList<FileEntry> List(string id, string? path, out string normalized)
        {
            string full = Resolve(id, path);
            if (!Directory.Exists(full)) { throw new FileOperationException("Folder not found. The server may not be installed yet.", FileProblem.NotFound); }
            normalized = Relative(id, full);

            var di = new DirectoryInfo(full);
            var dirs = di.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FileEntry(d.Name, true, null, d.LastWriteTimeUtc));
            var files = di.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileEntry(f.Name, false, f.Length, f.LastWriteTimeUtc));
            return dirs.Concat(files).ToList();
        }

        public TextFile Read(string id, string path)
        {
            string full = Resolve(id, path);
            if (!File.Exists(full)) { throw new FileOperationException("File not found.", FileProblem.NotFound); }
            var info = new FileInfo(full);
            string rel = Relative(id, full);

            byte[] head = ReadChunk(full, 0, (int)Math.Min(info.Length, 8000));
            if (head.Take(8000).Any(b => b == 0))
            {
                return new TextFile(rel, info.Name, info.Length, info.LastWriteTimeUtc, null, true, true, "Binary file — download it to view.");
            }
            if (info.Length <= MaxEditableBytes)
            {
                return new TextFile(rel, info.Name, info.Length, info.LastWriteTimeUtc, Encoding.UTF8.GetString(ReadAll(full)), false, false, null);
            }

            string text = Encoding.UTF8.GetString(ReadChunk(full, info.Length - TailViewBytes, (int)TailViewBytes));
            int nl = text.IndexOf('\n'); // drop the partial first line
            if (nl >= 0 && nl < text.Length - 1) { text = text.Substring(nl + 1); }
            return new TextFile(rel, info.Name, info.Length, info.LastWriteTimeUtc, text, false, true,
                $"Showing the last {TailViewBytes / 1024} KB of a {info.Length / 1024:N0} KB file — read-only. Download it for the whole file.");
        }

        /// <summary>
        /// Saves text. With <paramref name="expectedModified"/>, refuses (Conflict) if the file changed on disk
        /// since it was opened — the game or another person may have written it.
        /// </summary>
        public async Task WriteAsync(string id, string path, string content, DateTimeOffset? expectedModified = null, CancellationToken cancellation = default)
        {
            string full = Resolve(id, path);
            if (!File.Exists(full)) { throw new FileOperationException("File not found.", FileProblem.NotFound); }
            if (Encoding.UTF8.GetByteCount(content ?? string.Empty) > MaxEditableBytes) { throw new FileOperationException("Content is too large.", FileProblem.TooLarge); }
            if (expectedModified != null && Math.Abs((File.GetLastWriteTimeUtc(full) - expectedModified.Value.UtcDateTime).TotalSeconds) > 1)
            {
                throw new FileOperationException("The file changed on disk since you opened it. Reload it, then make your change again.", FileProblem.Conflict);
            }

            string temp = full + ".wgsm-save";
            await File.WriteAllTextAsync(temp, content ?? string.Empty, new UTF8Encoding(false), cancellation).ConfigureAwait(false);
            File.Move(temp, full, overwrite: true);
        }

        /// <summary>A file to stream to the browser (full path, checked).</summary>
        public string ForDownload(string id, string path)
        {
            string full = Resolve(id, path);
            if (!File.Exists(full)) { throw new FileOperationException("File not found.", FileProblem.NotFound); }
            return full;
        }

        public string CreateFolder(string id, string? parent, string name)
        {
            RequireName(name);
            string full = Resolve(id, Combine(parent, name));
            if (Directory.Exists(full) || File.Exists(full)) { throw new FileOperationException("A file or folder with that name already exists.", FileProblem.Conflict); }
            Directory.CreateDirectory(full);
            return Relative(id, full);
        }

        public string Rename(string id, string path, string newName)
        {
            RequireName(newName);
            string full = ResolveBelowRoot(id, path);
            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full)) { throw new FileOperationException("Not found.", FileProblem.NotFound); }
            string target = Resolve(id, Combine(Relative(id, Path.GetDirectoryName(full)!), newName));
            if (Directory.Exists(target) || File.Exists(target)) { throw new FileOperationException("A file or folder with that name already exists.", FileProblem.Conflict); }
            if (isDir) { Directory.Move(full, target); } else { File.Move(full, target); }
            return Relative(id, target);
        }

        public void Delete(string id, string path)
        {
            string full = ResolveBelowRoot(id, path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(full); } // works for a link whose target is gone, unlike Exists
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new FileOperationException("Not found.", FileProblem.NotFound); }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // A link: remove the link itself, never what it points at.
                if (attributes.HasFlag(FileAttributes.Directory)) { Directory.Delete(full, recursive: false); } else { File.Delete(full); }
            }
            else if (Directory.Exists(full)) { Directory.Delete(full, recursive: true); }
            else if (File.Exists(full)) { File.Delete(full); }
            else { throw new FileOperationException("Not found.", FileProblem.NotFound); }
        }

        /// <summary>Saves an uploaded file into <paramref name="folder"/> (replacing one of the same name).</summary>
        public async Task<string> SaveUploadAsync(string id, string? folder, string fileName, Stream content, CancellationToken cancellation = default)
        {
            string name = Path.GetFileName(fileName ?? string.Empty);
            RequireName(name);
            string dir = Resolve(id, folder);
            if (!Directory.Exists(dir)) { throw new FileOperationException("Target folder not found.", FileProblem.NotFound); }
            string dest = Resolve(id, Combine(Relative(id, dir), name));

            string temp = dest + ".wgsm-upload";
            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await content.CopyToAsync(output, cancellation).ConfigureAwait(false);
                }
                File.Move(temp, dest, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) { File.Delete(temp); } } catch { /* best effort */ }
            }
            return Relative(id, dest);
        }

        // ── Jail ──

        /// <summary>Full path for a relative path, guaranteed to be inside the server's folder (links included).</summary>
        public string Resolve(string id, string? relative) => Resolve(id, relative, followLast: true);

        /// <param name="followLast">False when acting on the item itself (delete or rename a link) rather than
        /// what it points at — then only the folders leading to it must stay inside.</param>
        private string Resolve(string id, string? relative, bool followLast)
        {
            string root = Root(id);
            string rel = (relative ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, rel)); }
            catch { throw new FileOperationException("Invalid path.", FileProblem.Invalid); }
            if (!IsWithin(root, full)) { throw new FileOperationException("Invalid path.", FileProblem.Invalid); }

            // Walk each existing component below the root: a link must resolve to somewhere inside the root too.
            string[] parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            int index = 0;
            string current = root;
            foreach (string part in parts)
            {
                if (part == ".") { break; }
                if (!followLast && ++index == parts.Length) { break; }
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists) { break; }
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                FileSystemInfo? target;
                try { target = info.ResolveLinkTarget(returnFinalTarget: true); } catch { target = null; }
                if (target != null && !IsWithin(root, Path.GetFullPath(target.FullName)))
                {
                    throw new FileOperationException("That's a link to somewhere outside the server folder, which the file manager doesn't follow.", FileProblem.Invalid);
                }
            }
            return full;
        }

        private string ResolveBelowRoot(string id, string? relative)
        {
            string full = Resolve(id, relative, followLast: false);
            if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Root(id).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new FileOperationException("Invalid path.", FileProblem.Invalid);
            }
            return full;
        }

        public string Relative(string id, string full)
        {
            string rel = Path.GetRelativePath(Root(id), full);
            return rel == "." ? string.Empty : rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        private static bool IsWithin(string root, string full)
        {
            string r = root.TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static string Combine(string? relDir, string name) =>
            string.IsNullOrEmpty(relDir) ? name : relDir.TrimEnd('/') + "/" + name;

        private static void RequireName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".."
                || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new FileOperationException("Invalid name.", FileProblem.Invalid);
            }
        }

        private static byte[] ReadAll(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[] ReadChunk(string path, long offset, int count)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > 0) { fs.Seek(offset, SeekOrigin.Begin); }
            byte[] buf = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = fs.Read(buf, total, count - total);
                if (n <= 0) { break; }
                total += n;
            }
            return total == count ? buf : buf.Take(total).ToArray();
        }
    }
}
