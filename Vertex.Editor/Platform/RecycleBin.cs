using System.Globalization;
using System.Runtime.InteropServices;

namespace Vertex.Editor.Platform
{
    /// <summary>
    /// The system's bin for deleted files: the Recycle Bin on Windows, the Trash on macOS and Linux. Files deleted in the
    /// editor go here rather than being gone for good, so a slip can be put back from there until the bin's emptied.
    /// </summary>
    public static class RecycleBin
    {
        /// <summary>What the system calls it, for messages: "Recycle Bin" or "Trash".</summary>
        public static string Name => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

        /// <summary>A folder that stands in for the bin, for tests: what's deleted is moved there instead.</summary>
        internal static string? StandIn { get; set; }

        /// <summary>Moves a file or a folder (and everything in it) to the bin. Throws if it can't, leaving it where it was.</summary>
        public static void Move(string path)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("It isn't there any more.", path);

            if (StandIn != null)
            {
                Directory.CreateDirectory(StandIn);
                MoveInto(path, StandIn);
            }
            else if (OperatingSystem.IsWindows())
            {
                MoveOnWindows(path);
            }
            else if (OperatingSystem.IsMacOS())
            {
                MoveInto(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash"));
            }
            else
            {
                MoveOnLinux(path);
            }
        }

        /// <summary>Into <paramref name="folder"/>, numbered if something there has its name already. Where it went.</summary>
        private static string MoveInto(string path, string folder)
        {
            Directory.CreateDirectory(folder);
            string name = Path.GetFileName(path), target = Path.Combine(folder, name);
            for (int n = 2; File.Exists(target) || Directory.Exists(target); n++)
            {
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}");
            }

            Relocate(path, target);
            return target;
        }

        /// <summary>Moves it, copying across if the bin's on another drive.</summary>
        private static void Relocate(string from, string to)
        {
            try
            {
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                return;
            }
            catch (IOException) when (Path.GetPathRoot(from) != Path.GetPathRoot(to) || !OperatingSystem.IsWindows())
            {
                // Another drive or mount: copied below, then the original taken away.
            }

            if (File.Exists(from))
            {
                File.Copy(from, to);
                File.Delete(from);
                return;
            }

            foreach (string folder in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, folder)));
            }

            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
            }

            Directory.Delete(from, recursive: true);
        }

        // The freedesktop.org Trash: the thing in files/, and a note in info/ of where it was and when, so file managers
        // can put it back.
        private static void MoveOnLinux(string path)
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            MoveToTrash(path, Path.Combine(data, "Trash"));
        }

        /// <summary>Into a freedesktop.org Trash folder (<paramref name="bin"/>), with its note. Where it went.</summary>
        internal static string MoveToTrash(string path, string bin)
        {
            string files = Path.Combine(bin, "files"), info = Path.Combine(bin, "info");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);

            string name = Path.GetFileName(path), stored = name;
            for (int n = 2; File.Exists(Path.Combine(files, stored)) || Directory.Exists(Path.Combine(files, stored)) || File.Exists(Path.Combine(info, stored + ".trashinfo")); n++)
            {
                stored = $"{Path.GetFileNameWithoutExtension(name)}.{n}{Path.GetExtension(name)}";
            }

            string note = Path.Combine(info, stored + ".trashinfo");
            string where = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            File.WriteAllText(note, $"[Trash Info]\nPath={where}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
            try
            {
                Relocate(path, Path.Combine(files, stored));
                return Path.Combine(files, stored);
            }
            catch
            {
                File.Delete(note);
                throw;
            }
        }

        private static void MoveOnWindows(string path)
        {
            var operation = new FileOperation
            {
                Function = 3, // delete
                From = path + "\0\0",
                Flags = 0x0040 | 0x0010 | 0x0400 | 0x0004 // to the bin, don't ask, no error windows, no progress window
            };

            int result = SHFileOperation(ref operation);
            if (result != 0 || operation.AnyAborted) throw new IOException($"Windows couldn't move it to the Recycle Bin (error {result}).");
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Windows didn't move it to the Recycle Bin.");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileOperation
        {
            public IntPtr Window;
            public uint Function;
            public string From;
            public string? To;
            public ushort Flags;
            [MarshalAs(UnmanagedType.Bool)] public bool AnyAborted;
            public IntPtr NameMappings;
            public string? ProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation(ref FileOperation operation);
    }
}
