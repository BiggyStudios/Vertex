using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Panels;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Picking several files in the Assets panel (Ctrl, Shift, Ctrl+A, a box), and moving and renaming them together.</summary>
    public class AssetSelectionTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
        private readonly EditorApp _app;
        private readonly string _root;

        public AssetSelectionTests()
        {
            Directory.CreateDirectory(_folder);
            Project.Create(Path.Combine(_folder, "Game" + Project.Extension), "Game");
            _app = new EditorApp();
            _app.Context.OpenProject(_folder);
            _root = _app.Context.Assets!.Root;
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) Assets.Forget(file);
            Directory.Delete(_folder, recursive: true);
        }

        /// <summary>Materials named <paramref name="names"/> in the Assets folder, shown in the panel.</summary>
        private List<string> Materials(params string[] names)
        {
            List<string> paths = names.Select(name => _app.Context.CreateMaterial(_root, name)).ToList();
            _app.Context.Assets!.Refresh();
            Frame(_app, 5);
            return paths;
        }

        private Vector2 TileOf(string path)
        {
            Rect? cell = _app.AssetsPanel.CellOf(path);
            Assert.NotNull(cell);
            return cell!.Value.Centre;
        }

        private void Click(Vector2 at, params Key[] holding)
        {
            InputSystem.MoveMouse(at);
            Frame(_app, 2);
            foreach (Key key in holding) InputSystem.PressKey(key);
            InputSystem.PressButton(MouseButton.Left);
            Frame(_app);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(_app);
            foreach (Key key in holding) InputSystem.ReleaseKey(key);
            Frame(_app, 2);
        }

        private void Drag(Vector2 from, Vector2 to)
        {
            // Long enough after any click that the press isn't a double-click.
            InputSystem.MoveMouse(from);
            Frame(_app, 30);
            InputSystem.PressButton(MouseButton.Left);
            Frame(_app);
            for (int i = 1; i <= 12; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 12f));
                Frame(_app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(_app, 3);
        }

        private void Tap(Key key, params Key[] holding)
        {
            foreach (Key held in holding) InputSystem.PressKey(held);
            InputSystem.PressKey(key);
            Frame(_app);
            InputSystem.ReleaseKey(key);
            foreach (Key held in holding) InputSystem.ReleaseKey(held);
            Frame(_app, 2);
        }

        private IEnumerable<string> Selected => _app.Context.Selection.Assets.Select(a => a.Path);

        [Fact]
        public void Ctrl_adds_shift_takes_a_range_and_ctrl_a_takes_them_all()
        {
            List<string> rocks = Materials("A", "B", "C", "D", "E");

            Click(TileOf(rocks[0]));
            Assert.Equal([rocks[0]], Selected);

            Click(TileOf(rocks[2]), Key.ControlLeft);
            Assert.Equal([rocks[0], rocks[2]], Selected);

            // Shift: from the last clicked (C) to E, the clicked one last as the main one.
            Click(TileOf(rocks[4]), Key.ShiftLeft);
            Assert.Equal([rocks[2], rocks[3], rocks[4]], Selected);
            Assert.Equal(rocks[4], _app.Context.Selection.Asset!.Path);

            // Ctrl on one already picked leaves it out.
            Click(TileOf(rocks[3]), Key.ControlLeft);
            Assert.Equal([rocks[2], rocks[4]], Selected);

            // A plain click on one of several: just it, once let go.
            Click(TileOf(rocks[2]));
            Assert.Equal([rocks[2]], Selected);

            Tap(Key.A, Key.ControlLeft);
            // Everything shown: the rocks, and what a new project starts with.
            Assert.Equal(_app.Context.Assets!.List(_root).Select(e => e.Path).ToHashSet(), Selected.ToHashSet());
            Assert.Subset(Selected.ToHashSet(), rocks.ToHashSet());

            // Clicking empty space lets them all go.
            Rect last = _app.AssetsPanel.CellOf(rocks[4])!.Value;
            Click(new Vector2(last.Right + 60f, last.Centre.Y));
            Assert.Empty(Selected);
        }

        [Fact]
        public void A_box_dragged_from_empty_space_selects_the_tiles_it_touches()
        {
            List<string> rocks = Materials("A", "B", "C", "D");

            Rect last = _app.AssetsPanel.CellOf(rocks[3])!.Value;
            Drag(new Vector2(last.Right + 60f, last.Y + 10f), TileOf(rocks[1]));

            Assert.Equal(new HashSet<string> { rocks[1], rocks[2], rocks[3] }, Selected.ToHashSet());
        }

        [Fact]
        public void F2_with_several_selected_numbers_them_from_a_name_in_the_order_shown()
        {
            List<string> rocks = Materials("Pebble", "Boulder", "Cobble");
            string[] shown = rocks.OrderBy(p => TileOf(p).X).ToArray();

            Click(TileOf(rocks[0]));
            Click(TileOf(rocks[1]), Key.ControlLeft);
            Click(TileOf(rocks[2]), Key.ControlLeft);
            Tap(Key.F2);
            Assert.True(Dialog.IsOpen);

            foreach (char c in "Stone") InputSystem.TypeCharacter(c);
            Frame(_app, 2);
            Tap(Key.Enter);
            Frame(_app, 3);

            Assert.False(Dialog.IsOpen);
            for (int i = 0; i < 3; i++)
            {
                Assert.False(File.Exists(shown[i]));
                Assert.True(File.Exists(Path.Combine(_root, $"Stone {i + 1}.material")), $"Stone {i + 1}");
            }

            // Still selected, under their new names.
            Assert.Equal(Enumerable.Range(1, 3).Select(i => Path.Combine(_root, $"Stone {i}.material")).ToHashSet(), Selected.ToHashSet());
        }

        [Fact]
        public void Several_dragged_onto_a_folder_all_move_into_it()
        {
            List<string> rocks = Materials("A", "B", "C");
            string folder = Directory.CreateDirectory(Path.Combine(_root, "Rocks")).FullName;
            _app.Context.Assets!.Refresh();
            Frame(_app, 3);

            Click(TileOf(rocks[0]));
            Click(TileOf(rocks[1]), Key.ControlLeft);
            Drag(TileOf(rocks[1]), TileOf(folder));

            Assert.True(File.Exists(Path.Combine(folder, "A.material")));
            Assert.True(File.Exists(Path.Combine(folder, "B.material")));
            Assert.True(File.Exists(rocks[2]));
        }

        [Fact]
        public void Renaming_several_swaps_names_and_stops_before_a_clash()
        {
            List<string> paths = Materials("Red", "Blue", "Green");
            File.WriteAllText(paths[0], File.ReadAllText(paths[0]).Replace("\n", "\n ") + " ");
            string red = File.ReadAllText(paths[0]);
            var entries = paths.Select(p => new AssetEntry(p, AssetKind.Material)).ToList();

            // Two swapping names go by stand-ins, so neither is in the other's way.
            _app.AssetsPanel.RenameMany(entries.Take(2).ToList(), ["Blue", "Red"]);
            Assert.Equal(red, File.ReadAllText(Path.Combine(_root, "Blue.material")));
            Assert.Equal(2, Directory.GetFiles(_root, "*.material").Count(f => Path.GetFileName(f) is "Red.material" or "Blue.material"));
            Assert.Empty(Directory.GetFiles(_root, "renaming *"));

            // Two the same, a name taken by a file not being renamed, a bad character, or no change: not done.
            Assert.NotNull(AssetsPanel.RenameProblem(entries, ["X", "X", "Y"]));
            Assert.NotNull(AssetsPanel.RenameProblem(entries.Take(1).ToList(), ["Green"]));
            Assert.NotNull(AssetsPanel.RenameProblem(entries.Take(1).ToList(), ["a/b"]));
            Assert.NotNull(AssetsPanel.RenameProblem(entries, ["Red", "Blue", "Green"]));
            Assert.Null(AssetsPanel.RenameProblem(entries, ["Blue", "Green", "Red"]));
        }

        [Fact]
        public void Replacing_text_changes_only_that_part_of_each_name()
        {
            List<string> paths = Materials("Rock_Moss", "Rock_Dry", "Sand");
            var entries = paths.Select(p => new AssetEntry(p, AssetKind.Material)).ToList();
            typeof(AssetsPanel).GetField("_renameByReplacing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(_app.AssetsPanel, true);
            typeof(AssetsPanel).GetField("_find", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(_app.AssetsPanel, "Rock_");
            typeof(AssetsPanel).GetField("_replaceWith", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(_app.AssetsPanel, "Stone ");

            Assert.Equal(["Stone Moss", "Stone Dry", "Sand"], _app.AssetsPanel.NewNames(entries));
        }

        [Fact]
        public void Del_then_Enter_deletes_what_is_selected()
        {
            List<string> rocks = Materials("A", "B");
            Click(TileOf(rocks[0]));
            Tap(Key.Delete);
            Assert.True(Dialog.IsOpen);

            Tap(Key.Enter);
            Frame(_app, 2);
            Assert.False(Dialog.IsOpen);
            Assert.False(File.Exists(rocks[0]));
            Assert.True(File.Exists(rocks[1]));
        }

        [Fact]
        public void Deleted_files_and_folders_go_to_the_bin_and_can_be_had_back_from_there()
        {
            string bin = Path.Combine(_folder, "Bin");
            Vertex.Editor.Platform.RecycleBin.StandIn = bin;
            List<string> rocks = Materials("Rock");
            string folder = Directory.CreateDirectory(Path.Combine(_root, "Old")).FullName;
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep");
            _app.Context.Assets!.Refresh();
            Frame(_app, 3);

            Click(TileOf(rocks[0]));
            Click(TileOf(folder), Key.ControlLeft);
            Tap(Key.Delete);
            Tap(Key.Enter);

            Assert.False(File.Exists(rocks[0]));
            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(bin, "Rock.material")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(bin, "Old", "notes.txt")));

            // Another of the same name doesn't go over the first.
            Materials("Rock");
            Vertex.Editor.Platform.RecycleBin.Move(rocks[0]);
            Assert.True(File.Exists(Path.Combine(bin, "Rock 2.material")));
        }

        [Fact]
        public void The_linux_trash_keeps_a_note_of_where_each_thing_came_from()
        {
            string bin = Path.Combine(_folder, "Trash");
            string file = Path.Combine(_folder, "A b.txt");
            File.WriteAllText(file, "x");
            string stored = Vertex.Editor.Platform.RecycleBin.MoveToTrash(file, bin);

            Assert.False(File.Exists(file));
            Assert.Equal(Path.Combine(bin, "files", "A b.txt"), stored);
            string note = File.ReadAllText(Path.Combine(bin, "info", "A b.txt.trashinfo"));
            Assert.StartsWith("[Trash Info]\nPath=", note);
            Assert.Contains("A%20b.txt", note);
            Assert.Contains("DeletionDate=", note);

            File.WriteAllText(file, "y");
            Assert.Equal(Path.Combine(bin, "files", "A b.2.txt"), Vertex.Editor.Platform.RecycleBin.MoveToTrash(file, bin));
        }
    }
}
