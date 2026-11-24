using Vertex.Input;
using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Editor.Editing
{
    /// <summary>
    /// Running the game inside the editor. Play starts it from the scene being edited, and Stop puts the scene back as
    /// it was: anything changed while playing is let go, as in other editors.
    /// <list type="bullet">
    /// <item>A world the game's code built (see <see cref="SceneOrigin.GameWorld"/>) runs with the game itself, and is
    /// built again by the game's code on Stop.</item>
    /// <item>Any other scene runs on its own (its components' Start and Update, and physics), from a copy taken on Play
    /// and put back on Stop.</item>
    /// </list>
    /// While playing, the game has the keyboard and mouse once its view is clicked; Shift+F1 or clicking the editor
    /// outside the view gives them back. Escape, while the game has locked the cursor, only shows it (see
    /// <see cref="Panels.SceneViewPanel.RouteInput"/>).
    /// </summary>
    public sealed class PlayMode
    {
        private readonly EditorContext _context;
        private GameLoop? _loop;
        private SceneFile.Snapshot? _snapshot;
        private bool _rebuild;
        private List<int> _selectedIndices = [];
        private bool _wasDirty;

        public bool Playing => _loop != null;

        /// <summary>Stops time while playing: nothing updates until unpaused.</summary>
        public bool Paused { get; set; }

        /// <summary>Whether the game has the keyboard and mouse, rather than the editor.</summary>
        public bool GameFocused { get; set; }

        /// <summary>
        /// The game fills the whole screen, with none of the editor showing: from <see cref="Preferences.FullscreenOnPlay"/>
        /// as play starts, and F11 while playing. Stopping puts the editor back.
        /// </summary>
        public bool Fullscreen { get; set; }

        public PlayMode(EditorContext context)
        {
            _context = context;
        }

        /// <summary>A game with no code of its own: playing a scene just runs its components.</summary>
        private sealed class SceneOnlyGame : Game;

        public void Start()
        {
            if (Playing || _context.Busy != null) return;

            // The scene plays as it is saved: not posed by an animation preview.
            _context.SettleScene();

            // The scene plays, not a prefab being edited.
            _context.ShowPrefab(null);

            Scene scene = _context.Scene;
            scene.ApplyChanges();
            List<GameObject> all = scene.GameObjects.ToList();
            _selectedIndices = _context.Selection.Objects.Select(o => all.IndexOf(o)).Where(i => i >= 0).ToList();
            _wasDirty = _context.Dirty;

            // Graphs light up afresh as this run goes; what went wrong last time is forgotten. Not shader graphs, which
            // don't run: what's wrong with one is what's wrong with how it's made, until it's put right.
            foreach (Vertex.Graphs.Graph graph in Vertex.Assets.LoadedGraphs.ToList())
            {
                if (graph.Kind != Vertex.ShaderGraphs.ShaderNodes.Kind) graph.ClearActivity();
            }

            Game game;
            Game? startingGame = null;
            if (_context.Origin == SceneOrigin.GameWorld && _context.WorldGame is { } worldGame)
            {
                game = worldGame;
                _rebuild = true;
                _snapshot = null;
            }
            else
            {
                // A game that starts in a scene plays the open one with its code around it: its Load runs, and gets this
                // scene where it opens its start scene. (A game that builds its world in code has nothing to add to it.)
                startingGame = _context.Game?.CreateGame() is { StartScene.Length: > 0 } withScene ? withScene : null;
                game = startingGame ?? new SceneOnlyGame();
                _rebuild = false;
                _snapshot = SceneFile.TakeSnapshot(scene);
            }

            if (_context.Project is { } project) Time.FixedDeltaTime = 1f / Math.Clamp(project.Settings.PhysicsRate, 10, 240);

            if (_context.Preferences.ClearConsoleOnPlay) _context.ConsoleLog.Clear();
            GameLoop.Start();
            if (startingGame != null) LoadAround(startingGame, scene);
            _loop = new GameLoop(game);
            Application.QuitRequested += Stop;
            Paused = false;
            GameFocused = true;
            Fullscreen = _context.Preferences.FullscreenOnPlay;
            _context.Notify(Fullscreen ? "Playing fullscreen: F11 shows the editor, Ctrl+P stops" : "Playing: Shift+F1 frees the mouse");
        }

        /// <summary>
        /// Runs the game's Load for playing <paramref name="open"/>: where it opens its start scene (or opens the open
        /// scene's own file), it gets the scene being edited instead, as it is now. Anything else it opens, it opens.
        /// </summary>
        private void LoadAround(Game game, Scene open)
        {
            string? openFile = _context.ScenePath is { } path ? Path.GetFullPath(path) : null;
            string? startFile = game.StartScene is { Length: > 0 } start ? Vertex.Assets.FindFile(start) : null;
            Scene.Opening = asked =>
            {
                string? file = Vertex.Assets.FindFile(asked);
                if (file == null) return null;
                file = Path.GetFullPath(file);
                bool isOpen = string.Equals(file, openFile, StringComparison.OrdinalIgnoreCase);
                bool isStart = startFile != null && string.Equals(file, Path.GetFullPath(startFile), StringComparison.OrdinalIgnoreCase);
                return isOpen || isStart ? open : null;
            };

            try
            {
                game.Load();
            }
            catch (Exception exception)
            {
                Log.Error($"The game's Load failed: {exception.GetType().Name}: {exception.Message}", exception);
            }
            finally
            {
                Scene.Opening = null;
            }
        }

        /// <summary>Runs one frame of the game, unless paused. Inside the game view's drawing, so the game's UI lands in it.</summary>
        internal void Step()
        {
            if (_loop == null || Paused) return;

            try
            {
                _loop.Step();
            }
            catch (Exception exception)
            {
                Log.Error($"The game stopped: {exception.GetType().Name}: {exception.Message}", exception);
                Stop();
                _context.ShowConsole();
                _context.Notify("The game stopped with an error: it's in the Console");
            }
        }

        public void Stop()
        {
            if (_loop == null) return;

            Application.QuitRequested -= Stop;
            GameLoop loop = _loop;
            _loop = null;

            try
            {
                loop.Shutdown();
            }
            catch (Exception)
            {
                // Whatever the game left half done is thrown away below anyway.
            }

            // What the game was playing stops with it: a looping sound would otherwise go on in the editor.
            Vertex.Audio.Sound.StopAll();
            InputSystem.CursorLocked = false;
            InputSystem.Muted = false;
            GameFocused = false;
            Paused = false;
            Fullscreen = false;
            Time.FixedDeltaTime = 1f / 60f;
            // A game stopped in the middle of a slow-motion moment mustn't leave the next play slowed.
            Time.Scale = 1f;
            // Nor a scene it asked for, as it stopped, open afterwards.
            Scene.Pending = null;

            if (_rebuild)
            {
                _context.LoadGameWorld();
            }
            else
            {
                Scene.Active = new Scene();
                Camera.Main = null;
                _snapshot?.Restore(Scene.Active);
                Scene.Active.ApplyChanges();
                _context.History.Clear();
                if (!_wasDirty) _context.MarkSaved();
            }

            _snapshot = null;
            IReadOnlyList<GameObject> objects = _context.Scene.GameObjects;
            _context.Selection.Select(_selectedIndices.Where(i => i < objects.Count).Select(i => objects[i]).ToList());
            _context.Notify("Stopped");
        }
    }
}
