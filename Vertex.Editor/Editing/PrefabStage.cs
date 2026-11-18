using System.Numerics;

using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Editor.Editing
{
    /// <summary>
    /// A prefab open in a tab of its own: a copy of it, alone in a scene of its own, to change. It has its own history of
    /// changes, and its file is saved as it's changed; copies of it in the scene are made again from it once the scene's
    /// shown again.
    /// </summary>
    public sealed class PrefabStage
    {
        public Prefab Prefab { get; }
        public string Path => Prefab.Source;
        public string Title => Prefab.Name;

        /// <summary>The scene it's shown in: just the prefab, lit by a light that isn't part of it.</summary>
        public Scene Scene { get; } = new();

        /// <summary>The prefab's top object. Everything else in it is under this.</summary>
        public GameObject Root { get; }

        public UndoHistory History { get; } = new();

        /// <summary>What was selected in it, kept while another tab shows.</summary>
        internal List<GameObject> Selected { get; set; } = new();

        /// <summary>Where the scene view's camera was looking at it, kept while another tab shows.</summary>
        internal SceneEditing.EditorCamera.Placement? Camera { get; set; }

        private int _savedVersion;
        private readonly Scene _lights = new();

        public PrefabStage(Prefab prefab, string[] assetFolders)
        {
            Prefab = prefab;
            Root = prefab.Instantiate(Scene, assetFolders);
            Scene.ApplyChanges();

            // Lit like a new scene, from above and to one side.
            var sun = new GameObject("Prefab Light", _lights);
            sun.Transform.EulerAngles = new Vector3(-50f, 35f, 0f);
            Scene.OutsideLights.Add(sun.AddComponent<Light>());
        }

        /// <summary>Whether it's been changed since it was last saved.</summary>
        public bool Changed => History.Version != _savedVersion;

        /// <summary>
        /// Saves the prefab's file if it's been changed since. Copies made from now on (and the scene's, once it's shown
        /// again) are made from what's saved. True if the file was written.
        /// </summary>
        public bool SaveIfChanged(string[] assetFolders)
        {
            if (!Changed) return false;
            _savedVersion = History.Version;

            string json = SceneFile.SavePrefab(Root, assetFolders);
            if (json == Prefab.Json) return false;

            Prefab.Save(Root, Path, assetFolders);
            Gui.Thumbnails.Forget(Path);
            return true;
        }

        /// <summary>Lets go of it once its tab is closed.</summary>
        public void Close()
        {
            Scene.OutsideLights.Clear();
            Scene.Discard();
            _lights.Discard();
        }
    }
}
