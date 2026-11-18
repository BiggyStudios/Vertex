using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The panel on the right: what the selected object is made of. Its name, then its transform, then each of its
    /// components as a card that folds open and shut, then Add Component. With the scene selected, its lighting and sky.
    /// </summary>
    public sealed class InspectorPanel : IDockPanel
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

        public string Title => "Inspector";
        public Icon Icon => Icons.Sliders;

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
            _scrollTop = scroll.Top;
            Ui.Opacity = appear;

            float x = content.X + Theme.Padding;
            float width = content.Width - Theme.Padding * 2f;
            float y = scroll.Top + Theme.Padding + (1f - appear) * 6f;
            float top = y;

            if (_context.Selection.Count > 1) y = DrawObjects(_context.Selection.Objects, x, y, width);
            else if (_context.Selection.Object is { } selected) y = DrawObject(selected, x, y, width);
            else if (_context.Selection.Scene) y = DrawScene(x, y, width);
            else if (_context.Selection.Asset is { } asset) y = DrawAsset(asset, x, y, width);
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

        // ---------------------------------------------------------------- An asset

        private readonly Dictionary<string, (int Width, int Height)> _imageSizes = new();

        private float DrawAsset(AssetEntry asset, float x, float y, float width)
        {
            var header = new Rect(x, y, width, 30f);
            AssetsPanel.DrawPicture(asset, header.TakeLeft(30f));
            Ui.Text(header.CutLeft(40f), asset.Name, Theme.Text, bold: true, size: Theme.TitleSize);
            y += 34f;
            Ui.Text(new Rect(x, y, width, 18f), _context.Assets?.Relative(asset.Path) ?? asset.Path, Theme.TextFaint, size: Theme.SmallTextSize);
            y += 28f;

            switch (asset.Kind)
            {
                case AssetKind.Texture:
                {
                    var preview = new Rect(x, y, width, width);
                    Ui.Rect(preview, Theme.Field, 6f);
                    AssetsPanel.DrawPicture(asset, preview.Shrink(6f));
                    y += width + 10f;

                    if (!_imageSizes.TryGetValue(asset.Path, out var size))
                    {
                        try
                        {
                            using FileStream stream = File.OpenRead(asset.Path);
                            var info = StbImageSharp.ImageInfo.FromStream(stream);
                            size = info is { } read ? (read.Width, read.Height) : (0, 0);
                        }
                        catch (Exception)
                        {
                            size = (0, 0);
                        }

                        _imageSizes[asset.Path] = size;
                    }

                    Ui.Text(new Rect(x, y, width, 20f), $"{size.Width} × {size.Height} pixels", Theme.TextDim);
                    y += 26f;
                    Ui.Text(new Rect(x, y, width, 20f), "Drag onto a texture slot, or onto an object in the scene.", Theme.TextFaint, size: Theme.SmallTextSize);
                    y += 24f;
                    break;
                }

                case AssetKind.Scene:
                    if (Widgets.Button(Ui.Id("asset.open"), new Rect(x, y, width, 30f), "Open Scene", ButtonStyle.Primary)) _context.OpenScene(asset.Path);
                    y += 40f;
                    break;

                case AssetKind.Sound:
                    y = SoundPreview.Draw(asset.Path, x, y, width);
                    break;

                case AssetKind.Material:
                {
                    Material material;
                    try
                    {
                        material = Assets.LoadMaterial(asset.Path);
                    }
                    catch (Exception exception)
                    {
                        Ui.Text(new Rect(x, y, width, 20f), $"Can't be read: {exception.Message}", Theme.Danger, size: Theme.SmallTextSize);
                        return y + 26f;
                    }

                    var ball = new Rect(x + width * 0.5f - 60f, y, 120f, 120f);
                    AssetsPanel.DrawPicture(asset, ball);
                    y += 130f;
                    y = Note("Shared by everything that uses it: changes are saved to the file as you make them.", x, y, width) + 8f;
                    y = _grid.Draw(material, x, y, width);
                    break;
                }

                case AssetKind.Prefab:
                    if (Widgets.Button(Ui.Id("asset.addPrefab"), new Rect(x, y, width, 30f), "Add to Scene", ButtonStyle.Primary)) _context.AddPrefab(asset.Path, _context.SpawnPoint);
                    y += 40f;
                    y = Note("Drag it into the scene for more copies. To change it, change a copy, then Apply in that copy's inspector: every copy updates.", x, y, width);
                    break;

                case AssetKind.Script:
                    y = DrawScript(asset, x, y, width);
                    break;

                case AssetKind.Model:
                    if (Widgets.Button(Ui.Id("asset.add"), new Rect(x, y, width, 30f), "Add to Scene", ButtonStyle.Primary)) AssetsPanel.AddModel(_context, asset.Path, _context.SpawnPoint);
                    y += 40f;
                    break;
            }

            return y;
        }

        /// <summary>A small faint note, wrapped onto as many lines as it takes. Returns where it ends.</summary>
        private static float Note(string text, float x, float y, float width)
        {
            foreach (string line in Ui.Wrap(text, width, Theme.SmallTextSize))
            {
                Ui.Text(new Rect(x, y, width, 17f), line, Theme.TextFaint, size: Theme.SmallTextSize);
                y += 17f;
            }

            return y + 4f;
        }

        private readonly Dictionary<string, (DateTime Written, string[] Lines)> _scripts = new();

        /// <summary>A script: open it in the code editor, and the start of it to read.</summary>
        private float DrawScript(AssetEntry asset, float x, float y, float width)
        {
            if (Widgets.Button(Ui.Id("asset.openScript"), new Rect(x, y, width, 30f), "Open in Code Editor", ButtonStyle.Primary))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(asset.Path) { UseShellExecute = true });
                }
                catch (Exception exception)
                {
                    _context.Notify($"Couldn't open {asset.Name}: {exception.Message}");
                }
            }

            y += 38f;
            y = Note("After changing code, rebuild it (Ctrl+R) for the editor to see it.", x, y, width) + 6f;

            DateTime written = File.Exists(asset.Path) ? File.GetLastWriteTimeUtc(asset.Path) : default;
            if (!_scripts.TryGetValue(asset.Path, out var script) || script.Written != written)
            {
                string[] lines;
                try
                {
                    lines = File.ReadLines(asset.Path).Take(80).Select(line => line.Replace("\t", "    ")).ToArray();
                }
                catch (Exception)
                {
                    lines = ["(can't be read)"];
                }

                script = (written, lines);
                _scripts[asset.Path] = script;
            }

            const float lineHeight = 16f;
            var code = new Rect(x, y, width, script.Lines.Length * lineHeight + 16f);
            Ui.Rect(code, Theme.Field, 6f);
            float lineY = code.Y + 8f;
            foreach (string line in script.Lines)
            {
                Ui.Text(new Rect(code.X + 10f, lineY, code.Width - 20f, lineHeight), line, line.TrimStart().StartsWith("//") ? Theme.TextFaint : Theme.TextDim,
                    size: Theme.SmallTextSize);
                lineY += lineHeight;
            }

            return code.Bottom + 10f;
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
            // Renamed as it's typed, so the outliner keeps up; one undo for the lot.
            int nameId = Ui.Id("object.name");
            if (TextInput.Draw(nameId, header.CutLeft(30f), ref name, live: true) && name.Trim().Length > 0)
            {
                SetValue.Apply(_context.History, "Rename", gameObject, typeof(GameObject).GetProperty(nameof(GameObject.Name))!, name.Trim());
            }

            _grid.TypingSession(nameId);

            y += 30f + 12f;

            if (gameObject.Prefab is { } prefab) y = PrefabBar(gameObject, prefab, x, y, width);

            // A piece of UI is placed by its rectangle, not its transform: that comes first instead.
            UIRect? uiRect = gameObject.GetComponent<UIRect>();
            if (uiRect != null)
            {
                y = Card(uiRect, "Rect", Icons.Scale, x, y, width, removable: true, body: cy => DrawUIRect(uiRect, x + 12f, cy, width - 24f));
            }
            else
            {
                y = Card(gameObject.Transform, "Transform", Icons.Move, x, y, width, removable: false, body: cy => DrawTransform(gameObject.Transform, x + 12f, cy, width - 24f));
            }

            y = DrawComponents(gameObject, uiRect, x, y, width);

            y += 4f;
            return AddComponentButton([gameObject], x, y, width);
        }

        // ---------------------------------------------------------------- Several objects

        private static readonly System.Reflection.PropertyInfo _activeProperty = typeof(GameObject).GetProperty(nameof(GameObject.Active))!;
        private static readonly System.Reflection.PropertyInfo _nameProperty = typeof(GameObject).GetProperty(nameof(GameObject.Name))!;

        /// <summary>
        /// Several objects at once: the main one's (the last picked) values are shown, and a change goes to them all. Its
        /// transform, then the components they all have; a value that isn't the same on them all shows a dash.
        /// </summary>
        private float DrawObjects(IReadOnlyList<GameObject> objects, float x, float y, float width)
        {
            GameObject main = objects[^1];
            List<GameObject> others = objects.Take(objects.Count - 1).ToList();

            var header = new Rect(x, y, width, 30f);
            bool active = main.Active;
            bool mixedActive = others.Any(o => o.Active != active);
            if (Widgets.Checkbox(Ui.Id("object.active"), header.TakeLeft(24f), ref active, "Switch them all on or off", mixedActive))
            {
                SetAll(objects, active ? "Switch On" : "Switch Off", _activeProperty, _ => active);
            }

            // One name for them all, as it's typed.
            string name = main.Name;
            bool sameName = others.All(o => o.Name == name);
            if (!sameName) name = "";
            int nameId = Ui.Id("object.name");
            if (TextInput.Draw(nameId, header.CutLeft(30f), ref name, sameName ? "" : "—", live: true) && name.Trim().Length > 0)
            {
                string named = name.Trim();
                SetAll(objects, "Rename", _nameProperty, _ => named, mergeKey: ("rename", main, objects.Count));
            }

            _grid.TypingSession(nameId);
            y += 32f;
            Ui.Text(new Rect(x, y, width, 18f), $"{objects.Count} objects selected  ·  changes go to them all", Theme.TextFaint, size: Theme.SmallTextSize);
            y += 26f;

            // Placed by a transform, or (all UI) by a rectangle. A mix of the two has neither in common.
            List<UIRect?> rects = objects.Select(o => o.GetComponent<UIRect>()).ToList();
            if (rects.All(r => r == null))
            {
                y = Card(main.Transform, "Transform", Icons.Move, x, y, width, removable: false,
                    body: cy => DrawTransform(main.Transform, x + 12f, cy, width - 24f, others.Select(o => o.Transform).ToList()));
            }
            else if (rects.All(r => r != null))
            {
                UIRect mainRect = rects[^1]!;
                List<UIRect> otherRects = rects.Take(rects.Count - 1).Select(r => r!).ToList();
                y = Card(mainRect, "Rect", Icons.Scale, x, y, width, removable: true, body: cy => DrawUIRect(mainRect, x + 12f, cy, width - 24f, otherRects),
                    alsoOn: otherRects);
            }
            else
            {
                y = Note("Some of these are UI, placed by a rectangle, and some aren't: select just one kind to move them together.", x, y, width) + 6f;
            }

            y = DrawSharedComponents(main, others, x, y, width);
            y += 4f;
            return AddComponentButton(objects, x, y, width);
        }

        /// <summary>The components every one of the objects has, each a card that changes them all. Returns where they end.</summary>
        private float DrawSharedComponents(GameObject main, List<GameObject> others, float x, float y, float width)
        {
            var seen = new Dictionary<Type, int>();
            int shared = 0;
            foreach (Component component in main.Components.ToList())
            {
                if (component is UIRect) continue;

                // The same kind of component on each of the others: the first if there's one, the second if this is the second...
                Type type = component.GetType();
                int nth = seen.GetValueOrDefault(type);
                seen[type] = nth + 1;

                var matching = new List<Component>(others.Count);
                foreach (GameObject other in others)
                {
                    if (other.Components.Where(c => c.GetType() == type).Skip(nth).FirstOrDefault() is not { } same) break;
                    matching.Add(same);
                }

                if (matching.Count < others.Count) continue;
                shared++;
                y = Card(component, ComponentCatalog.DisplayName(type), IconFor(component), x, y, width, removable: true,
                    body: cy => _grid.Draw(component, x + 12f, cy, width - 24f, others: matching), alsoOn: matching);
            }

            // Components only some have aren't shown: say so.
            bool hidden = others.Prepend(main).Any(o => o.Components.Count(c => c is not UIRect) != shared);
            if (hidden) y = Note("Components only some of them have aren't shown: select one on its own to see all of its own.", x, y, width) + 6f;
            return y;
        }

        /// <summary>Sets a property on each of <paramref name="targets"/>, as one change to undo.</summary>
        private void SetAll<T>(IEnumerable<T> targets, string name, System.Reflection.PropertyInfo property, Func<T, object?> value, object? mergeKey = null) where T : notnull
        {
            try
            {
                List<IUndoable> changes = targets.Select(t => (IUndoable)new SetValue(name, t, property, property.GetValue(t), value(t))).ToList();
                _context.History.Do(new ChangeGroup(name, changes), mergeKey);
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't change {property.Name}: {(exception.InnerException ?? exception).Message}");
            }
        }

        /// <summary>Which of X, Y and Z differ between <paramref name="value"/> and <paramref name="others"/>: 1, 2 and 4.</summary>
        private static int Differs(Vector3 value, IEnumerable<Vector3> others)
        {
            int mixed = 0;
            foreach (Vector3 other in others) mixed |= (other.X != value.X ? 1 : 0) | (other.Y != value.Y ? 2 : 0) | (other.Z != value.Z ? 4 : 0);
            return mixed;
        }

        /// <summary>A transform's position, rotation and scale; with <paramref name="others"/>, a change goes to them too (just the axes changed).</summary>
        private float DrawTransform(Transform transform, float x, float y, float width, IReadOnlyList<Transform>? others = null)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(transform), "transform");
            var position = typeof(Transform).GetProperty(nameof(Transform.Position))!;
            var rotation = typeof(Transform).GetProperty(nameof(Transform.Rotation))!;
            var scale = typeof(Transform).GetProperty(nameof(Transform.Scale))!;
            others ??= [];

            void Set(string name, System.Reflection.PropertyInfo property, Func<Transform, object?> value)
            {
                if (others.Count == 0) SetValue.Apply(_context.History, name, transform, property, value(transform));
                else SetAll(others.Prepend(transform), name, property, value, mergeKey: (transform, property, others.Count));
            }

            int pMixed = Differs(transform.Position, others.Select(t => t.Position));
            PropertyGrid.Row(Ui.Id(id, "p"), x, y, width, "Position", "Where it is, relative to its parent: in metres along X, Y and Z", out Rect field, pMixed != 0);
            Vector3 p = transform.Position, pBefore = p;
            if (NumberField.Vector3(Ui.Id(id, "position"), field, ref p, mixed: pMixed))
            {
                Set("Move", position, t => t == transform ? p : PropertyGrid.Blend(pBefore, p, t.Position));
            }

            y += Theme.RowHeight + PropertyGrid.RowGap;

            Vector3 angles = _grid.Angles(transform, "Rotation", transform.Rotation);
            int rMixed = 0;
            foreach (Transform other in others) rMixed |= PropertyGrid.AnglesDiffer(angles, _grid.Angles(other, "Rotation", other.Rotation));
            PropertyGrid.Row(Ui.Id(id, "r"), x, y, width, "Rotation", "How it's turned, in degrees about X (pitch), Y (yaw) and Z (roll)", out field, rMixed != 0);
            if (_grid.RotationField(Ui.Id(id, "rotation"), field, transform, "Rotation", transform.Rotation, others.Select(t => ((object)t, t.Rotation)).ToList(), rMixed) is { } turned)
            {
                Set("Rotate", rotation, t => t == transform ? turned[0] : turned[1 + others.ToList().IndexOf(t)]);
            }

            y += Theme.RowHeight + PropertyGrid.RowGap;

            int sMixed = Differs(transform.Scale, others.Select(t => t.Scale));
            PropertyGrid.Row(Ui.Id(id, "s"), x, y, width, "Scale", "How big it is along each axis: 1 is its own size", out field, sMixed != 0);
            Vector3 s = transform.Scale, sBefore = s;
            if (NumberField.Vector3(Ui.Id(id, "scale"), field, ref s, mixed: sMixed))
            {
                Set("Scale", scale, t => t == transform ? s : PropertyGrid.Blend(sBefore, s, t.Scale));
            }

            return y + Theme.RowHeight + PropertyGrid.RowGap;
        }

        /// <summary>
        /// A card with a header (arrow, icon, name, and for components an on/off box and a menu) and a body that slides
        /// open and shut. Returns where the next card goes.
        /// </summary>
        /// <param name="alsoOn">The same component on the other selected objects: switching it on or off, or removing it, does them all.</param>
        private float Card(object owner, string title, Icon icon, float x, float y, float width, bool removable, Func<float, float> body, bool draggable = false,
            bool lifted = false, IReadOnlyList<Component>? alsoOn = null)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(owner), "card");
            bool open = !_folded.TryGetValue(owner, out _);
            float openness = Ui.Smooth(Ui.Id(id, "open"), open ? 1f : 0f, 16f);
            float bodyHeight = Ui.Recall(Ui.Id(id, "height"));
            float shownBody = bodyHeight * openness;

            var card = new Rect(x - 4f, y, width + 8f, _cardHeader + shownBody);

            // Picked up to move: it floats over the rest, with a shadow.
            if (lifted) Ui.Shadow(card, 6f, 22f, 0.9f);
            Ui.Rect(card, lifted ? Theme.ControlHover : Theme.PanelRaised, 6f);
            Ui.Outline(card, lifted ? Theme.Accent.WithAlpha(0.6f) : Theme.Border, 6f);

            // The header: clicking it folds the card, except over the box and the menu.
            var header = new Rect(card.X, card.Y, card.Width, _cardHeader);
            Rect menuButton = header.TakeRight(30f).Shrink(3f);
            Rect toggle = header.CutRight(30f).TakeRight(26f);
            Rect titleRect = removable ? header.CutRight(58f) : header;

            bool nowOpen = Widgets.Foldout(id, titleRect.Shrink(2f, 3f), title, open, icon: icon);

            // Its header can be dragged to move it up or down among the others; letting go after a drag isn't a click.
            if (draggable)
            {
                if (Ui.ActiveId == id && Ui.MousePressed()) PressCard(owner, y);
                bool overHeader = titleRect.Contains(Ui.Mouse) && Ui.HotId == id;
                float grip = Ui.Smooth(Ui.Id(id, "grip"), overHeader || lifted ? 1f : 0f, 18f);
                if (grip > 0.01f) DrawGrip(new Vector2(titleRect.Right - 10f, titleRect.Centre.Y), Theme.TextFaint.WithAlpha(grip));
            }

            if (nowOpen != open && _cardDropFrame != Ui.Frame)
            {
                if (nowOpen) _folded.Remove(owner);
                else _folded.AddOrUpdate(owner, owner);
            }

            if (owner is Component component)
            {
                var enabledProperty = typeof(Component).GetProperty(nameof(Component.Enabled))!;
                bool enabled = component.Enabled;
                bool mixed = alsoOn != null && alsoOn.Any(c => c.Enabled != enabled);
                if (Widgets.Checkbox(Ui.Id(id, "enabled"), toggle, ref enabled, enabled ? "On: click to switch it off" : "Off: click to switch it on", mixed))
                {
                    if (alsoOn is { Count: > 0 }) SetAll(alsoOn.Prepend(component), enabled ? "Enable" : "Disable", enabledProperty, _ => enabled);
                    else SetValue.Apply(_context.History, enabled ? "Enable" : "Disable", component, enabledProperty, enabled);
                }

                int menuId = Ui.Id(id, "menu");
                if (Widgets.IconButton(menuId, menuButton, Icons.More, "More"))
                {
                    List<MenuItem> items = alsoOn is { Count: > 0 } ? RemoveFromAllMenu([component, .. alsoOn]) : ComponentMenu(component);
                    Menu.Open(menuId, new Vector2(menuButton.X, menuButton.Bottom + 3f), items, flipBy: menuButton.Height + 6f);
                }
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

        // ---------------------------------------------------------------- Moving components by dragging

        // A component card pressed on, where, and how far down its header the mouse was; and the one being dragged.
        private object? _pressedCard;
        private Vector2 _pressedAt;
        private float _grabbedAt;
        private Component? _dragging;
        private int _dropIndex;
        private int _cardDropFrame = -1;

        // Cards slide to their places while a card is dragged, and for a moment after it's let go.
        private float _slideUntil;

        private void PressCard(object owner, float cardY)
        {
            _pressedCard = owner;
            _pressedAt = Ui.Mouse;
            _grabbedAt = Ui.Mouse.Y - cardY;
        }

        private static void DrawGrip(Vector2 centre, Color color)
        {
            for (int row = -1; row <= 1; row++)
            {
                for (int column = 0; column < 2; column++) Ui.Circle(centre + new Vector2((column - 0.5f) * 5f, row * 5f), 1.2f, color);
            }
        }

        /// <summary>
        /// The object's components as cards, under its transform. A card's header can be dragged up or down: the others
        /// slide apart to make room where it would go, and it settles into place when let go.
        /// </summary>
        private float DrawComponents(GameObject gameObject, UIRect? fixedFirst, float x, float y, float width)
        {
            // Through a copy: removing one from its menu changes the list. The UI rectangle is shown above, not here.
            List<Component> components = gameObject.Components.Where(c => c != fixedFirst).ToList();
            float scrollTop = _scrollTop;

            // A press that moves far enough becomes a drag; letting go drops it.
            if (_pressedCard is Component pressed && _dragging == null && Ui.MouseHeld() && Vector2.Distance(Ui.Mouse, _pressedAt) > 6f && components.Contains(pressed))
            {
                _dragging = pressed;
                Popup.Close();
            }

            if (!Ui.MouseHeld()) _pressedCard = null;
            if (_dragging != null && !components.Contains(_dragging)) _dragging = null;

            if (_dragging is { } dropped && !Ui.MouseHeld())
            {
                // Where it's let go from, so it eases from there into its new place.
                Ui.Snap(SlideKey(dropped), Ui.Mouse.Y - _grabbedAt - scrollTop);
                _dragging = null;
                _cardDropFrame = Ui.Frame;
                _slideUntil = Ui.Time + 0.6f;

                int from = gameObject.Components.ToList().IndexOf(dropped);
                int to = _dropIndex + (fixedFirst != null && gameObject.Components.ToList().IndexOf(fixedFirst) <= _dropIndex ? 1 : 0);
                if (from >= 0 && to != from) MoveTo(dropped, to);
                components = gameObject.Components.Where(c => c != fixedFirst).ToList();
            }

            float Height(Component component) => Ui.Recall(Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(component), "card"), "full"), _cardHeader + _cardGap);

            // Where each card goes: in order, with a gap where the dragged one would land.
            var targets = new List<(Component Component, float Y)>();
            float at = y;
            if (_dragging is { } held)
            {
                float heldHeight = Height(held);
                float heldCentre = Ui.Mouse.Y - _grabbedAt + heldHeight * 0.5f;
                List<Component> others = components.Where(c => c != held).ToList();

                _dropIndex = others.Count;
                float scan = y;
                for (int i = 0; i < others.Count; i++)
                {
                    float height = Height(others[i]);
                    if (scan + height * 0.5f > heldCentre)
                    {
                        _dropIndex = i;
                        break;
                    }

                    scan += height;
                }

                for (int i = 0; i < others.Count; i++)
                {
                    if (i == _dropIndex) at += heldHeight;
                    targets.Add((others[i], at));
                    at += Height(others[i]);
                }

                if (_dropIndex == others.Count) at += heldHeight;
            }
            else
            {
                foreach (Component component in components)
                {
                    targets.Add((component, at));
                    at += Height(component);
                }
            }

            // Each card eases to its place (measured from the top of the scrolled list, so scrolling doesn't lag).
            bool slide = _dragging != null || Ui.Time < _slideUntil;
            foreach ((Component component, float target) in targets)
            {
                int key = SlideKey(component);
                float shown = slide ? Ui.Smooth(key, target - scrollTop, 18f, target - scrollTop) + scrollTop : target;
                if (!slide) Ui.Snap(key, target - scrollTop);

                float end = Card(component, ComponentCatalog.DisplayName(component.GetType()), IconFor(component), x, shown, width, removable: true,
                    body: cy => _grid.Draw(component, x + 12f, cy, width - 24f), draggable: true);
                Ui.Remember(Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(component), "card"), "full"), end - shown);
            }

            // The dragged card last, over the rest, following the mouse.
            if (_dragging is { } dragged)
            {
                float top = Ui.Mouse.Y - _grabbedAt;
                Ui.Cursor = Vertex.Input.CursorShape.Hand;
                float end = Card(dragged, ComponentCatalog.DisplayName(dragged.GetType()), IconFor(dragged), x, top, width, removable: true,
                    body: cy => _grid.Draw(dragged, x + 12f, cy, width - 24f), draggable: true, lifted: true);
                Ui.Remember(Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(dragged), "card"), "full"), end - top);
            }

            return at;
        }

        private static int SlideKey(Component component) => Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(component), "card"), "slide");

        // Where the inspector's scrolled contents start this frame, for the cards' sliding.
        private float _scrollTop;

        private void MoveTo(Component component, int to)
        {
            GameObject gameObject = component.GameObject;
            int from = gameObject.Components.ToList().IndexOf(component);
            to = Math.Clamp(to, 0, gameObject.Components.Count - 1);
            if (from == to) return;

            _context.History.Do(new ChangeGroup($"Move {ComponentCatalog.DisplayName(component.GetType())}",
            [
                new RemoveComponent(gameObject, component),
                new AddComponentAt(gameObject, component, to)
            ]));
        }

        // ---------------------------------------------------------------- Prefab copies

        /// <summary>A bar over a prefab copy: which prefab, and buttons to save its changes to it or throw them away.</summary>
        private float PrefabBar(GameObject gameObject, Prefab prefab, float x, float y, float width)
        {
            var bar = new Rect(x - 4f, y, width + 8f, 34f);
            Ui.Rect(bar, Theme.Hex(0x6FA8FF).WithAlpha(0.12f), 6f);
            Ui.Outline(bar, Theme.Hex(0x6FA8FF).WithAlpha(0.3f), 6f);
            Icons.Prefab(new Vector2(bar.X + 16f, bar.Centre.Y), 14f, Theme.Hex(0x6FA8FF));

            Rect buttons = bar.Shrink(5f).TakeRight(MathF.Min(196f, bar.Width * 0.62f));
            Rect apply = buttons.Column(2, 3, 4f), revert = buttons.Column(1, 3, 4f), select = buttons.Column(0, 3, 4f);
            Ui.Text(new Rect(bar.X + 30f, bar.Y, buttons.X - bar.X - 34f, bar.Height), prefab.Name, Theme.Hex(0x8FBCFF));

            if (Widgets.Button(Ui.Id("prefab.select"), select, "Select", ButtonStyle.Subtle, $"Show {prefab.Name}.prefab in the assets") &&
                _context.Assets != null && File.Exists(prefab.Source))
            {
                _context.Selection.SelectAsset(new AssetEntry(prefab.Source, AssetKind.Prefab));
            }

            if (Widgets.Button(Ui.Id("prefab.revert"), revert, "Revert", ButtonStyle.Subtle, "Throw away this copy's changes: make it again from the prefab"))
            {
                _context.RevertPrefab(gameObject);
            }

            if (Widgets.Button(Ui.Id("prefab.apply"), apply, "Apply", ButtonStyle.Primary, "Save this copy's changes to the prefab, and update every other copy"))
            {
                _context.ApplyPrefab(gameObject);
            }

            return y + bar.Height + 10f;
        }

        // ---------------------------------------------------------------- UI rectangles

        private static readonly (string Name, Vector2 Min, Vector2 Max)[] _anchorPresets =
        [
            ("Top Left", new(0f, 0f), new(0f, 0f)), ("Top", new(0.5f, 0f), new(0.5f, 0f)), ("Top Right", new(1f, 0f), new(1f, 0f)),
            ("Left", new(0f, 0.5f), new(0f, 0.5f)), ("Middle", new(0.5f), new(0.5f)), ("Right", new(1f, 0.5f), new(1f, 0.5f)),
            ("Bottom Left", new(0f, 1f), new(0f, 1f)), ("Bottom", new(0.5f, 1f), new(0.5f, 1f)), ("Bottom Right", new(1f, 1f), new(1f, 1f)),
            ("Stretch Across Top", new(0f, 0f), new(1f, 0f)), ("Stretch Across Middle", new(0f, 0.5f), new(1f, 0.5f)), ("Stretch Across Bottom", new(0f, 1f), new(1f, 1f)),
            ("Stretch Down Left", new(0f, 0f), new(0f, 1f)), ("Stretch Down Middle", new(0.5f, 0f), new(0.5f, 1f)), ("Stretch Down Right", new(1f, 0f), new(1f, 1f)),
            ("Fill", new(0f, 0f), new(1f, 1f))
        ];

        /// <summary>
        /// A UI rectangle: where it keeps to in its parent (picked from a grid of the usual choices, which keeps it where it
        /// is on screen), then its position and size.
        /// </summary>
        private float DrawUIRect(UIRect rect, float x, float y, float width, IReadOnlyList<UIRect>? others = null)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(rect), "uirect");
            bool mixed = others != null && others.Any(o => o.AnchorMin != rect.AnchorMin || o.AnchorMax != rect.AnchorMax);
            PropertyGrid.Row(Ui.Id(id, "anchors"), x, y, width, "Keep To", "Which part of its parent it stays put against as the screen changes size", out Rect field, mixed);

            int current = Array.FindIndex(_anchorPresets, p => p.Min == rect.AnchorMin && p.Max == rect.AnchorMax);
            string[] names = [.. _anchorPresets.Select(p => p.Name), .. current < 0 ? new[] { "Custom" } : []];
            int shown = mixed ? -1 : current < 0 ? names.Length - 1 : current;
            if (Widgets.Dropdown(Ui.Id(id, "preset"), field, names, shown) is int picked && picked < _anchorPresets.Length)
            {
                (Vector2 min, Vector2 max) = (_anchorPresets[picked].Min, _anchorPresets[picked].Max);
                _context.History.Do(new ChangeGroup("Change Anchors", (others ?? []).Prepend(rect).SelectMany(r => AnchorChanges(r, min, max)).ToList()));
            }

            y += Theme.RowHeight + PropertyGrid.RowGap;
            return _grid.Draw(rect, x, y, width, others: others);
        }

        /// <summary>The changes to a UI rectangle's anchors (and pivot to match) that keep it where it is on the screen.</summary>
        private static IEnumerable<IUndoable> AnchorChanges(UIRect rect, Vector2 min, Vector2 max)
        {
            (Vector2 parentPosition, Vector2 parentSize) = UISystem.ParentRect(rect.GameObject);
            Vector2 screenPosition = rect.ScreenPosition, screenSize = rect.ScreenSize;
            Vector2 pivot = new(min.X == max.X ? min.X : 0.5f, min.Y == max.Y ? min.Y : 0.5f);

            var after = new UIRect { AnchorMin = min, AnchorMax = max, Pivot = pivot };
            (Vector2 position, Vector2 size) = after.PlacementFor(screenPosition, screenSize, parentPosition, parentSize);

            Type type = typeof(UIRect);
            return
            [
                new SetValue("Anchors", rect, type.GetProperty(nameof(UIRect.AnchorMin))!, rect.AnchorMin, min),
                new SetValue("Anchors", rect, type.GetProperty(nameof(UIRect.AnchorMax))!, rect.AnchorMax, max),
                new SetValue("Anchors", rect, type.GetProperty(nameof(UIRect.Pivot))!, rect.Pivot, pivot),
                new SetValue("Anchors", rect, type.GetProperty(nameof(UIRect.Position))!, rect.Position, position),
                new SetValue("Anchors", rect, type.GetProperty(nameof(UIRect.Size))!, rect.Size, size)
            ];
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

        /// <summary>The menu of a component the selected objects all have: taking it off them all.</summary>
        private List<MenuItem> RemoveFromAllMenu(IReadOnlyList<Component> components) =>
        [
            new MenuItem($"Remove from All {components.Count}", () =>
            {
                string name = ComponentCatalog.DisplayName(components[0].GetType());
                _context.History.Do(new ChangeGroup($"Remove {name}", components.Select(c => (IUndoable)new RemoveComponent(c.GameObject, c)).ToList()));
                _context.Notify($"Removed {name} from {components.Count} objects");
            }) { Icon = Icons.Trash }
        ];

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

        private float AddComponentButton(IReadOnlyList<GameObject> gameObjects, float x, float y, float width)
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

            Popup.Show(id, new Vector2(width, 320f), inner => AddComponentPopup(id, inner, gameObjects));
            return y + 30f + Theme.Padding;
        }

        private void AddComponentPopup(int id, Rect rect, IReadOnlyList<GameObject> gameObjects)
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
                    Add(gameObjects, type);
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

        /// <summary>Adds a new component of <paramref name="type"/> to each of the objects, as one change to undo.</summary>
        private void Add(IReadOnlyList<GameObject> gameObjects, ComponentType type)
        {
            var changes = new List<IUndoable>(gameObjects.Count);
            try
            {
                foreach (GameObject gameObject in gameObjects)
                {
                    var component = (Component)Activator.CreateInstance(type.Type)!;

                    // A new mesh renderer shows something straight away.
                    if (component is MeshRenderer renderer && renderer.Mesh == null) renderer.Mesh = Primitives.Cube;
                    changes.Add(new AddComponent(gameObject, component));
                }
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't add {type.Name}: {(exception.InnerException ?? exception).Message}");
                return;
            }

            if (changes.Count == 1) _context.History.Do(changes[0]);
            else _context.History.Do(new ChangeGroup($"Add {type.Name}", changes));
            _context.Notify(changes.Count == 1 ? $"Added {type.Name}" : $"Added {type.Name} to {changes.Count} objects");
        }
    }
}
