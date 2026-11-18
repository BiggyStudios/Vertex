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
        /// Reads a changed file again where it can be done in place (a material: everything using it sees the change, a
        /// prefab: copies made after), and otherwise forgets it, so it's read again next time it's asked for.
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
            else
            {
                Forget(full);
            }
        }

        /// <summary>The graphs loaded so far: for an editor, which saves them when they're changed.</summary>
        internal static IEnumerable<Graphs.Graph> LoadedGraphs => _graphs.Values;

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
        }
    }
}
