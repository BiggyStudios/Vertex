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
    /// outside the view gives them back.
    /// </summary>
    public sealed class PlayMode
    {
        private readonly EditorContext _context;
        private GameLoop? _loop;
        private SceneFile.Snapshot? _snapshot;
        private bool _rebuild;
        private int _selectedIndex;
        private bool _wasDirty;

        public bool Playing => _loop != null;

        /// <summary>Stops time while playing: nothing updates until unpaused.</summary>
        public bool Paused { get; set; }

        /// <summary>Whether the game has the keyboard and mouse, rather than the editor.</summary>
        public bool GameFocused { get; set; }

        public PlayMode(EditorContext context)
        {
            _context = context;
        }

        /// <summary>A game with no code of its own: playing a scene just runs its components.</summary>
        private sealed class SceneOnlyGame : Game;

        public void Start()
        {
            if (Playing || _context.Busy != null) return;

            Scene scene = _context.Scene;
            scene.ApplyChanges();
            _selectedIndex = _context.Selection.Object is { } selected ? scene.GameObjects.ToList().IndexOf(selected) : -1;
            _wasDirty = _context.Dirty;

            Game game;
            if (_context.Origin == SceneOrigin.GameWorld && _context.WorldGame is { } worldGame)
            {
                game = worldGame;
                _rebuild = true;
                _snapshot = null;
            }
            else
            {
                game = new SceneOnlyGame();
                _rebuild = false;
                _snapshot = SceneFile.TakeSnapshot(scene);
            }

            if (_context.Project is { } project) Time.FixedDeltaTime = 1f / Math.Clamp(project.Settings.PhysicsRate, 10, 240);

            GameLoop.Start();
            _loop = new GameLoop(game);
            Application.QuitRequested += Stop;
            Paused = false;
            GameFocused = true;
            _context.Notify("Playing: Shift+F1 frees the mouse");
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
                Stop();
                Gui.Dialog.Message("The game stopped with an error", exception.ToString());
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

            InputSystem.CursorLocked = false;
            InputSystem.Muted = false;
            GameFocused = false;
            Paused = false;
            Time.FixedDeltaTime = 1f / 60f;

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
            _context.Selection.Select(_selectedIndex >= 0 && _selectedIndex < objects.Count ? objects[_selectedIndex] : null);
            _context.Notify("Stopped");
        }
    }
}
