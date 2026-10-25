using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Audio;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Rendering;
using Vertex.Rendering.OpenGL;
using Vertex.UI;

namespace Vertex
{
    /// <summary>
    /// Creates the window and runs the game loop: receiving network messages, fixed-rate physics steps,
    /// <see cref="Game"/> and <see cref="Scene"/> updates, then rendering through <see cref="Renderer"/>.
    /// </summary>
    public class VertexWindow
    {
        // Caps how far physics can fall behind, so one slow frame doesn't make every frame after it slower too.
        private const float _maxFixedTimePerFrame = 0.25f;

        private readonly IWindow _window;
        private readonly Game _game;
        private float _fixedTimeAccumulator;

        public Renderer Renderer { get; }

        public int Width => _window.FramebufferSize.X;
        public int Height => _window.FramebufferSize.Y;

        /// <param name="backend">The graphics API backend to render with. Defaults to OpenGL.</param>
        public VertexWindow(int sizeX, int sizeY, Game game, string title = "Window", IRenderBackend? backend = null)
        {
            _game = game;
            Renderer = new Renderer(backend ?? new GLRenderBackend());

            WindowOptions options = Renderer.ConfigureWindow(WindowOptions.Default with
            {
                Size = new Vector2D<int>(sizeX, sizeY),
                Title = title
            });

            _window = Window.Create(options);

            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += OnRender;
            _window.FramebufferResize += OnResize;
            _window.Closing += OnClosing;
        }

        public void Run()
        {
            _window.Run();
        }

        public void Close()
        {
            _window.Close();
        }

        private void OnLoad()
        {
            Renderer.Initialise(_window);

            IInputContext input = _window.CreateInput();
            InputSystem.Initialise(input);
            Sound.Initialise();
            DevConsole.Initialise();
            DevConsole.AddCommand("quit", "Closes the game.", _ => Close());

            _game.Load();
        }

        private void OnUpdate(double deltaTime)
        {
            Time.Advance(deltaTime);
            Canvas.BeginFrame(Renderer.Width, Renderer.Height, _window.Size.Y);

            // The console goes first, so while it's open it takes the keyboard before the game sees it.
            DevConsole.Update();

            // Receive first, so this frame's game code sees the latest messages.
            Network.Update();

            _fixedTimeAccumulator = Math.Min(_fixedTimeAccumulator + Time.DeltaTime, _maxFixedTimePerFrame);
            if (_fixedTimeAccumulator >= Time.FixedDeltaTime) Scene.Active.BeginFixedSteps();

            while (_fixedTimeAccumulator >= Time.FixedDeltaTime)
            {
                Scene.Active.FixedUpdate(Time.FixedDeltaTime);
                _fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            // The time left over is how far this frame is between the last physics step and the next one.
            Scene.Active.InterpolatePhysics(_fixedTimeAccumulator / Time.FixedDeltaTime);

            _game.Update();
            Scene.Active.Update();

            // Over the top of the game's own UI.
            DevConsole.Draw();

            // After everything has moved, so sounds come from where things are drawn.
            Sound.Update();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        private void OnRender(double deltaTime)
        {
            Renderer.Render(Scene.Active);
        }

        private void OnResize(Vector2D<int> size)
        {
            Renderer.Resize(size.X, size.Y);
        }

        private void OnClosing()
        {
            Network.Stop();
            Sound.Shutdown();
            Renderer.Dispose();
            Scene.Active.Shutdown();
        }
    }
}
