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

    /// <summary>
    /// What's selected: one or more objects, the scene itself (for its lighting and sky), an asset, or nothing. Of several
    /// objects, the one picked last is the main one: the handles sit on it, and the inspector shows its values.
    /// </summary>
    public sealed class Selection
    {
        private readonly List<GameObject> _objects = new();

        /// <summary>The selected objects, in the order they were picked.</summary>
        public IReadOnlyList<GameObject> Objects => _objects;

        /// <summary>The main selected object: the one picked last. Null if no object is selected.</summary>
        public GameObject? Object => _objects.Count > 0 ? _objects[^1] : null;

        public int Count => _objects.Count;

        /// <summary>The scene itself is selected, so the inspector shows its lighting and sky.</summary>
        public bool Scene { get; private set; }

        /// <summary>A file in the asset browser.</summary>
        public AssetEntry? Asset { get; private set; }

        /// <summary>Counts every change of selection, so panels can tell.</summary>
        public int Version { get; private set; }

        public bool Contains(GameObject gameObject) => _objects.Contains(gameObject);

        /// <summary>Selects just <paramref name="gameObject"/> (or nothing, for null).</summary>
        public void Select(GameObject? gameObject)
        {
            if (!Scene && Asset == null && (gameObject == null ? _objects.Count == 0 : _objects.Count == 1 && _objects[0] == gameObject)) return;
            _objects.Clear();
            if (gameObject != null) _objects.Add(gameObject);
            (Scene, Asset) = (false, null);
            Version++;
        }

        /// <summary>Selects these objects; the last is the main one.</summary>
        public void Select(IEnumerable<GameObject> objects)
        {
            List<GameObject> wanted = objects.Distinct().ToList();
            if (!Scene && Asset == null && wanted.SequenceEqual(_objects)) return;
            _objects.Clear();
            _objects.AddRange(wanted);
            (Scene, Asset) = (false, null);
            Version++;
        }

        /// <summary>Adds an object to what's selected, as the main one (Ctrl-click); it stays selected if it already was.</summary>
        public void Add(GameObject gameObject)
        {
            if (Object == gameObject && !Scene && Asset == null) return;
            if (Scene || Asset != null) _objects.Clear();
            _objects.Remove(gameObject);
            _objects.Add(gameObject);
            (Scene, Asset) = (false, null);
            Version++;
        }

        /// <summary>Ctrl-click: selects an object along with the others, or leaves it out if it was selected.</summary>
        public void Toggle(GameObject gameObject)
        {
            if (Contains(gameObject))
            {
                Remove(gameObject);
                return;
            }

            Add(gameObject);
        }

        public void Remove(GameObject gameObject)
        {
            if (_objects.Remove(gameObject)) Version++;
        }

        /// <summary>Leaves out the selected objects <paramref name="gone"/> says to: ones deleted, say.</summary>
        public void RemoveWhere(Predicate<GameObject> gone)
        {
            if (_objects.RemoveAll(gone) > 0) Version++;
        }

        public void SelectScene()
        {
            if (Scene) return;
            _objects.Clear();
            (Scene, Asset) = (true, null);
            Version++;
        }

        public void SelectAsset(AssetEntry? asset)
        {
            if (Asset == asset && _objects.Count == 0) return;
            _objects.Clear();
            (Scene, Asset) = (false, asset);
            Version++;
        }

        public void Clear() => Select((GameObject?)null);

        /// <summary>What's selected now, to put back with <see cref="Restore"/>.</summary>
        public (GameObject[] Objects, bool Scene, AssetEntry? Asset) Save() => ([.. _objects], Scene, Asset);

        /// <summary>Selects again what <see cref="Save"/> noted, leaving out objects deleted since.</summary>
        public void Restore((GameObject[] Objects, bool Scene, AssetEntry? Asset) saved)
        {
            if (saved.Scene) SelectScene();
            else if (saved.Asset != null) SelectAsset(saved.Asset);
            else Select(saved.Objects.Where(o => !o.IsDestroyed));
        }

        /// <summary>
        /// The selected objects that aren't under another selected one: what to delete, duplicate or move, since what's
        /// under an object goes with it.
        /// </summary>
        public List<GameObject> Tops()
        {
            var tops = new List<GameObject>();
            foreach (GameObject gameObject in _objects)
            {
                bool under = false;
                for (Transform? parent = gameObject.Transform.Parent; parent != null && !under; parent = parent.Parent) under = _objects.Contains(parent.GameObject);
                if (!under) tops.Add(gameObject);
            }

            return tops;
        }
    }

    /// <summary>
    /// What all the editor's panels share: the open project and the game's code, the scene being edited and where it
    /// came from, what's selected, the history of changes, play mode, and the commands in the menus.
    /// </summary>
    public sealed class EditorContext
    {
        /// <summary>The scene being edited: the one open, or the prefab whose tab is showing (see <see cref="Stage"/>).</summary>
        public Scene Scene => Vertex.Scene.Active;

        /// <summary>The scene that's open, even while a prefab's tab shows.</summary>
        public Scene MainScene => Stage != null && _mainScene != null ? _mainScene : Vertex.Scene.Active;

        public Selection Selection { get; } = new();
        /// <summary>The changes made to what's being edited: the scene's, or the prefab's whose tab is showing.</summary>
        public UndoHistory History => Stage?.History ?? _sceneHistory;

        private readonly UndoHistory _sceneHistory = new();
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
        public bool Dirty => _sceneHistory.Version != _savedVersion && !Play.Playing;

        public void MarkSaved() => _savedVersion = _sceneHistory.Version;

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

        /// <summary>Makes a new object, records it, and selects it. In a prefab's tab, it goes in the prefab (under its top object).</summary>
        public GameObject Create(string name, Action<GameObject>? build = null, Transform? parent = null)
        {
            var gameObject = new GameObject(name, Scene);
            build?.Invoke(gameObject);
            gameObject.Transform.Parent = parent ?? Stage?.Root.Transform;
            Scene.ApplyChanges();
            History.Record(new CreateObject(gameObject));
            Selection.Select(gameObject);
            return gameObject;
        }

        public void Delete(GameObject gameObject)
        {
            if (IsPrefabRoot(gameObject)) return;
            History.Do(new DeleteObject(gameObject));
            if (Selection.Contains(gameObject)) Selection.Clear();
            Notify($"Deleted {gameObject.Name}");
        }

        /// <summary>Deletes what's selected (an object, or several), as one change to undo.</summary>
        public void DeleteSelected()
        {
            List<GameObject> tops = Selection.Tops();
            if (tops.Any(IsPrefabRoot)) tops = Selection.Objects.Where(o => !IsPrefabRoot(o)).ToList();
            if (tops.Count == 0) return;
            if (tops.Count == 1)
            {
                Delete(tops[0]);
                return;
            }

            History.Do(new ChangeGroup($"Delete {tops.Count} Objects", tops.Select(o => (IUndoable)new DeleteObject(o)).ToList()));
            Selection.Clear();
            Notify($"Deleted {tops.Count} objects");
        }

        /// <summary>Whether <paramref name="gameObject"/> is the top of the prefab whose tab shows: it is the prefab, so it stays.</summary>
        private bool IsPrefabRoot(GameObject gameObject)
        {
            if (Stage?.Root != gameObject) return false;
            Notify($"{gameObject.Name} is the prefab itself, so it can't be deleted: delete the file in Assets instead");
            return true;
        }

        /// <summary>Duplicates what's selected, each copy just after its original, and selects the copies.</summary>
        public void DuplicateSelected()
        {
            List<GameObject> tops = Selection.Tops();
            if (tops.Count > 1) tops.Remove(Stage?.Root!);
            if (tops.Count == 1) Duplicate(tops[0]);
            if (tops.Count <= 1) return;

            var copies = new List<GameObject>();
            var changes = new List<IUndoable>();
            foreach (GameObject original in tops)
            {
                try
                {
                    GameObject copy = SceneFile.TakeSnapshot(Scene, [original]).Restore(Scene)[0];
                    copy.Transform.Parent = original.Transform.Parent;
                    Scene.ApplyChanges();
                    copy.Transform.SiblingIndex = original.Transform.SiblingIndex + 1;
                    copies.Add(copy);
                    changes.Add(new CreateObject(copy));
                }
                catch (Exception exception)
                {
                    Notify($"Couldn't duplicate {original.Name}: {exception.Message}");
                }
            }

            if (changes.Count == 0) return;
            History.Record(new ChangeGroup($"Duplicate {changes.Count} Objects", changes));
            Selection.Select(copies);
        }

        /// <summary>Copies an object and everything under it, just after it, and selects the copy.</summary>
        public GameObject? Duplicate(GameObject original)
        {
            if (Stage?.Root == original)
            {
                Notify("A prefab has one top object: duplicate what's under it instead");
                return null;
            }

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
            CloseAllPrefabs();
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

            // The prefab being edited is saved too (it's saved as it changes anyway), along with the scene.
            if (Stage != null) SaveOpenPrefab();

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
                SceneFile.Save(MainScene, path, AssetFolders);
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

            WatchCode();
            if (project.GameProjectFile is { } gameProject && File.Exists(gameProject))
            {
                Origin = SceneOrigin.Untitled;
                _untitledName = project.Name;
                BuildGame();
                _openingProject = true;
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

            Interlocked.Exchange(ref _codeChangedAt, 0);
            _buildStartedUtc = DateTime.UtcNow;
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
            SaveChangedGraphs();
            if (!InputSystem.GetMouseButton(MouseButton.Left)) SaveOpenPrefab();
            RebuildIfCodeChanged();
            if (_build is not { IsCompleted: true } build) return;

            _build = null;
            Busy = null;
            bool opening = _openingProject;
            _openingProject = false;
            (bool built, string output, string? assemblyPath) = build.Result;
            if (!built || assemblyPath == null)
            {
                // The project is still open (its scenes and assets work), just without the game's scripts (or with the
                // ones from before, rebuilding); the errors go to the Console, where double-clicking one opens the code at it.
                _pendingComponent = null;
                if (opening) OpenStartupScene();
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

            // Rebuilt, the scene being edited is kept (and what's selected), made again with the new code. A world the
            // game builds is built again instead.
            KeptScene? kept = !opening && Origin != SceneOrigin.GameWorld ? KeepScene() : null;

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

            if (kept != null) RestoreScene(kept);
            else if (Game.GameType != null) LoadGameWorld();
            else OpenStartupScene();
            AddPendingComponent();
            Notify(Game.GameType != null && kept == null ? $"Loaded {Game.GameType.Name}" : $"Loaded {Path.GetFileNameWithoutExtension(_buildingProject)}'s components");
        }

        /// <summary>The scene being edited, kept while the game's code is loaded again.</summary>
        private sealed record KeptScene(SceneFile.Snapshot Snapshot, string? Path, SceneOrigin Origin, string Name, bool Dirty, int[] Selected);

        private KeptScene KeepScene()
        {
            if (Play.Playing) Play.Stop();
            ShowPrefab(null);
            IReadOnlyList<GameObject> objects = Scene.GameObjects;
            int[] selected = Selection.Objects.Select(o => IndexOf(objects, o)).Where(i => i >= 0).ToArray();
            return new KeptScene(SceneFile.TakeSnapshot(Scene), ScenePath, Origin, _untitledName, Dirty, selected);
        }

        private void RestoreScene(KeptScene kept)
        {
            List<GameObject> made;
            try
            {
                made = kept.Snapshot.Restore(Scene);
            }
            catch (Exception exception)
            {
                Log.Error($"Couldn't keep the scene through the rebuild: {exception.Message}", exception);
                made = [];
            }

            Scene.ApplyChanges();
            ScenePath = kept.Path;
            Origin = kept.Origin;
            _untitledName = kept.Name;
            if (kept.Dirty) _savedVersion = -1;
            else MarkSaved();
            Selection.Select(kept.Selected.Where(i => i < made.Count).Select(i => made[i]));
        }

        private static int IndexOf(IReadOnlyList<GameObject> objects, GameObject gameObject)
        {
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] == gameObject) return i;
            }

            return -1;
        }

        // ---------------------------------------------------------------- Scripts

        /// <summary>Whether the editor's window is in front. Code changed in another program is rebuilt once it is again.</summary>
        public bool WindowFocused { get; set; } = true;

        private FileSystemWatcher? _codeWatcher;

        // When the game's code last changed (Environment.TickCount64), if it hasn't been rebuilt since; 0 if it has.
        private long _codeChangedAt;

        // A script made from Add Component: its class, added to these objects (by where they are in the scene) once it's built.
        private (string Class, int[] Objects)? _pendingComponent;
        private bool _openingProject;
        private DateTime _buildStartedUtc;

        /// <summary>Whether there are changes to the game's code waiting to be built.</summary>
        public bool CodeChanged => Interlocked.Read(ref _codeChangedAt) != 0;

        /// <summary>
        /// Makes a new C# script (a component) called <paramref name="name"/> in <paramref name="folder"/>, giving the
        /// project code of its own first if it hasn't any, and returns where. It's built once it's been named.
        /// </summary>
        public string CreateScript(string folder, string name = "NewScript")
        {
            if (Project == null) throw new InvalidOperationException("Open a project to make scripts in it");
            AddCode();

            Directory.CreateDirectory(folder);
            string className = ClassName(name);
            string path = Path.Combine(folder, className + ".cs");
            for (int i = 2; File.Exists(path) || Inspection.ComponentCatalog.All.Any(t => t.Type.Name == className); i++)
            {
                className = ClassName(name) + i;
                path = Path.Combine(folder, className + ".cs");
            }

            File.WriteAllText(path, ScriptTemplate(className));
            Assets?.Refresh();
            CodeWasChanged();
            return path;
        }

        /// <summary>
        /// Makes a new script called <paramref name="name"/> in the project's Scripts folder, builds the game, and adds
        /// the new component to <paramref name="objects"/> once it's built: Add Component's New Script.
        /// </summary>
        public string? CreateScriptFor(IReadOnlyList<GameObject> objects, string name)
        {
            if (Project is not { } project) return null;

            string path;
            try
            {
                path = CreateScript(Path.Combine(project.AssetsDirectory, "Scripts"), name);
            }
            catch (Exception exception)
            {
                Notify($"Couldn't make the script: {exception.Message}");
                return null;
            }

            IReadOnlyList<GameObject> all = Scene.GameObjects;
            _pendingComponent = (Path.GetFileNameWithoutExtension(path), objects.Select(o => IndexOf(all, o)).Where(i => i >= 0).ToArray());
            BuildGame();
            Notify($"Made {Path.GetFileName(path)}: it's added once the game's built");
            return path;
        }

        /// <summary>Gives the project a .csproj of its own if it has no code yet, so scripts can go in it.</summary>
        public void AddCode()
        {
            if (Project is not { } project || project.GameProjectFile is { } existing && File.Exists(existing)) return;

            string file = project.AddCode();
            WatchCode();
            Log.Info($"Set up {Path.GetFileName(file)} for the game's code: every .cs file in {project.Name}'s folder is part of it");
        }

        /// <summary>A class name made from what was typed: letters and digits, each word starting with a capital ("enemy ai" is EnemyAi).</summary>
        public static string ClassName(string name)
        {
            var built = new System.Text.StringBuilder();
            bool upper = true;
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    upper = true;
                    continue;
                }

                built.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }

            if (built.Length == 0) return "NewScript";
            if (char.IsDigit(built[0])) built.Insert(0, '_');
            return built.ToString();
        }

        public static string ScriptTemplate(string className) => $$"""
            using System.Numerics;

            using Vertex;
            using Vertex.Input;

            /// <summary>A component of the game's: add it to an object in the editor's inspector.</summary>
            public class {{className}} : Component
            {
                // Public properties and fields show in the inspector, and are saved with the scene.
                public float Speed { get; set; } = 1f;

                // Runs once, before the first Update.
                protected override void Start()
                {
                }

                // Runs every frame.
                protected override void Update()
                {
                }
            }

            """;

        /// <summary>Watches the project's folder for its code changing, to build it again.</summary>
        private void WatchCode()
        {
            _codeWatcher?.Dispose();
            _codeWatcher = null;
            if (Project?.GameProjectFile is not { } gameProject || !File.Exists(gameProject)) return;

            try
            {
                string folder = Path.GetDirectoryName(gameProject)!;
                var watcher = new FileSystemWatcher(folder, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                void Changed(string path)
                {
                    // What a build makes (in bin and obj) isn't the game's code.
                    string relative = Path.GetRelativePath(folder, path);
                    string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase)) return;
                    // Nor is a file last written before the build now going started: that build has it already.
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path) <= _buildStartedUtc) return;
                    CodeWasChanged();
                }

                watcher.Changed += (_, e) => Changed(e.FullPath);
                watcher.Created += (_, e) => Changed(e.FullPath);
                watcher.Deleted += (_, e) => Changed(e.FullPath);
                watcher.Renamed += (_, e) => Changed(e.FullPath);
                watcher.EnableRaisingEvents = true;
                _codeWatcher = watcher;
            }
            catch (Exception exception)
            {
                Log.Warning($"Couldn't watch the game's code for changes (rebuild it with Ctrl+R): {exception.Message}");
            }
        }

        private void CodeWasChanged() => Interlocked.Exchange(ref _codeChangedAt, Environment.TickCount64);

        /// <summary>
        /// Builds the game again once its code has changed: when the editor's in front again, the code has stopped
        /// changing for a moment, and nothing's being typed or played.
        /// </summary>
        private void RebuildIfCodeChanged()
        {
            long changedAt = Interlocked.Read(ref _codeChangedAt);
            if (changedAt == 0 || _build != null || !WindowFocused || Play.Playing || Ui.KeyboardTaken) return;
            if (Environment.TickCount64 - changedAt < 600) return;
            BuildGame();
        }

        private void AddPendingComponent()
        {
            if (_pendingComponent is not { } pending || Game == null) return;
            _pendingComponent = null;

            Type? type = null;
            try
            {
                type = Game.Assembly.GetTypes().FirstOrDefault(t => t.Name == pending.Class && t.IsSubclassOf(typeof(Component)));
            }
            catch (System.Reflection.ReflectionTypeLoadException exception)
            {
                type = exception.Types.FirstOrDefault(t => t != null && t.Name == pending.Class && t.IsSubclassOf(typeof(Component)));
            }

            if (type == null) return;
            IReadOnlyList<GameObject> all = Scene.GameObjects;
            var changes = new List<IUndoable>();
            foreach (int index in pending.Objects)
            {
                if (index >= all.Count) continue;
                try
                {
                    changes.Add(new AddComponent(all[index], (Component)Activator.CreateInstance(type)!));
                }
                catch (Exception exception)
                {
                    Notify($"Couldn't add {type.Name}: {(exception.InnerException ?? exception).Message}");
                }
            }

            if (changes.Count == 1) History.Do(changes[0]);
            else if (changes.Count > 1) History.Do(new ChangeGroup($"Add {type.Name}", changes));
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

        // ---------------------------------------------------------------- Graphs

        /// <summary>Raised to open a graph file (a .vscript) for editing, for the scene view to show it in a tab.</summary>
        public event Action<string>? GraphOpenRequested;

        public void OpenGraph(string path) => GraphOpenRequested?.Invoke(Path.GetFullPath(path));

        /// <summary>Makes a new visual script in <paramref name="folder"/>, to start from (something said on start), and returns where.</summary>
        public string CreateVisualScript(string folder, string name = "New Visual Script")
        {
            Directory.CreateDirectory(folder);
            string path = Panels.AssetsPanel.Unique(Path.Combine(folder, name), ".vscript");
            Vertex.Scripting.ScriptNodes.Starter().Save(path);
            Assets?.Refresh();
            return path;
        }

        /// <summary>Makes a new VFX graph in <paramref name="folder"/>, to start from (rising embers), and returns where.</summary>
        public string CreateVfxGraph(string folder, string name = "New VFX Graph")
        {
            Directory.CreateDirectory(folder);
            string path = Panels.AssetsPanel.Unique(Path.Combine(folder, name), ".vfx");
            Vertex.Effects.VfxNodes.Starter().Save(path);
            Assets?.Refresh();
            return path;
        }

        private int _graphsCheckedAt = -1;

        /// <summary>
        /// Writes graph files whose graphs have been changed (in a graph tab, or by undo), once the change is done (not in
        /// the middle of a drag): they're files of their own, kept up to date as they're edited.
        /// </summary>
        private void SaveChangedGraphs()
        {
            if (History.Version == _graphsCheckedAt || InputSystem.GetMouseButton(MouseButton.Left)) return;
            _graphsCheckedAt = History.Version;

            foreach (Vertex.Graphs.Graph graph in Vertex.Assets.LoadedGraphs.ToList())
            {
                if (graph.Source is not { } path || !File.Exists(path)) continue;
                try
                {
                    graph.SaveIfChanged();
                }
                catch (Exception exception)
                {
                    Notify($"Couldn't save {Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }

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
            if (Stage != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(Stage.Path), StringComparison.OrdinalIgnoreCase))
            {
                Notify($"{Stage.Title} can't go inside itself");
                return null;
            }

            try
            {
                Prefab prefab = Vertex.Assets.LoadPrefab(path);
                GameObject copy = prefab.Instantiate(Scene, AssetFolders);
                // In a prefab's tab, it goes in the prefab, under its top object.
                copy.Transform.Parent = parent ?? Stage?.Root.Transform;
                if (parent == null) copy.Transform.WorldPosition = position;
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

        // ---------------------------------------------------------------- Prefabs open in tabs of their own

        private readonly List<PrefabStage> _prefabs = new();
        private Scene? _mainScene;
        private List<GameObject> _sceneSelected = new();

        // Each prefab opened, and what it was when the scene's copies of it were last made: copies of one that's changed
        // since are made again when the scene shows again.
        private readonly Dictionary<Prefab, string> _copiesMadeFrom = new();

        /// <summary>The prefab whose tab is showing, edited in place of the scene; null while the scene is.</summary>
        public PrefabStage? Stage { get; private set; }

        /// <summary>The prefabs open in tabs.</summary>
        public IReadOnlyList<PrefabStage> OpenPrefabs => _prefabs;

        /// <summary>Raised when a prefab is opened in a tab of its own, for the scene view to show it.</summary>
        public event Action<PrefabStage>? PrefabOpened;

        /// <summary>Opens the prefab in <paramref name="path"/> in a tab of its own (or shows the tab it's in already).</summary>
        public PrefabStage? OpenPrefab(string path)
        {
            if (Play.Playing)
            {
                Notify("Stop playing to edit prefabs");
                return null;
            }

            path = Path.GetFullPath(path);
            PrefabStage? stage = _prefabs.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Path), path, StringComparison.OrdinalIgnoreCase));
            if (stage == null)
            {
                try
                {
                    Prefab prefab = Vertex.Assets.LoadPrefab(path);
                    stage = new PrefabStage(prefab, AssetFolders);
                    _copiesMadeFrom.TryAdd(prefab, prefab.Json);
                }
                catch (Exception exception)
                {
                    Notify($"Couldn't open {Path.GetFileName(path)}: {exception.Message}");
                    return null;
                }

                _prefabs.Add(stage);
                ShowPrefab(stage);
                PrefabOpened?.Invoke(stage);
            }
            else
            {
                ShowPrefab(stage);
            }

            return stage;
        }

        /// <summary>
        /// Shows <paramref name="stage"/>'s prefab to edit in place of the scene (the outliner, inspector and undo work on
        /// it), or the scene again for null. Each keeps what was selected in it.
        /// </summary>
        public void ShowPrefab(PrefabStage? stage)
        {
            if (Stage == stage) return;

            if (Stage is { } leaving)
            {
                leaving.Selected = Selection.Objects.ToList();
                SaveOpenPrefab();
            }
            else
            {
                _sceneSelected = Selection.Objects.ToList();
                _mainScene = Vertex.Scene.Active;
            }

            Stage = stage;
            Vertex.Scene.Active = stage?.Scene ?? _mainScene ?? Vertex.Scene.Active;
            Selection.Select((stage?.Selected ?? _sceneSelected).Where(o => !o.IsDestroyed && o.Scene == Scene));
            if (stage == null)
            {
                _mainScene = null;
                UpdatePrefabCopies();
            }
        }

        /// <summary>Closes a prefab's tab, saving it first.</summary>
        public void ClosePrefab(PrefabStage stage)
        {
            if (!_prefabs.Contains(stage)) return;
            if (Stage == stage) ShowPrefab(null);

            stage.SaveIfChanged(AssetFolders);
            _prefabs.Remove(stage);
            stage.Close();
            if (Stage == null) UpdatePrefabCopies();
        }

        private void CloseAllPrefabs()
        {
            if (Stage != null) ShowPrefab(null);
            foreach (PrefabStage stage in _prefabs)
            {
                stage.SaveIfChanged(AssetFolders);
                stage.Close();
            }

            _prefabs.Clear();
            _copiesMadeFrom.Clear();
        }

        /// <summary>Saves the prefab being edited, if it's been changed.</summary>
        private void SaveOpenPrefab()
        {
            if (Stage is not { } stage) return;
            try
            {
                if (stage.SaveIfChanged(AssetFolders)) Assets?.Refresh();
            }
            catch (Exception exception)
            {
                Notify($"Couldn't save {stage.Title}: {exception.Message}");
            }
        }

        /// <summary>
        /// Makes the scene's copies of prefabs changed in their tabs again from them (each keeping its own place, name and
        /// parent), as one change to undo.
        /// </summary>
        private void UpdatePrefabCopies()
        {
            var changes = new List<IUndoable>();
            var names = new List<string>();
            foreach ((Prefab prefab, string madeFrom) in _copiesMadeFrom.ToList())
            {
                if (prefab.Json == madeFrom) continue;

                _copiesMadeFrom[prefab] = prefab.Json;
                int before = changes.Count;
                foreach (GameObject copy in Scene.GameObjects.ToList())
                {
                    if (copy.Prefab == prefab && !copy.IsDestroyed) changes.AddRange(Remake(copy, prefab));
                }

                if (changes.Count > before) names.Add(prefab.Name);
            }

            // Prefabs no longer open have nothing more to bring over.
            foreach (Prefab prefab in _copiesMadeFrom.Keys.ToList())
            {
                if (!_prefabs.Any(p => p.Prefab == prefab)) _copiesMadeFrom.Remove(prefab);
            }

            if (changes.Count == 0) return;
            _sceneHistory.Record(new ChangeGroup($"Update {string.Join(", ", names)}", changes));
            Notify($"Updated {changes.Count / 2} cop{(changes.Count == 2 ? "y" : "ies")} of {string.Join(", ", names)} in the scene");
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
