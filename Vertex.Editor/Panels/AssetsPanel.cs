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

            // The whole area takes right-clicks and clicks on empty space; the tiles, drawn after, are on top of it.
            Interaction background = Ui.Interact(Ui.Id("assets.background"), area, rightClick: true);
            if (background.Clicked) _context.Selection.SelectAsset(null);
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

        private void DrawTile(AssetEntry entry, Rect cell, float tile)
        {
            int id = Ui.Id("asset:" + entry.Path);
            Interaction interaction = Ui.Interact(id, cell, rightClick: true);
            bool selected = _context.Selection.Asset?.Path == entry.Path;

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
                _context.Selection.SelectAsset(entry);
                DragDrop.Begin(entry, entry.Name);
            }

            if (interaction.DoubleClicked) Open(entry);
            if (interaction.RightClicked)
            {
                _context.Selection.SelectAsset(entry);
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, EntryMenu(entry));
            }

            // Files dragged onto a folder move into it.
            if (dropInto && DragDrop.Dropped && DragDrop.Payload is AssetEntry moved) Move(moved, entry.Path);
        }

        /// <summary>An asset's picture: the image itself for textures, a ball of its colour for materials, otherwise an icon for its kind.</summary>
        public static void DrawPicture(AssetEntry entry, Rect rect, bool small = false)
        {
            if (entry.Kind == AssetKind.Texture && Thumbnails.Get(entry.Path) is { } thumbnail)
            {
                float aspect = thumbnail.Width / (float)thumbnail.Height;
                Vector2 fit = aspect >= 1f ? new Vector2(rect.Width, rect.Width / aspect) : new Vector2(rect.Height * aspect, rect.Height);
                Rect fitted = rect.CentreOf(fit);
                Ui.Rect(fitted.Grow(1f), Theme.Field, 5f);
                Vertex.UI.Canvas.DrawImage(thumbnail, fitted.Position, fitted.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White), 4f);
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
                default: Icons.File(centre, size, Theme.TextFaint); break;
            }
        }

        /// <summary>A material as a ball: its texture (or colour), shaded darker towards the bottom right as if lit from above.</summary>
        private static void DrawMaterialBall(string path, Rect rect)
        {
            Material? material = null;
            try
            {
                if (path.Length > 0) material = Assets.LoadMaterial(path);
            }
            catch (Exception)
            {
                // A file that can't be read shows as a grey ball.
            }

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
                    _context.AddPrefab(entry.Path, _context.SpawnPoint);
                    break;
                case AssetKind.Sound:
                    SoundPreview.Toggle(entry.Path);
                    break;
                case AssetKind.VisualScript:
                    _context.OpenGraph(entry.Path);
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
                AssetKind.Folder or AssetKind.Scene or AssetKind.VisualScript => "Open",
                AssetKind.Model or AssetKind.Prefab => "Add to Scene",
                AssetKind.Sound => SoundPreview.IsPlaying(entry.Path) ? "Stop" : "Play",
                AssetKind.Script => "Open in Code Editor",
                _ => null
            };
            if (open != null) items.Add(new MenuItem(open, () => Open(entry)));
            items.Add(new MenuItem("Rename", () => BeginRename(entry)) { Shortcut = "F2" });
            if (entry.Kind != AssetKind.Folder) items.Add(new MenuItem("Duplicate", () => Duplicate(entry)) { Shortcut = "Ctrl+D" });
            items.Add(new MenuItem("Delete", () => AskDelete(entry)) { Shortcut = "Del", Icon = Icons.Trash });
            items.Add(MenuItem.Separator);
            items.Add(new MenuItem(ShowInLabel, () => NativeDialogs.ShowInFolder(entry.Path)) { Icon = Icons.Folder });
            items.Add(new MenuItem("Copy Path", () => InputSystem.ClipboardText = _context.Assets!.Relative(entry.Path)));
            items.Add(MenuItem.Separator);
            items.AddRange(NewItems(_folder!));
            return items;
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

        /// <summary>The things that can be made in <paramref name="folder"/>: each is made, then named.</summary>
        private List<MenuItem> NewItems(string folder)
        {
            var items = new List<MenuItem>
            {
                new("New Folder", () => Made(Directory.CreateDirectory(Unique(Path.Combine(folder, "New Folder"), "")).FullName)) { Icon = Icons.Folder },
                new("New Material", () => Made(_context.CreateMaterial(folder))) { Icon = (c, s, col) => Icons.Ring(c, s * 0.3f, col) },
                new("New Scene", () => Made(NewScene(folder))) { Icon = Icons.Scene },
                new("New Visual Script", () => Made(_context.CreateVisualScript(folder))) { Icon = Icons.Graph }
            };

            if (_context.Project?.GameProjectFile != null) items.Add(new MenuItem("New Script", () => Made(NewScript(folder))) { Icon = Icons.Component });
            if (_context.Selection.Object is { } selected)
            {
                items.Add(new MenuItem($"New Prefab from {selected.Name}", () =>
                {
                    if (_context.CreatePrefab(selected, folder) is { } prefab) Made(prefab.Source);
                }) { Icon = Icons.Prefab });
            }

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

        private static string NewScript(string folder)
        {
            string path = Unique(Path.Combine(folder, "NewScript"), ".cs");
            string name = Path.GetFileNameWithoutExtension(path).Replace(" ", "");
            File.WriteAllText(path, ScriptTemplate(name));
            return path;
        }

        private static string ScriptTemplate(string name) => $$"""
            using System.Numerics;

            using Vertex;
            using Vertex.Input;

            /// <summary>A component of the game's: add it to an object in the editor's inspector.</summary>
            public class {{name}} : Component
            {
                // Public properties and fields show in the inspector, and are saved with the scene.
                public float Speed { get; set; } = 1f;

                // Runs once, before the first Update.
                protected override void Start()
                {
                }

                // Runs every frame.
                protected override void Update()
                {
                }
            }

            """;

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

                // A script's class is renamed with it, so the two still match.
                if (entry.Kind == AssetKind.Script)
                {
                    string oldName = Path.GetFileNameWithoutExtension(entry.Path), newName = name.Replace(" ", "");
                    string code = File.ReadAllText(target);
                    if (code.Contains($"class {oldName}")) File.WriteAllText(target, code.Replace($"class {oldName}", $"class {newName}"));
                }
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't rename {entry.Name}: {exception.Message}");
            }
        }

        private void Move(AssetEntry entry, string folder)
        {
            string target = Path.Combine(folder, entry.Name);
            if (string.Equals(Path.GetDirectoryName(entry.Path), folder, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(target) || Directory.Exists(target))
            {
                _context.Notify($"{Path.GetFileName(folder)} already has a {entry.Name}");
                return;
            }

            try
            {
                MoveFile(entry, target);
                _context.Notify($"Moved {entry.Name} into {Path.GetFileName(folder)}");
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't move {entry.Name}: {exception.Message}");
            }
        }

        private void MoveFile(AssetEntry entry, string target)
        {
            if (File.Exists(target) || Directory.Exists(target) && !string.Equals(target, entry.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"there's already a {Path.GetFileName(target)}");
            }

            if (entry.Kind == AssetKind.Folder) Directory.Move(entry.Path, target);
            else File.Move(entry.Path, target);

            Assets.Forget(entry.Path);
            Thumbnails.Forget(entry.Path);
            _context.Assets?.Refresh();
            if (_context.Selection.Asset?.Path == entry.Path) _context.Selection.SelectAsset(new AssetEntry(target, entry.Kind));
        }

        private void Duplicate(AssetEntry entry)
        {
            try
            {
                string path = Unique(Path.Combine(Path.GetDirectoryName(entry.Path)!, entry.NameWithoutExtension + " copy"), Path.GetExtension(entry.Path));
                File.Copy(entry.Path, path);
                Made(path);
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't duplicate {entry.Name}: {exception.Message}");
            }
        }

        private void AskDelete(AssetEntry entry)
        {
            string what = entry.Kind == AssetKind.Folder ? $"the folder {entry.Name} and everything in it" : entry.Name;
            Dialog.Ask("Delete", $"Delete {what}? This can't be undone.",
                ("Delete", () => Delete(entry)),
                ("Cancel", () => { }));
        }

        private void Delete(AssetEntry entry)
        {
            try
            {
                if (entry.Kind == AssetKind.Sound) SoundPreview.Forget(entry.Path);
                if (entry.Kind == AssetKind.Folder) Directory.Delete(entry.Path, recursive: true);
                else File.Delete(entry.Path);

                Assets.Forget(entry.Path);
                Thumbnails.Forget(entry.Path);
                _context.Assets?.Refresh();
                if (_context.Selection.Asset?.Path == entry.Path) _context.Selection.SelectAsset(null);
                _context.Notify($"Deleted {entry.Name}");
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't delete {entry.Name}: {exception.Message}");
            }
        }

        private void HandleKeys()
        {
            if (_context.Selection.Asset is not { } selected) return;
            bool control = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);

            if (InputSystem.GetKeyDown(Key.F2)) BeginRename(selected);
            if (InputSystem.GetKeyDown(Key.Delete)) AskDelete(selected);
            if (control && InputSystem.GetKeyDown(Key.D) && selected.Kind != AssetKind.Folder)
            {
                InputSystem.Consume(Key.D);
                Duplicate(selected);
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
