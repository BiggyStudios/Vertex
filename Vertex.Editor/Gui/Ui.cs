using System.Numerics;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>What the mouse did to a control this frame. See <see cref="Ui.Interact"/>.</summary>
    public readonly record struct Interaction(bool Hovered, bool Pressed, bool Held, bool Released, bool Clicked, bool DoubleClicked)
    {
        /// <summary>Pressed with the right button: for context menus.</summary>
        public bool RightClicked { get; init; }
    }

    /// <summary>
    /// The editor's UI: immediate mode, like the engine's <see cref="Canvas"/>. Controls are function calls made every frame
    /// they show, which draw themselves and report what happened, so there's no tree of widgets to keep in step with
    /// the scene. What does need remembering from frame to frame (which control the mouse is over, what has keyboard
    /// focus, values in the middle of animating) is kept here, keyed by each control's id.
    /// <para>
    /// Ids come from <see cref="Id(string)"/>, mixed with the ids pushed by <see cref="PushId(int)"/>, so the same label
    /// in two places (two components' "Color") gets two ids.
    /// </para>
    /// <para>
    /// When controls overlap, the one drawn last (on top) gets the mouse, worked out a frame behind, which can't be seen.
    /// That's how popups, drawn last of all, keep clicks from going through them.
    /// </para>
    /// </summary>
    public static class Ui
    {
        private static readonly List<int> _idStack = new();

        private struct Animated
        {
            public float Value;
            public int Frame;
        }

        private static readonly Dictionary<int, Animated> _animated = new();
        private static readonly List<int> _stale = new();

        // The control the mouse is over, as found while drawing the last frame; and the one being found this frame.
        private static int _nextHot;
        private static int _nextScrollTarget;

        // Drawn after everything else, over the top: popups, menus, tooltips, drags.
        private static readonly List<Action> _overlays = new();

        private static int _tooltipId;
        private static string? _tooltipText;
        private static float _tooltipTime;
        private static Vector2 _tooltipAt;

        private static int _lastClickId;
        private static float _lastClickTime;
        private static Vector2 _lastClickAt;

        /// <summary>Counts up every frame.</summary>
        public static int Frame { get; private set; }

        /// <summary>Seconds since the last frame, kept sensible so a stall doesn't make animations jump.</summary>
        public static float DeltaTime { get; private set; }

        /// <summary>Seconds since the editor started.</summary>
        public static float Time { get; private set; }

        /// <summary>The mouse pointer, in UI units.</summary>
        public static Vector2 Mouse { get; private set; }

        private static int _mouseMovedFrame;

        /// <summary>
        /// Whether the mouse has moved in the last few frames: lists follow the mouse with their highlight only then, so
        /// the arrow keys can move it while the mouse rests on the list.
        /// </summary>
        public static bool MouseMovedLately => Frame - _mouseMovedFrame <= 2;

        /// <summary>How far the mouse moved this frame, in UI units. Keeps counting while the cursor is locked for a drag.</summary>
        public static Vector2 MouseDelta { get; private set; }

        /// <summary>The control under the mouse, found last frame: the only one that reacts to it. 0 for none.</summary>
        public static int HotId { get; private set; }

        /// <summary>The control being pressed or dragged, which keeps the mouse until it's let go. 0 for none.</summary>
        public static int ActiveId { get; set; }

        /// <summary>The control the keyboard types into. 0 for none, when shortcuts are free to act.</summary>
        public static int FocusId { get; set; }

        /// <summary>The scroll area the mouse wheel moves: the innermost one under the mouse.</summary>
        public static int ScrollTarget { get; private set; }

        /// <summary>What the mouse pointer looks like this frame. Controls set it while the mouse is over them.</summary>
        public static CursorShape Cursor { get; set; }

        /// <summary>
        /// Multiplies the alpha of everything drawn through <see cref="Ui"/>: popups fade in by drawing their contents
        /// under a value that goes from 0 to 1.
        /// </summary>
        public static float Opacity { get; set; } = 1f;

        private static int _focusKeptFrame;

        /// <summary>Called each frame by the control with <see cref="FocusId"/>, to keep it: focus left with a control that's gone is let go.</summary>
        public static void KeepFocus(int id)
        {
            if (FocusId == id) _focusKeptFrame = Frame;
        }

        /// <summary>Gives control <paramref name="id"/> the keyboard.</summary>
        public static void Focus(int id)
        {
            FocusId = id;
            _focusKeptFrame = Frame;
        }

        /// <summary>True while a text box has the keyboard, so single-key shortcuts must leave the keys alone.</summary>
        public static bool KeyboardTaken => FocusId != 0;

        /// <summary>Whether the mouse is over a control that isn't the one passed, so whatever's underneath should leave it alone.</summary>
        public static bool MouseTakenBy(int id) => HotId != 0 && HotId != id;

        // ---------------------------------------------------------------- Frames

        /// <summary>Starts a frame. The engine's Canvas and input must already have this frame's state.</summary>
        public static void BeginFrame(float deltaTime)
        {
            Frame++;
            DeltaTime = Math.Clamp(deltaTime, 0f, 0.1f);
            Time += DeltaTime;

            if (Canvas.MousePosition != Mouse) _mouseMovedFrame = Frame;
            Mouse = Canvas.MousePosition;
            MouseDelta = InputSystem.MouseDelta * Canvas.WindowToPixels / Canvas.Scale;

            HotId = _nextHot;
            ScrollTarget = _nextScrollTarget;
            _nextHot = 0;
            _nextScrollTarget = 0;

            Cursor = CursorShape.Arrow;
            Opacity = 1f;
            _idStack.Clear();

            // A press anywhere but the text box being typed in takes the keyboard away from it (it notices, and finishes up).
            if ((InputSystem.GetMouseButtonDown(MouseButton.Left) || InputSystem.GetMouseButtonDown(MouseButton.Right)) && HotId != FocusId) FocusId = 0;

            // As does the text box no longer being drawn (its popup closed).
            if (_focusKeptFrame < Frame - 1) FocusId = 0;
        }

        /// <summary>Draws the overlays (popups, tooltips), then forgets animations no control has asked for in a while.</summary>
        public static void EndFrame()
        {
            // Overlays can add more overlays (a menu opening a submenu); they go after.
            for (int i = 0; i < _overlays.Count; i++)
            {
                Opacity = 1f;
                _idStack.Clear();
                _overlays[i]();
            }

            _overlays.Clear();
            Opacity = 1f;
            _idStack.Clear();

            Menu.Draw();
            DrawTooltip();
            TextInput.Tidy();

            if (!InputSystem.GetMouseButton(MouseButton.Left) && !InputSystem.GetMouseButton(MouseButton.Right) &&
                !InputSystem.GetMouseButton(MouseButton.Middle))
            {
                ActiveId = 0;
            }

            if (Frame % 120 == 0)
            {
                foreach ((int key, Animated value) in _animated)
                {
                    if (Frame - value.Frame > 120) _stale.Add(key);
                }

                foreach (int key in _stale) _animated.Remove(key);
                _stale.Clear();
            }

            InputSystem.CursorShape = Cursor;
        }

        /// <summary>Runs <paramref name="draw"/> at the end of the frame, on top of everything: for popups and drag previews.</summary>
        public static void Overlay(Action draw) => _overlays.Add(draw);

        // ---------------------------------------------------------------- Ids

        public static int Id(string label) => HashCode.Combine(Top, label);
        public static int Id(int value) => HashCode.Combine(Top, value, 0x51ED);

        /// <summary>An id made from two parts, for controls inside a list (a row and its toggle).</summary>
        public static int Id(int parent, string label) => HashCode.Combine(parent, label);

        /// <summary>An id for item <paramref name="index"/> of a list inside control <paramref name="parent"/>.</summary>
        public static int Id(int parent, int index) => HashCode.Combine(parent, index, 0x3C1);

        public static void PushId(int id) => _idStack.Add(id);
        public static void PushId(string label) => _idStack.Add(Id(label));
        public static void PopId() => _idStack.RemoveAt(_idStack.Count - 1);

        private static int Top => _idStack.Count > 0 ? _idStack[^1] : 17;

        // ---------------------------------------------------------------- Mouse

        /// <summary>
        /// Lets the control <paramref name="id"/> covering <paramref name="rect"/> take part in the mouse: whether it's
        /// under the pointer, pressed, held, or clicked (pressed and let go over it). Call it before drawing the control,
        /// so the drawing can show the state.
        /// </summary>
        public static Interaction Interact(int id, Rect rect, bool rightClick = false)
        {
            bool inside = rect.Contains(Mouse) && Canvas.InClip(Mouse);
            if (inside && (ActiveId == 0 || ActiveId == id)) _nextHot = id;

            bool hovered = HotId == id && inside && (ActiveId == 0 || ActiveId == id);
            bool pressed = hovered && InputSystem.GetMouseButtonDown(MouseButton.Left);
            bool doubleClicked = false;

            if (pressed)
            {
                ActiveId = id;
                doubleClicked = _lastClickId == id && Time - _lastClickTime < 0.35f && Vector2.Distance(_lastClickAt, Mouse) < 4f;
                _lastClickId = doubleClicked ? 0 : id;
                _lastClickTime = Time;
                _lastClickAt = Mouse;
            }

            bool held = ActiveId == id && InputSystem.GetMouseButton(MouseButton.Left);
            bool released = ActiveId == id && InputSystem.GetMouseButtonUp(MouseButton.Left);
            bool clicked = released && inside;
            if (released) ActiveId = 0;

            return new Interaction(hovered || held, pressed, held, released, clicked, doubleClicked)
            {
                RightClicked = rightClick && hovered && InputSystem.GetMouseButtonDown(MouseButton.Right)
            };
        }

        /// <summary>Marks <paramref name="rect"/> as a scroll area: the mouse wheel goes to the innermost one under the mouse.</summary>
        public static void ScrollArea(int id, Rect rect)
        {
            if (rect.Contains(Mouse) && Canvas.InClip(Mouse)) _nextScrollTarget = id;
        }

        /// <summary>Takes the mouse over <paramref name="rect"/> so nothing beneath reacts to it, without being a control itself.</summary>
        public static void Block(int id, Rect rect) => Interact(id, rect);

        public static bool MousePressed(MouseButton button = MouseButton.Left) => InputSystem.GetMouseButtonDown(button);
        public static bool MouseHeld(MouseButton button = MouseButton.Left) => InputSystem.GetMouseButton(button);
        public static bool MouseReleased(MouseButton button = MouseButton.Left) => InputSystem.GetMouseButtonUp(button);

        // ---------------------------------------------------------------- Animation

        /// <summary>
        /// Eases a value towards <paramref name="target"/>, a bit more each frame: the further away, the faster it moves,
        /// so it settles smoothly. <paramref name="speed"/> is how quickly: about 1/speed seconds to get two thirds of the
        /// way, the same at any frame rate. The value starts at <paramref name="initial"/>, or the target if not given.
        /// </summary>
        public static float Smooth(int key, float target, float speed = 18f, float? initial = null)
        {
            if (!_animated.TryGetValue(key, out Animated animated))
            {
                animated.Value = initial ?? target;
            }

            animated.Value = Approach(animated.Value, target, speed, DeltaTime);
            animated.Frame = Frame;
            _animated[key] = animated;
            return animated.Value;
        }

        /// <summary>
        /// Two <see cref="Smooth"/> values together.
        /// </summary>
        public static Vector2 Smooth(int key, Vector2 target, float speed = 18f, Vector2? initial = null) =>
            new(Smooth(HashCode.Combine(key, 1), target.X, speed, initial?.X), Smooth(HashCode.Combine(key, 2), target.Y, speed, initial?.Y));

        /// <summary>A colour easing towards <paramref name="target"/>, for hover and press fades.</summary>
        public static Color Smooth(int key, Color target, float speed = 18f)
        {
            return new Color(Smooth(HashCode.Combine(key, 11), target.R, speed), Smooth(HashCode.Combine(key, 12), target.G, speed),
                Smooth(HashCode.Combine(key, 13), target.B, speed), Smooth(HashCode.Combine(key, 14), target.A, speed));
        }

        /// <summary>Jumps an animated value straight to <paramref name="value"/>, without easing.</summary>
        public static void Snap(int key, float value) => _animated[key] = new Animated { Value = value, Frame = Frame };

        /// <summary>Keeps a number from one frame to the next (how tall something was last time), under <paramref name="key"/>.</summary>
        public static void Remember(int key, float value) => Snap(key, value);

        /// <summary>The number kept by <see cref="Remember"/>, or <paramref name="fallback"/> if there isn't one.</summary>
        public static float Recall(int key, float fallback = 0f)
        {
            if (!_animated.TryGetValue(key, out Animated animated)) return fallback;

            animated.Frame = Frame;
            _animated[key] = animated;
            return animated.Value;
        }

        /// <summary>One step of easing from <paramref name="current"/> to <paramref name="target"/> over <paramref name="deltaTime"/> seconds.</summary>
        public static float Approach(float current, float target, float speed, float deltaTime)
        {
            float t = 1f - MathF.Exp(-speed * deltaTime);
            float next = current + (target - current) * t;

            // Close enough to stop: saves easing a value forever by amounts too small to see.
            return MathF.Abs(target - next) < 0.0005f ? target : next;
        }

        /// <summary>Slows towards the end: for things that start quickly and settle, like a popup opening.</summary>
        public static float EaseOut(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float inverse = 1f - t;
            return 1f - inverse * inverse * inverse;
        }

        // ---------------------------------------------------------------- Drawing

        public static Color Fade(Color color) => color.WithAlpha(Opacity);

        public static void Rect(Rect rect, Color color, float radius = 0f) => Canvas.DrawRect(rect.Position, rect.Size, Fade(color), radius);

        public static void Outline(Rect rect, Color color, float radius = 0f, float thickness = 1f) =>
            Canvas.DrawRectOutline(rect.Position, rect.Size, Fade(color), thickness, radius);

        public static void Shadow(Rect rect, float radius, float blur, float strength = 1f) =>
            Canvas.DrawShadow(rect.Position + new Vector2(0f, blur * 0.3f), rect.Size, Fade(Theme.Shadow.WithAlpha(strength)), radius, blur);

        public static void Line(Vector2 from, Vector2 to, Color color, float thickness = 1f) => Canvas.DrawLine(from, to, thickness, Fade(color));

        public static void Circle(Vector2 centre, float radius, Color color) => Canvas.DrawCircle(centre, radius, Fade(color));

        /// <summary>Text drawn with its top-left at <paramref name="position"/>. Returns its width.</summary>
        public static float Text(string text, Vector2 position, Color color, float size = Theme.TextSize, bool bold = false) =>
            Canvas.DrawText(bold ? Theme.Bold : Theme.Font, text, position, Fade(color), size);

        /// <summary>
        /// Text in <paramref name="rect"/>, centred top to bottom, placed left, centre or right by <paramref name="align"/>
        /// (0, 0.5 or 1). Text too long for the rectangle ends in "…".
        /// </summary>
        public static void Text(Rect rect, string text, Color color, float align = 0f, float size = Theme.TextSize, bool bold = false)
        {
            if (string.IsNullOrEmpty(text) || rect.Width <= 0f) return;

            Font font = bold ? Theme.Bold : Theme.Font;
            string shown = Fit(text, rect.Width, size, bold);
            Vector2 measured = Canvas.MeasureText(font, shown, size);
            float lineHeight = Canvas.LineHeight(font, size);
            var position = new Vector2(rect.X + (rect.Width - measured.X) * align, rect.Y + (rect.Height - lineHeight) * 0.5f);
            Canvas.DrawText(font, shown, position, Fade(color), size);
        }

        public static float TextWidth(string text, float size = Theme.TextSize, bool bold = false) =>
            Canvas.MeasureText(bold ? Theme.Bold : Theme.Font, text, size).X;

        public static float LineHeight(float size = Theme.TextSize, bool bold = false) => Canvas.LineHeight(bold ? Theme.Bold : Theme.Font, size);

        /// <summary><paramref name="text"/>, cut short with "…" if it's wider than <paramref name="width"/>.</summary>
        public static string Fit(string text, float width, float size = Theme.TextSize, bool bold = false)
        {
            if (TextWidth(text, size, bold) <= width) return text;

            // Longest start that fits with the ellipsis, found by halving.
            int low = 0, high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (TextWidth(string.Concat(text.AsSpan(0, middle), "…"), size, bold) <= width) low = middle;
                else high = middle - 1;
            }

            return low == 0 ? "" : string.Concat(text.AsSpan(0, low).TrimEnd(), "…");
        }

        public static void PushClip(Rect rect) => Canvas.PushClip(rect.Position, rect.Size);
        public static void PopClip() => Canvas.PopClip();

        // ---------------------------------------------------------------- Tooltips

        /// <summary>Shows <paramref name="text"/> near the mouse once it has rested on control <paramref name="id"/> for a moment.</summary>
        public static void Tooltip(int id, string? text)
        {
            if (string.IsNullOrEmpty(text) || HotId != id) return;

            if (_tooltipId != id)
            {
                _tooltipId = id;
                _tooltipTime = 0f;
            }

            _tooltipText = text;
            _tooltipAt = Mouse;
        }

        private static void DrawTooltip()
        {
            // The control has to ask again each frame for its tooltip to stay.
            if (_tooltipText == null || HotId != _tooltipId || ActiveId != 0)
            {
                _tooltipId = 0;
                _tooltipText = null;
                return;
            }

            string text = _tooltipText;
            _tooltipText = null;
            _tooltipTime += DeltaTime;
            float shown = Math.Clamp((_tooltipTime - 0.45f) / 0.12f, 0f, 1f);
            if (shown <= 0f) return;

            const float maxWidth = 320f;
            float width = MathF.Min(TextWidth(text, Theme.SmallTextSize) + 16f, maxWidth);
            var rect = new Rect(_tooltipAt.X + 12f, _tooltipAt.Y + 20f, width, 24f);
            if (rect.Right > Canvas.Width - 4f) rect = rect with { X = Canvas.Width - 4f - rect.Width };
            if (rect.Bottom > Canvas.Height - 4f) rect = rect with { Y = _tooltipAt.Y - 30f };

            Opacity = EaseOut(shown);
            rect = rect.Offset(new Vector2(0f, (1f - Opacity) * 4f));
            Shadow(rect, 5f, 10f, 0.7f);
            Rect(rect, Theme.Popup, 5f);
            Outline(rect, Theme.BorderStrong, 5f);
            Text(rect.Shrink(8f, 0f), text, Theme.Text, size: Theme.SmallTextSize);
            Opacity = 1f;
        }
    }
}
