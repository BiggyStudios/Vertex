using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Platform;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor
{
    /// <summary>
    /// The editor's window: the engine's renderer and input, like a game's window, but driving the editor instead of a
    /// game. The scene isn't run, only drawn, so nothing moves until play is pressed (once there is one).
    /// </summary>
    internal sealed class EditorWindow
    {
        private readonly IWindow _window;
        private readonly Renderer _renderer;
        private EditorApp _app = null!;
        private readonly Action<EditorContext>? _buildScene;

        public EditorWindow(Action<EditorContext>? buildScene = null)
        {
            _buildScene = buildScene;
            _renderer = new Renderer(RenderBackends.CreateDefault());

            WindowOptions options = _renderer.ConfigureWindow(WindowOptions.Default with
            {
                Size = new Vector2D<int>(1600, 900),
                Title = "Vertex Editor",
                WindowState = WindowState.Maximized
            });

            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += _ => _renderer.Render(Scene.Active, screenCamera: null, _app.Views);
            _window.FramebufferResize += size => _renderer.Resize(size.X, size.Y);
            _window.FocusChanged += focused =>
            {
                if (!focused) InputSystem.ReleaseAll();
            };
            _window.Closing += OnClosing;
        }

        public void Run() => _window.Run();

        private void OnLoad()
        {
            _renderer.Initialise(_window);
            _ = new SilkInput(_window);
            Theme.LoadFonts();

            // Sized for the screen's display scale (125%, 150%...), not the window's height as a game's UI is.
            Canvas.FixedScale = DisplayScale();

            _app = new EditorApp(_renderer);
            if (_buildScene != null) _buildScene(_app.Context);
            else StarterScene.Build(_app.Context);

            _app.Context.Scene.ApplyChanges();
            _app.Context.History.Clear();
        }

        private void OnUpdate(double deltaTime)
        {
            Time.Advance(deltaTime);
            Canvas.BeginFrame(_renderer.Width, _renderer.Height, _window.Size.Y);
            _app.Update((float)deltaTime);
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        private unsafe float DisplayScale()
        {
            try
            {
                if (_window.Native?.Glfw is nint handle)
                {
                    // Not in Silk's bindings, so called straight from GLFW.
                    nint function = GlfwProvider.GLFW.Value.Context.GetProcAddress("glfwGetWindowContentScale");
                    if (function != 0)
                    {
                        float x, y;
                        ((delegate* unmanaged[Cdecl]<nint, float*, float*, void>)function)(handle, &x, &y);
                        if (y > 0.5f) return y;
                    }
                }
            }
            catch (Exception)
            {
                // No GLFW: assume an ordinary screen.
            }

            return 1f;
        }

        private void OnClosing()
        {
            Scene.Active.Unload();
            _renderer.Dispose();
            InputSystem.SetPlatform(null);
        }
    }
}
