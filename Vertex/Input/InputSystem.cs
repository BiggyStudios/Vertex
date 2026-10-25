using System.Numerics;

using Silk.NET.Input;

namespace Vertex.Input
{
    public static class InputSystem
    {
        private static IKeyboard? _keyboard;
        private static IMouse? _mouse;

        private static readonly HashSet<Key> _keysPressed = new();
        private static readonly HashSet<Key> _keysReleased = new();
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

        /// <summary>The system clipboard's text, for copy and paste. Empty if there's no keyboard.</summary>
        public static string ClipboardText
        {
            get => _keyboard?.ClipboardText ?? "";
            set
            {
                if (_keyboard != null) _keyboard.ClipboardText = value;
            }
        }

        public static float ScrollDelta { get; private set; }

        public static Vector2 MousePosition => _mouse?.Position ?? Vector2.Zero;

        public static bool CursorLocked
        {
            get => _cursorLocked;
            set
            {
                if (_mouse == null || _cursorLocked == value) return;

                _cursorLocked = value;
                _mouse.Cursor.CursorMode = !value ? CursorMode.Normal
                    : _mouse.Cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw
                    : CursorMode.Disabled;

                _hasMousePosition = false;
            }
        }

        internal static void Initialise(IInputContext input)
        {
            _keyboard = input.Keyboards.FirstOrDefault();
            _mouse = input.Mice.FirstOrDefault();

            if (_keyboard != null)
            {
                _keyboard.KeyDown += (_, key, _) => _keysPressed.Add(key);
                _keyboard.KeyUp += (_, key, _) => _keysReleased.Add(key);
                _keyboard.KeyChar += (_, character) => _typedText.Append(character);
            }

            if (_mouse != null)
            {
                _mouse.MouseDown += (_, button) => _buttonsPressed.Add(button);
                _mouse.MouseUp += (_, button) => _buttonsReleased.Add(button);
                _mouse.Scroll += (_, wheel) => ScrollDelta += wheel.Y;
                _mouse.MouseMove += OnMouseMove;
            }
        }

        public static bool GetKey(Key key) => _keyboard?.IsKeyPressed(key) ?? false;

        public static bool GetKeyDown(Key key) => _keysPressed.Contains(key);

        /// <summary>
        /// Marks this frame's press of <paramref name="key"/> as handled, so <see cref="GetKeyDown"/> is false for
        /// everything after: closing a menu with Escape shouldn't also open another one.
        /// </summary>
        public static void Consume(Key key) => _keysPressed.Remove(key);

        /// <summary>Marks this frame's <see cref="TypedText"/> as handled, so nothing after receives it.</summary>
        public static void ConsumeTypedText() => _typedText.Clear();

        public static bool GetKeyUp(Key key) => _keysReleased.Contains(key);

        public static bool GetMouseButton(MouseButton button) => _mouse?.IsButtonPressed(button) ?? false;
        public static bool GetMouseButtonDown(MouseButton button) => _buttonsPressed.Contains(button);
        public static bool GetMouseButtonUp(MouseButton button) => _buttonsReleased.Contains(button);

        private static void OnMouseMove(IMouse mouse, Vector2 position)
        {
            if (_hasMousePosition) MouseDelta += position - _lastMousePosition;

            _lastMousePosition = position;
            _hasMousePosition = true;
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
    }
}
