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
        private static bool _cursorFreed;
        private static readonly System.Text.StringBuilder _typedText = new();

        public static Vector2 MouseDelta { get => Muted ? Vector2.Zero : _mouseDelta; private set => _mouseDelta = value; }

        private static Vector2 _mouseDelta;
        private static float _scrollDelta;

        /// <summary>
        /// While true, reading input gives nothing pressed and no movement, though it carries on being tracked: an editor
        /// hides input from the game it's playing while the editor has it, and the other way round.
        /// </summary>
        internal static bool Muted { get; set; }

        /// <summary>
        /// The characters typed this frame, with Shift, accents and the keyboard layout already applied:
        /// use this for text boxes rather than reading keys. Doesn't include Enter, Backspace or other control keys.
        /// </summary>
        public static string TypedText => Muted ? "" : _typedText.ToString();

        /// <summary>The system clipboard's text, for copy and paste. Empty if there's nowhere to get it from.</summary>
        public static string ClipboardText
        {
            get => _platform?.ClipboardText ?? "";
            set
            {
                if (_platform != null) _platform.ClipboardText = value;
            }
        }

        public static float ScrollDelta { get => Muted ? 0f : _scrollDelta; private set => _scrollDelta = value; }

        /// <summary>The mouse pointer's position in window coordinates, from the top-left corner.</summary>
        public static Vector2 MousePosition { get; private set; }

        public static bool CursorLocked
        {
            get => _cursorLocked;
            set
            {
                if (_cursorLocked == value) return;

                _cursorLocked = value;
                if (!value) _cursorFreed = false;
                _platform?.SetCursorLocked(value);
                _hasMousePosition = false;
            }
        }

        /// <summary>
        /// While true, the cursor shows and moves about though the game still has it locked (<see cref="CursorLocked"/>
        /// stays true, and moving the mouse still moves <see cref="MouseDelta"/>): an editor lets it go like this while
        /// playing, so the game can still be steered and the cursor taken back with a click. Unlocking ends it.
        /// </summary>
        internal static bool CursorFreed
        {
            get => _cursorFreed;
            set
            {
                value &= _cursorLocked;
                if (_cursorFreed == value) return;

                _cursorFreed = value;
                _platform?.SetCursorLocked(!value);
                _hasMousePosition = false;
            }
        }

        /// <summary>What the mouse pointer looks like, such as a text cursor over a text box. Stays as set until changed.</summary>
        public static CursorShape CursorShape
        {
            get => _cursorShape;
            set
            {
                if (_cursorShape == value) return;

                _cursorShape = value;
                _platform?.SetCursorShape(value);
            }
        }

        private static CursorShape _cursorShape;

        public static bool GetKey(Key key) => ButtonOf(key) is MouseButton button ? GetMouseButton(button) : !Muted && _keysHeld.Contains(key);

        public static bool GetKeyDown(Key key) => ButtonOf(key) is MouseButton button ? GetMouseButtonDown(button) : !Muted && _keysPressed.Contains(key);

        /// <summary>The mouse button a <see cref="Key"/> stands for (<see cref="Key.MouseLeft"/> and on), or null for a key on the keyboard.</summary>
        public static MouseButton? ButtonOf(Key key) => key >= Key.MouseLeft && key <= Key.MouseForward ? (MouseButton)(key - Key.MouseLeft) : null;

        /// <summary>
        /// Marks this frame's press of <paramref name="key"/> as handled, so <see cref="GetKeyDown"/> is false for
        /// everything after: closing a menu with Escape shouldn't also open another one.
        /// </summary>
        public static void Consume(Key key)
        {
            if (ButtonOf(key) is MouseButton button) Consume(button);
            else _keysPressed.Remove(key);
        }

        /// <summary>
        /// Marks this frame's press of <paramref name="button"/> as handled: nothing after sees it pressed, held or let go.
        /// An editor's click that takes the cursor back for the game shouldn't also fire its gun.
        /// </summary>
        internal static void Consume(MouseButton button)
        {
            if (_buttonsPressed.Remove(button)) _buttonsHeld.Remove(button);
        }

        /// <summary>Marks this frame's <see cref="TypedText"/> as handled, so nothing after receives it.</summary>
        public static void ConsumeTypedText() => _typedText.Clear();

        public static bool GetKeyUp(Key key) => ButtonOf(key) is MouseButton button ? GetMouseButtonUp(button) : !Muted && _keysReleased.Contains(key);

        public static bool GetMouseButton(MouseButton button) => !Muted && _buttonsHeld.Contains(button);
        public static bool GetMouseButtonDown(MouseButton button) => !Muted && _buttonsPressed.Contains(button);
        public static bool GetMouseButtonUp(MouseButton button) => !Muted && _buttonsReleased.Contains(button);

        /// <summary>Connects the clipboard and cursor locking to whatever hosts the game. Null disconnects them.</summary>
        internal static void SetPlatform(IInputHost? platform)
        {
            _platform = platform;
            if (platform != null && _cursorLocked && !_cursorFreed) platform.SetCursorLocked(true);
        }

        internal static void PressKey(Key key)
        {
            if (ButtonOf(key) is MouseButton button) PressButton(button);
            else if (_keysHeld.Add(key)) _keysPressed.Add(key);
        }

        internal static void ReleaseKey(Key key)
        {
            if (ButtonOf(key) is MouseButton button) ReleaseButton(button);
            else if (_keysHeld.Remove(key)) _keysReleased.Add(key);
        }

        internal static void PressButton(MouseButton button)
        {
            if (_buttonsHeld.Add(button)) _buttonsPressed.Add(button);
        }

        internal static void ReleaseButton(MouseButton button)
        {
            if (_buttonsHeld.Remove(button)) _buttonsReleased.Add(button);
        }

        private static readonly HashSet<MouseButton> _deferredPresses = new();

        /// <summary>
        /// Holds this frame's press of <paramref name="button"/> back until the next frame, as if it came a frame later:
        /// an editor finishing typing in a box clicked away from lets the box hand over its text before the click does anything.
        /// </summary>
        internal static void DeferPress(MouseButton button)
        {
            if (_buttonsPressed.Remove(button)) _deferredPresses.Add(button);
        }

        internal static void TypeCharacter(char character) => _typedText.Append(character);

        internal static void Scroll(float amount) => _scrollDelta += amount;

        internal static void MoveMouse(Vector2 position)
        {
            if (_hasMousePosition) _mouseDelta += position - _lastMousePosition;

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

            // Presses held back a frame arrive now (let go already: the whole click arrives at once).
            foreach (MouseButton button in _deferredPresses)
            {
                _buttonsPressed.Add(button);
                if (!_buttonsHeld.Contains(button)) _buttonsReleased.Add(button);
            }

            _deferredPresses.Clear();
        }

        /// <summary>Forgets everything, as if the game had just started. For tests.</summary>
        internal static void Reset()
        {
            _deferredPresses.Clear();
            EndFrame();
            _keysHeld.Clear();
            _buttonsHeld.Clear();
            _hasMousePosition = false;
            _cursorLocked = false;
            _cursorFreed = false;
            Muted = false;
            MousePosition = Vector2.Zero;
        }
    }

    /// <summary>What the mouse pointer looks like, to hint at what clicking will do.</summary>
    public enum CursorShape
    {
        Arrow,

        /// <summary>The text cursor, over something to type in.</summary>
        IBeam,

        /// <summary>A pointing hand, over a link.</summary>
        Hand,

        /// <summary>Over something that drags left and right: a divider between panels, or a number to drag.</summary>
        ResizeHorizontal,

        /// <summary>Over something that drags up and down.</summary>
        ResizeVertical,

        Crosshair
    }

    /// <summary>The parts of input that need the host's help: the clipboard, capturing the cursor and its shape.</summary>
    internal interface IInputHost
    {
        string ClipboardText { get; set; }
        void SetCursorLocked(bool locked);
        void SetCursorShape(CursorShape shape) {}
    }
}
