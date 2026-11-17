using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The panel on the right: what the selected object is made of. Its name, then its transform, then each of its
    /// components as a card that folds open and shut, then Add Component. With the scene selected, its lighting and sky.
    /// </summary>
    public sealed class InspectorPanel
    {
        private readonly EditorContext _context;
        private readonly PropertyGrid _grid;

        // Component cards that have been folded shut.
        private readonly ConditionalWeakTable<object, object> _folded = new();

        private int _shownSelection = -1;
        private float _selectedAt;

        private string _search = "";
        private int _highlighted;

        private const float _cardHeader = 32f;
        private const float _cardGap = 8f;

        public InspectorPanel(EditorContext context)
        {
            _context = context;
            _grid = new PropertyGrid(context);
        }

        public void Draw(Rect rect)
        {
            Rect content = PanelFrame.Draw(rect, "Inspector");

            // A new selection eases in.
            if (_shownSelection != _context.Selection.Version)
            {
                _shownSelection = _context.Selection.Version;
                _selectedAt = Ui.Time;
                Popup.Close();
            }

            float appear = Ui.EaseOut((Ui.Time - _selectedAt) / 0.2f);
            int scrollId = Ui.Id("inspector.scroll");
            var scroll = ScrollView.Begin(scrollId, content);
            Ui.Opacity = appear;

            float x = content.X + Theme.Padding;
            float width = content.Width - Theme.Padding * 2f;
            float y = scroll.Top + Theme.Padding + (1f - appear) * 6f;
            float top = y;

            if (_context.Selection.Object is { } selected) y = DrawObject(selected, x, y, width);
            else if (_context.Selection.Scene) y = DrawScene(x, y, width);
            else DrawNothing(content);

            Ui.Opacity = 1f;
            scroll.End(y - top + Theme.Padding * 2f);
        }

        private static void DrawNothing(Rect content)
        {
            Vector2 centre = content.Centre - new Vector2(0f, 30f);
            Icons.Cube(centre, 34f, Theme.TextFaint);
            Ui.Text(new Rect(content.X, centre.Y + 26f, content.Width, 20f), "Nothing selected", Theme.TextDim, align: 0.5f);
            Ui.Text(new Rect(content.X, centre.Y + 46f, content.Width, 18f), "Pick an object in the scene or the outliner", Theme.TextFaint,
                align: 0.5f, size: Theme.SmallTextSize);
        }

        // ---------------------------------------------------------------- The scene

        private float DrawScene(float x, float y, float width)
        {
            var header = new Rect(x, y, width, 30f);
            Icons.Scene(new Vector2(header.X + 10f, header.Centre.Y), 18f, Theme.AccentText);
            string name = _context.SceneName;
            if (TextInput.Draw(Ui.Id("scene.name"), header.CutLeft(26f), ref name) && name.Trim().Length > 0) _context.SceneName = name.Trim();
            y += 30f + 12f;

            Lighting lighting = _context.Scene.Lighting;
            return Card(lighting, "Lighting & Sky", Icons.Light, x, y, width, removable: false, body: (cy) => _grid.Draw(lighting, x + 12f, cy, width - 24f));
        }

        // ---------------------------------------------------------------- An object

        private float DrawObject(GameObject gameObject, float x, float y, float width)
        {
            // Name, and whether it's switched on.
            var header = new Rect(x, y, width, 30f);
            bool active = gameObject.Active;
            if (Widgets.Checkbox(Ui.Id("object.active"), header.TakeLeft(24f), ref active, active ? "Switched on: click to hide it and stop its scripts" : "Switched off"))
            {
                SetValue.Apply(_context.History, active ? "Switch On" : "Switch Off", gameObject, typeof(GameObject).GetProperty(nameof(GameObject.Active))!, active);
            }

            string name = gameObject.Name;
            if (TextInput.Draw(Ui.Id("object.name"), header.CutLeft(30f), ref name) && name.Trim().Length > 0)
            {
                SetValue.Apply(_context.History, "Rename", gameObject, typeof(GameObject).GetProperty(nameof(GameObject.Name))!, name.Trim());
            }

            y += 30f + 12f;

            y = Card(gameObject.Transform, "Transform", Icons.Move, x, y, width, removable: false, body: cy => DrawTransform(gameObject.Transform, x + 12f, cy, width - 24f));

            // Through a copy: removing one from its menu changes the list.
            foreach (Component component in gameObject.Components.ToArray())
            {
                y = Card(component, ComponentCatalog.DisplayName(component.GetType()), IconFor(component), x, y, width, removable: true,
                    body: cy => _grid.Draw(component, x + 12f, cy, width - 24f));
            }

            y += 4f;
            return AddComponentButton(gameObject, x, y, width);
        }

        private float DrawTransform(Transform transform, float x, float y, float width)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(transform), "transform");
            var position = typeof(Transform).GetProperty(nameof(Transform.Position))!;
            var rotation = typeof(Transform).GetProperty(nameof(Transform.Rotation))!;
            var scale = typeof(Transform).GetProperty(nameof(Transform.Scale))!;

            PropertyGrid.Row(Ui.Id(id, "p"), x, y, width, "Position", "Where it is, relative to its parent: in metres along X, Y and Z", out Rect field);
            Vector3 p = transform.Position;
            if (NumberField.Vector3(Ui.Id(id, "position"), field, ref p)) SetValue.Apply(_context.History, "Move", transform, position, p);
            y += Theme.RowHeight + PropertyGrid.RowGap;

            PropertyGrid.Row(Ui.Id(id, "r"), x, y, width, "Rotation", "How it's turned, in degrees about X (pitch), Y (yaw) and Z (roll)", out field);
            _grid.RotationField(Ui.Id(id, "rotation"), field, transform, "Rotation", transform.Rotation,
                next => SetValue.Apply(_context.History, "Rotate", transform, rotation, next));
            y += Theme.RowHeight + PropertyGrid.RowGap;

            PropertyGrid.Row(Ui.Id(id, "s"), x, y, width, "Scale", "How big it is along each axis: 1 is its own size", out field);
            Vector3 s = transform.Scale;
            if (NumberField.Vector3(Ui.Id(id, "scale"), field, ref s)) SetValue.Apply(_context.History, "Scale", transform, scale, s);
            return y + Theme.RowHeight + PropertyGrid.RowGap;
        }

        /// <summary>
        /// A card with a header (arrow, icon, name, and for components an on/off box and a menu) and a body that slides
        /// open and shut. Returns where the next card goes.
        /// </summary>
        private float Card(object owner, string title, Icon icon, float x, float y, float width, bool removable, Func<float, float> body)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(owner), "card");
            bool open = !_folded.TryGetValue(owner, out _);
            float openness = Ui.Smooth(Ui.Id(id, "open"), open ? 1f : 0f, 16f);
            float bodyHeight = Ui.Recall(Ui.Id(id, "height"));
            float shownBody = bodyHeight * openness;

            var card = new Rect(x - 4f, y, width + 8f, _cardHeader + shownBody);
            Ui.Rect(card, Theme.PanelRaised, 6f);
            Ui.Outline(card, Theme.Border, 6f);

            // The header: clicking it folds the card, except over the box and the menu.
            var header = new Rect(card.X, card.Y, card.Width, _cardHeader);
            Rect menuButton = header.TakeRight(30f).Shrink(3f);
            Rect toggle = header.CutRight(30f).TakeRight(26f);
            Rect titleRect = removable ? header.CutRight(58f) : header;

            bool nowOpen = Widgets.Foldout(id, titleRect.Shrink(2f, 3f), title, open, icon: icon);
            if (nowOpen != open)
            {
                if (nowOpen) _folded.Remove(owner);
                else _folded.AddOrUpdate(owner, owner);
            }

            if (owner is Component component)
            {
                bool enabled = component.Enabled;
                if (Widgets.Checkbox(Ui.Id(id, "enabled"), toggle, ref enabled, enabled ? "On: click to switch it off" : "Off: click to switch it on"))
                {
                    SetValue.Apply(_context.History, enabled ? "Enable" : "Disable", component, typeof(Component).GetProperty(nameof(Component.Enabled))!, enabled);
                }

                int menuId = Ui.Id(id, "menu");
                if (Widgets.IconButton(menuId, menuButton, Icons.More, "More")) Menu.Open(menuId, new Vector2(menuButton.X, menuButton.Bottom + 3f), ComponentMenu(component));
            }

            if (openness > 0.001f)
            {
                float top = y + _cardHeader;
                Ui.PushClip(new Rect(card.X, top, card.Width, shownBody));
                Ui.Rect(new Rect(card.X + 8f, top, card.Width - 16f, 1f), Theme.Border);
                float end = body(top + 10f) + 6f;
                Ui.PopClip();
                Ui.Remember(Ui.Id(id, "height"), end - top);
            }

            return y + card.Height + _cardGap;
        }

        private List<MenuItem> ComponentMenu(Component component)
        {
            GameObject gameObject = component.GameObject;
            int index = gameObject.Components.ToList().IndexOf(component);
            return
            [
                new MenuItem("Move Up", () => Move(component, -1)) { Enabled = index > 0, Icon = (c, s, col) => Icons.Chevron(c, s, col, -90f) },
                new MenuItem("Move Down", () => Move(component, 1)) { Enabled = index < gameObject.Components.Count - 1, Icon = (c, s, col) => Icons.Chevron(c, s, col, 90f) },
                MenuItem.Separator,
                new MenuItem("Remove Component", () =>
                {
                    _context.History.Do(new RemoveComponent(gameObject, component));
                    _context.Notify($"Removed {ComponentCatalog.DisplayName(component.GetType())}");
                }) { Icon = Icons.Trash }
            ];
        }

        private void Move(Component component, int by)
        {
            GameObject gameObject = component.GameObject;
            int from = gameObject.Components.ToList().IndexOf(component);
            int to = Math.Clamp(from + by, 0, gameObject.Components.Count - 1);
            if (from == to) return;

            _context.History.Do(new ChangeGroup(by < 0 ? "Move Component Up" : "Move Component Down",
            [
                new RemoveComponent(gameObject, component),
                new AddComponentAt(gameObject, component, to)
            ]));
        }

        /// <summary>Puts a component back in at a given place: the second half of moving it.</summary>
        private sealed class AddComponentAt(GameObject gameObject, Component component, int index) : IUndoable
        {
            public string Name => "Move Component";
            public void Redo() => gameObject.AttachComponent(component, index);
            public void Undo() => gameObject.DetachComponent(component);
        }

        public static Icon IconFor(Component component) => component switch
        {
            Camera => Icons.Camera,
            Light => Icons.Light,
            MeshRenderer => Icons.Cube,
            Collider or Rigidbody or CharacterController => Icons.Scale,
            _ => Icons.Component
        };

        // ---------------------------------------------------------------- Adding components

        private float AddComponentButton(GameObject gameObject, float x, float y, float width)
        {
            int id = Ui.Id("addComponent");
            var button = new Rect(x + width * 0.15f, y, width * 0.7f, 30f);
            if (Widgets.Button(id, button, "Add Component", ButtonStyle.Normal, icon: Icons.Plus) && !Popup.IsOpen(id))
            {
                Popup.Open(id, button.Grow(0f) with { X = x, Width = width });
                _search = "";
                _highlighted = 0;
                TextInput.Focus(Ui.Id(id, "search"), selectAll: true);
            }

            Popup.Show(id, new Vector2(width, 320f), inner => AddComponentPopup(id, inner, gameObject));
            return y + 30f + Theme.Padding;
        }

        private void AddComponentPopup(int id, Rect rect, GameObject gameObject)
        {
            int searchId = Ui.Id(id, "search");

            // Read before the search box takes them: Escape closes the list, Enter adds what's highlighted.
            if (InputSystem.GetKeyDown(Key.Escape))
            {
                InputSystem.Consume(Key.Escape);
                Popup.Close();
                Ui.FocusId = 0;
                return;
            }

            bool enter = InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter);
            if (enter) InputSystem.Consume(Key.Enter);
            Ui.KeepFocus(searchId);
            if (TextInput.Draw(searchId, rect.TakeTop(28f), ref _search, "Search components", live: true, icon: Icons.Search)) _highlighted = 0;

            string search = _search.Trim();
            List<ComponentType> matches = ComponentCatalog.All
                .Where(type => search.Length == 0 || type.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                               type.Type.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // While typing in the search box, the arrows move through the list.
            if (InputSystem.GetKeyDown(Key.Down)) _highlighted = Math.Min(_highlighted + 1, matches.Count - 1);
            if (InputSystem.GetKeyDown(Key.Up)) _highlighted = Math.Max(_highlighted - 1, 0);

            Rect listRect = rect.CutTop(34f);
            int scrollId = Ui.Id(id, "list");
            var scroll = ScrollView.Begin(scrollId, listRect);
            float y = scroll.Top;
            string? category = null;
            const float rowHeight = 26f;
            float highlightY = -1f;

            for (int i = 0; i < matches.Count; i++)
            {
                ComponentType type = matches[i];
                if (search.Length == 0 && type.Category != category)
                {
                    category = type.Category;
                    Ui.Text(new Rect(listRect.X + 6f, y + 4f, listRect.Width, 20f), category.ToUpperInvariant(), Theme.TextFaint, size: 10.5f, bold: true);
                    y += 24f;
                }

                var row = new Rect(listRect.X, y, listRect.Width - 8f, rowHeight);
                Interaction interaction = Ui.Interact(Ui.Id(id, type.Type.FullName!), row);
                if (interaction.Hovered && Ui.MouseMovedLately) _highlighted = i;
                if (i == _highlighted) highlightY = y;

                if (interaction.Clicked || enter && i == _highlighted)
                {
                    Add(gameObject, type);
                    Popup.Close();
                    Ui.FocusId = 0;
                }

                y += rowHeight;
            }

            if (highlightY >= 0f)
            {
                float slid = Ui.Smooth(Ui.Id(id, "highlight"), highlightY - scroll.Top, 30f) + scroll.Top;
                Ui.Rect(new Rect(listRect.X, slid, listRect.Width - 8f, rowHeight), Theme.Accent.WithAlpha(0.85f), 4f);
                ScrollView.Reveal(scrollId, highlightY - scroll.Top - 24f, highlightY - scroll.Top + rowHeight, listRect.Height);
            }

            // Text over the highlight, so it's drawn second.
            y = scroll.Top;
            category = null;
            for (int i = 0; i < matches.Count; i++)
            {
                ComponentType type = matches[i];
                if (search.Length == 0 && type.Category != category)
                {
                    category = type.Category;
                    y += 24f;
                }

                var row = new Rect(listRect.X, y, listRect.Width - 8f, rowHeight);
                bool lit = i == _highlighted;
                Ui.Text(row.Shrink(10f, 0f), type.Name, lit ? Color.White : Theme.Text);
                if (search.Length > 0) Ui.Text(row.Shrink(10f, 0f), type.Category, lit ? Color.White.WithAlpha(0.7f) : Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
                y += rowHeight;
            }

            if (matches.Count == 0) Ui.Text(new Rect(listRect.X, listRect.Y + 10f, listRect.Width, 20f), "No components match", Theme.TextFaint, align: 0.5f);
            scroll.End(y - scroll.Top);
        }

        private void Add(GameObject gameObject, ComponentType type)
        {
            Component component;
            try
            {
                component = (Component)Activator.CreateInstance(type.Type)!;
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't add {type.Name}: {(exception.InnerException ?? exception).Message}");
                return;
            }

            // A new mesh renderer shows something straight away.
            if (component is MeshRenderer renderer && renderer.Mesh == null) renderer.Mesh = Primitives.Cube;

            _context.History.Do(new AddComponent(gameObject, component));
            _context.Notify($"Added {type.Name}");
        }
    }
}
