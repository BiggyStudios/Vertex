namespace Vertex.Editor.Editing
{
    public enum AssetKind
    {
        Folder,
        Texture,
        Model,
        Scene,
        Sound,
        Font,
        Material,
        Prefab,
        Script,
        Other
    }

    /// <summary>A file or folder in the project's Assets folder.</summary>
    public sealed record AssetEntry(string Path, AssetKind Kind)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string NameWithoutExtension => Kind == AssetKind.Folder ? Name : System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    /// <summary>
    /// What's in a project's Assets folder, by kind. Folders are read when first asked for and again whenever anything in
    /// them changes on disk, so files copied in show up by themselves.
    /// </summary>
    public sealed class AssetDatabase : IDisposable
    {
        private readonly Dictionary<string, List<AssetEntry>> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssetKind, List<AssetEntry>> _byKind = new();
        private readonly FileSystemWatcher? _watcher;
        private volatile bool _changed;

        public string Root { get; }

        public AssetDatabase(string root)
        {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Root);

            try
            {
                _watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                _watcher.Created += (_, _) => _changed = true;
                _watcher.Deleted += (_, _) => _changed = true;
                _watcher.Renamed += (_, _) => _changed = true;
            }
            catch (Exception)
            {
                // Without watching, Refresh still works.
            }
        }

        /// <summary>Forgets what was read, so it's read again: after files change.</summary>
        public void Refresh()
        {
            _all = null;
            _folders.Clear();
            _byKind.Clear();
            _changed = false;
        }

        /// <summary>What's in <paramref name="folder"/>: folders first, then files, each by name.</summary>
        public IReadOnlyList<AssetEntry> List(string folder)
        {
            if (_changed) Refresh();
            if (_folders.TryGetValue(folder, out List<AssetEntry>? cached)) return cached;

            var entries = new List<AssetEntry>();
            try
            {
                entries.AddRange(Directory.GetDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new AssetEntry(d, AssetKind.Folder)));
                entries.AddRange(Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).StartsWith('.')).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new AssetEntry(f, KindOf(f))));
            }
            catch (Exception)
            {
                // A folder that's gone or can't be read is shown empty.
            }

            _folders[folder] = entries;
            return entries;
        }

        private List<AssetEntry>? _all;

        /// <summary>Every file anywhere in the project: for searching.</summary>
        public IReadOnlyList<AssetEntry> AllFiles()
        {
            if (_changed) Refresh();
            if (_all != null) return _all;

            try
            {
                _all = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Select(f => new AssetEntry(f, KindOf(f))).ToList();
            }
            catch (Exception)
            {
                _all = new List<AssetEntry>();
            }

            return _all;
        }

        /// <summary>Every asset of one kind anywhere in the project: for pickers.</summary>
        public IReadOnlyList<AssetEntry> All(AssetKind kind)
        {
            if (_changed) Refresh();
            if (_byKind.TryGetValue(kind, out List<AssetEntry>? cached)) return cached;

            var found = new List<AssetEntry>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    if (KindOf(file) == kind) found.Add(new AssetEntry(file, kind));
                }
            }
            catch (Exception)
            {
                // Whatever was found before the problem.
            }

            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _byKind[kind] = found;
            return found;
        }

        /// <summary>A path inside the project as it reads from the Assets folder ("Textures/Brick.png").</summary>
        public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

        public static AssetKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".gif" or ".hdr" => AssetKind.Texture,
            ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" => AssetKind.Model,
            ".scene" => AssetKind.Scene,
            ".wav" or ".ogg" or ".mp3" => AssetKind.Sound,
            ".ttf" or ".otf" => AssetKind.Font,
            ".material" => AssetKind.Material,
            ".prefab" => AssetKind.Prefab,
            ".cs" => AssetKind.Script,
            _ => AssetKind.Other
        };

        public void Dispose() => _watcher?.Dispose();
    }
}
