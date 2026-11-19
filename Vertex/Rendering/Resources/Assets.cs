using Vertex.Audio;
using Vertex.Rendering;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex
{
    /// <summary>
    /// Files loaded once and shared: asking for the same texture again gives the one already loaded, so a hundred
    /// objects using it share one copy on the GPU. The same goes for materials (so changing one changes everything that
    /// uses it), prefabs, fonts and sounds.
    /// </summary>
    public static class Assets
    {
        private static readonly Dictionary<string, Texture> _textures = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Material> _materials = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Prefab> _prefabs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Font> _fonts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AudioClip> _sounds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Graphs.Graph> _graphs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Shader> _shaders = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The texture in <paramref name="path"/>, loaded the first time it's asked for.</summary>
        public static Texture LoadTexture(string path) => Load(_textures, path, Texture.Load);

        /// <summary>
        /// The material in a .material file (made in an editor), loaded the first time it's asked for. Everything given it
        /// shares it: change it, and they all change.
        /// </summary>
        public static Material LoadMaterial(string path) => Load(_materials, path, full =>
        {
            var material = new Material { Source = full };
            SceneFile.LoadMaterial(File.ReadAllText(full), material);
            return material;
        });

        /// <summary>The prefab in a .prefab file, loaded the first time it's asked for. See <see cref="Prefab"/>.</summary>
        public static Prefab LoadPrefab(string path) => Load(_prefabs, path, full => new Prefab(full, File.ReadAllText(full)));

        /// <summary>The font in a .ttf or .otf file, loaded the first time it's asked for.</summary>
        public static Font LoadFont(string path) => Load(_fonts, path, full => Font.Load(full));

        /// <summary>The sound in a .wav file, loaded the first time it's asked for.</summary>
        public static AudioClip LoadSound(string path) => Load(_sounds, path, AudioClip.Load);

        /// <summary>
        /// The graph in a graph file (a .vscript visual script), loaded the first time it's asked for. Everything using it
        /// shares it, so a change made in an editor reaches scripts already running.
        /// </summary>
        public static Graphs.Graph LoadGraph(string path) => Load(_graphs, path, Graphs.Graph.Load);

        /// <summary>
        /// The shader in a .glsl file or a shader graph (a .shadergraph file; see <see cref="Shader"/>), loaded the first
        /// time it's asked for. Everything using it shares it, so when the file changes, they all change. A shader graph's
        /// shader follows its graph (see <see cref="LoadGraph"/>), so a change made in an editor shows straight away.
        /// </summary>
        public static Shader LoadShader(string path) => Load(_shaders, path, full =>
            Path.GetExtension(full).Equals(ShaderGraphs.ShaderNodes.Extension, StringComparison.OrdinalIgnoreCase) ? Shader.FromGraph(LoadGraph(full)) : Shader.Load(full));

        /// <summary>
        /// A project file's path as it's kept in graphs and sent over the network: from the first of
        /// <see cref="Application.AssetFolders"/> it's in ("Prefabs/Rocket.prefab"), so it's found wherever the game's
        /// installed. A file outside them keeps its full path.
        /// </summary>
        public static string RelativePath(string path)
        {
            string full = Path.GetFullPath(path);
            foreach (string folder in Application.AssetFolders)
            {
                string relative = Path.GetRelativePath(Path.GetFullPath(folder), full);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative.Replace('\\', '/');
            }

            return full;
        }

        private static readonly Dictionary<string, string?> _found = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a file kept by <see cref="RelativePath"/> is: looked for in <see cref="Application.AssetFolders"/>; null if it isn't there.</summary>
        public static string? FindFile(string saved)
        {
            if (saved.Length == 0) return null;
            if (_found.TryGetValue(saved, out string? known) && known != null && File.Exists(known)) return known;

            string? found = Path.IsPathRooted(saved) ? (File.Exists(saved) ? saved : null) : null;
            foreach (string folder in Application.AssetFolders)
            {
                if (found != null) break;
                string candidate = Path.GetFullPath(Path.Combine(folder, saved));
                if (File.Exists(candidate)) found = candidate;
            }

            _found[saved] = found;
            return found;
        }

        private static T Load<T>(Dictionary<string, T> loaded, string path, Func<string, T> load)
        {
            string full = Path.GetFullPath(path);
            if (loaded.TryGetValue(full, out T? asset)) return asset;

            asset = load(full);
            loaded[full] = asset;
            return asset;
        }

        /// <summary>The materials loaded from files so far: for an editor, which saves them when they're changed.</summary>
        internal static IEnumerable<Material> LoadedMaterials => _materials.Values;

        /// <summary>The prefabs loaded so far.</summary>
        internal static IEnumerable<Prefab> LoadedPrefabs => _prefabs.Values;

        /// <summary>
        /// Reads a changed file again where it can be done in place (a material, graph or shader: everything using it sees
        /// the change; a prefab: copies made after), and otherwise forgets it, so it's read again next time it's asked for.
        /// </summary>
        public static void Reload(string path)
        {
            string full = Path.GetFullPath(path);
            if (_materials.TryGetValue(full, out Material? material) && File.Exists(full))
            {
                try
                {
                    SceneFile.LoadMaterial(File.ReadAllText(full), material);
                }
                catch (Exception)
                {
                    // Half-written, perhaps: kept as it was.
                }
            }
            else if (_prefabs.TryGetValue(full, out Prefab? prefab) && File.Exists(full))
            {
                try
                {
                    prefab.Json = File.ReadAllText(full);
                }
                catch (Exception)
                {
                    // Kept as it was.
                }
            }
            else if (_graphs.TryGetValue(full, out Graphs.Graph? graph) && File.Exists(full))
            {
                try
                {
                    // Not when it's what was just saved from here (an editor saving as it goes).
                    string json = File.ReadAllText(full);
                    if (json != graph.SavedJson)
                    {
                        graph.ReadJson(json);
                        graph.SavedJson = json;
                    }
                }
                catch (Exception)
                {
                    // Half-written, perhaps: kept as it was.
                }
            }
            else if (_shaders.TryGetValue(full, out Shader? shader) && File.Exists(full))
            {
                try
                {
                    string code = File.ReadAllText(full);
                    if (code != shader.Code) shader.SetCode(code);
                }
                catch (IOException)
                {
                    // Still being written: kept as it was.
                }
            }
            else
            {
                Forget(full);
            }
        }

        /// <summary>The graphs loaded so far: for an editor, which saves them when they're changed.</summary>
        internal static IEnumerable<Graphs.Graph> LoadedGraphs => _graphs.Values;

        /// <summary>
        /// A file (or a folder of them) moved or renamed: what was loaded from it is kept, and told its new place, so
        /// everything using it still does, and saves the new path with it.
        /// </summary>
        public static void Moved(string from, string to)
        {
            string before = Path.GetFullPath(from), after = Path.GetFullPath(to);
            string? Remap(string key) =>
                string.Equals(key, before, StringComparison.OrdinalIgnoreCase) ? after
                : key.StartsWith(before + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? after + key[before.Length..]
                : null;

            MoveIn(_textures, Remap, (texture, path) => texture.Source = path);
            MoveIn(_materials, Remap, (material, path) => material.Source = path);
            MoveIn(_prefabs, Remap, (prefab, path) => prefab.Source = path);
            MoveIn(_fonts, Remap, (font, path) => font.Source = path);
            MoveIn(_sounds, Remap, (clip, path) => clip.Source = path);
            MoveIn(_graphs, Remap, (graph, path) => graph.Source = path);
            MoveIn(_shaders, Remap, (shader, path) => shader.Source = path);
            _found.Clear();
        }

        private static void MoveIn<T>(Dictionary<string, T> loaded, Func<string, string?> remap, Action<T, string> tell)
        {
            foreach (string key in loaded.Keys.ToList())
            {
                if (remap(key) is not { } moved) continue;
                T asset = loaded[key];
                loaded.Remove(key);
                loaded[moved] = asset;
                tell(asset, moved);
            }
        }

        /// <summary>Forgets a loaded file, so it's read again next time: after it's been changed on disk, moved or deleted.</summary>
        public static void Forget(string path)
        {
            string full = Path.GetFullPath(path);
            _textures.Remove(full);
            _materials.Remove(full);
            _prefabs.Remove(full);
            _fonts.Remove(full);
            _sounds.Remove(full);
            _graphs.Remove(full);
            _shaders.Remove(full);
        }
    }
}
