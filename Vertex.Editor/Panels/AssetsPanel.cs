using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Editor.Platform;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The panel under the scene: the project's Assets folder as tiles, images showing as themselves. Double-click a
    /// folder to go in, a scene to open it, a model or prefab to add it to the scene, a sound to hear it; drag textures
    /// and materials onto slots or objects, and models and prefabs into the scene. Drag an object here from the outliner
    /// to make a prefab of it. Right-click for new folders, materials, scenes and scripts.
    /// </summary>
    public sealed class AssetsPanel : IDockPanel
    {
        private readonly EditorContext _context;
        private string? _folder;
        private string _search = "";

        // A file or folder being renamed in place, and its new name so far.
        private string? _renaming;
        private string _renameText = "";

        // Clicked in last, so F2, Delete and Ctrl+D are its.
        private bool _focused;

        // The tiles shown this frame, in order, and where each is: for Shift-click ranges and boxes dragged round several.
        private IReadOnlyList<AssetEntry> _entries = [];
        private readonly List<(AssetEntry Entry, Rect Cell)> _cells = new();

        // Where a Shift-click range runs from: the tile clicked last without Shift.
        private string? _anchor;

        // A tile pressed while it's one of several selected: let go without dragging, it's selected on its own.
        private string? _pressedAmong;

        // A box dragged from empty space, selecting the tiles it touches; and those it touches so far.
        private Vector2? _boxFrom;
        private bool _boxing;
        private readonly HashSet<string> _inBox = new();

        // Renaming several at once: numbered ("Rock 1", "Rock 2"...) or by replacing part of their names.
        private bool _renameByReplacing;
        private string _manyName = "";
        private int _manyStart = 1;
        private string _find = "";
        private string _replaceWith = "";

        public string Title => "Assets";
        public Icon Icon => Icons.Folder;

        public AssetsPanel(EditorContext context)
        {
            _context = context;
        }

        /// <summary>The folder being shown: where new things are made.</summary>
        public string? Folder => _folder;

        public void Draw(Rect rect)
        {
            Rect content = PanelFrame.Draw(rect, "Assets");
            AssetDatabase? assets = _context.Assets;

            if (assets == null)
            {
                Ui.Text(new Rect(content.X, content.Y + 18f, content.Width, 20f), "Open a project to see its assets", Theme.TextDim, align: 0.5f);
                var open = new Rect(content.Centre.X - 80f, content.Y + 48f, 160f, 30f);
                if (Widgets.Button(Ui.Id("assets.open"), open, "Open Project…", ButtonStyle.Primary)) EditorApp.OpenProjectDialog(_context);
                return;
            }

            if (_folder == null || !_folder.StartsWith(assets.Root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(_folder)) _folder = assets.Root;
            if (Ui.MousePressed() || Ui.MousePressed(MouseButton.Right)) _focused = rect.Contains(Ui.Mouse) && !Menu.IsAnyOpen && !Dialog.IsOpen;

            // Its controls go along the header, right of the title or tabs, or on a row of their own when that's too narrow.
            Rect bar = new(PanelFrame.TitleRight, rect.Y, rect.Right - PanelFrame.TitleRight, PanelFrame.HeaderHeight);
            if (bar.Width < 520f)
            {
                bar = content.TakeTop(34f).Shrink(4f, 0f);
                content = content.CutTop(32f);
            }

            DrawHeader(bar, assets);

            // The tiles.
            float tile = _context.Preferences.AssetTileSize;
            string search = _search.Trim();
            IReadOnlyList<AssetEntry> entries = search.Length > 0
                ? assets.AllFiles().Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList()
                : assets.List(_folder);

            Rect area = content.Shrink(Theme.Padding, 6f);
            _entries = entries;
            _cells.Clear();

            // The whole area takes right-clicks and clicks on empty space; the tiles, drawn after, are on top of it.
            // Dragged from empty space, it's a box round the tiles to select.
            Interaction background = Ui.Interact(Ui.Id("assets.background"), area, rightClick: true);
            if (background.Pressed)
            {
                _boxFrom = Ui.Mouse;
                _boxing = false;
            }

            if (background.Clicked && !_boxing && !Control) _context.Selection.SelectAsset(null);
            if (background.RightClicked) Menu.Open(Ui.Id("assets.background"), Ui.Mouse, FolderMenu(_folder));

            int scrollId = Ui.Id("assets.scroll");
            var scroll = ScrollView.Begin(scrollId, area);

            float cellWidth = tile + 12f, cellHeight = tile + 30f;
            int columns = Math.Max((int)((area.Width - 8f) / cellWidth), 1);
            for (int i = 0; i < entries.Count; i++)
            {
                var cell = new Rect(area.X + i % columns * cellWidth, scroll.Top + i / columns * cellHeight, cellWidth - 4f, cellHeight - 4f);
                if (cell.Bottom < area.Y || cell.Y > area.Bottom) continue;
                DrawTile(entries[i], cell, tile);
            }

            if (entries.Count == 0)
            {
                Ui.Text(new Rect(area.X, area.Y + 20f, area.Width, 20f), search.Length > 0 ? "Nothing matches" : "Empty: right-click to make something, or copy files in",
                    Theme.TextFaint, align: 0.5f);
            }

            float rows = MathF.Ceiling(entries.Count / (float)columns);
            scroll.End(rows * cellHeight + 8f);

            DrawBox(area);
            if (!Ui.MouseHeld()) _pressedAmong = null;
            HandleObjectDrop(area);
            if (_focused && !Ui.KeyboardTaken && !Menu.IsAnyOpen && !Dialog.IsOpen) HandleKeys();
        }

        private void DrawHeader(Rect bar, AssetDatabase assets)
        {
            // Where we are, inside a folder: each folder on the way down from Assets, clickable to go back up.
            float x = bar.X;
            float y = bar.Centre.Y - 11f;
            string relative = Path.GetRelativePath(assets.Root, _folder!);
            string[] parts = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = assets.Root;
            for (int i = parts.Length == 0 ? 0 : -1; i < parts.Length; i++)
            {
                if (i >= 0)
                {
                    Icons.Chevron(new Vector2(x + 5f, y + 11f), 10f, Theme.TextFaint);
                    x += 12f;
                    path = Path.Combine(path, parts[i]);
                }

                string name = i < 0 ? "Assets" : parts[i];
                float width = Ui.TextWidth(name) + 20f;
                string target = path;
                var crumb = new Rect(x, y, width, 22f);
                bool last = i == parts.Length - 1;
                if (Widgets.Button(Ui.Id("assets.crumb" + i), crumb, name, ButtonStyle.Subtle) && !last) _folder = target;
                x += width + 2f;
            }

            // Search, tile size and a new-thing button on the right.
            float searchWidth = Math.Clamp(bar.Width - 300f, 100f, 200f);
            Rect searchRect = new(bar.Right - Theme.Padding - searchWidth, bar.Centre.Y - 13f, searchWidth, 26f);
            TextInput.Draw(Ui.Id("assets.search"), searchRect, ref _search, "Search assets", live: true, icon: Icons.Search);

            int addId = Ui.Id("assets.add");
            var add = new Rect(searchRect.X - 32f, bar.Centre.Y - 12f, 24f, 24f);
            if (Widgets.IconButton(addId, add, Icons.Plus, "Make something new here")) Menu.Open(addId, new Vector2(add.X, add.Bottom + 4f), NewItems(_folder!), flipBy: add.Height + 8f);

            float size = _context.Preferences.AssetTileSize;
            var slider = new Rect(add.X - 106f, bar.Centre.Y - 10f, 90f, 20f);
            if (Widgets.ThinSlider(Ui.Id("assets.size"), slider, ref size, 56f, 140f, "Tile size")) _context.Preferences.AssetTileSize = MathF.Round(size);
        }

        /// <summary>Where the tile of the file at <paramref name="path"/> was drawn last frame, if it was.</summary>
        internal Rect? CellOf(string path) => _cells.FirstOrDefault(c => c.Entry.Path == path) is { Entry: not null } found ? found.Cell : null;

        private static bool Control => InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
        private static bool Shift => InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

        /// <summary>Whether a tile shows as selected: it is, or a box being dragged takes it in (or, with Ctrl, adds to what is).</summary>
        private bool ShownSelected(AssetEntry entry) => _boxing
            ? _inBox.Contains(entry.Path) || Control && _context.Selection.ContainsAsset(entry.Path)
            : _context.Selection.ContainsAsset(entry.Path);

        /// <summary>
        /// The box dragged from empty space: the tiles it touches are shown selected as it grows, and are selected when
        /// it's let go (with Ctrl, along with those already).
        /// </summary>
        private void DrawBox(Rect area)
        {
            if (_boxFrom is not Vector2 from) return;

            if (!Ui.MouseHeld())
            {
                if (_boxing)
                {
                    List<AssetEntry> boxed = _entries.Where(e => _inBox.Contains(e.Path)).ToList();
                    _context.Selection.SelectAssets(Control ? _context.Selection.Assets.Concat(boxed) : boxed);
                }

                _boxFrom = null;
                _boxing = false;
                _inBox.Clear();
                return;
            }

            if (!_boxing && Vector2.Distance(from, Ui.Mouse) < 5f) return;
            _boxing = true;

            Vector2 to = Vector2.Clamp(Ui.Mouse, area.Position, area.Position + area.Size);
            var box = new Rect(MathF.Min(from.X, to.X), MathF.Min(from.Y, to.Y), MathF.Abs(to.X - from.X), MathF.Abs(to.Y - from.Y));
            _inBox.Clear();
            foreach ((AssetEntry entry, Rect cell) in _cells)
            {
                if (cell.X < box.Right && cell.Right > box.X && cell.Y < box.Bottom && cell.Bottom > box.Y) _inBox.Add(entry.Path);
            }

            Ui.Rect(box, Theme.Accent.WithAlpha(0.14f));
            Ui.Outline(box, Theme.Accent.WithAlpha(0.9f), 0f, 1f);
        }

        /// <summary>A press on a tile: just it, Ctrl to add it to (or take it from) the others, Shift for every tile from the last one clicked.</summary>
        private void PressTile(AssetEntry entry)
        {
            Selection selection = _context.Selection;
            if (Shift && _anchor != null && _entries.FirstOrDefault(e => e.Path == _anchor) is { } anchor)
            {
                int from = _entries.ToList().IndexOf(anchor), to = _entries.ToList().IndexOf(entry);
                int step = to >= from ? 1 : -1;

                // From the anchor to the tile clicked, so the clicked one is the main one.
                var range = new List<AssetEntry>();
                for (int i = from; i != to + step; i += step) range.Add(_entries[i]);
                selection.SelectAssets(Control ? selection.Assets.Concat(range) : range);
                return;
            }

            _anchor = entry.Path;
            if (Control) selection.ToggleAsset(entry);
            else if (selection.ContainsAsset(entry.Path) && selection.Assets.Count > 1) _pressedAmong = entry.Path;
            else selection.SelectAsset(entry);
        }

        private void DrawTile(AssetEntry entry, Rect cell, float tile)
        {
            int id = Ui.Id("asset:" + entry.Path);
            Interaction interaction = Ui.Interact(id, cell, rightClick: true);
            _cells.Add((entry, cell));
            bool selected = ShownSelected(entry);

            // A folder lights up while something that can go in it is dragged over.
            bool dropInto = entry.Kind == AssetKind.Folder && cell.Contains(Ui.Mouse) &&
                            (DragDrop.Dragging && DragDrop.Payload is AssetEntry dragged && dragged.Path != entry.Path || OutlinerPanel.Dragged != null);

            float hover = Ui.Smooth(Ui.Id(id, "hover"), interaction.Hovered || dropInto ? 1f : 0f, 20f);
            if (selected) Ui.Rect(cell, Theme.Selection, 6f);
            else if (hover > 0.01f) Ui.Rect(cell, Theme.Hover.WithAlpha(1.5f * hover), 6f);
            if (dropInto) Ui.Outline(cell, Theme.Accent, 6f, 1.5f);

            var picture = new Rect(cell.X + (cell.Width - tile) * 0.5f, cell.Y + 4f, tile, tile);
            DrawPicture(entry, picture);

            // A sound that's playing shows it.
            if (entry.Kind == AssetKind.Sound && SoundPreview.IsPlaying(entry.Path))
            {
                Vector2 corner = new(picture.Right - 14f, picture.Y + 14f);
                Ui.Circle(corner, 10f, Theme.Accent);
                Icons.Stop(corner, 8f, Color.White);
            }

            var label = new Rect(cell.X + 2f, picture.Bottom + 2f, cell.Width - 4f, 22f);
            string name = entry.Kind == AssetKind.Folder ? entry.Name : entry.NameWithoutExtension;
            if (_renaming == entry.Path) DrawRename(entry, label);
            else Ui.Text(label, name, selected ? Color.White : Theme.Text, align: 0.5f, size: Theme.SmallTextSize);
            Ui.Tooltip(id, _context.Assets!.Relative(entry.Path));

            if (interaction.Pressed)
            {
                PressTile(entry);

                // Dragged, it takes the others selected with it (onto a folder).
                int count = _context.Selection.Assets.Count;
                if (_context.Selection.ContainsAsset(entry.Path)) DragDrop.Begin(entry, count > 1 ? $"{count} files" : entry.Name);
            }

            // Pressed among several selected and let go without dragging: just it.
            if (interaction.Clicked && _pressedAmong == entry.Path && !DragDrop.Dragging)
            {
                _context.Selection.SelectAsset(entry);
                _pressedAmong = null;
            }

            if (interaction.DoubleClicked) Open(entry);
            if (interaction.RightClicked)
            {
                // On one of several selected, the menu is for them all.
                if (!_context.Selection.ContainsAsset(entry.Path))
                {
                    _context.Selection.SelectAsset(entry);
                    _anchor = entry.Path;
                }

                List<MenuItem> items = _context.Selection.Assets.Count > 1 ? ManyMenu([.. _context.Selection.Assets]) : EntryMenu(entry);
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, items);
            }

            // Files dragged onto a folder move into it: all those selected, if it's one of them.
            if (dropInto && DragDrop.Dropped && DragDrop.Payload is AssetEntry moved)
            {
                List<AssetEntry> moving = _context.Selection.ContainsAsset(moved.Path) ? [.. _context.Selection.Assets] : [moved];
                MoveAll(moving.Where(m => m.Path != entry.Path).ToList(), entry.Path);
            }
        }

        /// <summary>
        /// An asset's picture: the image itself for textures, a ball of it for materials, the model itself for models (see
        /// <see cref="Thumbnails"/>), otherwise an icon for its kind.
        /// </summary>
        public static void DrawPicture(AssetEntry entry, Rect rect, bool small = false)
        {
            Texture? thumbnail = entry.Kind is AssetKind.Texture or AssetKind.Material or AssetKind.Model && entry.Path.Length > 0 ? Thumbnails.Get(entry.Path) : null;
            if (entry.Kind == AssetKind.Texture && thumbnail != null)
            {
                float aspect = thumbnail.Width / (float)thumbnail.Height;
                Vector2 fit = aspect >= 1f ? new Vector2(rect.Width, rect.Width / aspect) : new Vector2(rect.Height * aspect, rect.Height);
                Rect fitted = rect.CentreOf(fit);
                Ui.Rect(fitted.Grow(1f), Theme.Field, 5f);
                Vertex.UI.Canvas.DrawImage(thumbnail, fitted.Position, fitted.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White), 4f);
                return;
            }

            // Drawn by the renderer, on nothing: the ball's drawn a touch smaller than the space, as the flat one was.
            if (thumbnail != null)
            {
                Rect shown = entry.Kind == AssetKind.Material ? rect.Shrink(rect.Width * (small ? 0.06f : 0.1f)) : rect.Shrink(rect.Width * (small ? 0f : 0.04f));
                Vertex.UI.Canvas.DrawImage(thumbnail, shown.Position, shown.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White));
                return;
            }

            if (entry.Kind == AssetKind.Material)
            {
                DrawMaterialBall(entry.Path, rect.Shrink(rect.Width * (small ? 0.1f : 0.14f)));
                return;
            }

            if (!small) Ui.Rect(rect.Shrink(rect.Width * 0.08f), Theme.Field, 8f);
            Vector2 centre = rect.Centre;
            float size = rect.Width * (small ? 0.62f : 0.42f);
            switch (entry.Kind)
            {
                case AssetKind.Folder: Icons.Folder(centre, size, Theme.Hex(0xD9B25F)); break;
                case AssetKind.Texture: Icons.Image(centre, size, Theme.TextFaint); break;
                case AssetKind.Model: Icons.Cube(centre, size, Theme.AccentText); break;
                case AssetKind.Prefab: Icons.Prefab(centre, size, Theme.Hex(0x6FA8FF)); break;
                case AssetKind.Scene: Icons.Scene(centre, size, Theme.AxisY); break;
                case AssetKind.Sound: Icons.Sound(centre, size, Theme.Hex(0xC98BF5)); break;
                case AssetKind.Font: Ui.Text(rect, "Aa", Theme.Text, align: 0.5f, size: size * 0.7f, bold: true); break;
                case AssetKind.Script: Ui.Text(rect, "C#", Theme.Hex(0x9B7BE0), align: 0.5f, size: size * 0.62f, bold: true); break;
                case AssetKind.VisualScript: Icons.Graph(centre, size, Theme.Hex(0x5FC9B5)); break;
                case AssetKind.VisualEffect: Icons.Graph(centre, size, Theme.Hex(0xF2994A)); break;
                case AssetKind.AudioGraph: Icons.Sound(centre, size, Theme.Hex(0x57CCDB)); break;
                case AssetKind.Shader: Icons.ScreenEffect(centre, size, Theme.Hex(0xE07BB5)); break;
                case AssetKind.ShaderGraph: Icons.Graph(centre, size, Theme.Hex(0xE07BB5)); break;
                case AssetKind.Animation: Icons.Keyframes(centre, size, Theme.Hex(0x5CD6A8)); break;
                case AssetKind.AnimationGraph: Icons.Graph(centre, size, Theme.Hex(0x5CD6A8)); break;
                case AssetKind.BehaviorTree: Icons.BehaviorTree(centre, size, Theme.Hex(0xF2B45A)); break;
                default: Icons.File(centre, size, Theme.TextFaint); break;
            }
        }

        /// <summary>
        /// A material as a flat ball while its thumbnail's being made: its texture (or colour), shaded darker towards the
        /// bottom right as if lit from above. Only a material already loaded is shown as itself, so a folder of them
        /// doesn't load every one (and its textures) at once.
        /// </summary>
        private static void DrawMaterialBall(string path, Rect rect)
        {
            Material? material = path.Length > 0 ? Assets.LoadedMaterial(path) : null;

            float radius = rect.Width * 0.5f;
            Color color = material?.Color ?? Theme.TextFaint;
            Texture? texture = material?.Texture is { Source: { } source } ? Thumbnails.Get(source) : null;
            Vertex.UI.Canvas.DrawImage(texture, rect.Position, rect.Size, Vector2.Zero, Vector2.One, Ui.Fade(color), radius);

            // Light and shade: a soft highlight up and to the left, darker round the edge.
            Vertex.UI.Canvas.DrawGradient(rect.Position, rect.Size, new Color(1f, 1f, 1f, 0.32f * Ui.Opacity), new Color(1f, 1f, 1f, 0.05f * Ui.Opacity),
                new Color(0f, 0f, 0f, 0.42f * Ui.Opacity), new Color(0f, 0f, 0f, 0.12f * Ui.Opacity), radius);
            if (material?.Emission is { } emission && (emission.R + emission.G + emission.B) * material.EmissionStrength > 0.05f)
            {
                Ui.Outline(rect.Grow(1.5f), emission.WithAlpha(0.8f), radius + 1.5f, 2f);
            }
        }

        private void Open(AssetEntry entry)
        {
            switch (entry.Kind)
            {
                case AssetKind.Folder:
                    _folder = entry.Path;
                    _search = "";
                    break;
                case AssetKind.Scene:
                    _context.OpenScene(entry.Path);
                    break;
                case AssetKind.Model:
                    AddModel(_context, entry.Path, _context.SpawnPoint);
                    break;
                case AssetKind.Prefab:
                    _context.OpenPrefab(entry.Path);
                    break;
                case AssetKind.Sound:
                    SoundPreview.Toggle(entry.Path);
                    break;
                case AssetKind.VisualScript or AssetKind.VisualEffect or AssetKind.AudioGraph or AssetKind.ShaderGraph or AssetKind.AnimationGraph or AssetKind.BehaviorTree:
                    _context.OpenGraph(entry.Path);
                    break;
                case AssetKind.Animation:
                    _context.OpenAnimation(entry.Path);
                    break;
                case AssetKind.Material:
                    _context.Selection.SelectAsset(entry);
                    break;
                default:
                    OpenWithSystem(entry.Path);
                    break;
            }
        }

        /// <summary>Opens a file in whatever the system opens it with (a script in the code editor).</summary>
        private void OpenWithSystem(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                NativeDialogs.ShowInFolder(path);
            }
        }

        /// <summary>Loads a model into the scene at <paramref name="position"/>, as one undoable change, and selects it.</summary>
        public static GameObject? AddModel(EditorContext context, string path, Vector3 position)
        {
            try
            {
                GameObject root = Model.Load(path);
                root.Transform.Position = position;
                context.Scene.ApplyChanges();
                context.History.Record(new CreateObject(root, $"Add {root.Name}"));
                context.Selection.Select(root);
                return root;
            }
            catch (Exception exception)
            {
                context.Notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- Making prefabs

        /// <summary>An object dragged here from the outliner becomes a prefab, in the folder shown or the folder it's dropped on.</summary>
        private void HandleObjectDrop(Rect area)
        {
            if (OutlinerPanel.Dragged is not { } dragged || !area.Contains(Ui.Mouse)) return;

            Ui.Outline(area.Grow(3f), Theme.Accent.WithAlpha(0.8f), 6f, 1.5f);
            string text = $"Make a prefab of {dragged.Name}";
            var chip = new Rect(area.Centre.X - (Ui.TextWidth(text) + 28f) * 0.5f, area.Bottom - 34f, Ui.TextWidth(text) + 28f, 26f);
            Ui.Rect(chip, Theme.Accent, 13f);
            Ui.Text(chip, text, Color.White, align: 0.5f);

            if (!Ui.MouseReleased()) return;

            // Onto a folder's tile, into that folder.
            string folder = _folder!;
            foreach (AssetEntry entry in _context.Assets!.List(_folder!))
            {
                if (entry.Kind == AssetKind.Folder && Ui.HotId == Ui.Id("asset:" + entry.Path)) folder = entry.Path;
            }

            _context.CreatePrefab(dragged, folder);
        }

        // ---------------------------------------------------------------- Menus

        private List<MenuItem> EntryMenu(AssetEntry entry)
        {
            var items = new List<MenuItem>();
            string? open = entry.Kind switch
            {
                AssetKind.Folder or AssetKind.Scene or AssetKind.VisualScript or AssetKind.VisualEffect or AssetKind.AudioGraph or AssetKind.ShaderGraph or AssetKind.AnimationGraph or AssetKind.BehaviorTree or
                    AssetKind.Animation or AssetKind.Prefab => "Open",
                AssetKind.Model => "Add to Scene",
                AssetKind.Sound => SoundPreview.IsPlaying(entry.Path) ? "Stop" : "Play",
                AssetKind.Script or AssetKind.Shader => "Open in Code Editor",
                _ => null
            };
            if (open != null) items.Add(new MenuItem(open, () => Open(entry)));
            if (entry.Kind == AssetKind.Prefab) items.Add(new MenuItem("Add to Scene", () => _context.AddPrefab(entry.Path, _context.SpawnPoint)));
            items.Add(new MenuItem("Rename", () => BeginRename(entry)) { Shortcut = "F2" });
            if (entry.Kind != AssetKind.Folder) items.Add(new MenuItem("Duplicate", () => DuplicateAll([entry])) { Shortcut = "Ctrl+D" });
            items.Add(new MenuItem("Delete", () => AskDelete([entry])) { Shortcut = "Del", Icon = Icons.Trash });
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem(ShowInLabel, () => NativeDialogs.ShowInFolder(entry.Path)) { Icon = Icons.Folder });
            items.Add(new MenuItem("Copy Path", () => InputSystem.ClipboardText = _context.Assets!.Relative(entry.Path)));

            // Making something new here is to one side, so the file's own choices stand out.
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem("Create") { Icon = Icons.Plus, Children = Flat(NewItems(_folder!)) });
            return items;
        }

        /// <summary>
        /// A menu's sections laid out in one list, between lines: for a menu that's already beside another (a menu
        /// opens only one more beside it).
        /// </summary>
        private static List<MenuItem> Flat(List<MenuItem> items)
        {
            var flat = new List<MenuItem>();
            void Line()
            {
                if (flat.Count > 0 && !flat[^1].IsSeparator) flat.Add(MenuItem.Separator);
            }

            foreach (MenuItem item in items)
            {
                if (item.IsSeparator) Line();
                else if (item.Children != null)
                {
                    Line();
                    foreach (MenuItem child in item.Children)
                    {
                        if (child.IsSeparator) continue;
                        flat.Add(child);
                    }
                }
                else flat.Add(item);
            }

            return flat;
        }

        /// <summary>The menu of several files selected together: what can be done to them all.</summary>
        private List<MenuItem> ManyMenu(List<AssetEntry> picked)
        {
            List<AssetEntry> files = picked.Where(p => p.Kind != AssetKind.Folder).ToList();
            return
            [
                new MenuItem($"Rename {picked.Count} Items…", () => AskRenameMany(picked)) { Shortcut = "F2" },
                new MenuItem(files.Count == picked.Count ? $"Duplicate {files.Count} Items" : "Duplicate the Files", () => DuplicateAll(files))
                    { Shortcut = "Ctrl+D", Enabled = files.Count > 0 },
                new MenuItem($"Delete {picked.Count} Items", () => AskDelete(picked)) { Shortcut = "Del", Icon = Icons.Trash },
                MenuItem.Separator,
                new MenuItem("Create") { Icon = Icons.Plus, Children = Flat(NewItems(_folder!)) }
            ];
        }

        private List<MenuItem> FolderMenu(string folder)
        {
            List<MenuItem> items = NewItems(folder);
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem(ShowInLabel, () => NativeDialogs.ShowInFolder(folder)) { Icon = Icons.Folder });
            items.Add(new MenuItem("Refresh", () => _context.Assets?.Refresh()));
            return items;
        }

        private static string ShowInLabel => OperatingSystem.IsWindows() ? "Show in Explorer" : OperatingSystem.IsMacOS() ? "Show in Finder" : "Show in File Manager";

        /// <summary>
        /// The things that can be made in <paramref name="folder"/>: each is made, then named. The everyday ones first,
        /// the rest in sections that open beside the menu, so it stays short.
        /// </summary>
        private List<MenuItem> NewItems(string folder)
        {
            var items = new List<MenuItem>
            {
                new("New Folder", () => Made(Directory.CreateDirectory(Unique(Path.Combine(folder, "New Folder"), "")).FullName)) { Icon = Icons.Folder },
                new("New Scene", () => Made(NewScene(folder))) { Icon = Icons.Scene }
            };

            if (_context.Selection.Object is { } selected)
            {
                items.Add(new MenuItem($"New Prefab from {selected.Name}", () =>
                {
                    if (_context.CreatePrefab(selected, folder) is { } prefab) Made(prefab.Source);
                }) { Icon = Icons.Prefab });
            }

            items.Add(MenuItem.Separator);

            var scripts = new List<MenuItem>();
            if (_context.Project != null) scripts.Add(new MenuItem("C# Script", () => Made(_context.CreateScript(folder))) { Icon = Icons.Component });
            scripts.Add(new MenuItem("Visual Script", () => Made(_context.CreateVisualScript(folder))) { Icon = Icons.Graph });

            items.Add(new MenuItem("Script") { Icon = Icons.Component, Children = scripts });
            items.Add(new MenuItem("Material & Shaders")
            {
                Icon = (c, s, col) => Icons.Ring(c, s * 0.3f, col),
                Children =
                [
                    new("Material", () => Made(_context.CreateMaterial(folder))) { Icon = (c, s, col) => Icons.Ring(c, s * 0.3f, col) },
                    new("Shader Graph", () => Made(_context.CreateShaderGraph(folder))) { Icon = Icons.Graph },
                    MenuItem.Separator,
                    new("Post Effect", () => Made(_context.CreatePostEffect(folder))) { Icon = Icons.ScreenEffect },
                    new("Post Effect Graph", () => Made(_context.CreatePostEffectGraph(folder))) { Icon = Icons.ScreenEffect }
                ]
            });
            items.Add(new MenuItem("Animation")
            {
                Icon = Icons.Keyframes,
                Children =
                [
                    new("Animation", () => Made(_context.CreateAnimation(folder))) { Icon = Icons.Keyframes },
                    new("Animation Graph", () => Made(_context.CreateAnimationGraph(folder))) { Icon = Icons.Graph }
                ]
            });
            items.Add(new MenuItem("Effects & Audio")
            {
                Icon = Icons.Sound,
                Children =
                [
                    new("VFX Graph", () => Made(_context.CreateVfxGraph(folder))) { Icon = Icons.Graph },
                    new("Audio Graph", () => Made(_context.CreateAudioGraph(folder))) { Icon = Icons.Sound }
                ]
            });
            items.Add(new MenuItem("AI")
            {
                Icon = Icons.BehaviorTree,
                Children = [new("Behavior Tree", () => Made(_context.CreateBehaviorTree(folder))) { Icon = Icons.BehaviorTree }]
            });

            return items;
        }

        /// <summary>Shows something just made: selected, and its name ready to type over.</summary>
        private void Made(string path)
        {
            _context.Assets?.Refresh();
            var entry = new AssetEntry(path, Directory.Exists(path) ? AssetKind.Folder : AssetDatabase.KindOf(path));
            _context.Selection.SelectAsset(entry);
            BeginRename(entry);
        }

        private string NewScene(string folder)
        {
            string path = Unique(Path.Combine(folder, "New Scene"), ".scene");
            var scene = new Scene();
            var sun = new GameObject("Sun", scene);
            sun.AddComponent<Light>();
            sun.Transform.EulerAngles = new Vector3(-50f, 35f, 0f);
            var camera = new GameObject("Main Camera", scene);
            camera.Transform.Position = new Vector3(0f, 2f, 8f);
            camera.Transform.EulerAngles = new Vector3(-10f, 0f, 0f);
            camera.AddComponent<Camera>();
            Vertex.Serialization.SceneFile.Save(scene, path, _context.AssetFolders);
            scene.Discard();
            return path;
        }

        // ---------------------------------------------------------------- Renaming, moving, deleting

        private void BeginRename(AssetEntry entry)
        {
            _renaming = entry.Path;
            _renameText = entry.Kind == AssetKind.Folder ? entry.Name : entry.NameWithoutExtension;
            TextInput.Focus(Ui.Id("assets.rename"), selectAll: true);
        }

        private void DrawRename(AssetEntry entry, Rect label)
        {
            int id = Ui.Id("assets.rename");
            if (TextInput.Draw(id, label.Grow(1f), ref _renameText, textSize: Theme.SmallTextSize)) Rename(entry, _renameText.Trim());
            if (Ui.FocusId != id) _renaming = null;
        }

        private void Rename(AssetEntry entry, string name)
        {
            _renaming = null;
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                if (name.Length > 0) _context.Notify("A name can't have / \\ : * ? \" < > | in it");
                return;
            }

            string extension = entry.Kind == AssetKind.Folder ? "" : Path.GetExtension(entry.Path);
            string target = Path.Combine(Path.GetDirectoryName(entry.Path)!, name + extension);
            if (string.Equals(target, entry.Path, StringComparison.Ordinal)) return;

            try
            {
                MoveFile(entry, target);
                if (entry.Kind == AssetKind.Script) RenameClass(target, Path.GetFileNameWithoutExtension(entry.Path));
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't rename {entry.Name}: {exception.Message}");
            }
        }

        /// <summary>Moves files into <paramref name="folder"/>, telling how it went once for them all.</summary>
        private void MoveAll(List<AssetEntry> entries, string folder)
        {
            int moved = 0;
            string? problem = null;
            foreach (AssetEntry entry in entries)
            {
                string target = Path.Combine(folder, entry.Name);
                if (string.Equals(Path.GetDirectoryName(entry.Path), folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    problem ??= $"{Path.GetFileName(folder)} already has a {entry.Name}";
                    continue;
                }

                try
                {
                    MoveFile(entry, target);
                    moved++;
                }
                catch (Exception exception)
                {
                    problem ??= $"Couldn't move {entry.Name}: {exception.Message}";
                }
            }

            if (problem != null) _context.Notify(problem);
            else if (moved > 0) _context.Notify(moved == 1 ? $"Moved {entries[0].Name} into {Path.GetFileName(folder)}" : $"Moved {moved} items into {Path.GetFileName(folder)}");
        }

        private void MoveFile(AssetEntry entry, string target)
        {
            if (File.Exists(target) || Directory.Exists(target) && !string.Equals(target, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"there's already a {Path.GetFileName(target)}");
            }

            if (entry.Kind == AssetKind.Folder) Directory.Move(entry.Path, target);
            else File.Move(entry.Path, target);

            // What was loaded from it follows it, so what uses it (a script's graph, a scene's prefabs) still does.
            Assets.Moved(entry.Path, target);
            _context.FileMoved(entry.Path, target);
            Thumbnails.Forget(entry.Path);
            _context.Assets?.Refresh();

            // Still selected under its new name (with the others selected alongside it).
            Selection selection = _context.Selection;
            if (selection.ContainsAsset(entry.Path)) selection.SelectAssets(selection.Assets.Select(a => a.Path == entry.Path ? new AssetEntry(target, entry.Kind) : a));
        }

        /// <summary>Copies files beside themselves ("Brick copy"). One copy is selected and named; several are selected.</summary>
        private void DuplicateAll(List<AssetEntry> entries)
        {
            var copies = new List<AssetEntry>();
            foreach (AssetEntry entry in entries.Where(e => e.Kind != AssetKind.Folder))
            {
                try
                {
                    string path = Unique(Path.Combine(Path.GetDirectoryName(entry.Path)!, entry.NameWithoutExtension + " copy"), Path.GetExtension(entry.Path));
                    File.Copy(entry.Path, path);
                    copies.Add(new AssetEntry(path, entry.Kind));
                }
                catch (Exception exception)
                {
                    _context.Notify($"Couldn't duplicate {entry.Name}: {exception.Message}");
                }
            }

            if (copies.Count == 1) Made(copies[0].Path);
            else if (copies.Count > 1)
            {
                _context.Assets?.Refresh();
                _context.Selection.SelectAssets(copies);
                _context.Notify($"Duplicated {copies.Count} items");
            }
        }

        private void AskDelete(List<AssetEntry> entries)
        {
            if (entries.Count == 0) return;
            int folders = entries.Count(e => e.Kind == AssetKind.Folder);
            string what = entries.Count == 1
                ? entries[0].Kind == AssetKind.Folder ? $"the folder {entries[0].Name} and everything in it" : entries[0].Name
                : folders == 0 ? $"these {entries.Count} items" : $"these {entries.Count} items, and everything in the {(folders == 1 ? "folder" : $"{folders} folders")} among them";
            Dialog.Ask("Delete", $"Delete {what}? This can't be undone.",
                ("Delete", () => DeleteAll(entries)),
                ("Cancel", () => { }));
        }

        private void DeleteAll(List<AssetEntry> entries)
        {
            int deleted = 0;
            foreach (AssetEntry entry in entries)
            {
                try
                {
                    if (entry.Kind == AssetKind.Sound) SoundPreview.Forget(entry.Path);
                    if (entry.Kind == AssetKind.Folder) Directory.Delete(entry.Path, recursive: true);
                    else if (File.Exists(entry.Path)) File.Delete(entry.Path);
                    else continue;

                    Assets.Forget(entry.Path);
                    Thumbnails.Forget(entry.Path);
                    deleted++;
                }
                catch (Exception exception)
                {
                    _context.Notify($"Couldn't delete {entry.Name}: {exception.Message}");
                }
            }

            _context.Assets?.Refresh();
            HashSet<string> gone = entries.Select(e => e.Path).ToHashSet();
            _context.Selection.SelectAssets(_context.Selection.Assets.Where(a => !gone.Contains(a.Path)));
            if (deleted > 0) _context.Notify(deleted == 1 ? $"Deleted {entries[0].Name}" : $"Deleted {deleted} items");
        }

        // ---------------------------------------------------------------- Renaming several at once

        /// <summary>Renames <paramref name="picked"/> together, in a dialog: numbered from a name, or by replacing text in their names.</summary>
        private void AskRenameMany(List<AssetEntry> picked)
        {
            // In the order they're shown, so the numbers run along the tiles.
            List<AssetEntry> shown = _entries.ToList();
            picked = picked.OrderBy(p => shown.FindIndex(e => e.Path == p.Path) is var at and >= 0 ? at : int.MaxValue).ToList();

            // Starting from the main one's name without its number ("Rock 3" → "Rock").
            string main = _context.Selection.Asset is { } asset && picked.Any(p => p.Path == asset.Path) ? NameOf(asset) : NameOf(picked[0]);
            _manyName = System.Text.RegularExpressions.Regex.Replace(main, @"[\s_-]*\d+$", "");
            if (_manyName.Length == 0) _manyName = main;
            _manyStart = 1;
            _find = "";
            _replaceWith = "";

            int nameId = Ui.Id("assets.renameMany.name");
            Dialog.Show($"Rename {picked.Count} Items", new Vector2(500f, 400f), rect => DrawRenameMany(rect, picked));
            TextInput.Focus(_renameByReplacing ? Ui.Id("assets.renameMany.find") : nameId);
        }

        private static string NameOf(AssetEntry entry) => entry.Kind == AssetKind.Folder ? entry.Name : entry.NameWithoutExtension;

        /// <summary>The new names <paramref name="picked"/> get from what's typed in the dialog.</summary>
        internal List<string> NewNames(IReadOnlyList<AssetEntry> picked) => _renameByReplacing
            ? picked.Select(p => _find.Length == 0 ? NameOf(p) : NameOf(p).Replace(_find, _replaceWith, StringComparison.Ordinal).Trim()).ToList()
            : picked.Select((_, i) => $"{_manyName.Trim()} {_manyStart + i}").ToList();

        /// <summary>What's wrong with renaming <paramref name="picked"/> to <paramref name="names"/>, if anything.</summary>
        internal static string? RenameProblem(IReadOnlyList<AssetEntry> picked, IReadOnlyList<string> names)
        {
            if (names.Any(n => n.Length == 0)) return "Every one needs a name";
            if (names.Any(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return "A name can't have / \\ : * ? \" < > | in it";
            if (picked.Select((p, i) => NameOf(p) == names[i]).All(same => same)) return "Nothing would change";

            HashSet<string> sources = picked.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < picked.Count; i++)
            {
                string target = TargetOf(picked[i], names[i]);
                if (!targets.Add(target)) return $"Two would be called {Path.GetFileName(target)}";
                if ((File.Exists(target) || Directory.Exists(target)) && !sources.Contains(target)) return $"There's already a {Path.GetFileName(target)}";
            }

            return null;
        }

        private static string TargetOf(AssetEntry entry, string name) =>
            Path.Combine(Path.GetDirectoryName(entry.Path)!, name + (entry.Kind == AssetKind.Folder ? "" : Path.GetExtension(entry.Path)));

        private void DrawRenameMany(Rect rect, List<AssetEntry> picked)
        {
            // Enter renames them, even from a field (which takes the key when it finishes).
            bool enter = InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter);
            float y = rect.Y;

            // How: numbered, or by replacing text.
            var number = new Rect(rect.X, y, 150f, 28f);
            var replace = new Rect(number.Right + 6f, y, 150f, 28f);
            if (Widgets.Button(Ui.Id("assets.renameMany.number"), number, "Number Them", _renameByReplacing ? ButtonStyle.Subtle : ButtonStyle.Primary))
            {
                _renameByReplacing = false;
                TextInput.Focus(Ui.Id("assets.renameMany.name"));
            }

            if (Widgets.Button(Ui.Id("assets.renameMany.replace"), replace, "Replace Text", _renameByReplacing ? ButtonStyle.Primary : ButtonStyle.Subtle))
            {
                _renameByReplacing = true;
                TextInput.Focus(Ui.Id("assets.renameMany.find"));
            }

            y += 42f;
            const float labelWidth = 110f;
            void Field(string label, string id, ref string text, string placeholder)
            {
                Ui.Text(new Rect(rect.X, y, labelWidth, 28f), label, Theme.TextDim);
                TextInput.Draw(Ui.Id(id), new Rect(rect.X + labelWidth, y, rect.Width - labelWidth, 28f), ref text, placeholder, live: true);
                y += 36f;
            }

            if (_renameByReplacing)
            {
                Field("Find", "assets.renameMany.find", ref _find, "Text in their names");
                Field("Replace with", "assets.renameMany.with", ref _replaceWith, "Nothing, to take it out");
            }
            else
            {
                Field("Name", "assets.renameMany.name", ref _manyName, "What they're called");
                Ui.Text(new Rect(rect.X, y, labelWidth, 28f), "Numbered from", Theme.TextDim);
                NumberField.Int(Ui.Id("assets.renameMany.start"), new Rect(rect.X + labelWidth, y, 90f, 28f), ref _manyStart, min: 0);
                y += 36f;
            }

            // What they'd become, as many as fit.
            List<string> names = NewNames(picked);
            string? problem = RenameProblem(picked, names);
            y += 4f;
            Ui.Rect(new Rect(rect.X, y, rect.Width, 1f), Theme.Border);
            y += 10f;

            float bottom = rect.Bottom - 44f;
            int fits = Math.Max((int)((bottom - y) / 24f), 1);
            int show = picked.Count > fits ? fits - 1 : picked.Count;
            for (int i = 0; i < show; i++)
            {
                float half = (rect.Width - 30f) * 0.5f;
                Ui.Text(new Rect(rect.X, y, half, 24f), NameOf(picked[i]), Theme.TextDim, size: Theme.SmallTextSize);
                Icons.Chevron(new Vector2(rect.X + half + 15f, y + 12f), 11f, Theme.TextFaint);
                Ui.Text(new Rect(rect.X + half + 30f, y, half, 24f), names[i], names[i] == NameOf(picked[i]) ? Theme.TextFaint : Theme.Text, size: Theme.SmallTextSize);
                y += 24f;
            }

            if (show < picked.Count) Ui.Text(new Rect(rect.X, y, rect.Width, 20f), $"and {picked.Count - show} more", Theme.TextFaint, size: Theme.SmallTextSize);

            // Rename (or Enter), once it's all right; why not, if it isn't.
            Rect buttons = rect.TakeBottom(30f);
            if (Widgets.Button(Ui.Id("assets.renameMany.cancel"), buttons.TakeRight(90f), "Cancel")) Dialog.Close();
            if (problem != null) Ui.Text(buttons.CutRight(100f), problem, problem == "Nothing would change" ? Theme.TextFaint : Theme.Warning, size: Theme.SmallTextSize);
            else if (Widgets.Button(Ui.Id("assets.renameMany.go"), buttons.CutRight(98f).TakeRight(100f), "Rename", ButtonStyle.Primary) || enter)
            {
                InputSystem.Consume(Key.Enter);
                InputSystem.Consume(Key.KeypadEnter);
                Dialog.Close();
                RenameMany(picked, names);
            }
        }

        /// <summary>
        /// Renames <paramref name="picked"/> to <paramref name="names"/>. When one's new name is another's old one (two
        /// swapping), they all go by stand-in names first, so none is in another's way.
        /// </summary>
        internal void RenameMany(IReadOnlyList<AssetEntry> picked, IReadOnlyList<string> names)
        {
            List<(AssetEntry Entry, string Target)> moves = picked.Select((p, i) => (p, TargetOf(p, names[i])))
                .Where(m => !string.Equals(m.Item1.Path, m.Item2, StringComparison.Ordinal)).ToList();
            HashSet<string> sources = picked.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool inTheWay = moves.Any(m => sources.Contains(m.Target));

            int renamed = 0;
            try
            {
                var steps = new List<(AssetEntry From, string To, string OldName)>();
                foreach ((AssetEntry entry, string target) in moves)
                {
                    AssetEntry from = entry;
                    if (inTheWay)
                    {
                        string standIn = Unique(Path.Combine(Path.GetDirectoryName(entry.Path)!, $"renaming {Guid.NewGuid():N}"), Path.GetExtension(entry.Path));
                        if (entry.Kind == AssetKind.Folder) standIn = Path.Combine(Path.GetDirectoryName(entry.Path)!, $"renaming {Guid.NewGuid():N}");
                        MoveFile(entry, standIn);
                        from = new AssetEntry(standIn, entry.Kind);
                    }

                    steps.Add((from, target, Path.GetFileNameWithoutExtension(entry.Path)));
                }

                foreach ((AssetEntry from, string to, string oldName) in steps)
                {
                    MoveFile(from, to);
                    if (from.Kind == AssetKind.Script) RenameClass(to, oldName);
                    renamed++;
                }
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't rename them all: {exception.Message}");
                return;
            }

            _context.Notify($"Renamed {renamed} items");
        }

        /// <summary>A script's class is renamed with its file, so the two still match.</summary>
        private static void RenameClass(string script, string oldName)
        {
            string newName = EditorContext.ClassName(Path.GetFileNameWithoutExtension(script));
            string code = File.ReadAllText(script);
            if (code.Contains($"class {oldName}")) File.WriteAllText(script, code.Replace($"class {oldName}", $"class {newName}"));
        }

        private void HandleKeys()
        {
            bool control = Control;

            // Ctrl+A: every tile shown.
            if (control && InputSystem.GetKeyDown(Key.A))
            {
                InputSystem.Consume(Key.A);
                _context.Selection.SelectAssets(_entries);
                return;
            }

            if (_context.Selection.Asset is not { } selected) return;
            List<AssetEntry> picked = [.. _context.Selection.Assets];

            if (InputSystem.GetKeyDown(Key.F2))
            {
                if (picked.Count > 1) AskRenameMany(picked);
                else BeginRename(selected);
            }

            if (InputSystem.GetKeyDown(Key.Delete)) AskDelete(picked);
            if (control && InputSystem.GetKeyDown(Key.D) && picked.Any(p => p.Kind != AssetKind.Folder))
            {
                InputSystem.Consume(Key.D);
                DuplicateAll(picked);
            }

            if (InputSystem.GetKeyDown(Key.Enter))
            {
                InputSystem.Consume(Key.Enter);
                Open(selected);
            }
        }

        /// <summary><paramref name="path"/> plus <paramref name="extension"/>, numbered if something's there already ("Brick 2.material").</summary>
        public static string Unique(string path, string extension)
        {
            string candidate = path + extension;
            for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++) candidate = $"{path} {i}{extension}";
            return candidate;
        }
    }
}
