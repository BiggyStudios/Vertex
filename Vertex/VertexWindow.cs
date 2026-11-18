using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Audio;
using Vertex.Input;
using Vertex.Platform;
using Vertex.Rendering;
using Vertex.Rendering.Vulkan;
using Vertex.UI;

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

        private RenderStatsOverlay _stats = null!;

        /// <summary>The numbers <c>r_showstats</c> last showed, as text (empty while it's off), for logging.</summary>
        public string StatsSummary => _stats?.LastSummary ?? "";

        public int Width => _window.FramebufferSize.X;
        public int Height => _window.FramebufferSize.Y;

        /// <param name="backend">The graphics API backend to render with. Defaults to <see cref="RenderBackends.CreateDefault"/>: Vulkan where it can, otherwise OpenGL.</param>
        public VertexWindow(int sizeX, int sizeY, Game game, string title = "Window", IRenderBackend? backend = null)
        {
            Terminal.Attach();
            _loop = new GameLoop(game);
            Renderer = new Renderer(backend ?? RenderBackends.CreateDefault());

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

            DevConsole.AddCommand("r_backend", "Says which graphics API is rendering. Start with -opengl or -vulkan to choose.",
                _ => DevConsole.Print(Renderer.Backend is VulkanRenderBackend vulkan ? $"Vulkan on {vulkan.DeviceName}" : Renderer.Backend.Name));

            _stats = new RenderStatsOverlay(Renderer);
            _loop.DrawingOverlays += _stats.Update;
            DevConsole.AddVariable("r_showstats", "Shows the frame rate, what drawing takes on the CPU and GPU (by pass), and how many draws. 1 on, 0 off.",
                () => _stats.Visible ? 1f : 0f,
                value =>
                {
                    _stats.Visible = value >= 0.5f;
                    Renderer.MeasureGpuTime = _stats.Visible;
                });
            DevConsole.AddVariable("r_shadowresolution", "How detailed the sun's shadows are: 2048 normally, 1024 for faster drawing on integrated graphics (from 256 to 4096).",
                () => Renderer.ShadowResolution, value => Renderer.ShadowResolution = Math.Clamp((int)value, 256, 4096));

            _loop.Load();
        }

        private void OnUpdate(double deltaTime)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _loop.Update(deltaTime, Renderer.Width, Renderer.Height, _window.Size.Y);
            _stats.UpdateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
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
