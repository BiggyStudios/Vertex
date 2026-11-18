using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>
    /// Arranging the scene's UI in the game's picture, while not playing: click a piece to select it, drag it to move it,
    /// drag its handles to resize it, and the arrow keys nudge it. Hold Ctrl to snap to the grid (or not, if snapping is
    /// on). Each drag is one change to undo.
    /// </summary>
    public sealed class UIEditing
    {
        private readonly EditorContext _context;

        // The piece being dragged, by which handle (0, 0 for the middle: moving), and where it and the mouse were.
        private UIRect? _dragged;
        private Vector2 _handle;
        private Vector2 _pressedMouse, _pressedPosition, _pressedSize;
        private Vector2 _startPosition, _startSize;
        private bool _moved;

        private const float _gridStep = 10f;

        public UIEditing(EditorContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Draws the scene's UI into <paramref name="viewport"/> as the game would, then the editing on top. Call with the
        /// canvas back in the editor's own units; <paramref name="focused"/> says whether the keys are the game view's.
        /// </summary>
        public void Draw(Rect viewport, bool focused)
        {
            // The UI, laid out and drawn as on a screen the size of the picture.
            Vector2 origin = viewport.Position * Canvas.Scale;
            Canvas.BeginView(origin, Math.Max((int)(viewport.Width * Canvas.Scale), 1), Math.Max((int)(viewport.Height * Canvas.Scale), 1));
            UISystem.Update(_context.Scene, interactive: false);
            Vector2 viewSize = Canvas.Size;
            Canvas.EndView();

            // From the UI's units to the editor's, and back.
            float scale = viewport.Width / MathF.Max(viewSize.X, 1f);
            Vector2 ToEditor(Vector2 point) => viewport.Position + point * scale;
            Vector2 ToView(Vector2 point) => (point - viewport.Position) / scale;

            IReadOnlyList<UISystem.Element> elements = UISystem.Elements;
            int id = Ui.Id("uiEditing");
            Interaction interaction = Ui.Interact(id, viewport);
            Ui.PushClip(viewport);

            if (elements.Count == 0)
            {
                string hint = HasCanvas() ? "This canvas is empty: Create ▸ UI Panel, Text, Image or Button" : "No UI yet: Create ▸ UI Canvas, or any UI piece";
                Hint(viewport, hint);
                if (interaction.Pressed) _context.Selection.Clear();
                Ui.PopClip();
                return;
            }

            UIRect? selected = _context.Selection.Object?.GetComponent<UIRect>();
            if (selected != null && !elements.Any(e => e.Rect == selected)) selected = null;

            // What's under the mouse: the topmost piece, as the game would see it.
            Vector2 mouse = ToView(Ui.Mouse);
            UIRect? hovered = null;
            for (int i = elements.Count - 1; i >= 0 && hovered == null; i--)
            {
                if (elements[i].Rect.Contains(mouse)) hovered = elements[i].Rect;
            }

            // The selected piece's handles, which take a press before the pieces do.
            Vector2? handleUnder = null;
            if (selected != null && _dragged == null)
            {
                foreach (Vector2 handle in Handles)
                {
                    if (Vector2.Distance(Ui.Mouse, HandlePoint(selected, handle, ToEditor)) < 7f) handleUnder = handle;
                }
            }

            if (interaction.Pressed)
            {
                if (handleUnder is Vector2 handle && selected != null) Press(selected, handle, mouse);
                else if (hovered != null)
                {
                    _context.Selection.Select(hovered.GameObject);
                    Press(hovered, Vector2.Zero, mouse);
                }
                else _context.Selection.Clear();
            }

            if (_dragged != null) Drag(mouse);

            // Outlines: the hovered piece faintly, the selected one with its handles and the anchors it keeps to.
            if (hovered != null && hovered != selected && _dragged == null) Outline(hovered, ToEditor, Theme.AccentText.WithAlpha(0.45f), 1f);
            if (selected != null)
            {
                DrawAnchors(selected, ToEditor);
                Outline(selected, ToEditor, Theme.Accent, 1.5f);
                foreach (Vector2 handle in Handles)
                {
                    Vector2 point = HandlePoint(selected, handle, ToEditor);
                    bool lit = handleUnder == handle || _dragged == selected && _handle == handle;
                    Ui.Rect(new Rect(point - new Vector2(4.5f), new Vector2(9f)), lit ? Theme.AccentHover : Color.White, 2f);
                    Ui.Outline(new Rect(point - new Vector2(4.5f), new Vector2(9f)), Theme.Accent, 2f, 1.2f);
                }

                if (handleUnder is Vector2 under) Ui.Cursor = CursorFor(under);
                else if (hovered == selected && interaction.Hovered) Ui.Cursor = CursorShape.Hand;

                // Its size, under it.
                Vector2 bottom = ToEditor(selected.ScreenPosition + new Vector2(selected.ScreenSize.X * 0.5f, selected.ScreenSize.Y));
                string size = $"{selected.ScreenSize.X:0} × {selected.ScreenSize.Y:0}";
                var chip = new Rect(bottom.X - (Ui.TextWidth(size, Theme.SmallTextSize) + 14f) * 0.5f, bottom.Y + 8f, Ui.TextWidth(size, Theme.SmallTextSize) + 14f, 18f);
                Ui.Rect(chip, Theme.Accent, 9f);
                Ui.Text(chip, size, Color.White, align: 0.5f, size: Theme.SmallTextSize);

                if (focused && !Ui.KeyboardTaken) Nudge(selected);
            }

            Ui.PopClip();
            Hint(viewport, "Arranging UI  ·  drag to move, handles to resize, arrows to nudge, Ctrl to snap");
        }

        private bool HasCanvas() => _context.Scene.GameObjects.Any(o => o.GetComponent<UICanvas>() != null);

        private static readonly Vector2[] Handles =
        [
            new(-1f, -1f), new(0f, -1f), new(1f, -1f),
            new(-1f, 0f), new(1f, 0f),
            new(-1f, 1f), new(0f, 1f), new(1f, 1f)
        ];

        private static Vector2 HandlePoint(UIRect rect, Vector2 handle, Func<Vector2, Vector2> toEditor) =>
            toEditor(rect.ScreenPosition + rect.ScreenSize * (handle + Vector2.One) * 0.5f);

        private static CursorShape CursorFor(Vector2 handle) =>
            handle.X == 0f ? CursorShape.ResizeVertical : handle.Y == 0f ? CursorShape.ResizeHorizontal : CursorShape.Crosshair;

        private void Press(UIRect rect, Vector2 handle, Vector2 mouse)
        {
            _dragged = rect;
            _handle = handle;
            _pressedMouse = mouse;
            _pressedPosition = rect.ScreenPosition;
            _pressedSize = rect.ScreenSize;
            _startPosition = rect.Position;
            _startSize = rect.Size;
            _moved = false;
        }

        /// <summary>Moves or resizes the dragged piece to follow the mouse, then records it as one change when let go.</summary>
        private void Drag(Vector2 mouse)
        {
            UIRect rect = _dragged!;
            if (!Ui.MouseHeld())
            {
                if (_moved) Record(rect, _handle == Vector2.Zero ? "Move" : "Resize");
                _dragged = null;
                return;
            }

            Vector2 delta = mouse - _pressedMouse;
            if (!_moved && delta.Length() < 3f) return;
            _moved = true;

            bool snap = _context.Snap.Enabled ^ (InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight));
            Vector2 position = _pressedPosition, size = _pressedSize;
            if (_handle == Vector2.Zero)
            {
                position += delta;
                if (snap) position = Round(position);
            }
            else
            {
                // Each edge the handle's on follows the mouse; the opposite one stays put.
                Vector2 left = position, right = position + size;
                if (_handle.X < 0f) left.X = Snapped(left.X + delta.X, snap);
                if (_handle.X > 0f) right.X = Snapped(right.X + delta.X, snap);
                if (_handle.Y < 0f) left.Y = Snapped(left.Y + delta.Y, snap);
                if (_handle.Y > 0f) right.Y = Snapped(right.Y + delta.Y, snap);
                if (right.X - left.X < 4f) { if (_handle.X < 0f) left.X = right.X - 4f; else right.X = left.X + 4f; }
                if (right.Y - left.Y < 4f) { if (_handle.Y < 0f) left.Y = right.Y - 4f; else right.Y = left.Y + 4f; }
                position = left;
                size = right - left;
            }

            Place(rect, position, size);
        }

        private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X / _gridStep) * _gridStep, MathF.Round(v.Y / _gridStep) * _gridStep);

        private static float Snapped(float value, bool snap) => snap ? MathF.Round(value / _gridStep) * _gridStep : MathF.Round(value);

        /// <summary>Sets a piece's position and size so it lands at <paramref name="position"/>/<paramref name="size"/> on the screen, keeping its anchors.</summary>
        private static void Place(UIRect rect, Vector2 position, Vector2 size)
        {
            (Vector2 parentPosition, Vector2 parentSize) = UISystem.ParentRect(rect.GameObject);
            (Vector2 newPosition, Vector2 newSize) = rect.PlacementFor(position, size, parentPosition, parentSize);
            rect.Position = new Vector2(MathF.Round(newPosition.X, 2), MathF.Round(newPosition.Y, 2));
            rect.Size = new Vector2(MathF.Round(newSize.X, 2), MathF.Round(newSize.Y, 2));

            // Laid out again now, so the outline and handles are where it is this frame, not where it was.
            rect.Layout(parentPosition, parentSize);
        }

        private void Record(UIRect rect, string name)
        {
            Type type = typeof(UIRect);
            _context.History.Record(new ChangeGroup($"{name} {rect.GameObject.Name}",
            [
                new SetValue(name, rect, type.GetProperty(nameof(UIRect.Position))!, _startPosition, rect.Position),
                new SetValue(name, rect, type.GetProperty(nameof(UIRect.Size))!, _startSize, rect.Size)
            ]));
        }

        /// <summary>The arrow keys move the selected piece a unit (ten with Shift).</summary>
        private void Nudge(UIRect rect)
        {
            Vector2 step = Vector2.Zero;
            if (InputSystem.GetKeyDown(Key.Left)) step.X -= 1f;
            if (InputSystem.GetKeyDown(Key.Right)) step.X += 1f;
            if (InputSystem.GetKeyDown(Key.Up)) step.Y -= 1f;
            if (InputSystem.GetKeyDown(Key.Down)) step.Y += 1f;
            if (step == Vector2.Zero) return;

            if (InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight)) step *= 10f;
            SetValue.Apply(_context.History, "Nudge", rect, typeof(UIRect).GetProperty(nameof(UIRect.Position))!, rect.Position + step);
        }

        private static void Outline(UIRect rect, Func<Vector2, Vector2> toEditor, Color color, float thickness)
        {
            Vector2 from = toEditor(rect.ScreenPosition), to = toEditor(rect.ScreenPosition + rect.ScreenSize);
            Ui.Outline(new Rect(from, to - from), color, 0f, thickness);
        }

        /// <summary>Little marks at the points of the parent the piece keeps to, joined by a line when it stretches.</summary>
        private static void DrawAnchors(UIRect rect, Func<Vector2, Vector2> toEditor)
        {
            (Vector2 parentPosition, Vector2 parentSize) = UISystem.ParentRect(rect.GameObject);
            Vector2 min = toEditor(parentPosition + rect.AnchorMin * parentSize), max = toEditor(parentPosition + rect.AnchorMax * parentSize);
            Color color = Theme.Hex(0xF2C14E);
            if (min != max) Ui.Outline(new Rect(min, max - min), color.WithAlpha(0.5f), 0f, 1f);

            foreach (Vector2 corner in new[] { min, max, new Vector2(min.X, max.Y), new Vector2(max.X, min.Y) }.Distinct())
            {
                Vertex.UI.Canvas.DrawTriangle(corner, corner + new Vector2(-5f, -9f), corner + new Vector2(5f, -9f), Ui.Fade(color));
            }
        }

        private static void Hint(Rect viewport, string text)
        {
            float width = Ui.TextWidth(text, Theme.SmallTextSize) + 24f;
            var chip = new Rect(viewport.Centre.X - width * 0.5f, viewport.Bottom - 34f, width, 24f);
            Ui.Rect(chip, Theme.Popup.WithAlpha(0.92f), 12f);
            Ui.Text(chip, text, Theme.TextDim, align: 0.5f, size: Theme.SmallTextSize);
        }
    }
}
