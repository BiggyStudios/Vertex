using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Audio;
using Vertex.Input;
using Vertex.Platform;
using Vertex.Rendering;
using Vertex.Rendering.OpenGL;

namespace Vertex
{
    /// <summary>
    /// Runs a game in a window of its own: creates the window, feeds it keyboard and mouse input, runs a
    /// <see cref="GameLoop"/> every frame and renders the result through <see cref="Renderer"/>.
    /// </summary>
    public class VertexWindow
    {
        private readonly IWindow _window;
        private readonly GameLoop _loop;

        public Renderer Renderer { get; }

        public int Width => _window.FramebufferSize.X;
        public int Height => _window.FramebufferSize.Y;

        /// <param name="backend">The graphics API backend to render with. Defaults to OpenGL.</param>
        public VertexWindow(int sizeX, int sizeY, Game game, string title = "Window", IRenderBackend? backend = null)
        {
            _loop = new GameLoop(game);
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
            _window.FocusChanged += OnFocusChanged;
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
            Screen.Initialise(_window);
            _ = new SilkInput(_window);
            Sound.Initialise();
            Application.QuitRequested += Close;

            _loop.Load();
        }

        private void OnUpdate(double deltaTime)
        {
            _loop.Update(deltaTime, Renderer.Width, Renderer.Height, _window.Size.Y);
        }

        private void OnRender(double deltaTime)
        {
            Renderer.Render(Scene.Active);
        }

        private void OnResize(Vector2D<int> size)
        {
            Renderer.Resize(size.X, size.Y);
        }

        // Keys held when the window loses focus are let go of where the game can't see, so let go of them here.
        private void OnFocusChanged(bool focused)
        {
            if (!focused) InputSystem.ReleaseAll();
        }

        private void OnClosing()
        {
            Application.QuitRequested -= Close;

            // The scene first, while the renderer can still free what its objects hand back.
            _loop.Shutdown();
            Sound.Shutdown();
            Renderer.Dispose();
            InputSystem.SetPlatform(null);
        }
    }
}
