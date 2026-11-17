using System.Numerics;

namespace Vertex.Input
{
    /// <summary>
    /// What the keyboard and mouse are doing. Whatever hosts the game (a window, later an editor's game view) feeds it
    /// events through the internal Press/Release methods; game code only reads it.
    /// </summary>
    public static class InputSystem
    {
        private static IInputHost? _platform;

        private static readonly HashSet<Key> _keysHeld = new();
        private static readonly HashSet<Key> _keysPressed = new();
        private static readonly HashSet<Key> _keysReleased = new();
        private static readonly HashSet<MouseButton> _buttonsHeld = new();
        private static readonly HashSet<MouseButton> _buttonsPressed = new();
        private static readonly HashSet<MouseButton> _buttonsReleased = new();

        private static Vector2 _lastMousePosition;
        private static bool _hasMousePosition;
        private static bool _cursorLocked;
        private static readonly System.Text.StringBuilder _typedText = new();

        public static Vector2 MouseDelta { get; private set; }

        /// <summary>
        /// The characters typed this frame, with Shift, accents and the keyboard layout already applied:
        /// use this for text boxes rather than reading keys. Doesn't include Enter, Backspace or other control keys.
        /// </summary>
        public static string TypedText => _typedText.ToString();

        /// <summary>The system clipboard's text, for copy and paste. Empty if there's nowhere to get it from.</summary>
        public static string ClipboardText
        {
            get => _platform?.ClipboardText ?? "";
            set
            {
                if (_platform != null) _platform.ClipboardText = value;
            }
        }

        public static float ScrollDelta { get; private set; }

        /// <summary>The mouse pointer's position in window coordinates, from the top-left corner.</summary>
        public static Vector2 MousePosition { get; private set; }

        public static bool CursorLocked
        {
            get => _cursorLocked;
            set
            {
                if (_cursorLocked == value) return;

                _cursorLocked = value;
                _platform?.SetCursorLocked(value);
                _hasMousePosition = false;
            }
        }

        public static bool GetKey(Key key) => _keysHeld.Contains(key);

        public static bool GetKeyDown(Key key) => _keysPressed.Contains(key);

        /// <summary>
        /// Marks this frame's press of <paramref name="key"/> as handled, so <see cref="GetKeyDown"/> is false for
        /// everything after: closing a menu with Escape shouldn't also open another one.
        /// </summary>
        public static void Consume(Key key) => _keysPressed.Remove(key);

        /// <summary>Marks this frame's <see cref="TypedText"/> as handled, so nothing after receives it.</summary>
        public static void ConsumeTypedText() => _typedText.Clear();

        public static bool GetKeyUp(Key key) => _keysReleased.Contains(key);

        public static bool GetMouseButton(MouseButton button) => _buttonsHeld.Contains(button);
        public static bool GetMouseButtonDown(MouseButton button) => _buttonsPressed.Contains(button);
        public static bool GetMouseButtonUp(MouseButton button) => _buttonsReleased.Contains(button);

        /// <summary>Connects the clipboard and cursor locking to whatever hosts the game. Null disconnects them.</summary>
        internal static void SetPlatform(IInputHost? platform)
        {
            _platform = platform;
            if (platform != null && _cursorLocked) platform.SetCursorLocked(true);
        }

        internal static void PressKey(Key key)
        {
            if (_keysHeld.Add(key)) _keysPressed.Add(key);
        }

        internal static void ReleaseKey(Key key)
        {
            if (_keysHeld.Remove(key)) _keysReleased.Add(key);
        }

        internal static void PressButton(MouseButton button)
        {
            if (_buttonsHeld.Add(button)) _buttonsPressed.Add(button);
        }

        internal static void ReleaseButton(MouseButton button)
        {
            if (_buttonsHeld.Remove(button)) _buttonsReleased.Add(button);
        }

        internal static void TypeCharacter(char character) => _typedText.Append(character);

        internal static void Scroll(float amount) => ScrollDelta += amount;

        internal static void MoveMouse(Vector2 position)
        {
            if (_hasMousePosition) MouseDelta += position - _lastMousePosition;

            _lastMousePosition = position;
            _hasMousePosition = true;
            MousePosition = position;
        }

        /// <summary>Lets go of every key and button, for when the game stops receiving input (focus lost, play mode stopped).</summary>
        internal static void ReleaseAll()
        {
            foreach (Key key in _keysHeld) _keysReleased.Add(key);
            foreach (MouseButton button in _buttonsHeld) _buttonsReleased.Add(button);
            _keysHeld.Clear();
            _buttonsHeld.Clear();
        }

        internal static void EndFrame()
        {
            _keysPressed.Clear();
            _keysReleased.Clear();
            _buttonsPressed.Clear();
            _buttonsReleased.Clear();
            MouseDelta = Vector2.Zero;
            ScrollDelta = 0f;
            _typedText.Clear();
        }

        /// <summary>Forgets everything, as if the game had just started. For tests.</summary>
        internal static void Reset()
        {
            EndFrame();
            _keysHeld.Clear();
            _buttonsHeld.Clear();
            _hasMousePosition = false;
            _cursorLocked = false;
            MousePosition = Vector2.Zero;
        }
    }

    /// <summary>The parts of input that need the host's help: the clipboard and capturing the cursor.</summary>
    internal interface IInputHost
    {
        string ClipboardText { get; set; }
        void SetCursorLocked(bool locked);
    }
}
