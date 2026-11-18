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
        /// <summary>One piece of UI laid out this frame, in the order drawn.</summary>
        internal readonly record struct Element(GameObject GameObject, UIRect Rect);

        private static readonly List<Element> _elements = new();
        private static UIButton? _pressed;

        /// <summary>
        /// The size of the screen the UI was last laid out on, in UI units: the whole window in a game, the game's picture in
        /// an editor. Pieces at the top of a canvas are placed against this.
        /// </summary>
        public static Vector2 ScreenSize { get; private set; }

        /// <summary>Whether the mouse is over a piece of UI that takes clicks (a button), so the game can leave the click alone.</summary>
        public static bool MouseOverUI { get; private set; }

        /// <summary>What was laid out last frame, in the order drawn (so the last is on top): for an editor picking pieces out.</summary>
        internal static IReadOnlyList<Element> Elements => _elements;

        /// <summary>Lays out and draws the scene's UI, and clicks its buttons. <paramref name="interactive"/> false draws it without the mouse doing anything.</summary>
        internal static void Update(Scene scene, bool interactive = true)
        {
            Layout(scene);

            // The topmost button under the mouse gets it; the cursor must be free to point with.
            Vector2 mouse = Canvas.MousePosition;
            bool canPoint = interactive && !InputSystem.CursorLocked;
            UIButton? hovered = null;
            if (canPoint)
            {
                for (int i = _elements.Count - 1; i >= 0 && hovered == null; i--)
                {
                    if (!_elements[i].Rect.Contains(mouse) || !Canvas.InClip(mouse)) continue;
                    foreach (Component component in _elements[i].GameObject.Components)
                    {
                        if (component is UIButton { Enabled: true, Interactable: true } button) hovered = button;
                    }
                }
            }

            MouseOverUI = hovered != null;
            if (canPoint && hovered != null && InputSystem.GetMouseButtonDown(MouseButton.Left)) _pressed = hovered;
            if (!InputSystem.GetMouseButton(MouseButton.Left))
            {
                UIButton? released = _pressed;
                _pressed = null;
                if (released != null && released == hovered && InputSystem.GetMouseButtonUp(MouseButton.Left)) released.Click();
            }

            foreach (Element element in _elements)
            {
                foreach (Component component in element.GameObject.Components)
                {
                    if (component is not UIGraphic { Enabled: true } graphic) continue;
                    bool over = graphic == hovered;
                    graphic.Draw(element.Rect.ScreenPosition, element.Rect.ScreenSize, over, over && _pressed == graphic);
                }
            }
        }

        /// <summary>Works out where every piece of the scene's UI goes on the screen, without drawing it.</summary>
        internal static void Layout(Scene scene)
        {
            _elements.Clear();
            ScreenSize = Canvas.Size;

            var canvases = new List<UICanvas>();
            foreach (GameObject gameObject in scene.GameObjects)
            {
                if (!gameObject.ActiveInHierarchy) continue;
                foreach (Component component in gameObject.Components)
                {
                    if (component is UICanvas { Enabled: true } canvas) canvases.Add(canvas);
                }
            }

            // Highest order last, so on top; a canvas inside another is laid out with it, not again.
            canvases.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (UICanvas canvas in canvases)
            {
                if (HasCanvasAbove(canvas.GameObject)) continue;
                LayoutTree(canvas.GameObject, Vector2.Zero, Canvas.Size);
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

        private static void LayoutTree(GameObject gameObject, Vector2 parentPosition, Vector2 parentSize)
        {
            if (!gameObject.Active) return;

            if (gameObject.GetComponent<UIRect>() is { } rect)
            {
                rect.Layout(parentPosition, parentSize);
                _elements.Add(new Element(gameObject, rect));
                parentPosition = rect.ScreenPosition;
                parentSize = rect.ScreenSize;
            }

            foreach (Transform child in gameObject.Transform.Children) LayoutTree(child.GameObject, parentPosition, parentSize);
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
            MouseOverUI = false;
        }
    }
}
