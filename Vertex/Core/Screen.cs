using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

using Monitor = Silk.NET.GLFW.Monitor;

namespace Vertex
{
    /// <summary>
    /// The game's window as the game sees it. Fullscreen is borderless: the window loses its frame and covers the
    /// whole monitor it's on, so Alt+Tab is instant, the desktop resolution is kept, and recording software can capture
    /// the window.
    /// </summary>
    public static class Screen
    {
        private static IWindow? _window;
        private static bool _fullscreen;

        // Where the window was before going fullscreen, to put it back afterwards.
        private static Vector2D<int> _windowedPosition;
        private static Vector2D<int> _windowedSize;

        /// <summary>Can be set before the window opens; it takes effect as soon as it does.</summary>
        public static bool Fullscreen
        {
            get => _fullscreen;
            set
            {
                if (_fullscreen == value) return;

                _fullscreen = value;
                if (_window != null) Apply();
            }
        }

        internal static void Initialise(IWindow window)
        {
            _window = window;
            if (_fullscreen) Apply();
        }

        private static void Apply()
        {
            if (_fullscreen)
            {
                // The monitor the window is mostly on, so a game on a second screen goes fullscreen there.
                IMonitor? monitor = _window!.Monitor;
                if (monitor == null)
                {
                    _fullscreen = false;
                    return;
                }

                _windowedPosition = _window.Position;
                _windowedSize = _window.Size;

                // The video mode is the whole screen; Silk's Bounds is only the part the taskbar leaves free.
                // One pixel taller than the screen, hanging off the bottom: a window that exactly covers the monitor gets
                // flipped straight to the screen by some graphics drivers (NVIDIA's OpenGL driver especially), skipping the
                // desktop compositor, and then window capture (OBS's Window Capture, Discord streaming) only sees black.
                _window.WindowBorder = WindowBorder.Hidden;
                _window.Size = (monitor.VideoMode.Resolution ?? monitor.Bounds.Size) + new Vector2D<int>(0, 1);
                _window.Position = MonitorOrigin(monitor);
            }
            else
            {
                _window!.WindowBorder = WindowBorder.Resizable;
                _window.Size = _windowedSize;
                _window.Position = _windowedPosition;
            }
        }

        /// <summary>
        /// The monitor's top-left corner on the desktop. Silk only gives the free area's corner, which is off by the
        /// taskbar when that's along the top or left, so this asks GLFW (which Silk's windows run on) directly.
        /// </summary>
        private static unsafe Vector2D<int> MonitorOrigin(IMonitor monitor)
        {
            Glfw glfw = Glfw.GetApi();
            Monitor** monitors = glfw.GetMonitors(out int count);
            if (monitors == null || monitor.Index < 0 || monitor.Index >= count) return monitor.Bounds.Origin;

            glfw.GetMonitorPos(monitors[monitor.Index], out int x, out int y);
            return new Vector2D<int>(x, y);
        }
    }
}
