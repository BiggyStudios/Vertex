using System.Numerics;
using System.Runtime.InteropServices;

using Silk.NET.GLFW;

using CursorShape = Vertex.Input.CursorShape;
using MouseButton = Vertex.Input.MouseButton;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Vertex.Audio;
using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Platform;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor
{
    /// <summary>
    /// The editor's window: the engine's renderer and input, like a game's window, but driving the editor. It has no
    /// border of the system's: the editor draws its own title bar, so this moves, maximises and resizes the window for it.
    /// </summary>
    internal sealed class EditorWindow : IWindowControls
    {
        private readonly IWindow _window;
        private readonly Renderer _renderer;
        private readonly Preferences _preferences = Preferences.Load();
        private EditorApp _app = null!;

        // Moving and resizing by hand, with the cursor in screen coordinates.
        private bool _moving;
        private Vector2D<int> _moveFromCursor, _moveFromWindow;
        private int _resizeEdges;
        private Vector2D<int> _resizeFromCursor, _resizeFromPosition, _resizeFromSize;

        // Where the window was before it was maximised, to go back to.
        private (Vector2D<int> Position, Vector2D<int> Size)? _restore;
        private bool _closeConfirmed;

        private const int _edge = 6;
        private int _screenCheck;

        public EditorWindow()
        {
            IRenderBackend backend = RenderBackends.CreateDefault();

            // A steady frame rate matters more in the editor than the last refresh of input lag: one finished frame may
            // wait to be shown, so a frame that runs a little long doesn't halve the frame rate on two-GPU laptops.
            if (backend is Rendering.Vulkan.VulkanRenderBackend vulkan) vulkan.MaxQueuedFrames = 1;
            _renderer = new Renderer(backend);

            WindowOptions options = _renderer.ConfigureWindow(WindowOptions.Default with
            {
                Size = new Vector2D<int>(1600, 900),
                Title = "Vertex Editor",
                WindowBorder = WindowBorder.Hidden
            });

            _window = Window.Create(options);
            _window.Load += OnLoad;
            _window.Update += OnUpdate;
            _window.Render += _ =>
            {
                // Nothing to draw into while minimised.
                if (_renderer.Width > 0 && _renderer.Height > 0 && _window.WindowState != WindowState.Minimized) _renderer.Render(Scene.Active, screenCamera: null, _app.Views);
            };
            _window.FramebufferResize += size => _renderer.Resize(size.X, size.Y);
            _window.FocusChanged += focused =>
            {
                if (!focused) InputSystem.ReleaseAll();
                _app.Context.WindowFocused = focused;
            };
            _window.Closing += OnClosing;
        }

        public void Run() => _window.Run();

        private void OnLoad()
        {
            _renderer.Initialise(_window);
            AllowResizing();
            _ = new SilkInput(_window);
            Sound.Initialise();
            Theme.LoadFonts();

            _app = new EditorApp(_renderer, this, _preferences) { DisplayScale = DisplayScale() };
            Maximize();

            string? last = _preferences.RecentProjects.FirstOrDefault();
            if (_preferences.OpenLastProject && last != null && File.Exists(last)) _app.Context.OpenProject(last);
            else
            {
                StarterScene.Build(_app.Context);
                _app.Context.Scene.ApplyChanges();
                _app.Context.History.Clear();
                _app.Context.MarkSaved();
            }
        }

        private void OnUpdate(double deltaTime)
        {
            if (_window.WindowState == WindowState.Minimized || _renderer.Width <= 0 || _renderer.Height <= 0)
            {
                InputSystem.EndFrame();
                return;
            }

            // The screen the window's on, now and then: it may have been moved to another.
            if (_screenCheck-- <= 0)
            {
                _screenCheck = 30;
                _app.DisplayScale = DisplayScale();
                _app.ScreenHeight = ScreenHeight();
            }

            Time.Advance(deltaTime);
            Canvas.BeginFrame(_renderer.Width, _renderer.Height, _window.Size.Y);

            // The window's edges come before the editor: dragging one resizes the window, and the editor leaves the mouse alone.
            bool resizing = HandleEdges();
            int edges = _resizeEdges != 0 ? _resizeEdges : _moving ? 0 : EdgesUnderCursor();
            _app.InputBlocked = resizing;
            _app.CursorOverride = edges != 0 ? CursorFor(edges) : null;
            _app.Update((float)deltaTime);
            UpdateMove();

            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        // ---------------------------------------------------------------- The title bar's buttons

        public bool Maximized => _restore != null || _window.WindowState == WindowState.Maximized;

        public void Minimize() => _window.WindowState = WindowState.Minimized;

        public void ToggleMaximize()
        {
            if (Maximized) Restore();
            else Maximize();
        }

        public void Close()
        {
            _closeConfirmed = true;
            _window.Close();
        }

        public void BeginMove()
        {
            _moving = true;
            _moveFromCursor = Cursor();
            _moveFromWindow = _window.Position;
        }

        private void UpdateMove()
        {
            if (!_moving) return;
            if (!InputSystem.GetMouseButton(MouseButton.Left))
            {
                _moving = false;
                return;
            }

            Vector2D<int> cursor = Cursor();
            if (cursor == _moveFromCursor) return;

            // Dragging a maximised window brings it back to its own size, under the cursor where it was grabbed.
            if (Maximized && _restore is { } restore)
            {
                float across = (_moveFromCursor.X - _window.Position.X) / (float)Math.Max(_window.Size.X, 1);
                _restore = null;
                _window.Size = restore.Size;
                _moveFromWindow = new Vector2D<int>(cursor.X - (int)(restore.Size.X * across), _window.Position.Y);
                _moveFromCursor = cursor;
            }

            _window.Position = _moveFromWindow + (cursor - _moveFromCursor);
        }

        private void Maximize()
        {
            if (WorkArea() is { } area)
            {
                _restore ??= (_window.Position, _window.Size);
                _window.Position = area.Position;
                _window.Size = area.Size;
            }
            else
            {
                _window.WindowState = WindowState.Maximized;
            }
        }

        private void Restore()
        {
            if (_restore is { } restore)
            {
                _restore = null;
                _window.Position = restore.Position;
                _window.Size = restore.Size;

                // Restoring something that started maximised: a sensible size in the middle.
                if (WorkArea() is { } area && restore.Size.X >= area.Size.X - 4)
                {
                    _window.Size = new Vector2D<int>(area.Size.X * 3 / 4, area.Size.Y * 3 / 4);
                    _window.Position = area.Position + (area.Size - _window.Size) / 2;
                }
            }
            else
            {
                _window.WindowState = WindowState.Normal;
            }
        }

        // ---------------------------------------------------------------- Resizing by the edges

        /// <summary>Resizes the window while an edge is dragged. True while it is, so the editor leaves the mouse alone.</summary>
        private bool HandleEdges()
        {
            if (_resizeEdges != 0)
            {
                if (!InputSystem.GetMouseButton(MouseButton.Left))
                {
                    _resizeEdges = 0;
                    return false;
                }

                Vector2D<int> moved = Cursor() - _resizeFromCursor;
                Vector2D<int> position = _resizeFromPosition, size = _resizeFromSize;
                const int minimum = 640;
                if ((_resizeEdges & 1) != 0) { int width = Math.Max(size.X - moved.X, minimum); position.X += size.X - width; size.X = width; }
                if ((_resizeEdges & 2) != 0) size.X = Math.Max(size.X + moved.X, minimum);
                if ((_resizeEdges & 4) != 0) { int height = Math.Max(size.Y - moved.Y, 400); position.Y += size.Y - height; size.Y = height; }
                if ((_resizeEdges & 8) != 0) size.Y = Math.Max(size.Y + moved.Y, 400);
                _window.Position = position;
                _window.Size = size;
                return true;
            }

            int edges = EdgesUnderCursor();
            if (edges == 0 || !InputSystem.GetMouseButtonDown(MouseButton.Left)) return false;

            _resizeEdges = edges;
            _resizeFromCursor = Cursor();
            _resizeFromPosition = _window.Position;
            _resizeFromSize = _window.Size;
            return true;
        }

        /// <summary>Which edges the cursor is on: 1 left, 2 right, 4 top, 8 bottom. None while maximised.</summary>
        private int EdgesUnderCursor()
        {
            if (Maximized) return 0;
            Vector2 mouse = InputSystem.MousePosition;
            Vector2D<int> size = _window.Size;
            int edges = 0;
            if (mouse.X < _edge) edges |= 1;
            if (mouse.X >= size.X - _edge) edges |= 2;
            if (mouse.Y < _edge) edges |= 4;
            if (mouse.Y >= size.Y - _edge) edges |= 8;
            return edges;
        }

        private static CursorShape CursorFor(int edges) => (edges & 3) != 0 && (edges & 12) == 0 ? CursorShape.ResizeHorizontal
            : (edges & 12) != 0 && (edges & 3) == 0 ? CursorShape.ResizeVertical
            : CursorShape.Crosshair;

        // ---------------------------------------------------------------- The system

        /// <summary>The cursor in screen coordinates: where the window is plus where the cursor is in it.</summary>
        private Vector2D<int> Cursor()
        {
            if (OperatingSystem.IsWindows() && GetCursorPos(out Point point)) return new Vector2D<int>(point.X, point.Y);
            Vector2 mouse = InputSystem.MousePosition;
            return _window.Position + new Vector2D<int>((int)mouse.X, (int)mouse.Y);
        }

        /// <summary>The part of the screen the window is on that isn't the taskbar: where a maximised window goes.</summary>
        private (Vector2D<int> Position, Vector2D<int> Size)? WorkArea()
        {
            if (!OperatingSystem.IsWindows() || _window.Native?.Win32 is not { } win32) return null;

            IntPtr monitor = MonitorFromWindow(win32.Hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return null;

            RectangleInt work = info.Work;
            return (new Vector2D<int>(work.Left, work.Top), new Vector2D<int>(work.Right - work.Left, work.Bottom - work.Top));
        }

        /// <summary>
        /// Silk.NET makes a window without a border fixed-size too. On Windows that changes nothing (the editor resizes it
        /// itself), but on Linux a fixed-size window reads as a dialog, which tiling window managers (Hyprland, Sway, i3)
        /// float instead of tiling, and which can't be maximised. So it's made resizable again.
        /// </summary>
        private unsafe void AllowResizing()
        {
            if (OperatingSystem.IsWindows()) return;

            try
            {
                if (_window.Native?.Glfw is nint handle)
                {
                    GlfwProvider.GLFW.Value.SetWindowAttrib((WindowHandle*)handle, WindowAttributeSetter.Resizable, true);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Vertex] Couldn't make the window resizable: {exception.Message}");
            }
        }

        /// <summary>How many pixels tall the screen the window is on is; 0 if it can't be found out.</summary>
        private float ScreenHeight()
        {
            try
            {
                if (OperatingSystem.IsWindows() && _window.Native?.Win32 is { } win32)
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(MonitorFromWindow(win32.Hwnd, 2), ref info)) return info.Monitor.Bottom - info.Monitor.Top;
                }

                return _window.Monitor?.VideoMode.Resolution?.Y ?? 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
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
            // Closed some other way (Alt+F4) with changes unsaved: ask first.
            if (!_closeConfirmed && _app.Context.Dirty)
            {
                _window.IsClosing = false;
                _app.RequestExit();
                return;
            }

            _preferences.Save();
            if (_app.Context.Play.Playing) _app.Context.Play.Stop();
            Scene.Active.Discard();
            Sound.Shutdown();
            _renderer.Dispose();
            InputSystem.SetPlatform(null);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RectangleInt
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public RectangleInt Monitor;
            public RectangleInt Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
