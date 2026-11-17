using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Platform;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Editor
{
    /// <summary>What the move, rotate and scale handles in the scene view do.</summary>
    public enum TransformTool
    {
        Move,
        Rotate,
        Scale
    }

    /// <summary>Where the scene being edited came from, which decides how it's saved and how play mode puts it back.</summary>
    public enum SceneOrigin
    {
        /// <summary>Made in the editor and not saved yet.</summary>
        Untitled,

        /// <summary>Opened from (or saved to) a .scene file.</summary>
        File,

        /// <summary>Built by the game's own code (its <see cref="Game.Load"/>), as the game builds it when it starts.</summary>
        GameWorld
    }

    /// <summary>What's selected: an object, the scene itself (for its lighting and sky), an asset, or nothing.</summary>
    public sealed class Selection
    {
        public GameObject? Object { get; private set; }

        /// <summary>The scene itself is selected, so the inspector shows its lighting and sky.</summary>
        public bool Scene { get; private set; }

        /// <summary>A file in the asset browser.</summary>
        public AssetEntry? Asset { get; private set; }

        /// <summary>Counts every change of selection, so panels can tell.</summary>
        public int Version { get; private set; }

        public void Select(GameObject? gameObject)
        {
            if (Object == gameObject && !Scene && Asset == null) return;
            (Object, Scene, Asset) = (gameObject, false, null);
            Version++;
        }

        public void SelectScene()
        {
            if (Scene) return;
            (Object, Scene, Asset) = (null, true, null);
            Version++;
        }

        public void SelectAsset(AssetEntry? asset)
        {
            if (Asset == asset) return;
            (Object, Scene, Asset) = (null, false, asset);
            Version++;
        }

        public void Clear() => Select(null);
    }

    /// <summary>
    /// What all the editor's panels share: the open project and the game's code, the scene being edited and where it
    /// came from, what's selected, the history of changes, play mode, and the commands in the menus.
    /// </summary>
    public sealed class EditorContext
    {
        public Scene Scene => Vertex.Scene.Active;

        public Selection Selection { get; } = new();
        public UndoHistory History { get; } = new();
        public Preferences Preferences { get; }
        public PlayMode Play { get; }
        public TransformTool Tool { get; set; } = TransformTool.Move;
        public SnapSettings Snap => Preferences.Snapping;

        /// <summary>Where new objects go: the point the scene view's camera is looking at, set by the scene view.</summary>
        public Vector3 SpawnPoint { get; set; }

        public Project? Project { get; private set; }
        public GameCode? Game { get; private set; }
        public AssetDatabase? Assets { get; private set; }

        /// <summary>The game whose <see cref="Vertex.Game.Load"/> built the scene, if it was built that way.</summary>
        public Game? WorldGame { get; private set; }

        public SceneOrigin Origin { get; private set; } = SceneOrigin.Untitled;
        public string? ScenePath { get; private set; }

        private string _untitledName = "Untitled";
        private int _savedVersion;

        /// <summary>What the editor is busy with in the background (building the game), shown while it is; null when it isn't.</summary>
        public string? Busy { get; private set; }

        private Task<(bool Built, string Output, string? AssemblyPath)>? _build;
        private string? _buildingProject;

        public EditorContext(Preferences? preferences = null)
        {
            Preferences = preferences ?? new Preferences();
            Play = new PlayMode(this);
        }

        /// <summary>The scene's name: its file's, or what the game builds, or what it's been called.</summary>
        public string SceneName
        {
            get => ScenePath != null ? Path.GetFileNameWithoutExtension(ScenePath) : Origin == SceneOrigin.GameWorld ? $"{Project?.Name ?? "Game"} world" : _untitledName;
            set => _untitledName = value;
        }

        /// <summary>Whether there are changes since the scene was last saved (or opened).</summary>
        public bool Dirty => History.Version != _savedVersion && !Play.Playing;

        public void MarkSaved() => _savedVersion = History.Version;

        /// <summary>Where textures and models a scene refers to are looked for.</summary>
        public string[] AssetFolders => Project?.AssetFolders(Game?.OutputDirectory) ?? [Application.AssetPath()];

        /// <summary>Raised to show a short note at the bottom of the window ("Undo Move Cube").</summary>
        public event Action<string>? Notified;

        /// <summary>Raised when an object should be renamed in place (F2, or the Edit menu), for the outliner to do.</summary>
        public event Action<GameObject>? RenameRequested;

        public void Notify(string message) => Notified?.Invoke(message);

        public void Rename(GameObject gameObject) => RenameRequested?.Invoke(gameObject);

        /// <summary>Whether <paramref name="gameObject"/> is in the scene: not deleted (deleting can be undone, so deleted objects live on).</summary>
        public bool InScene(GameObject gameObject)
        {
            if (gameObject.IsDestroyed || gameObject.Scene != Scene) return false;
            foreach (GameObject member in Scene.GameObjects)
            {
                if (member == gameObject) return true;
            }

            return false;
        }

        // ---------------------------------------------------------------- Objects

        /// <summary>Makes a new object, records it, and selects it.</summary>
        public GameObject Create(string name, Action<GameObject>? build = null, Transform? parent = null)
        {
            var gameObject = new GameObject(name, Scene);
            build?.Invoke(gameObject);
            gameObject.Transform.Parent = parent;
            Scene.ApplyChanges();
            History.Record(new CreateObject(gameObject));
            Selection.Select(gameObject);
            return gameObject;
        }

        public void Delete(GameObject gameObject)
        {
            History.Do(new DeleteObject(gameObject));
            if (Selection.Object == gameObject) Selection.Clear();
            Notify($"Deleted {gameObject.Name}");
        }

        /// <summary>Copies an object and everything under it, just after it, and selects the copy.</summary>
        public GameObject? Duplicate(GameObject original)
        {
            try
            {
                List<GameObject> made = SceneFile.TakeSnapshot(Scene, [original]).Restore(Scene);
                GameObject copy = made[0];
                copy.Transform.Parent = original.Transform.Parent;
                Scene.ApplyChanges();
                copy.Transform.SiblingIndex = original.Transform.SiblingIndex + 1;
                History.Record(new CreateObject(copy, $"Duplicate {original.Name}"));
                Selection.Select(copy);
                return copy;
            }
            catch (Exception exception)
            {
                Notify($"Couldn't duplicate {original.Name}: {exception.Message}");
                return null;
            }
        }

        public void Undo()
        {
            IUndoable? change = History.Undo();
            if (change != null) Notify($"Undo {change.Name}");
        }

        public void Redo()
        {
            IUndoable? change = History.Redo();
            if (change != null) Notify($"Redo {change.Name}");
        }

        // ---------------------------------------------------------------- Scenes

        /// <summary>Swaps in a new, empty scene to fill.</summary>
        private void ReplaceScene()
        {
            if (Play.Playing) Play.Stop();
            // The scene being edited never ran, so it has nothing to clean up: thrown away without running its code.
            Vertex.Scene.Active.Discard();
            Vertex.Scene.Active = new Scene();
            Camera.Main = null;
            Selection.Clear();
            History.Clear();
            WorldGame = null;
        }

        /// <summary>A new scene with a sun and a camera.</summary>
        public void NewScene()
        {
            ReplaceScene();
            var sun = new GameObject("Sun", Scene);
            sun.AddComponent<Light>();
            sun.Transform.EulerAngles = new Vector3(-50f, 35f, 0f);
            var camera = new GameObject("Main Camera", Scene);
            camera.Transform.Position = new Vector3(0f, 2f, 8f);
            camera.Transform.EulerAngles = new Vector3(-10f, 0f, 0f);
            camera.AddComponent<Camera>();
            Scene.ApplyChanges();

            Origin = SceneOrigin.Untitled;
            ScenePath = null;
            _untitledName = "Untitled";
            MarkSaved();
        }

        /// <summary>Opens a .scene file in place of the scene being edited.</summary>
        public void OpenScene(string path)
        {
            ReplaceScene();
            try
            {
                SceneFile.Load(path, Scene, AssetFolders);
            }
            catch (Exception exception)
            {
                Dialog.Message("Couldn't open the scene", $"{Path.GetFileName(path)}: {exception.Message}");
            }

            Scene.ApplyChanges();
            Origin = SceneOrigin.File;
            ScenePath = Path.GetFullPath(path);
            MarkSaved();
            Notify($"Opened {SceneName}");
        }

        /// <summary>Saves the scene to its file, asking where first if it hasn't one (or with <paramref name="saveAs"/>). False if not saved.</summary>
        public bool SaveScene(bool saveAs = false)
        {
            if (Play.Playing)
            {
                Notify("Stop playing to save: changes made while playing aren't kept");
                return false;
            }

            string? path = ScenePath;
            if (path == null || saveAs)
            {
                string folder = Project != null ? Path.Combine(Project.AssetsDirectory, "Scenes") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                Directory.CreateDirectory(folder);
                path = NativeDialogs.SaveFile("Save Scene", folder, SceneName + ".scene", ("Scenes", "*.scene"));
                if (path == null)
                {
                    if (!NativeDialogs.Available) AskForPath("Save Scene As", Path.Combine(folder, SceneName + ".scene"), chosen => SaveSceneTo(chosen));
                    return false;
                }
            }

            return SaveSceneTo(path);
        }

        private bool SaveSceneTo(string path)
        {
            try
            {
                SceneFile.Save(Scene, path, AssetFolders);
            }
            catch (Exception exception)
            {
                Dialog.Message("Couldn't save the scene", exception.Message);
                return false;
            }

            ScenePath = Path.GetFullPath(path);
            Origin = SceneOrigin.File;
            WorldGame = null;
            MarkSaved();
            Assets?.Refresh();
            Notify($"Saved {SceneName}");
            return true;
        }

        /// <summary>For systems without file dialogs: asks for a path in a dialog of the editor's own.</summary>
        public static void AskForPath(string title, string initial, Action<string> chosen)
        {
            string path = initial;
            Dialog.Show(title, new Vector2(560f, 140f), rect =>
            {
                TextInput.Draw(Ui.Id("path.input"), rect.TakeTop(30f), ref path, "Full path", live: true);
                if (Widgets.Button(Ui.Id("path.ok"), rect.TakeBottom(30f).TakeRight(100f), "OK", ButtonStyle.Primary) || InputSystem.GetKeyDown(Key.Enter))
                {
                    Dialog.Close();
                    chosen(path.Trim().Trim('"'));
                }
            });
        }

        // ---------------------------------------------------------------- Projects

        /// <summary>
        /// Opens a project: a .vertexproject, a game's .csproj (which gets a project file made for it), or a folder with
        /// either in. A game is built in the background and then loaded; until then the scene is empty.
        /// </summary>
        public void OpenProject(string path)
        {
            Project project;
            try
            {
                project = Project.Open(path);
            }
            catch (Exception exception)
            {
                Dialog.Message("Couldn't open the project", exception.Message);
                return;
            }

            ReplaceScene();
            UnloadGame();
            Project = project;
            Preferences.Remember(project.FilePath);
            Assets?.Dispose();
            Assets = new AssetDatabase(project.AssetsDirectory);
            Application.BaseDirectory = project.Directory;

            if (project.GameProjectFile is { } gameProject && File.Exists(gameProject))
            {
                Origin = SceneOrigin.Untitled;
                _untitledName = project.Name;
                BuildGame();
            }
            else if (project.Settings.StartupScene.Length > 0 && File.Exists(Path.Combine(project.AssetsDirectory, project.Settings.StartupScene)))
            {
                OpenScene(Path.Combine(project.AssetsDirectory, project.Settings.StartupScene));
            }
            else
            {
                NewScene();
            }

            Notify($"Opened {project.Name}");
        }

        /// <summary>Makes a project in a new folder: asks where, then opens it.</summary>
        public void NewProject()
        {
            string? path = NativeDialogs.SaveFile("New Project: where should it go?", null, "My Game" + Project.Extension, ("Vertex projects", "*" + Project.Extension));
            if (path == null) return;

            // Each project gets a folder of its own, named after it.
            string name = Path.GetFileNameWithoutExtension(path);
            string folder = Path.Combine(Path.GetDirectoryName(path)!, name);
            Directory.CreateDirectory(folder);
            Project.Create(Path.Combine(folder, name + Project.Extension), name);
            OpenProject(Path.Combine(folder, name + Project.Extension));
        }

        /// <summary>Builds the project's game code again and reloads it: after changing the code.</summary>
        public void BuildGame()
        {
            if (Project?.GameProjectFile is not { } gameProject || _build != null) return;
            if (Play.Playing) Play.Stop();

            _buildingProject = gameProject;
            Busy = $"Building {Path.GetFileNameWithoutExtension(gameProject)}…";
            _build = Task.Run(() => GameCode.Build(gameProject));
        }

        private void UnloadGame()
        {
            WorldGame = null;
            Game?.Unload();
            Game = null;
        }

        /// <summary>Picks up background work that's finished. Called once a frame.</summary>
        public void Update()
        {
            if (_build is not { IsCompleted: true } build) return;

            _build = null;
            Busy = null;
            (bool built, string output, string? assemblyPath) = build.Result;
            if (!built || assemblyPath == null)
            {
                Dialog.Message("The game didn't build", output.Trim().Length > 0 ? output.Trim() : "dotnet build failed with no output.");
                return;
            }

            try
            {
                ReplaceScene();
                UnloadGame();
                Game = GameCode.Load(_buildingProject!, assemblyPath);

                // The game finds its files (Assets, steam_appid.txt) where it was built, as if run from there.
                Application.BaseDirectory = Game.OutputDirectory;
                Directory.SetCurrentDirectory(Game.OutputDirectory);
            }
            catch (Exception exception)
            {
                Dialog.Message("Couldn't load the game's code", exception.ToString());
                return;
            }

            if (Game.GameType != null) LoadGameWorld();
            else NewScene();
            Notify(Game.GameType != null ? $"Loaded {Game.GameType.Name}" : $"Loaded {Path.GetFileNameWithoutExtension(_buildingProject)}'s components");
        }

        /// <summary>
        /// Builds the world as the game does when it starts, by running its <see cref="Vertex.Game.Load"/>, without
        /// running anything after: so the editor shows what the game would start with.
        /// </summary>
        public void LoadGameWorld()
        {
            if (Game?.CreateGame() is not { } game) return;

            ReplaceScene();
            GameLoop.Start();
            try
            {
                game.Load();
            }
            catch (Exception exception)
            {
                Dialog.Message("The game's Load failed", exception.ToString());
            }

            // Whatever the game set up for playing, the editor isn't playing yet.
            InputSystem.CursorLocked = false;
            Scene.ApplyChanges();

            WorldGame = game;
            Origin = SceneOrigin.GameWorld;
            ScenePath = null;
            History.Clear();
            MarkSaved();
        }
    }
}
