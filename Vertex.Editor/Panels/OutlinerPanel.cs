using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The panel on the left: every object in the scene as a tree, children under their parents. Click to select,
    /// double-click or F2 to rename, drag one onto another to put it under it, right-click for more. The search box
    /// narrows it to objects whose names match. The arrow keys move through it once it's been clicked in.
    /// </summary>
    public sealed class OutlinerPanel
    {
        private readonly EditorContext _context;

        private readonly HashSet<GameObject> _expanded = new();

        private string _search = "";

        // The object being renamed in place, and its new name so far.
        private GameObject? _renaming;
        private string _renameText = "";

        // Dragging an object to put it under another.
        private GameObject? _pressed;
        private Vector2 _pressedAt;
        private GameObject? _dragging;
        private GameObject? _dropTarget;
        private bool _dropOnRoot;

        // The rows drawn last frame, in order, for the arrow keys.
        private readonly List<GameObject> _rows = new();
        private readonly List<GameObject> _drawing = new();

        private int _shownSelection = -1;

        // Clicked in last, so the arrow keys are its.
        private bool _focused;

        private const float _rowHeight = 24f;
        private const float _indent = 16f;

        private float _scrollTop;
        private float _selectedY = -1f;
        private int _scrollId;

        public OutlinerPanel(EditorContext context)
        {
            _context = context;
        }

        public void Draw(Rect rect)
        {
            Rect content = PanelFrame.Draw(rect, "Outliner");

            int addId = Ui.Id("outliner.add");
            Rect addButton = PanelFrame.HeaderButton(rect, 0);
            if (Widgets.IconButton(addId, addButton, Icons.Plus, "Create an object"))
            {
                Menu.Open(addId, new Vector2(addButton.X, addButton.Bottom + 4f), CreateMenu.Items(_context));
            }

            if (Ui.MousePressed() || Ui.MousePressed(MouseButton.Right)) _focused = rect.Contains(Ui.Mouse) && !Menu.IsAnyOpen;

            // The search box.
            Rect searchRect = content.TakeTop(28f + Theme.Padding).Shrink(Theme.Padding, 0f).CutTop(Theme.Padding - 4f);
            TextInput.Draw(Ui.Id("outliner.search"), searchRect.TakeTop(28f), ref _search, "Search", live: true, icon: Icons.Search);
            if (_search.Length > 0)
            {
                Rect clear = searchRect.TakeTop(28f).TakeRight(26f).Shrink(3f);
                if (Widgets.IconButton(Ui.Id("outliner.clear"), clear, Icons.Close, "Clear search")) _search = "";
            }

            RevealSelection();

            Rect list = content.CutTop(28f + Theme.Padding + 4f);
            _scrollId = Ui.Id("outliner.scroll");
            var scroll = ScrollView.Begin(_scrollId, list.Shrink(4f, 0f));
            _scrollTop = scroll.Top;
            _drawing.Clear();
            _selectedY = -1f;
            _dropTarget = null;
            _dropOnRoot = false;

            float x = list.X + 6f;
            float width = list.Width - 12f;
            float y = scroll.Top + 2f;

            // The selection's highlight, drawn under the rows, slides from row to row.
            DrawSelectionHighlight(x, width);

            y = DrawSceneRow(x, y, width);

            string search = _search.Trim();
            if (search.Length > 0) y = DrawMatches(search, x, y, width);
            else
            {
                foreach (GameObject gameObject in _context.Scene.GameObjects)
                {
                    if (gameObject.Transform.Parent == null) y = DrawTree(gameObject, 0, x, y, width);
                }
            }

            // The space below the rows: dropping there takes an object to the top level, and clicking it clears the selection.
            var below = new Rect(list.X, y, list.Width, MathF.Max(list.Bottom - y, 0f));
            Interaction empty = Ui.Interact(Ui.Id("outliner.empty"), below, rightClick: true);
            if (empty.Clicked) _context.Selection.Clear();
            if (empty.RightClicked) Menu.Open(Ui.Id("outliner.empty"), Ui.Mouse, CreateMenu.Items(_context));
            if (_dragging != null && below.Contains(Ui.Mouse) && Canvas_InClip()) _dropOnRoot = true;

            scroll.End(y - scroll.Top + 8f);

            _rows.Clear();
            _rows.AddRange(_drawing);

            UpdateDrag(list);
            if (_focused && !Ui.KeyboardTaken && !Menu.IsAnyOpen) HandleKeys();
        }

        private static bool Canvas_InClip() => Vertex.UI.Canvas.InClip(Ui.Mouse);

        // ---------------------------------------------------------------- Rows

        private float DrawSceneRow(float x, float y, float width)
        {
            var row = new Rect(x, y, width, _rowHeight);
            int id = Ui.Id("outliner.sceneRow");
            Interaction interaction = Ui.Interact(id, row);
            if (interaction.Pressed) _context.Selection.SelectScene();
            if (_context.Selection.Scene) _selectedY = row.Y;

            HoverBackground(id, row, interaction.Hovered);
            Icons.Scene(new Vector2(row.X + 12f, row.Centre.Y), 15f, Theme.AccentText);
            Ui.Text(new Rect(row.X + 26f, row.Y, row.Width - 30f, row.Height), _context.SceneName, Theme.Text, bold: true);
            return y + _rowHeight + 2f;
        }

        private float DrawTree(GameObject gameObject, int depth, float x, float y, float width)
        {
            y = DrawRow(gameObject, depth, x, y, width, showArrow: true, highlight: null);

            IReadOnlyList<Transform> children = gameObject.Transform.Children;
            if (children.Count == 0) return y;

            // Children slide open and shut: drawn into a window that grows to fit them.
            int id = RuntimeHelpers.GetHashCode(gameObject);
            bool open = _expanded.Contains(gameObject);
            float openness = Ui.Smooth(Ui.Id(id, "open"), open ? 1f : 0f, 18f);
            if (openness < 0.001f) return y;

            int heightKey = Ui.Id(id, "height");
            float shown = Ui.Recall(heightKey) * openness;
            Ui.PushClip(new Rect(x - 4f, y, width + 8f, shown));

            float top = y;
            int rowsBefore = _drawing.Count;
            foreach (Transform child in children) y = DrawTree(child.GameObject, depth + 1, x, y, width);
            Ui.PopClip();

            // While closing, the rows under it aren't for the arrow keys.
            if (!open) _drawing.RemoveRange(rowsBefore, _drawing.Count - rowsBefore);

            Ui.Remember(heightKey, y - top);
            return top + shown;
        }

        private float DrawMatches(string search, float x, float y, float width)
        {
            foreach (GameObject gameObject in _context.Scene.GameObjects)
            {
                int at = gameObject.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) y = DrawRow(gameObject, 0, x, y, width, showArrow: false, highlight: (at, search.Length));
            }

            if (_drawing.Count == 0) Ui.Text(new Rect(x, y + 6f, width, 20f), "No objects match", Theme.TextFaint, align: 0.5f);
            return y;
        }

        private float DrawRow(GameObject gameObject, int depth, float x, float y, float width, bool showArrow, (int Start, int Length)? highlight)
        {
            _drawing.Add(gameObject);
            int id = RuntimeHelpers.GetHashCode(gameObject);
            var row = new Rect(x, y, width, _rowHeight);
            bool selected = _context.Selection.Object == gameObject;
            if (selected) _selectedY = row.Y;

            float indent = depth * _indent;
            bool hasChildren = gameObject.Transform.Children.Count > 0;

            // The arrow, which opens and closes without selecting.
            var arrow = new Rect(row.X + indent, row.Y, 18f, row.Height);
            if (showArrow && hasChildren)
            {
                Interaction arrowInteraction = Ui.Interact(Ui.Id(id, "arrow"), arrow);
                if (arrowInteraction.Pressed) Toggle(gameObject);
            }

            Interaction interaction = Ui.Interact(Ui.Id(id, "row"), row, rightClick: true);
            if (interaction.Pressed)
            {
                _context.Selection.Select(gameObject);
                _pressed = gameObject;
                _pressedAt = Ui.Mouse;
            }

            if (interaction.DoubleClicked) BeginRename(gameObject);
            if (interaction.RightClicked)
            {
                _context.Selection.Select(gameObject);
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, RowMenu(gameObject));
            }

            // Where a dragged object would go.
            bool dropHere = _dragging != null && _dragging != gameObject && row.Contains(Ui.Mouse) && Canvas_InClip() && !IsUnder(gameObject, _dragging);
            if (dropHere) _dropTarget = gameObject;

            if (!selected) HoverBackground(Ui.Id(id, "row"), row, interaction.Hovered && _dragging == null);
            if (dropHere) Ui.Outline(row, Theme.Accent, 4f, 1.5f);

            bool active = gameObject.ActiveInHierarchy;
            Color textColor = selected ? Color.White : active ? Theme.Text : Theme.TextFaint;
            Color iconColor = selected ? Color.White : active ? Theme.TextDim : Theme.TextFaint;

            if (showArrow && hasChildren)
            {
                float turn = Ui.Smooth(Ui.Id(id, "turn"), _expanded.Contains(gameObject) ? 90f : 0f, 22f);
                Icons.Chevron(arrow.Centre, 13f, iconColor, turn);
            }

            float iconX = row.X + indent + 18f;
            IconFor(gameObject)(new Vector2(iconX + 8f, row.Centre.Y), 15f, iconColor);

            var nameRect = new Rect(iconX + 22f, row.Y, row.Right - iconX - 24f, row.Height);
            if (_renaming == gameObject) DrawRename(nameRect);
            else if (highlight is (int start, int length)) DrawHighlighted(nameRect, gameObject.Name, start, length, textColor);
            else Ui.Text(nameRect, gameObject.Name, textColor);

            return y + _rowHeight;
        }

        private void HoverBackground(int id, Rect row, bool hovered)
        {
            float hover = Ui.Smooth(Ui.Id(id, "hover"), hovered ? 1f : 0f, 20f);
            if (hover > 0.01f) Ui.Rect(row, Theme.Hover.WithAlpha(hover * 1.4f), 4f);
        }

        private void DrawSelectionHighlight(float x, float width)
        {
            // Where the selected row was last frame: rows aren't laid out yet. It eases there, so it slides when the
            // selection changes; a selection out of view, or just made, appears where it is.
            int key = Ui.Id("outliner.highlight");
            float target = Ui.Recall(Ui.Id("outliner.selectedRow"), -1f);
            if (target < 0f)
            {
                Ui.Snap(key, -1f);
                return;
            }

            float current = Ui.Recall(key, -1f);
            float y = current < 0f || MathF.Abs(current - target) > 400f ? target : Ui.Smooth(key, target, 26f);
            Ui.Snap(key, y);

            float rowY = _scrollTop + y;
            Ui.Rect(new Rect(x, rowY, width, _rowHeight), Theme.Accent.WithAlpha(0.9f), 4f);
        }

        private static void DrawHighlighted(Rect rect, string name, int start, int length, Color color)
        {
            // The part that matches the search stands out.
            float x = rect.X;
            float lineY = rect.Y + (rect.Height - Ui.LineHeight()) * 0.5f;
            x += Ui.Text(name[..start], new Vector2(x, lineY), color);
            float matchWidth = Ui.TextWidth(name.Substring(start, length));
            Ui.Rect(new Rect(x - 1f, rect.Y + 4f, matchWidth + 2f, rect.Height - 8f), Theme.Accent.WithAlpha(0.3f), 3f);
            x += Ui.Text(name.Substring(start, length), new Vector2(x, lineY), Theme.AccentText);
            Ui.Text(name[(start + length)..], new Vector2(x, lineY), color);
        }

        public static Icon IconFor(GameObject gameObject)
        {
            foreach (Component component in gameObject.Components)
            {
                if (component is Camera) return Icons.Camera;
                if (component is Light) return Icons.Light;
            }

            foreach (Component component in gameObject.Components)
            {
                if (component is MeshRenderer) return Icons.Cube;
            }

            return gameObject.Components.Count == 0 ? Icons.Empty : Icons.Component;
        }

        // ---------------------------------------------------------------- Renaming

        private void BeginRename(GameObject gameObject)
        {
            _renaming = gameObject;
            _renameText = gameObject.Name;
            TextInput.Focus(Ui.Id("outliner.rename"), selectAll: true);
        }

        private void DrawRename(Rect rect)
        {
            int id = Ui.Id("outliner.rename");
            GameObject gameObject = _renaming!;
            if (TextInput.Draw(id, rect.Grow(1f) with { X = rect.X - 6f, Width = rect.Width + 6f }, ref _renameText) && _renameText.Trim().Length > 0)
            {
                SetValue.Apply(_context.History, "Rename", gameObject, typeof(GameObject).GetProperty(nameof(GameObject.Name))!, _renameText.Trim());
            }

            if (Ui.FocusId != id) _renaming = null;
        }

        // ---------------------------------------------------------------- Menus and keys

        private List<MenuItem> RowMenu(GameObject gameObject)
        {
            var items = new List<MenuItem>
            {
                new("Rename", () => BeginRename(gameObject)) { Shortcut = "F2" },
                new("Delete", () => _context.Delete(gameObject)) { Shortcut = "Del", Icon = Icons.Trash }
            };

            if (gameObject.Transform.Parent != null)
            {
                items.Add(new MenuItem("Move to Top Level", () => _context.History.Do(new Reparent(gameObject, null))));
            }

            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Create Empty Child", () =>
            {
                _expanded.Add(gameObject);
                _context.Create("Empty", parent: gameObject.Transform);
            }) { Icon = Icons.Plus });
            return items;
        }

        private void HandleKeys()
        {
            GameObject? selected = _context.Selection.Object;
            int index = selected == null ? -1 : _rows.IndexOf(selected);

            if (InputSystem.GetKeyDown(Key.Down) && _rows.Count > 0) _context.Selection.Select(_rows[Math.Min(index + 1, _rows.Count - 1)]);
            if (InputSystem.GetKeyDown(Key.Up) && _rows.Count > 0) _context.Selection.Select(_rows[Math.Max(index - 1, 0)]);
            if (selected == null) return;

            if (InputSystem.GetKeyDown(Key.Right) && selected.Transform.Children.Count > 0) _expanded.Add(selected);
            if (InputSystem.GetKeyDown(Key.Left))
            {
                if (_expanded.Contains(selected)) _expanded.Remove(selected);
                else if (selected.Transform.Parent != null) _context.Selection.Select(selected.Transform.Parent.GameObject);
            }

            if (InputSystem.GetKeyDown(Key.F2)) BeginRename(selected);
        }

        private void Toggle(GameObject gameObject)
        {
            if (!_expanded.Remove(gameObject)) _expanded.Add(gameObject);
        }

        /// <summary>When something new is selected (in the scene view, say), opens its parents and scrolls it into view.</summary>
        private void RevealSelection()
        {
            // Last frame's row position, saved for the highlight.
            Ui.Remember(Ui.Id("outliner.selectedRow"), _selectedY >= 0f ? _selectedY - _scrollTop : -1f);

            if (_shownSelection == _context.Selection.Version) return;
            _shownSelection = _context.Selection.Version;

            if (_context.Selection.Object is not { } selected) return;
            for (Transform? parent = selected.Transform.Parent; parent != null; parent = parent.Parent) _expanded.Add(parent.GameObject);

            int index = _rows.IndexOf(selected);
            if (index >= 0) ScrollView.Reveal(_scrollId, index * _rowHeight, (index + 2) * _rowHeight + 8f, 200f);
        }

        // ---------------------------------------------------------------- Dragging

        private void UpdateDrag(Rect list)
        {
            if (_pressed != null && _dragging == null && Ui.MouseHeld() && Vector2.Distance(Ui.Mouse, _pressedAt) > 5f) _dragging = _pressed;

            if (_dragging != null)
            {
                GameObject dragged = _dragging;
                Vector2 mouse = Ui.Mouse;
                Ui.Overlay(() =>
                {
                    // What's being dragged follows the mouse.
                    string text = Ui.Fit(dragged.Name, 180f);
                    var label = new Rect(mouse.X + 14f, mouse.Y + 6f, Ui.TextWidth(text) + 36f, 24f);
                    Ui.Shadow(label, 5f, 10f, 0.6f);
                    Ui.Rect(label, Theme.Popup, 5f);
                    IconFor(dragged)(new Vector2(label.X + 13f, label.Centre.Y), 14f, Theme.TextDim);
                    Ui.Text(label.CutLeft(26f), text, Theme.Text);
                });

                if (_dropOnRoot && dragged.Transform.Parent != null)
                {
                    Ui.Rect(new Rect(list.X + 6f, list.Bottom - 3f, list.Width - 12f, 2f), Theme.Accent, 1f);
                }
            }

            if (!Ui.MouseReleased()) return;

            if (_dragging != null)
            {
                if (_dropTarget != null)
                {
                    _context.History.Do(new Reparent(_dragging, _dropTarget.Transform));
                    _expanded.Add(_dropTarget);
                }
                else if (_dropOnRoot && _dragging.Transform.Parent != null)
                {
                    _context.History.Do(new Reparent(_dragging, null));
                }
            }

            _pressed = null;
            _dragging = null;
        }

        /// <summary>Whether <paramref name="gameObject"/> is <paramref name="ancestor"/> or somewhere under it.</summary>
        private static bool IsUnder(GameObject gameObject, GameObject ancestor)
        {
            for (Transform? t = gameObject.Transform; t != null; t = t.Parent)
            {
                if (t.GameObject == ancestor) return true;
            }

            return false;
        }
    }
}
