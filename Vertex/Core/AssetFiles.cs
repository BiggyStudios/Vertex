using System.Text;

namespace Vertex
{
    /// <summary>
    /// Reads the game's files wherever they are: on disk, or in a built game's <see cref="AssetPack"/>, whose files are
    /// found at the paths they'd have had in its Assets folder (<c>Application.AssetPath("Textures", "Brick.png")</c> works
    /// either way). The engine loads every asset through here, so a game's code needn't know whether its assets were
    /// packed, as long as it asks here too: <c>AssetFiles.Exists(path)</c> rather than <c>File.Exists(path)</c>.
    /// </summary>
    public static class AssetFiles
    {
        private static (string Folder, AssetPack Pack)[] _mounted = [];
        private static readonly object _lock = new();

        /// <summary>Whether any pack's files are being read from (a built game whose assets were packed).</summary>
        public static bool Packed => _mounted.Length > 0;

        /// <summary>Makes a pack's files appear in <paramref name="folder"/> (normally the game's Assets folder), as if they were there.</summary>
        public static void Mount(AssetPack pack, string folder)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            lock (_lock) _mounted = [.. _mounted, (full, pack)];
        }

        /// <summary>Stops reading from every pack, and closes them.</summary>
        public static void UnmountAll()
        {
            (string, AssetPack Pack)[] mounted;
            lock (_lock)
            {
                mounted = _mounted;
                _mounted = [];
            }

            foreach ((_, AssetPack pack) in mounted) pack.Dispose();
        }

        /// <summary>Whether the file's there, in a pack or on disk.</summary>
        public static bool Exists(string path) => Find(path) is not null || File.Exists(path);

        /// <summary>A file's bytes, from a pack or the disk.</summary>
        public static byte[] ReadAllBytes(string path) => Find(path) is { } found ? found.Pack.Read(found.Relative) : File.ReadAllBytes(path);

        /// <summary>A text file's text (UTF-8 unless it starts by saying otherwise, as <see cref="File.ReadAllText(string)"/> reads it).</summary>
        public static string ReadAllText(string path)
        {
            if (Find(path) is not { } found) return File.ReadAllText(path);
            using var reader = new StreamReader(new MemoryStream(found.Pack.Read(found.Relative)), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>A file to read from start to end (a pack's file is read into memory first).</summary>
        public static Stream OpenRead(string path) => Find(path) is { } found ? new MemoryStream(found.Pack.Read(found.Relative), writable: false) : File.OpenRead(path);

        /// <summary>Every file in a folder and the folders inside it, from packs and the disk, as full paths.</summary>
        public static IEnumerable<string> Files(string folder)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string mounted, AssetPack pack) in _mounted)
            {
                foreach (string relative in pack.Files)
                {
                    string file = Path.GetFullPath(Path.Combine(mounted, relative));
                    if (file.StartsWith(full, StringComparison.OrdinalIgnoreCase) && seen.Add(file)) yield return file;
                }
            }

            if (!Directory.Exists(full)) yield break;
            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (seen.Add(file)) yield return file;
            }
        }

        /// <summary>The pack a file is in, and its path inside the pack's folder; null if it's in none.</summary>
        private static (AssetPack Pack, string Relative)? Find(string path)
        {
            (string Folder, AssetPack Pack)[] mounted = _mounted;
            if (mounted.Length == 0 || path.Length == 0) return null;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            foreach ((string folder, AssetPack pack) in mounted)
            {
                if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = full[folder.Length..];
                if (pack.Contains(relative)) return (pack, relative);
            }

            return null;
        }
    }
}
