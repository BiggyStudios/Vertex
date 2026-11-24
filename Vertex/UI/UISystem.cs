using System.Numerics;

using Vertex.Input;

namespace Vertex.UI
{
    /// <summary>
    /// Lays out, draws and clicks the UI made of objects (see <see cref="UICanvas"/>): once a frame, after the scene has
    /// updated, over the top of the game. Each canvas's objects are drawn parents first, then children in order, and each
    /// object's graphics in the order of its components.
    /// </summary>
    public static class UISystem
    {
        /// <summary>
        /// One piece of UI laid out this frame, in the order drawn, and the rectangle a <see cref="UIMask"/> above it keeps
        /// it inside (null if none does).
        /// </summary>
        internal readonly record struct Element(GameObject GameObject, UIRect Rect, (Vector2 Position, Vector2 Size)? Clip = null)
        {
            /// <summary>Whether <paramref name="point"/> is on it and not hidden by a mask.</summary>
            public bool Shows(Vector2 point) =>
                Rect.Contains(point) && (Clip is not { } clip || point.X >= clip.Position.X && point.Y >= clip.Position.Y &&
                    point.X < clip.Position.X + clip.Size.X && point.Y < clip.Position.Y + clip.Size.Y);
        }

        private static readonly List<Element> _elements = new();
        private static UIGraphic? _pressed;
        private static UIInputField? _focused;

        /// <summary>
        /// The size of the screen the UI was last laid out on, in UI units: the whole window in a game, the game's picture in
        /// an editor. Pieces at the top of a canvas are placed against this.
        /// </summary>
        public static Vector2 ScreenSize { get; private set; }

        /// <summary>Whether the mouse is over a piece of UI that takes clicks (a button), so the game can leave the click alone.</summary>
        public static bool MouseOverUI { get; private set; }

        /// <summary>The text box being typed in, if any. Clicking another piece of UI, or Enter or Escape, lets it go.</summary>
        public static UIInputField? FocusedInput
        {
            get => _focused is { GameObject.IsDestroyed: false, Enabled: true } ? _focused : null;
            set => _focused = value;
        }

        /// <summary>Whether a text box is being typed in, so the game can leave the keys alone (not jump on Space, say).</summary>
        public static bool Typing => FocusedInput != null;

        /// <summary>What was laid out last frame, in the order drawn (so the last is on top): for an editor picking pieces out.</summary>
        internal static IReadOnlyList<Element> Elements => _elements;

        /// <summary>Lays out and draws the scene's UI, and clicks its buttons. <paramref name="interactive"/> false draws it without the mouse doing anything.</summary>
        internal static void Update(Scene scene, bool interactive = true)
        {
            Layout(scene);

            // The topmost piece under the mouse that takes it gets it; the cursor must be free to point with.
            Vector2 mouse = Canvas.MousePosition;
            bool canPoint = interactive && !InputSystem.CursorLocked;
            UIGraphic? hovered = null;
            if (canPoint)
            {
                for (int i = _elements.Count - 1; i >= 0 && hovered == null; i--)
                {
                    if (!_elements[i].Shows(mouse) || !Canvas.InClip(mouse)) continue;
                    foreach (Component component in _elements[i].GameObject.Components)
                    {
                        if (component is UIGraphic { Enabled: true, TakesMouse: true } graphic) hovered = graphic;
                    }
                }
            }

            MouseOverUI = hovered != null;
            if (canPoint && InputSystem.GetMouseButtonDown(MouseButton.Left))
            {
                // Pressing anywhere but the text box being typed in lets it go.
                if (hovered != _focused) _focused = null;
                _pressed = hovered;
                if (hovered != null && RectOf(hovered) is { } rect) hovered.Press(mouse, rect.ScreenPosition, rect.ScreenSize);
            }

            if (_pressed != null && InputSystem.GetMouseButton(MouseButton.Left))
            {
                if (_pressed.GameObject.IsDestroyed) _pressed = null;
                else if (RectOf(_pressed) is { } rect) _pressed.Drag(mouse, rect.ScreenPosition, rect.ScreenSize);
            }

            if (!InputSystem.GetMouseButton(MouseButton.Left))
            {
                UIGraphic? released = _pressed;
                _pressed = null;
                released?.Release(released == hovered && InputSystem.GetMouseButtonUp(MouseButton.Left));
            }

            if (interactive) FocusedInput?.Type();

            foreach (Element element in _elements)
            {
                if (element.Clip is { } clip) Canvas.PushClip(clip.Position, clip.Size);
                foreach (Component component in element.GameObject.Components)
                {
                    if (component is not UIGraphic { Enabled: true } graphic) continue;
                    bool over = graphic == hovered;
                    graphic.Draw(element.Rect.ScreenPosition, element.Rect.ScreenSize, over, graphic == _pressed && (over || graphic is UISlider));
                }

                if (element.Clip != null) Canvas.PopClip();
            }
        }

        private static UIRect? RectOf(UIGraphic graphic)
        {
            foreach (Element element in _elements)
            {
                if (element.GameObject == graphic.GameObject) return element.Rect;
            }

            return null;
        }

        /// <summary>Works out where every piece of the scene's UI goes on the screen, without drawing it.</summary>
        private static readonly List<UICanvas> _canvases = new();

        internal static void Layout(Scene scene)
        {
            _elements.Clear();
            ScreenSize = Canvas.Size;

            _canvases.Clear();
            _canvases.AddRange(scene.FindComponents<UICanvas>());
            List<UICanvas> canvases = _canvases;

            // Highest order last, so on top; a canvas inside another is laid out with it, not again.
            canvases.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (UICanvas canvas in canvases)
            {
                if (HasCanvasAbove(canvas.GameObject)) continue;
                LayoutTree(canvas.GameObject, Vector2.Zero, Canvas.Size, clip: null, placed: false);
            }
        }

        private static bool HasCanvasAbove(GameObject gameObject)
        {
            for (Transform? parent = gameObject.Transform.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.GameObject.GetComponent<UICanvas>() is { Enabled: true }) return true;
            }

            return false;
        }

        /// <param name="placed">Its rectangle has already been set by a layout group above it, so its own anchors don't place it.</param>
        private static void LayoutTree(GameObject gameObject, Vector2 parentPosition, Vector2 parentSize, (Vector2 Position, Vector2 Size)? clip, bool placed)
        {
            if (!gameObject.Active) return;

            if (gameObject.GetComponent<UIRect>() is { } rect)
            {
                if (!placed) rect.Layout(parentPosition, parentSize);
                _elements.Add(new Element(gameObject, rect, clip));
                parentPosition = rect.ScreenPosition;
                parentSize = rect.ScreenSize;

                // What's under a mask is kept inside its rectangle (and any mask's above it).
                if (gameObject.GetComponent<UIMask>() is { Enabled: true }) clip = Intersect(clip, (parentPosition, parentSize));
            }

            IReadOnlyList<Transform> children = gameObject.Transform.Children;
            if (gameObject.GetComponent<UILayoutGroup>() is { Enabled: true } group)
            {
                var arranged = new List<UIRect>();
                foreach (Transform child in children)
                {
                    if (child.GameObject.Active && child.GameObject.GetComponent<UIRect>() is { } childRect) arranged.Add(childRect);
                }

                group.Arrange(arranged, parentPosition, parentSize);
                foreach (Transform child in children)
                {
                    LayoutTree(child.GameObject, parentPosition, parentSize, clip, placed: child.GameObject.GetComponent<UIRect>() != null);
                }

                return;
            }

            foreach (Transform child in children) LayoutTree(child.GameObject, parentPosition, parentSize, clip, placed: false);
        }

        private static (Vector2 Position, Vector2 Size) Intersect((Vector2 Position, Vector2 Size)? outer, (Vector2 Position, Vector2 Size) inner)
        {
            if (outer is not { } o) return inner;
            Vector2 from = Vector2.Max(o.Position, inner.Position);
            Vector2 to = Vector2.Min(o.Position + o.Size, inner.Position + inner.Size);
            return (from, Vector2.Max(to - from, Vector2.Zero));
        }

        /// <summary>The rectangle a piece of UI is laid out in: its parent's, or the whole screen at the top.</summary>
        public static (Vector2 Position, Vector2 Size) ParentRect(GameObject gameObject)
        {
            for (Transform? parent = gameObject.Transform.Parent; parent != null; parent = parent.Parent)
            {
                if (parent.GameObject.GetComponent<UIRect>() is { } rect) return (rect.ScreenPosition, rect.ScreenSize);
                if (parent.GameObject.GetComponent<UICanvas>() != null) break;
            }

            return (Vector2.Zero, ScreenSize);
        }

        internal static void Reset()
        {
            _elements.Clear();
            _pressed = null;
            _focused = null;
            MouseOverUI = false;
        }
    }
}
