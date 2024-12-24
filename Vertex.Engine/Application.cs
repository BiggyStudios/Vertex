using System.ComponentModel;

using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

using Vertex.Engine.Core;

namespace Vertex.Engine
{
    public class Application
    {
        private static Application? _instance;
        private GameWindow? _window;
        private Scene _activeScene;
        private bool _isRunning;

        public static Application Instance
        {
            get
            {
                _instance ??= new Application();
                return _instance;
            }
        }

        public Scene ActiveScene => _activeScene;
        public GameWindow? Window => _window;

        public event Action? OnInitialize;
        public event Action<double>? OnUpdate;
        public event Action<double>? OnRender;
        public event Action? OnShutdown;

        public void Initialize(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        {
            if (_window !=  null)
                throw new InvalidOperationException("Application already running!!!");

            _window = new GameWindow(gameWindowSettings, nativeWindowSettings);
            _activeScene = new Scene("Scene");

            _window.Load += OnLoad;
            _window.UpdateFrame += OnUpdateFrame;
            _window.RenderFrame += OnRenderFrame;
            _window.Resize += OnResize;
            _window.Closing += OnClosing;

            OnInitialize?.Invoke();
        }

        public void Run()
        {
            if (_window == null)
                throw new InvalidOperationException("Application already running!!!");

            _isRunning = true;
            _window.Run();
        }

        public void Stop()
        {
            _isRunning = false;
            _window?.Close();
        }

        public void SetActiveScene(Scene scene)
        {
            _activeScene = scene;
        }

        public void OnLoad()
        {
            GL.ClearColor(0.0f, 0.3f, 0.3f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
        }

        private void OnUpdateFrame(FrameEventArgs args)
        {
            if (!_isRunning) return;
            
            _activeScene.Update(args.Time);
            OnUpdate?.Invoke(args.Time);
        }

        private void OnRenderFrame(FrameEventArgs args)
        {
            if (!_isRunning) return;

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            _activeScene.Render();
            OnRender?.Invoke(args.Time);
            _window?.SwapBuffers();
        }

        private void OnResize(ResizeEventArgs args)
        {
            if (_window != null)
            {
                GL.Viewport(0, 0, _window.Size.X, _window.Size.Y);
            }
        }

        private void OnClosing(CancelEventArgs args)
        {
            OnShutdown?.Invoke();
        }
    }
}