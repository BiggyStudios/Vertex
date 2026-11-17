using Vertex.Audio;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace Vertex
{
    /// <summary>
    /// Runs a <see cref="Game"/> one frame at a time: network messages, fixed-rate physics steps, then the game's and
    /// the scene's updates. It knows nothing about windows or graphics: <see cref="VertexWindow"/> calls it every frame
    /// and then renders, and an editor's play mode or a test can drive it the same way.
    /// </summary>
    public sealed class GameLoop
    {
        // Caps how far physics can fall behind, so one slow frame doesn't make every frame after it slower too.
        private const float _maxFixedTimePerFrame = 0.25f;

        private readonly Game _game;
        private float _fixedTimeAccumulator;

        /// <summary>Raised each frame once the game has drawn its UI, before the console, for things drawn over the top (like <c>r_showstats</c>).</summary>
        public event Action? DrawingOverlays;

        public GameLoop(Game game)
        {
            _game = game;
        }

        /// <summary>Sets up the engine's own services, then the game's <see cref="Game.Load"/>. Call once, before the first <see cref="Update"/>.</summary>
        public void Load()
        {
            DevConsole.Initialise();
            DevConsole.AddCommand("quit", "Closes the game.", _ => Application.Quit());

            _game.Load();
        }

        /// <summary>
        /// Advances the game by <paramref name="deltaTime"/> seconds. The view is <paramref name="width"/> by
        /// <paramref name="height"/> pixels; <paramref name="windowHeight"/> is the same height in the coordinates
        /// <see cref="InputSystem.MousePosition"/> uses, which differ from pixels on high-DPI screens.
        /// </summary>
        public void Update(double deltaTime, int width, int height, int windowHeight)
        {
            Time.Advance(deltaTime);
            Canvas.BeginFrame(width, height, windowHeight);

            // The console goes first, so while it's open it takes the keyboard before the game sees it.
            DevConsole.Update();

            // Receive first, so this frame's game code sees the latest messages.
            Network.Update();

            Scene scene = Scene.Active;
            _fixedTimeAccumulator = Math.Min(_fixedTimeAccumulator + Time.DeltaTime, _maxFixedTimePerFrame);
            if (_fixedTimeAccumulator >= Time.FixedDeltaTime) scene.BeginFixedSteps();

            while (_fixedTimeAccumulator >= Time.FixedDeltaTime)
            {
                scene.FixedUpdate(Time.FixedDeltaTime);
                _fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            // The time left over is how far this frame is between the last physics step and the next one.
            scene.InterpolatePhysics(_fixedTimeAccumulator / Time.FixedDeltaTime);

            _game.Update();

            // The game may have switched scenes in its own Update: update whichever is active now.
            Scene.Active.Update();

            // Over the top of the game's own UI.
            DrawingOverlays?.Invoke();
            DevConsole.Draw();

            // After everything has moved, so sounds come from where things are drawn.
            Sound.Update();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        /// <summary>Leaves any network session and unloads the active scene. Call once, when the game stops.</summary>
        public void Shutdown()
        {
            Network.Stop();
            Scene.Active.Unload();
        }
    }
}
