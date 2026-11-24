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
            : this(game, new PlayerSettings { Name = title, Width = sizeX, Height = sizeY }, backend)
        {
        }

        // The picture for the window's icon, from the settings, set once it's open.
        private readonly string _icon;

        /// <summary>A window for <paramref name="game"/> as its <paramref name="settings"/> say: its title, size, mode and icon.</summary>
        public VertexWindow(Game game, PlayerSettings settings, IRenderBackend? backend = null)
        {
            Terminal.Attach();
            _loop = new GameLoop(game);
            Renderer = new Renderer(backend ?? RenderBackends.Create(settings.Renderer));
            _icon = settings.Icon;

            WindowOptions options = Renderer.ConfigureWindow(WindowOptions.Default with
            {
                Size = new Vector2D<int>(Math.Max(settings.Width, 160), Math.Max(settings.Height, 120)),
                Title = settings.Name,
                WindowBorder = settings.Resizable ? WindowBorder.Resizable : WindowBorder.Fixed,
                WindowState = settings.WindowMode == WindowMode.Maximized ? WindowState.Maximized : WindowState.Normal
            });

            if (settings.WindowMode == WindowMode.Fullscreen) Screen.Fullscreen = true;
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
            SetIcon();
            Screen.Initialise(_window);
            _ = new SilkInput(_window);
            Sound.Initialise();
            Application.QuitRequested += Close;

            DevConsole.AddCommand("r_backend", "Says which graphics API is rendering. Start with -opengl or -vulkan to choose.",
                _ => DevConsole.Print(Renderer.Backend is VulkanRenderBackend vulkan ? $"Vulkan on {vulkan.DeviceName}" : Renderer.Backend.Name));

            _stats = new RenderStatsOverlay(Renderer);
            _loop.DrawingOverlays += _stats.Update;
            DevConsole.AddVariable("r_showstats", "Shows the frame rate, what drawing takes on the CPU and GPU (by pass), and how many draws. 1 on, 0 off.",
                () => _stats.Visible ? 1f : 0f, value => _stats.Visible = value >= 0.5f);
            DevConsole.AddVariable("r_shadowresolution", "How detailed the sun's shadows are: 2048 normally, 1024 for faster drawing on integrated graphics (from 256 to 4096).",
                () => Renderer.ShadowResolution, value => Renderer.ShadowResolution = Math.Clamp((int)value, 256, 4096));

            // A debug build's console opens whether or not the game gives it a font (its own Load can still change it).
            if (Application.IsDebugBuild)
            {
                DevConsole.Font ??= Font.Default;
                Log.Info("Debug build: F3 shows the frame rate and frame times; the key left of 1 opens the console.");
            }

            _loop.Load();
        }

        /// <summary>Gives the window the game's icon, if it has one (macOS shows the app's own instead).</summary>
        private void SetIcon()
        {
            if (string.IsNullOrEmpty(_icon)) return;
            string path = Application.AssetPath(_icon);
            if (!File.Exists(path))
            {
                Log.Warning($"The game's icon isn't there: {path}");
                return;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                StbImageSharp.ImageResult image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var icon = new Silk.NET.Core.RawImage(image.Width, image.Height, image.Data);
                _window.SetWindowIcon(ref icon);
            }
            catch (Exception exception)
            {
                Log.Warning($"Couldn't set the window's icon: {exception.Message}");
            }
        }

        private void OnUpdate(double deltaTime)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            _loop.Update(deltaTime, Renderer.Width, Renderer.Height, _window.Size.Y);
            _stats.UpdateMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        private void OnRender(double deltaTime)
        {
            if (Renderer.VSync != Screen.VSync) Renderer.VSync = Screen.VSync;
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
