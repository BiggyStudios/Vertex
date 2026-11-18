using System.Numerics;

using Vertex.Serialization;

namespace Vertex
{
    /// <summary>
    /// An object, with everything under it, saved to a .prefab file to make copies of: a crate placed all over a level,
    /// or a rocket made each time one's fired. Each copy is an ordinary object of its own, which remembers the prefab it
    /// came from (<see cref="GameObject.Prefab"/>), so an editor can update copies when the prefab changes.
    /// <code>
    /// Prefab rocket = Assets.LoadPrefab(Application.AssetPath("Prefabs", "Rocket.prefab"));
    /// GameObject fired = rocket.Instantiate(muzzle.WorldPosition, muzzle.WorldRotation);
    /// </code>
    /// A component can also hold a prefab in a property (<c>public Prefab? Rocket { get; set; }</c>) set in the editor.
    /// </summary>
    public sealed class Prefab
    {
        /// <summary>The .prefab file, in full.</summary>
        public string Source { get; internal set; }

        public string Name => Path.GetFileNameWithoutExtension(Source);

        /// <summary>What's in the file, read once: copies are made from this.</summary>
        internal string Json { get; set; }

        internal Prefab(string source, string json)
        {
            Source = source;
            Json = json;
        }

        /// <summary>Loads a .prefab file, or gives the one already loaded from there. The same as <see cref="Assets.LoadPrefab"/>.</summary>
        public static Prefab Load(string path) => Assets.LoadPrefab(path);

        /// <summary>A copy in the active scene, where the prefab was when it was saved.</summary>
        public GameObject Instantiate() => Instantiate(Scene.Active);

        /// <summary>A copy in the active scene at <paramref name="position"/>, turned by <paramref name="rotation"/>, under <paramref name="parent"/> if given.</summary>
        public GameObject Instantiate(Vector3 position, Quaternion? rotation = null, Transform? parent = null)
        {
            GameObject copy = Instantiate(Scene.Active);
            copy.Transform.Parent = parent;
            copy.Transform.Position = position;
            if (rotation is Quaternion turned) copy.Transform.Rotation = turned;
            return copy;
        }

        /// <summary>
        /// A copy in <paramref name="scene"/>, with files it refers to found in <paramref name="assetFolders"/> (by
        /// default <see cref="Application.AssetFolders"/>). Returns the copy's top object; the rest are under it.
        /// </summary>
        public GameObject Instantiate(Scene scene, params string[] assetFolders)
        {
            List<GameObject> made = SceneFile.LoadJson(Json, scene, assetFolders);
            if (made.Count == 0) throw new InvalidDataException($"{Name}.prefab has nothing in it.");

            GameObject root = made.FirstOrDefault(o => o.Transform.Parent == null) ?? made[0];
            root.Prefab = this;
            return root;
        }

        /// <summary>
        /// Saves <paramref name="root"/> and everything under it as the prefab at <paramref name="path"/> (made, or
        /// replaced), and returns the prefab. <paramref name="root"/> becomes a copy of it.
        /// </summary>
        public static Prefab Save(GameObject root, string path, params string[] assetFolders)
        {
            string json = SceneFile.SavePrefab(root, assetFolders);
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder != null) Directory.CreateDirectory(folder);
            File.WriteAllText(path, json);

            Prefab prefab = Assets.LoadPrefab(path);
            prefab.Json = json;
            root.Prefab = prefab;
            return prefab;
        }

        public override string ToString() => Name;
    }
}
