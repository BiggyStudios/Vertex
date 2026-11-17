using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Platform;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The panel under the scene: the project's Assets folder as tiles, images showing as themselves. Double-click a
    /// folder to go in, a scene to open it, a model to add it to the scene; drag a texture onto a texture slot or an
    /// object in the scene, or a model into the scene.
    /// </summary>
    public sealed class AssetsPanel
    {
        private readonly EditorContext _context;
        private string? _folder;
        private string _search = "";

        public AssetsPanel(EditorContext context)
        {
            _context = context;
        }

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

            DrawHeader(rect, assets);

            // The tiles.
            float tile = _context.Preferences.AssetTileSize;
            string search = _search.Trim();
            IReadOnlyList<AssetEntry> entries = search.Length > 0
                ? assets.AllFiles().Where(e => e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList()
                : assets.List(_folder);

            Rect area = content.Shrink(Theme.Padding, 6f);
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
                Ui.Text(new Rect(area.X, area.Y + 20f, area.Width, 20f), search.Length > 0 ? "Nothing matches" : "Empty: copy files into this folder to add them",
                    Theme.TextFaint, align: 0.5f);
            }

            float rows = MathF.Ceiling(entries.Count / (float)columns);
            var below = new Rect(area.X, scroll.Top + rows * cellHeight, area.Width, MathF.Max(area.Bottom - (scroll.Top + rows * cellHeight), 0f));
            Interaction background = Ui.Interact(Ui.Id("assets.background"), below, rightClick: true);
            if (background.Clicked) _context.Selection.SelectAsset(null);
            if (background.RightClicked) Menu.Open(Ui.Id("assets.background"), Ui.Mouse, FolderMenu(_folder));

            scroll.End(rows * cellHeight + 8f);
        }

        private void DrawHeader(Rect rect, AssetDatabase assets)
        {
            // Where we are: each folder on the way down from Assets, clickable to go back up.
            float x = rect.X + 80f;
            float y = rect.Y + (PanelFrame.HeaderHeight - 22f) * 0.5f;
            string relative = Path.GetRelativePath(assets.Root, _folder!);
            string[] parts = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string path = assets.Root;
            for (int i = -1; i < parts.Length; i++)
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

            // Search and tile size on the right.
            Rect searchRect = new(rect.Right - Theme.Padding - 200f, rect.Y + 5f, 200f, 26f);
            TextInput.Draw(Ui.Id("assets.search"), searchRect, ref _search, "Search assets", live: true, icon: Icons.Search);

            float size = _context.Preferences.AssetTileSize;
            var slider = new Rect(searchRect.X - 110f, rect.Y + 8f, 90f, 20f);
            if (Widgets.ThinSlider(Ui.Id("assets.size"), slider, ref size, 56f, 140f, "Tile size")) _context.Preferences.AssetTileSize = MathF.Round(size);
        }

        private void DrawTile(AssetEntry entry, Rect cell, float tile)
        {
            int id = Ui.Id("asset:" + entry.Path);
            Interaction interaction = Ui.Interact(id, cell, rightClick: true);
            bool selected = _context.Selection.Asset?.Path == entry.Path;

            float hover = Ui.Smooth(Ui.Id(id, "hover"), interaction.Hovered ? 1f : 0f, 20f);
            if (selected) Ui.Rect(cell, Theme.Selection, 6f);
            else if (hover > 0.01f) Ui.Rect(cell, Theme.Hover.WithAlpha(1.5f * hover), 6f);

            var picture = new Rect(cell.X + (cell.Width - tile) * 0.5f, cell.Y + 4f, tile, tile);
            DrawPicture(entry, picture);

            string name = entry.Kind == AssetKind.Folder ? entry.Name : entry.NameWithoutExtension;
            Ui.Text(new Rect(cell.X + 2f, picture.Bottom + 2f, cell.Width - 4f, 22f), name, selected ? Color.White : Theme.Text, align: 0.5f, size: Theme.SmallTextSize);
            Ui.Tooltip(id, _context.Assets!.Relative(entry.Path));

            if (interaction.Pressed)
            {
                _context.Selection.SelectAsset(entry);
                if (entry.Kind != AssetKind.Folder) DragDrop.Begin(entry, entry.Name);
            }

            if (interaction.DoubleClicked) Open(entry);
            if (interaction.RightClicked)
            {
                _context.Selection.SelectAsset(entry);
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse, EntryMenu(entry));
            }
        }

        /// <summary>An asset's picture: the image itself for textures, otherwise an icon for its kind.</summary>
        public static void DrawPicture(AssetEntry entry, Rect rect)
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

            Ui.Rect(rect.Shrink(rect.Width * 0.08f), Theme.Field, 8f);
            Vector2 centre = rect.Centre;
            float size = rect.Width * 0.42f;
            switch (entry.Kind)
            {
                case AssetKind.Folder: Icons.Folder(centre, size, Theme.Hex(0xD9B25F)); break;
                case AssetKind.Texture: Icons.Image(centre, size, Theme.TextFaint); break;
                case AssetKind.Model: Icons.Cube(centre, size, Theme.AccentText); break;
                case AssetKind.Scene: Icons.Scene(centre, size, Theme.AxisY); break;
                case AssetKind.Sound: Icons.Sound(centre, size, Theme.Hex(0xC98BF5)); break;
                case AssetKind.Font: Ui.Text(rect, "Aa", Theme.Text, align: 0.5f, size: size * 0.7f, bold: true); break;
                default: Icons.File(centre, size, Theme.TextFaint); break;
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
                default:
                    NativeDialogs.ShowInFolder(entry.Path);
                    break;
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

        private List<MenuItem> EntryMenu(AssetEntry entry)
        {
            var items = new List<MenuItem>();
            if (entry.Kind is AssetKind.Folder or AssetKind.Scene or AssetKind.Model) items.Add(new MenuItem(entry.Kind == AssetKind.Model ? "Add to Scene" : "Open", () => Open(entry)));
            items.Add(new MenuItem("Show in Explorer", () => NativeDialogs.ShowInFolder(entry.Path)) { Icon = Icons.Folder });
            items.Add(new MenuItem("Copy Path", () => Vertex.Input.InputSystem.ClipboardText = _context.Assets!.Relative(entry.Path)));
            items.Add(MenuItem.Separator);
            items.AddRange(FolderMenu(_folder!));
            return items;
        }

        private List<MenuItem> FolderMenu(string folder) =>
        [
            new MenuItem("New Folder", () =>
            {
                string path = Unique(Path.Combine(folder, "New Folder"), "");
                Directory.CreateDirectory(path);
                _context.Assets?.Refresh();
            }) { Icon = Icons.Folder },
            new MenuItem("Show Folder in Explorer", () => NativeDialogs.ShowInFolder(folder)),
            new MenuItem("Refresh", () => _context.Assets?.Refresh())
        ];

        private static string Unique(string path, string extension)
        {
            string candidate = path + extension;
            for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++) candidate = $"{path} {i}{extension}";
            return candidate;
        }
    }
}
