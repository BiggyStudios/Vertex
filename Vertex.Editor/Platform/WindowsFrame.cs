using System.Runtime.InteropServices;

namespace Vertex.Editor.Platform
{
    /// <summary>
    /// Gives the editor's borderless window what Windows only gives windows with a frame: snapping to the screen's edges
    /// and the snap bar at the top while it's dragged, Win+arrow keys, shaking, and the minimise and maximise animations.
    /// The window gets a frame's styles, and the frame is made no width at all, so it still looks borderless.
    /// </summary>
    internal static class WindowsFrame
    {
        private static WindowProcedure? _procedure;
        private static IntPtr _original;
        private static IntPtr _window;

        /// <summary>Whether the window has it: moving and maximising are then left to Windows.</summary>
        public static bool Installed => _window != IntPtr.Zero;

        public static void Install(IntPtr window)
        {
            if (Installed) return;

            // Kept in a field, so it isn't collected while Windows still calls it.
            _procedure = Procedure;
            _original = SetWindowLongPtr(window, _windowProcedureIndex, Marshal.GetFunctionPointerForDelegate(_procedure));
            if (_original == IntPtr.Zero) return;
            _window = window;

            long style = GetWindowLongPtr(window, _styleIndex).ToInt64();
            style = (style & ~_popup) | _caption | _thickFrame | _systemMenu | _minimizeBox | _maximizeBox;
            SetWindowLongPtr(window, _styleIndex, new IntPtr(style));
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, _frameChanged | _noMove | _noSize | _noZOrder | _noActivate);
        }

        /// <summary>
        /// Lets Windows move the window, with the left button held, until it's let go: it snaps where Windows snaps
        /// windows, and comes back to its own size if it was maximised. Returns once the move is over.
        /// </summary>
        public static void Drag()
        {
            // Started with the button up, Windows' move would carry the window about until the next click. (The
            // physical button is read, so with the buttons swapped the left one is the right one.)
            int button = GetSystemMetrics(_swappedButtons) != 0 ? 0x02 : 0x01;
            if (!Installed || (GetAsyncKeyState(button) & 0x8000) == 0) return;

            ReleaseCapture();
            SendMessage(_window, _systemCommand, new IntPtr(_dragMove), IntPtr.Zero);
        }

        private static IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            // The frame takes no room: the whole window is the editor's picture. Maximised, it's kept to the part of the
            // screen that isn't the taskbar, which Windows would otherwise let the frame hang past.
            if (message == _calculateSize && wParam != IntPtr.Zero)
            {
                if (IsZoomed(window))
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) Marshal.StructureToPtr(info.Work, lParam, false);
                }

                return IntPtr.Zero;
            }

            return CallWindowProc(_original, window, message, wParam, lParam);
        }

        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private const int _windowProcedureIndex = -4, _styleIndex = -16;
        private const long _popup = 0x80000000L, _caption = 0x00C00000L, _thickFrame = 0x00040000L, _systemMenu = 0x00080000L;
        private const long _minimizeBox = 0x00020000L, _maximizeBox = 0x00010000L;
        private const uint _frameChanged = 0x0020, _noMove = 0x0002, _noSize = 0x0001, _noZOrder = 0x0004, _noActivate = 0x0010;
        private const uint _calculateSize = 0x0083, _systemCommand = 0x0112;
        private const int _swappedButtons = 23;

        // SC_MOVE with the caption's hit-test code: a move by the mouse, as if the title bar were dragged.
        private const int _dragMove = 0xF012;

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

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
