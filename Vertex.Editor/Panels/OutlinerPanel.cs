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
    public sealed class OutlinerPanel : IDockPanel
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

        // Where on the target it would go: before it, after it, or under it.
        private enum DropZone { Before, Inside, After }
        private DropZone _dropZone;
        private bool _dropOnRoot;

        // The rows drawn last frame, in order, for the arrow keys.
        private readonly List<GameObject> _rows = new();
        private readonly List<GameObject> _drawing = new();

        private int _shownSelection = -1;

        // The selection's version when the rows were last drawn, to tell if it changed while they were.
        private int _measuredVersion = -1;

        // Clicked in last, so the arrow keys are its.
        private bool _focused;

        private const float _rowHeight = 24f;
        private const float _indent = 16f;

        private float _scrollTop;
        private float _selectedY = -1f;
        private int _scrollId;

        // How many parents being folded shut the rows now being drawn are under.
        private int _closing;

        public string Title => "Outliner";
        public Icon Icon => Icons.Tree;

        public OutlinerPanel(EditorContext context)
        {
            _context = context;
            context.RenameRequested += BeginRename;
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

            // A prefab open in its tab: its name, and (its scene being just for showing it) nothing to select.
            if (_context.Stage is { } stage)
            {
                HoverBackground(id, row, false);
                Icons.Prefab(new Vector2(row.X + 12f, row.Centre.Y), 14f, Theme.Hex(0x6FA8FF));
                Ui.Text(new Rect(row.X + 26f, row.Y, row.Width - 30f, row.Height), $"{stage.Title}  ·  prefab", Theme.Hex(0x8FBCFF), bold: true);
                return y + _rowHeight + 2f;
            }

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
            if (!open) _closing++;
            foreach (Transform child in children) y = DrawTree(child.GameObject, depth + 1, x, y, width);
            if (!open) _closing--;
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
            bool main = _context.Selection.Object == gameObject;
            bool selected = main || _context.Selection.Count > 1 && _context.Selection.Contains(gameObject);
            // Not while it's sliding away under a parent being folded: the highlight leaves straight away.
            if (main && _closing == 0) _selectedY = row.Y;

            // Rows scrolled out of sight take their place but aren't drawn: big scenes have thousands.
            if (Vertex.UI.Canvas.ClipRect is var (clipAt, clipSize) && (row.Bottom < clipAt.Y || row.Y > clipAt.Y + clipSize.Y)) return y + _rowHeight;

            float indent = depth * _indent;
            bool hasChildren = gameObject.Transform.Children.Count > 0;

            // The arrow opens and closes without selecting. It's part of the row (a control of its own under the row
            // would never get the mouse: the row, drawn after, is on top), so a press on it is told apart by where it is.
            var arrow = new Rect(row.X + indent, row.Y, 20f, row.Height);
            bool onArrow = showArrow && hasChildren && arrow.Contains(Ui.Mouse);

            Interaction interaction = Ui.Interact(Ui.Id(id, "row"), row, rightClick: true);
            if (interaction.Pressed && onArrow) Toggle(gameObject);
            else if (interaction.Pressed) Press(gameObject);

            if (interaction.DoubleClicked && !onArrow && !Ctrl && !Shift) BeginRename(gameObject);
            if (interaction.RightClicked)
            {
                // Right-clicking one of several selected keeps them all, for the menu to act on.
                if (!_context.Selection.Contains(gameObject)) _context.Selection.Select(gameObject);
                else _context.Selection.Add(gameObject);
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, RowMenu(gameObject));
            }

            // Where a dragged object would go.
            bool dropHere = _dragging != null && !_draggingAll.Contains(gameObject) && row.Contains(Ui.Mouse) && Canvas_InClip() && !_draggingAll.Any(d => IsUnder(gameObject, d));
            if (dropHere)
            {
                _dropTarget = gameObject;
                float along = (Ui.Mouse.Y - row.Y) / row.Height;
                _dropZone = along < 0.28f ? DropZone.Before : along > 0.72f ? DropZone.After : DropZone.Inside;
            }

            if (!selected) HoverBackground(Ui.Id(id, "row"), row, interaction.Hovered && _dragging == null);

            // The main selection's highlight slides from row to row (drawn under the rows); the others' sit still.
            else if (!main) Ui.Rect(row, Theme.Accent.WithAlpha(0.5f), 4f);
            if (dropHere)
            {
                // A line between rows to go there, or an outline to go under this one.
                float lineX = row.X + indent + 16f;
                if (_dropZone == DropZone.Inside) Ui.Outline(row, Theme.Accent, 4f, 1.5f);
                else
                {
                    float lineY = _dropZone == DropZone.Before ? row.Y : row.Bottom;
                    Ui.Rect(new Rect(lineX, lineY - 1f, row.Right - lineX, 2f), Theme.Accent, 1f);
                    Ui.Circle(new Vector2(lineX, lineY), 3f, Theme.Accent);
                }
            }

            // A copy of a prefab is blue, like the prefab in the assets.
            bool active = gameObject.ActiveInHierarchy;
            bool prefab = gameObject.Prefab != null;
            Color textColor = selected ? Color.White : !active ? Theme.TextFaint : prefab ? Theme.Hex(0x8FBCFF) : Theme.Text;
            Color iconColor = selected ? Color.White : !active ? Theme.TextFaint : prefab ? Theme.Hex(0x6FA8FF) : Theme.TextDim;

            if (showArrow && hasChildren)
            {
                float turn = Ui.Smooth(Ui.Id(id, "turn"), _expanded.Contains(gameObject) ? 90f : 0f, 22f);
                if (onArrow && interaction.Hovered && _dragging == null) Ui.Rect(arrow.Shrink(2f, 3f), selected ? Color.White.WithAlpha(0.15f) : Theme.Hover.WithAlpha(2.5f), 4f);
                Icons.Chevron(arrow.Centre, 13f, onArrow && interaction.Hovered && !selected ? Theme.Text : iconColor, turn);
            }

            float iconX = row.X + indent + 18f;
            (prefab ? Icons.Prefab : IconFor(gameObject))(new Vector2(iconX + 8f, row.Centre.Y), prefab ? 13f : 15f, iconColor);

            var nameRect = new Rect(iconX + 22f, row.Y, row.Right - iconX - 24f, row.Height);
            if (_renaming == gameObject) DrawRename(nameRect);
            else if (highlight is (int start, int length)) DrawHighlighted(nameRect, gameObject.Name, start, length, textColor);
            else Ui.Text(nameRect, gameObject.Name, textColor);

            return y + _rowHeight;
        }

        private static bool Ctrl => InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
        private static bool Shift => InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

        // Where a Shift-click's range starts: the row last clicked without Shift.
        private GameObject? _anchor;

        // A press on one of several selected rows: it may be the start of dragging them all, so they stay selected
        // until it's let go without dragging, which selects just it.
        private bool _selectOnRelease;

        /// <summary>
        /// A row pressed: selects it, or with Ctrl adds it to the selection (or takes it out), or with Shift selects every
        /// row from the last one clicked to it.
        /// </summary>
        private void Press(GameObject gameObject)
        {
            Selection selection = _context.Selection;
            _selectOnRelease = false;

            // From the row last clicked, if it's still selected; otherwise from whatever's selected (picked in the scene, say).
            GameObject? anchor = _anchor != null && selection.Contains(_anchor) ? _anchor : selection.Object;
            if (Shift && anchor != null && _rows.Contains(anchor) && _rows.Contains(gameObject))
            {
                int from = _rows.IndexOf(anchor), to = _rows.IndexOf(gameObject);
                _anchor = anchor;
                IEnumerable<GameObject> range = from <= to ? _rows.Skip(from).Take(to - from + 1) : _rows.Skip(to).Take(from - to + 1).Reverse();

                // With Ctrl too, the range is added to what's selected.
                selection.Select((Ctrl ? selection.Objects.Except(range) : []).Concat(range.Where(o => o != gameObject)).Append(gameObject).ToList());
                return;
            }

            _anchor = gameObject;
            if (Ctrl)
            {
                selection.Toggle(gameObject);
                return;
            }

            if (selection.Count > 1 && selection.Contains(gameObject))
            {
                _selectOnRelease = true;
            }
            else
            {
                _selectedBeforePress = selection.Save();
                selection.Select(gameObject);
            }

            _pressed = gameObject;
            _pressedAt = Ui.Mouse;
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
            if (gameObject.GetComponent<Vertex.UI.UIRect>() != null || gameObject.GetComponent<Vertex.UI.UICanvas>() != null) return Icons.UI;

            foreach (Component component in gameObject.Components)
            {
                if (component is Camera) return Icons.Camera;
                if (component is Light) return Icons.Light;
            }

            foreach (Component component in gameObject.Components)
            {
                if (component is MeshRenderer) return Icons.Cube;
            }

            // Otherwise, what its first component is (a sound, a rigidbody, a script).
            return gameObject.Components.Count == 0 ? Icons.Empty : ComponentIcons.For(gameObject.Components[0]);
        }

        // ---------------------------------------------------------------- Renaming

        private void BeginRename(GameObject gameObject)
        {
            for (Transform? parent = gameObject.Transform.Parent; parent != null; parent = parent.Parent) _expanded.Add(parent.GameObject);
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
            // Several selected: what's done to one is done to them all.
            int count = _context.Selection.Count;
            if (count > 1)
            {
                List<GameObject> tops = _context.Selection.Tops();
                var several = new List<MenuItem>
                {
                    new($"Duplicate {count} Objects", _context.DuplicateSelected) { Shortcut = "Ctrl+D" },
                    new($"Delete {count} Objects", _context.DeleteSelected) { Shortcut = "Del", Icon = Icons.Trash },
                    MenuItem.Separator,
                    new("Group Under New Object", () => GroupSelected(tops))
                };

                if (tops.Any(o => o.Transform.Parent != null))
                {
                    several.Add(new MenuItem("Move to Top Level", () => _context.History.Do(new ChangeGroup("Move to Top Level",
                        tops.Where(o => o.Transform.Parent != null).Select(o => (IUndoable)new Reparent(o, null)).ToList()))));
                }

                return several;
            }

            // Things made from here go under the object: a cube on an empty, a light on a lamp post.
            var items = new List<MenuItem>
            {
                new("Create") { Icon = Icons.Plus, Children = CreateMenu.Items(_context, gameObject.Transform, flat: true) },
                new("Duplicate", () => _context.Duplicate(gameObject)) { Shortcut = "Ctrl+D" },
                MenuItem.Separator,
                new("Rename", () => BeginRename(gameObject)) { Shortcut = "F2" },
                new("Delete", () => _context.Delete(gameObject)) { Shortcut = "Del", Icon = Icons.Trash }
            };

            if (gameObject.Transform.Parent != null)
            {
                items.Add(new MenuItem("Move to Top Level", () => _context.History.Do(new Reparent(gameObject, null))));
            }

            if (gameObject.Transform.Children.Count > 0)
            {
                items.Add(new MenuItem("Expand All", () => SetOpen(gameObject, true)));
                items.Add(new MenuItem("Collapse All", () => SetOpen(gameObject, false)));
            }

            if (_context.Assets is { } assets)
            {
                items.Add(MenuItem.Separator);
                items.Add(new MenuItem("Make Prefab", () => _context.CreatePrefab(gameObject, Path.Combine(assets.Root, "Prefabs"))) { Icon = Icons.Prefab });
                if (gameObject.Prefab != null)
                {
                    items.Add(new MenuItem("Open Prefab", () => _context.OpenPrefab(gameObject.Prefab.Source)) { Icon = Icons.Prefab });
                    items.Add(new MenuItem("Apply to Prefab", () => _context.ApplyPrefab(gameObject)));
                    items.Add(new MenuItem("Revert to Prefab", () => _context.RevertPrefab(gameObject)));
                }
            }

            return items;
        }

        /// <summary>
        /// Puts the objects under a new empty one, in the middle of them: under their parent if they share one. The new
        /// object is selected, ready to move them all by.
        /// </summary>
        private void GroupSelected(List<GameObject> tops)
        {
            if (tops.Count == 0) return;
            tops = InRowOrder(tops);
            Transform? parent = tops.All(o => o.Transform.Parent == tops[0].Transform.Parent) ? tops[0].Transform.Parent : null;
            Vector3 centre = tops.Aggregate(Vector3.Zero, (sum, o) => sum + o.Transform.WorldPosition) / tops.Count;
            int index = tops[0].Transform.Parent == parent ? tops[0].Transform.SiblingIndex : -1;

            var group = new GameObject("Group", _context.Scene);
            group.Transform.Parent = parent;
            group.Transform.WorldPosition = centre;
            _context.Scene.ApplyChanges();
            if (index >= 0) group.Transform.SiblingIndex = index;

            var changes = new List<IUndoable> { new CreateObject(group, "Group") };
            foreach (GameObject gameObject in tops)
            {
                var move = new MoveInHierarchy(gameObject, group.Transform, group.Transform.Children.Count);
                move.Redo();
                changes.Add(move);
            }

            _context.History.Record(new ChangeGroup($"Group {tops.Count} Objects", changes));
            _expanded.Add(group);
            _context.Selection.Select(group);
        }

        /// <summary>The objects in the order their rows are shown, top to bottom (ones not shown last).</summary>
        private List<GameObject> InRowOrder(IEnumerable<GameObject> objects) =>
            objects.OrderBy(o => _rows.IndexOf(o) is int at && at >= 0 ? at : int.MaxValue).ToList();

        private void HandleKeys()
        {
            GameObject? selected = _context.Selection.Object;
            int index = selected == null ? -1 : _rows.IndexOf(selected);

            // Up and down move the selection; with Shift, they take in the next row too.
            GameObject? next = null;
            if (InputSystem.GetKeyDown(Key.Down) && _rows.Count > 0) next = _rows[Math.Min(index + 1, _rows.Count - 1)];
            if (InputSystem.GetKeyDown(Key.Up) && _rows.Count > 0) next = _rows[Math.Max(index - 1, 0)];
            if (next != null)
            {
                if (Shift) _context.Selection.Add(next);
                else
                {
                    _context.Selection.Select(next);
                    _anchor = next;
                }
            }

            if (selected == null) return;

            if (InputSystem.GetKeyDown(Key.Right) && selected.Transform.Children.Count > 0) _expanded.Add(selected);
            if (InputSystem.GetKeyDown(Key.Left))
            {
                if (_expanded.Contains(selected)) _expanded.Remove(selected);
                else if (selected.Transform.Parent != null) _context.Selection.Select(selected.Transform.Parent.GameObject);
            }

            if (InputSystem.GetKeyDown(Key.F2)) BeginRename(selected);
        }

        /// <summary>Opens or shuts an object's children; with Alt held, everything under it too.</summary>
        private void Toggle(GameObject gameObject)
        {
            bool open = !_expanded.Contains(gameObject);
            if (InputSystem.GetKey(Key.AltLeft) || InputSystem.GetKey(Key.AltRight)) SetOpen(gameObject, open);
            else if (open) _expanded.Add(gameObject);
            else _expanded.Remove(gameObject);
        }

        private void SetOpen(GameObject gameObject, bool open)
        {
            if (gameObject.Transform.Children.Count == 0) return;
            if (open) _expanded.Add(gameObject);
            else _expanded.Remove(gameObject);
            foreach (Transform child in gameObject.Transform.Children) SetOpen(child.GameObject, open);
        }

        /// <summary>When something new is selected (in the scene view, say), opens its parents and scrolls it into view.</summary>
        private void RevealSelection()
        {
            // Last frame's row position, saved for the highlight: unless the selection changed while the rows were being
            // drawn (a row clicked above the selected one, which was then drawn unselected), which leaves no position to
            // trust. The old one's kept instead, so the highlight slides from there.
            if (_measuredVersion == _context.Selection.Version)
            {
                Ui.Remember(Ui.Id("outliner.selectedRow"), _selectedY >= 0f ? _selectedY - _scrollTop : -1f);
            }

            _measuredVersion = _context.Selection.Version;

            if (_shownSelection == _context.Selection.Version) return;
            _shownSelection = _context.Selection.Version;

            if (_context.Selection.Object is not { } selected) return;
            for (Transform? parent = selected.Transform.Parent; parent != null; parent = parent.Parent) _expanded.Add(parent.GameObject);

            int index = _rows.IndexOf(selected);
            if (index >= 0) ScrollView.Reveal(_scrollId, index * _rowHeight, (index + 2) * _rowHeight + 8f, 200f);
        }

        // ---------------------------------------------------------------- Dragging

        /// <summary>
        /// The object being dragged out of the outliner, for other panels to take (the assets make a prefab of it). Still
        /// set on the frame it's let go, so whoever it's dropped on sees it whichever order panels are drawn in.
        /// </summary>
        public static GameObject? Dragged { get; private set; }

        private bool _draggedLetGo;

        private void UpdateDrag(Rect list)
        {
            if (_draggedLetGo)
            {
                Dragged = null;
                _draggedLetGo = false;
            }

            if (_pressed != null && _dragging == null && Ui.MouseHeld() && Vector2.Distance(Ui.Mouse, _pressedAt) > 5f)
            {
                // Dragging one of several selected takes them all (what's under them comes along anyway).
                _dragging = _pressed;
                _draggingAll = _context.Selection.Count > 1 && _context.Selection.Contains(_pressed) ? InRowOrder(_context.Selection.Tops()) : [_pressed];

                // What was selected before the press stays in the inspector while dragging, so the object can be dropped
                // into one of its slots (a gun's muzzle, say). It's selected again if it's let go in here.
                if (_selectedBeforePress is { } before) _context.Selection.Restore(before);
            }

            if (_dragging != null) Dragged = _dragging;
            HandleAssetDrop(list);

            if (_dragging != null)
            {
                GameObject dragged = _dragging;
                int count = _draggingAll.Count;
                Vector2 mouse = Ui.Mouse;
                Ui.Overlay(() =>
                {
                    // What's being dragged follows the mouse.
                    string text = Ui.Fit(count > 1 ? $"{count} objects" : dragged.Name, 180f);
                    var label = new Rect(mouse.X + 14f, mouse.Y + 6f, Ui.TextWidth(text) + 36f, 24f);
                    Ui.Shadow(label, 5f, 10f, 0.6f);
                    Ui.Rect(label, Theme.Popup, 5f);
                    IconFor(dragged)(new Vector2(label.X + 13f, label.Centre.Y), 14f, Theme.TextDim);
                    Ui.Text(label.CutLeft(26f), text, Theme.Text);
                });

                if (_dropOnRoot && _draggingAll.Any(o => o.Transform.Parent != null))
                {
                    Ui.Rect(new Rect(list.X + 6f, list.Bottom - 3f, list.Width - 12f, 2f), Theme.Accent, 1f);
                }
            }

            if (!Ui.MouseReleased()) return;

            if (_dragging != null)
            {
                if (_selectedBeforePress != null && list.Contains(Ui.Mouse)) _context.Selection.Select(_draggingAll);

                if (_dropTarget != null)
                {
                    Drop(_draggingAll, _dropTarget, _dropZone);
                }
                else if (_dropOnRoot && _draggingAll.Any(o => o.Transform.Parent != null))
                {
                    List<IUndoable> changes = _draggingAll.Where(o => o.Transform.Parent != null).Select(o => (IUndoable)new Reparent(o, null)).ToList();
                    _context.History.Do(changes.Count == 1 ? changes[0] : new ChangeGroup("Move to Top Level", changes));
                }
            }
            else if (_selectOnRelease && _pressed != null)
            {
                // Pressed one of several and let go without dragging: just it, then.
                _context.Selection.Select(_pressed);
            }

            _pressed = null;
            _dragging = null;
            _draggingAll = [];
            _selectOnRelease = false;
            _selectedBeforePress = null;
            _draggedLetGo = Dragged != null;
        }

        private List<GameObject> _draggingAll = [];

        // What was selected before a press on a row that wasn't, to show again while that row is dragged out.
        private (GameObject[] Objects, bool Scene, AssetEntry? Asset)? _selectedBeforePress;

        /// <summary>A prefab or model dragged here from the assets goes into the scene: under the row it's dropped on, or at the top level.</summary>
        private void HandleAssetDrop(Rect list)
        {
            if (!DragDrop.Dragging || DragDrop.Payload is not AssetEntry { Kind: AssetKind.Prefab or AssetKind.Model } asset || !list.Contains(Ui.Mouse)) return;

            GameObject? under = null;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (Ui.HotId == Ui.Id(RuntimeHelpers.GetHashCode(_rows[i]), "row")) under = _rows[i];
            }

            Ui.Outline(list.Shrink(2f), Theme.Accent.WithAlpha(0.7f), 6f, 1.5f);
            if (!DragDrop.Dropped) return;

            if (asset.Kind == AssetKind.Prefab) _context.AddPrefab(asset.Path, _context.SpawnPoint, under?.Transform);
            else if (AssetsPanel.AddModel(_context, asset.Path, _context.SpawnPoint) is { } model && under != null)
            {
                model.Transform.Parent = under.Transform;
                model.Transform.Position = Vector3.Zero;
            }

            if (under != null) _expanded.Add(under);
        }

        /// <summary>Puts the dragged objects before, after or under <paramref name="target"/>, in order, keeping where they are in the world: one undo.</summary>
        private void Drop(List<GameObject> dragged, GameObject target, DropZone zone)
        {
            if (dragged.Count == 1)
            {
                if (Move(dragged[0], target, zone) is { } move) _context.History.Do(move);
                return;
            }

            // One at a time, each worked out from where the last left things; after the target, each goes after the last.
            var changes = new List<IUndoable>();
            GameObject after = target;
            foreach (GameObject gameObject in dragged)
            {
                if (gameObject == target || IsUnder(target, gameObject)) continue;
                if (Move(gameObject, zone == DropZone.After ? after : target, zone) is { } move)
                {
                    move.Redo();
                    changes.Add(move);
                }

                after = gameObject;
            }

            if (changes.Count > 0) _context.History.Record(new ChangeGroup($"Move {changes.Count} Objects", changes));
        }

        /// <summary>The change that puts <paramref name="dragged"/> before, after or under <paramref name="target"/>; null if it's there already.</summary>
        private MoveInHierarchy? Move(GameObject dragged, GameObject target, DropZone zone)
        {
            Transform? parent = zone == DropZone.Inside ? target.Transform : target.Transform.Parent;
            int index;
            if (zone == DropZone.Inside)
            {
                index = target.Transform.Children.Count - (dragged.Transform.Parent == target.Transform ? 1 : 0);
                _expanded.Add(target);
            }
            else
            {
                // Counted as if the dragged object were already out of the way.
                index = target.Transform.SiblingIndex;
                if (dragged.Transform.Parent == parent && dragged.Transform.SiblingIndex < index) index--;
                if (zone == DropZone.After) index++;
            }

            if (dragged.Transform.Parent == parent && dragged.Transform.SiblingIndex == index) return null;
            return new MoveInHierarchy(dragged, parent, index);
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
