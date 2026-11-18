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

        /// <summary>Shows a short note. Ones about something that went wrong ("Couldn't ...") go in the Console too, to look at later.</summary>
        public void Notify(string message)
        {
            if (message.StartsWith("Couldn't", StringComparison.Ordinal)) Log.Warning(message);
            Notified?.Invoke(message);
        }

        /// <summary>Everything logged while the editor runs, for the Console.</summary>
        public EditorLog ConsoleLog => EditorLog.Shared;

        /// <summary>Raised when the Console should be shown (an error the user needs to see).</summary>
        public event Action? ConsoleRequested;

        public void ShowConsole() => ConsoleRequested?.Invoke();

        private readonly System.Diagnostics.Stopwatch _buildTimer = new();

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

        /// <summary>
        /// Saves the scene to its file, asking where first if it hasn't one (or with <paramref name="saveAs"/>). True if
        /// it was saved straight away; if it had to ask, <paramref name="saved"/> runs once it's been saved.
        /// </summary>
        public bool SaveScene(bool saveAs = false, Action? saved = null)
        {
            if (Play.Playing)
            {
                Notify("Stop playing to save: changes made while playing aren't kept");
                return false;
            }

            if (ScenePath != null && !saveAs) return SaveSceneTo(ScenePath);

            string folder = Project != null ? Path.Combine(Project.AssetsDirectory, "Scenes") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Directory.CreateDirectory(folder);
            FilePicker.Save("Save Scene", folder, SceneName + ".scene", ("Scenes", "*.scene"), path =>
            {
                if (SaveSceneTo(path)) saved?.Invoke();
            });
            return false;
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
            Application.AssetFolders = AssetFolders;

            if (project.GameProjectFile is { } gameProject && File.Exists(gameProject))
            {
                Origin = SceneOrigin.Untitled;
                _untitledName = project.Name;
                BuildGame();
            }
            else
            {
                OpenStartupScene();
            }

            Notify($"Opened {project.Name}");
        }

        /// <summary>Opens the project's startup scene if it has one, otherwise a new scene.</summary>
        private void OpenStartupScene()
        {
            if (Project is { } project && project.Settings.StartupScene.Length > 0 && File.Exists(Path.Combine(project.AssetsDirectory, project.Settings.StartupScene)))
            {
                OpenScene(Path.Combine(project.AssetsDirectory, project.Settings.StartupScene));
            }
            else
            {
                NewScene();
            }
        }

        /// <summary>Makes a project in a new folder: asks where, then opens it.</summary>
        public void NewProject()
        {
            FilePicker.Save("New Project: where should it go?", null, "My Game" + Project.Extension, ("Vertex projects", "*" + Project.Extension), path =>
            {
                // Each project gets a folder of its own, named after it.
                string name = Path.GetFileNameWithoutExtension(path);
                string folder = Path.Combine(Path.GetDirectoryName(path)!, name);
                Directory.CreateDirectory(folder);
                Project.Create(Path.Combine(folder, name + Project.Extension), name);
                OpenProject(Path.Combine(folder, name + Project.Extension));
            });
        }

        /// <summary>Builds the project's game code again and reloads it: after changing the code.</summary>
        public void BuildGame()
        {
            if (Project?.GameProjectFile is not { } gameProject || _build != null) return;
            if (Play.Playing) Play.Stop();

            _buildingProject = gameProject;
            Busy = $"Building {Path.GetFileNameWithoutExtension(gameProject)}…";
            _buildTimer.Restart();
            _build = Task.Run(() => GameCode.Build(gameProject));
        }

        private void UnloadGame()
        {
            WorldGame = null;
            Game?.Unload();
            Game = null;
        }

        /// <summary>Picks up background work that's finished, and saves materials that have been changed. Called once a frame.</summary>
        public void Update()
        {
            ConsoleLog.Update();
            SaveChangedMaterials();
            if (_build is not { IsCompleted: true } build) return;

            _build = null;
            Busy = null;
            (bool built, string output, string? assemblyPath) = build.Result;
            if (!built || assemblyPath == null)
            {
                // The project is still open (its scenes and assets work), just without the game's scripts; the errors go
                // to the Console, where double-clicking one opens the code at it.
                OpenStartupScene();
                var errors = GameCode.ErrorsWithPlaces(output);
                string name = Path.GetFileNameWithoutExtension(_buildingProject) ?? "The game";
                if (errors.Count == 0) Log.Write(LogLevel.Error, $"{name} didn't build", output.Trim().Length > 0 ? output.Trim() : "dotnet build failed with no output.");
                foreach ((string shown, string? file, int line) in errors) Log.Write(LogLevel.Error, shown, file != null ? $"{file}({line})" : null);
                Log.Warning($"{name} didn't build ({errors.Count} error{(errors.Count == 1 ? "" : "s")}): its scripts aren't loaded until it does. Fix the code, then Rebuild Game Code (Ctrl+R).");
                ShowConsole();
                Notify($"{name} didn't build: the errors are in the Console");
                return;
            }

            Log.Info($"Built {Path.GetFileNameWithoutExtension(_buildingProject)} in {_buildTimer.Elapsed.TotalSeconds:0.0} s");

            try
            {
                ReplaceScene();
                UnloadGame();
                Game = GameCode.Load(_buildingProject!, assemblyPath);

                // The game finds its files (Assets, steam_appid.txt) where it was built, as if run from there.
                Application.BaseDirectory = Game.OutputDirectory;
                Application.AssetFolders = AssetFolders;
                Directory.SetCurrentDirectory(Game.OutputDirectory);
            }
            catch (Exception exception)
            {
                Log.Error($"Couldn't load the game's code: {exception.Message}", exception);
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
                Log.Error($"The game's Load failed: {exception.Message}", exception);
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

        // ---------------------------------------------------------------- Materials

        private int _materialsCheckedAt = -1;
        private readonly Dictionary<string, string> _materialsWritten = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Writes .material files whose materials have been changed (in the inspector, or by undo), once the change is
        /// done: they're shared, so their files are kept up to date rather than saved with the scene.
        /// </summary>
        private void SaveChangedMaterials()
        {
            if (History.Version == _materialsCheckedAt || InputSystem.GetMouseButton(MouseButton.Left)) return;
            _materialsCheckedAt = History.Version;

            foreach (Material material in Assets_LoadedMaterials())
            {
                if (material.Source is not { } path || !File.Exists(path)) continue;
                try
                {
                    string json = SceneFile.SaveMaterial(material, AssetFolders);
                    if (!_materialsWritten.TryGetValue(path, out string? written)) written = File.ReadAllText(path);
                    if (written == json) continue;

                    File.WriteAllText(path, json);
                    _materialsWritten[path] = json;
                    Thumbnails.Forget(path);
                }
                catch (Exception exception)
                {
                    Notify($"Couldn't save {Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

        private static IEnumerable<Material> Assets_LoadedMaterials() => Vertex.Assets.LoadedMaterials.ToList();

        /// <summary>Makes a new .material file with default settings, and returns where.</summary>
        public string CreateMaterial(string folder, string name = "New Material")
        {
            Directory.CreateDirectory(folder);
            string path = Panels.AssetsPanel.Unique(Path.Combine(folder, name), ".material");
            File.WriteAllText(path, SceneFile.SaveMaterial(new Material(), AssetFolders));
            Assets?.Refresh();
            return path;
        }

        // ---------------------------------------------------------------- Prefabs

        /// <summary>
        /// Saves <paramref name="gameObject"/> and what's under it as a new prefab in <paramref name="folder"/>, which it
        /// becomes a copy of. Returns the prefab, or null if it couldn't be saved.
        /// </summary>
        public Prefab? CreatePrefab(GameObject gameObject, string folder)
        {
            try
            {
                string path = Panels.AssetsPanel.Unique(Path.Combine(folder, SafeFileName(gameObject.Name)), ".prefab");
                Prefab prefab = Prefab.Save(gameObject, path, AssetFolders);
                Assets?.Refresh();
                Notify($"Made the prefab {prefab.Name}: drag it into the scene for more");
                return prefab;
            }
            catch (Exception exception)
            {
                Notify($"Couldn't make a prefab of {gameObject.Name}: {exception.Message}");
                return null;
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().Length > 0 ? name.Trim() : "Prefab";
        }

        /// <summary>A copy of the prefab in <paramref name="path"/> at <paramref name="position"/>, as one undoable change, selected.</summary>
        public GameObject? AddPrefab(string path, Vector3 position, Transform? parent = null)
        {
            try
            {
                Prefab prefab = Vertex.Assets.LoadPrefab(path);
                GameObject copy = prefab.Instantiate(Scene, AssetFolders);
                copy.Transform.Parent = parent;
                if (parent == null) copy.Transform.Position = position;
                Scene.ApplyChanges();
                History.Record(new CreateObject(copy, $"Add {prefab.Name}"));
                Selection.Select(copy);
                return copy;
            }
            catch (Exception exception)
            {
                Notify($"Couldn't add {Path.GetFileNameWithoutExtension(path)}: {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves a prefab copy's changes into its prefab, then makes every other copy in the scene again from it (each
        /// keeping its own place, name and parent). The scene's side can be undone; the file stays saved.
        /// </summary>
        public void ApplyPrefab(GameObject copy)
        {
            if (copy.Prefab is not { } prefab) return;

            try
            {
                Prefab.Save(copy, prefab.Source, AssetFolders);
            }
            catch (Exception exception)
            {
                Dialog.Message("Couldn't save the prefab", exception.Message);
                return;
            }

            var changes = new List<IUndoable>();
            foreach (GameObject other in Scene.GameObjects.ToList())
            {
                if (other.Prefab != prefab || other == copy || IsUnder(other, copy)) continue;
                changes.AddRange(Remake(other, prefab));
            }

            if (changes.Count > 0) History.Record(new ChangeGroup($"Apply {prefab.Name}", changes));
            Notify(changes.Count > 0 ? $"Saved {prefab.Name} and updated {changes.Count / 2} other cop{(changes.Count == 2 ? "y" : "ies")}" : $"Saved {prefab.Name}");
        }

        /// <summary>Makes a prefab copy again from its prefab, dropping its changes (but keeping its place, name and parent).</summary>
        public void RevertPrefab(GameObject copy)
        {
            if (copy.Prefab is not { } prefab) return;

            try
            {
                Vertex.Assets.Reload(prefab.Source);
                List<IUndoable> changes = Remake(copy, prefab);
                History.Record(new ChangeGroup($"Revert {copy.Name}", changes));
                if (changes.Count > 0 && changes[0] is CreateObject made) Selection.Select(made.GameObject);
            }
            catch (Exception exception)
            {
                Notify($"Couldn't revert {copy.Name}: {exception.Message}");
            }
        }

        /// <summary>Swaps a copy for a fresh one from the prefab, in the same place. Returns the two changes that did it, already done.</summary>
        private List<IUndoable> Remake(GameObject old, Prefab prefab)
        {
            GameObject fresh = prefab.Instantiate(Scene, AssetFolders);
            fresh.Name = old.Name;
            fresh.Active = old.Active;
            fresh.Transform.Parent = old.Transform.Parent;
            fresh.Transform.Position = old.Transform.Position;
            fresh.Transform.Rotation = old.Transform.Rotation;
            fresh.Transform.Scale = old.Transform.Scale;
            Scene.ApplyChanges();
            fresh.Transform.SiblingIndex = old.Transform.SiblingIndex;

            var create = new CreateObject(fresh);
            var delete = new DeleteObject(old);
            delete.Redo();
            if (Selection.Object == old) Selection.Select(fresh);
            return [create, delete];
        }

        private static bool IsUnder(GameObject gameObject, GameObject ancestor)
        {
            for (Transform? parent = gameObject.Transform.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.GameObject == ancestor) return true;
            }

            return false;
        }
    }
}
