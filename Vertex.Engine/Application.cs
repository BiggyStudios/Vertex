using System.ComponentModel;

using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

using Vertex.Engine.Core;

namespace Vertex.Engine
{
    /// <summary>
    /// Main application class
    /// </summary>
    public class Application
    {
        private static Application? _instance;
        private GameWindow? _window;
        private Scene? _activeScene;
        private bool _isRunning;

        /// <summary>
        /// Gets the instance of the application.
        /// </summary>
        public static Application Instance
        {
            get
            {
                _instance ??= new Application();
                return _instance;
            }
        }

        /// <summary>
        /// Gets the currently active scene.
        /// </summary>
        public Scene? ActiveScene => _activeScene;
        /// <summary>
        /// Gets the window instance.
        /// </summary>
        public GameWindow? Window => _window;

        //Event triggered when the application initializes.
        public event Action? OnInitialize;
        //Event triggered during the update loop.
        public event Action<double>? OnUpdate;
        //Event triggered during the render loop
        public event Action<double>? OnRender;
        //Event triggerd when the application is shutting down.
        public event Action? OnShutdown;

        /// <summary>
        /// Initializes the application with the specified window settings.
        /// </summary>
        /// <param name="gameWindowSettings">Game window settings.</param>
        /// <param name="nativeWindowSettings">Native window settings.</param>
        /// <exception cref="InvalidOperationException">Thrown if application is already running</exception>
        public void Initialize(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        {
            if (_window !=  null)
                throw new InvalidOperationException("Application already running!!!");

            _window = new GameWindow(gameWindowSettings, nativeWindowSettings);
            _window.CursorState = CursorState.Grabbed;
            _activeScene = new Scene("Scene");

            _window.Load += OnLoad;
            _window.UpdateFrame += OnUpdateFrame;
            _window.RenderFrame += OnRenderFrame;
            _window.Resize += OnResize;
            _window.Closing += OnClosing;

            OnInitialize?.Invoke();
        }

        /// <summary>
        /// Called when the application loads.
        /// </summary>
        public void OnLoad()
        {
            GL.ClearColor(0.0f, 0.3f, 0.3f, 1.0f);
            GL.Enable(EnableCap.DepthTest);

            if (_window != null)
            {
                GL.Viewport(0, 0, _window.Size.X, _window.Size.Y);
                var camera = _activeScene?.GetMainCamera();
                if (camera != null)
                {
                    camera.AspectRatio = _window.Size.X / (float)_window.Size.Y;
                }
            }
        }

        /// <summary>
        /// Starts running the application.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if application already running</exception>
        public void Run()
        {
            if (_window == null)
                throw new InvalidOperationException("Application already running!!!");

            _isRunning = true;
            _window.Run();
        }

        /// <summary>
        /// Stops the application and closes the window.
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _window?.Close();
        }

        /// <summary>
        /// Sets the active scene.
        /// </summary>
        /// <param name="scene">The scene to set as active.</param>
        public void SetActiveScene(Scene scene)
        {
            _activeScene = scene;
        }

        //Called when frame is updated
        private void OnUpdateFrame(FrameEventArgs args)
        {
            if (!_isRunning) return;
            
            _activeScene?.Update(args.Time);
            OnUpdate?.Invoke(args.Time);
        }

        //Called when frame is rendered
        private void OnRenderFrame(FrameEventArgs args)
        {
            if (!_isRunning) return;

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            _activeScene?.Render();
            OnRender?.Invoke(args.Time);
            _window?.SwapBuffers();
        }

        //Handles resize events
        private void OnResize(ResizeEventArgs args)
        {
            if (_window != null)
            {
                GL.Viewport(0, 0, _window.Size.X, _window.Size.Y);
                var camera = _activeScene?.GetMainCamera();
                if (camera != null)
                {
                    camera.AspectRatio = _window.Size.X / (float)_window.Size.Y;
                }
            }
        }

        //Called when window closes.
        private void OnClosing(CancelEventArgs args)
        {
            OnShutdown?.Invoke();
        }
    }
}