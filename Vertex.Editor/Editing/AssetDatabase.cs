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

        /// <summary>A .vscript file: a visual script, made of nodes.</summary>
        VisualScript,

        /// <summary>A .vfx file: a particle effect, made of nodes.</summary>
        VisualEffect,

        /// <summary>A .audiograph file: a sound made of sounds (a random one of several, at a random pitch), made of nodes.</summary>
        AudioGraph,

        /// <summary>A .glsl file: a shader written in GLSL, such as a post effect.</summary>
        Shader,

        /// <summary>A .shadergraph file: a shader made of nodes, for a material's surface or a post effect.</summary>
        ShaderGraph,

        /// <summary>
        /// An animation: a .anim file (made in the Animation panel), or one of a model's own, which has no file of its own
        /// and is kept as the model's path, a #, and its name ("Robot.glb#Walk").
        /// </summary>
        Animation,

        /// <summary>A .animgraph file: animations blended by what the game sets, made of nodes.</summary>
        AnimationGraph,
        Other
    }

    /// <summary>A file or folder in the project's Assets folder, or one of a model's animations (see <see cref="AssetKind.Animation"/>).</summary>
    public sealed record AssetEntry(string Path, AssetKind Kind)
    {
        /// <summary>Whether it's part of a file (a model's animation), not a file of its own.</summary>
        public bool IsPart => Path.Contains('#');

        public string Name => IsPart ? Path[(Path.LastIndexOf('#') + 1)..] : System.IO.Path.GetFileName(Path);
        public string NameWithoutExtension => Kind == AssetKind.Folder || IsPart ? Name : System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>For part of a file, the file's name without its extension ("Robot"); null for a file.</summary>
        public string? PartOf => IsPart ? System.IO.Path.GetFileNameWithoutExtension(Path[..Path.LastIndexOf('#')]) : null;
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

        // Shader files written since last asked (see TakeWrittenShaders): read again in place, so what uses them updates.
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _writtenShaders = new();

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
                _watcher.Changed += (_, e) =>
                {
                    if (KindOf(e.FullPath) == AssetKind.Shader) _writtenShaders.Enqueue(e.FullPath);
                };
            }
            catch (Exception)
            {
                // Without watching, Refresh still works.
            }
        }

        /// <summary>The shader files written since this was last asked, each once.</summary>
        public List<string> TakeWrittenShaders()
        {
            var written = new List<string>();
            while (_writtenShaders.TryDequeue(out string? path))
            {
                if (!written.Contains(path, StringComparer.OrdinalIgnoreCase)) written.Add(path);
            }

            return written;
        }

        /// <summary>Forgets what was read, so it's read again: after files change.</summary>
        public void Refresh()
        {
            _all = null;
            _folders.Clear();
            _byKind.Clear();
            _modelAnimationsRead = false;
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

        /// <summary>
        /// Every animation in the project, for pickers: the .anim files, then each model's own (read from the model the
        /// first time it's asked for; a model that can't be read has none).
        /// </summary>
        public IReadOnlyList<AssetEntry> Animations()
        {
            if (_changed) Refresh();
            if (_byKind.TryGetValue(AssetKind.Animation, out List<AssetEntry>? cached) && _modelAnimationsRead) return cached;

            var found = new List<AssetEntry>(All(AssetKind.Animation));
            foreach (AssetEntry model in All(AssetKind.Model))
            {
                try
                {
                    foreach (Animation.AnimationClip clip in Rendering.Model.LoadAnimations(model.Path)) found.Add(new AssetEntry($"{model.Path}#{clip.Name}", AssetKind.Animation));
                }
                catch (Exception)
                {
                    // A model that can't be read has no animations to offer.
                }
            }

            _byKind[AssetKind.Animation] = found;
            _modelAnimationsRead = true;
            return found;
        }

        private bool _modelAnimationsRead;

        public static AssetKind KindOf(string path)
        {
            // A model's own animation: its path, a #, and the animation's name.
            int hash = path.LastIndexOf('#');
            if (hash > 0 && KindOf(path[..hash]) == AssetKind.Model) return AssetKind.Animation;
            return KindOfFile(path);
        }

        private static AssetKind KindOfFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".gif" or ".hdr" => AssetKind.Texture,
            ".obj" or ".fbx" or ".gltf" or ".glb" or ".dae" or ".3ds" or ".blend" => AssetKind.Model,
            ".scene" => AssetKind.Scene,
            ".wav" or ".ogg" or ".mp3" => AssetKind.Sound,
            ".ttf" or ".otf" => AssetKind.Font,
            ".material" => AssetKind.Material,
            ".prefab" => AssetKind.Prefab,
            ".cs" => AssetKind.Script,
            ".vscript" => AssetKind.VisualScript,
            ".vfx" => AssetKind.VisualEffect,
            ".audiograph" => AssetKind.AudioGraph,
            ".glsl" => AssetKind.Shader,
            ".shadergraph" => AssetKind.ShaderGraph,
            ".anim" => AssetKind.Animation,
            ".animgraph" => AssetKind.AnimationGraph,
            _ => AssetKind.Other
        };

        public void Dispose() => _watcher?.Dispose();
    }
}
