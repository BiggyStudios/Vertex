using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Panels;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Prefabs, materials, UI, and the inspector's and menus' handling of the mouse.</summary>
    public class AssetWorkflowTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public AssetWorkflowTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) Assets.Forget(file);
            Directory.Delete(_folder, recursive: true);
        }

        private static void UiFrame(Action draw)
        {
            Time.Advance(1.0 / 60.0);
            Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
            Ui.BeginFrame(1f / 60f);
            draw();
            Ui.EndFrame();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        [Fact]
        public void A_right_click_menu_near_the_bottom_stays_open()
        {
            // Opened by a right-click near the bottom, it flips up above the mouse: the same click mustn't close it.
            int owner = Ui.Id("test.menu");
            InputSystem.MoveMouse(new Vector2(200f, ScreenHeight - 20f));
            UiFrame(() => { });
            InputSystem.PressButton(MouseButton.Right);
            UiFrame(() => Menu.Open(owner, Ui.Mouse, [new MenuItem("New Folder"), new MenuItem("New Material"), new MenuItem("Refresh")]));
            InputSystem.ReleaseButton(MouseButton.Right);
            UiFrame(() => { });
            UiFrame(() => { });

            Assert.True(Menu.IsOpen(owner));
        }

        [Fact]
        public void A_submenu_opens_beside_its_item_and_what_is_picked_from_it_runs()
        {
            string? picked = null;
            List<MenuItem> items =
            [
                new MenuItem("Rename", () => picked = "Rename"),
                new MenuItem("Create") { Children = [new MenuItem("Cube", () => picked = "Cube"), new MenuItem("Sphere", () => picked = "Sphere")] }
            ];
            int owner = Ui.Id("test.submenu");

            // Resting on "Create" (the second row) opens its menu to the right, level with it.
            InputSystem.MoveMouse(new Vector2(150f, 144f));
            UiFrame(() => Menu.Open(owner, new Vector2(100f, 100f), items));
            for (int i = 0; i < 20; i++) UiFrame(() => { });

            // Across into it, onto "Cube", and click.
            InputSystem.MoveMouse(new Vector2(300f, 144f));
            UiFrame(() => { });
            UiFrame(() => { });
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(() => { });
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(() => { });

            Assert.Equal("Cube", picked);
            Assert.False(Menu.IsAnyOpen);

            // And with the keys: down to "Create", right into it, down to "Sphere", Enter.
            UiFrame(() => Menu.Open(owner, new Vector2(100f, 100f), items));
            foreach (Key key in new[] { Key.Down, Key.Down, Key.Right, Key.Down, Key.Enter })
            {
                InputSystem.PressKey(key);
                UiFrame(() => { });
                InputSystem.ReleaseKey(key);
                UiFrame(() => { });
            }

            Assert.Equal("Sphere", picked);
        }

        [Fact]
        public void Something_made_from_an_objects_menu_goes_under_it()
        {
            var context = new EditorContext();
            GameObject holder = context.Create("Holder", g => g.Transform.Position = new Vector3(3f, 0f, 0f));

            MenuItem cube = CreateMenu.Items(context, holder.Transform).Single(item => item.Label == "Cube");
            cube.Action!();

            GameObject made = context.Selection.Object!;
            Assert.Same(holder.Transform, made.Transform.Parent);
            Assert.Equal(new Vector3(3f, 0f, 0f), made.Transform.WorldPosition);
        }

        [Fact]
        public void Build_errors_are_shown_once_each_and_short()
        {
            const string output = """
                /home/nn/Game/SampleGame/Multiplayer/VoteKick.cs(56,37): error CS0246: The type or namespace name 'PeerId' could not be found [/home/nn/Game/SampleGame/SampleGame.csproj]
                /home/nn/Game/SampleGame/Multiplayer/VoteKick.cs(56,37): error CS0246: The type or namespace name 'PeerId' could not be found [/home/nn/Game/SampleGame/SampleGame.csproj]
                /usr/share/dotnet/sdk/10.0.114/Microsoft.Common.CurrentVersion.targets(2189,5): warning MSB9008: The referenced project doesn't exist.
                C:\Game\Program.cs(11,31): error CS0246: The type or namespace name 'Game' could not be found [C:\Game\Game.csproj]
                """;

            Assert.Equal(
            [
                "VoteKick.cs(56): The type or namespace name 'PeerId' could not be found",
                "Program.cs(11): The type or namespace name 'Game' could not be found"
            ], GameCode.Errors(output));
        }

        [Fact]
        public void Applying_a_prefab_updates_its_other_copies_and_reverting_puts_one_back()
        {
            var context = new EditorContext();
            GameObject original = context.Create("Crate", g => g.AddComponent<Light>().Intensity = 1f);
            Prefab prefab = context.CreatePrefab(original, _folder)!;
            GameObject second = context.AddPrefab(prefab.Source, new Vector3(4f, 0f, 0f))!;
            second.Name = "Crate B";

            // Changed in one copy, then applied: the other copy is made again with the change, keeping its place and name.
            original.GetComponent<Light>()!.Intensity = 5f;
            context.ApplyPrefab(original);
            context.Scene.ApplyChanges();

            GameObject remade = context.Scene.Find("Crate B")!;
            Assert.NotSame(second, remade);
            Assert.Equal(5f, remade.GetComponent<Light>()!.Intensity);
            Assert.Equal(new Vector3(4f, 0f, 0f), remade.Transform.Position);
            Assert.Same(prefab, remade.Prefab);

            context.Undo();
            Assert.Same(second, context.Scene.Find("Crate B"));
            context.Redo();

            // A change thrown away.
            remade = context.Scene.Find("Crate B")!;
            remade.GetComponent<Light>()!.Intensity = 9f;
            context.RevertPrefab(remade);
            context.Scene.ApplyChanges();
            Assert.Equal(5f, context.Scene.Find("Crate B")!.GetComponent<Light>()!.Intensity);
        }

        [Fact]
        public void A_changed_material_file_is_saved_once_the_change_is_done()
        {
            var context = new EditorContext();
            string path = context.CreateMaterial(_folder, "Rust");
            Material rust = Assets.LoadMaterial(path);

            SetValue.Apply(context.History, "Change Roughness", rust, typeof(Material).GetProperty(nameof(Material.Roughness))!, 0.95f);
            context.Update();

            var reread = new Material();
            SceneFile.LoadMaterial(File.ReadAllText(path), reread, _folder);
            Assert.Equal(0.95f, reread.Roughness);
        }

        [Fact]
        public void UI_made_with_nothing_selected_gets_a_canvas_and_goes_inside_the_selection_after()
        {
            var context = new EditorContext();
            GameObject panel = CreateMenu.MakeUI(context, "Panel", new Vector2(300f, 200f), g => g.AddComponent<UIPanel>());
            GameObject canvas = panel.Transform.Parent!.GameObject;
            Assert.NotNull(canvas.GetComponent<UICanvas>());

            GameObject text = CreateMenu.MakeUI(context, "Text", new Vector2(100f, 30f), g => g.AddComponent<UIText>());
            Assert.Same(panel.Transform, text.Transform.Parent);

            // One undo takes away the panel and the canvas made for it.
            context.Undo();
            context.Undo();
            Assert.False(context.InScene(panel));
            Assert.False(context.InScene(canvas));
        }

        [Fact]
        public void A_UI_button_dragged_in_the_game_view_follows_the_mouse_without_jumping()
        {
            var context = new EditorContext();
            GameObject button = CreateMenu.MakeUI(context, "Button", new Vector2(200f, 50f), g => g.AddComponent<UIButton>());
            UIRect rect = button.GetComponent<UIRect>()!;
            rect.AnchorMin = rect.AnchorMax = rect.Pivot = new Vector2(0f, 1f);
            rect.Position = new Vector2(40f, -40f);

            // The game's picture: smaller than the window, and away from its corner, as in the editor.
            var editing = new Vertex.Editor.SceneEditing.UIEditing(context);
            var viewport = new Rect(300f, 80f, 900f, 500f);
            void Draw() => editing.Draw(viewport, focused: true);
            UiFrame(Draw);

            // From the picture's units to the window's.
            float scale = viewport.Width / UISystem.ScreenSize.X;
            Vector2 ToWindow(Vector2 point) => viewport.Position + point * scale;
            Vector2 grab = ToWindow(rect.ScreenPosition + rect.ScreenSize * 0.5f);
            Vector2 before = rect.ScreenPosition;

            InputSystem.MoveMouse(grab);
            UiFrame(Draw);
            UiFrame(Draw);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            for (int i = 1; i <= 10; i++)
            {
                InputSystem.MoveMouse(grab + new Vector2(6f, 3f) * i);
                UiFrame(Draw);

                // Wherever it is on the way, the point grabbed stays under the mouse.
                Vector2 expected = before + new Vector2(6f, 3f) * i / scale;
                Assert.True(Vector2.Distance(rect.ScreenPosition, expected) < 1.01f, $"Step {i}: at {rect.ScreenPosition}, expected {expected}");
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);

            context.Undo();
            Assert.Equal(new Vector2(40f, -40f), rect.Position);
        }

        [Fact]
        public void A_component_dragged_down_the_inspector_moves_below_the_next()
        {
            var app = new EditorApp();
            GameObject lamp = app.Context.Create("Lamp", g =>
            {
                g.AddComponent<Light>();
                g.AddComponent<Camera>();
                g.AddComponent<MeshRenderer>();
            });
            Frame(app, 20);

            // Find the light's header by feeling down the inspector for it.
            Light light = lamp.GetComponent<Light>()!;
            int header = Ui.Id(RuntimeHelpers.GetHashCode(light), "card");
            Vector2? found = null;
            for (float y = 60f; y < ScreenHeight && found == null; y += 6f)
            {
                InputSystem.MoveMouse(new Vector2(ScreenWidth - 250f, y));
                Frame(app, 2);
                if (Ui.HotId == header) found = new Vector2(ScreenWidth - 250f, y);
            }

            Assert.NotNull(found);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            for (int i = 1; i <= 20; i++)
            {
                InputSystem.MoveMouse(found!.Value + new Vector2(0f, i * 5f));
                Frame(app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app, 30);

            Assert.Equal([typeof(Camera), typeof(Light), typeof(MeshRenderer)], lamp.Components.Select(c => c.GetType()));
            app.Context.Undo();
            Assert.Equal([typeof(Light), typeof(Camera), typeof(MeshRenderer)], lamp.Components.Select(c => c.GetType()));
        }

        [Fact]
        public void Renaming_a_visual_script_in_use_keeps_it_used_and_saved_under_its_new_name()
        {
            Project.Create(Path.Combine(_folder, "Game" + Project.Extension), "Game");
            var context = new EditorContext();
            context.OpenProject(_folder);
            string scripts = Path.Combine(_folder, "Assets", "Scripts");
            string old = context.CreateVisualScript(scripts, "Player");
            Vertex.Graphs.Graph graph = Assets.LoadGraph(old);
            GameObject player = context.Create("Player", g => g.AddComponent<Vertex.Scripting.VisualScript>().Script = graph);

            // Renamed in the assets, as F2 does.
            var panel = new AssetsPanel(context);
            typeof(AssetsPanel).GetMethod("Rename", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(panel, [new AssetEntry(old, AssetKind.VisualScript), "Player Movement"]);
            string renamed = Path.Combine(scripts, "Player Movement.vscript");

            Assert.True(File.Exists(renamed));
            Assert.Equal(renamed, graph.Source);
            Assert.Same(graph, Assets.LoadGraph(renamed));

            // Changed afterwards, it's saved to the new file, and the old one isn't made again.
            graph.AddNode("Actions/Print", System.Numerics.Vector2.Zero);
            context.Update();
            Assert.False(File.Exists(old));
            Assert.Contains("Actions/Print", File.ReadAllText(renamed));

            // The scene refers to it by its new name.
            Assert.Contains("Player Movement.vscript", SceneFile.Save(context.Scene, context.AssetFolders));
            Assert.Same(graph, player.GetComponent<Vertex.Scripting.VisualScript>()!.Script);
        }

        [Fact]
        public void Clicking_an_objects_arrow_in_the_outliner_folds_it_without_selecting_it()
        {
            var context = new EditorContext();
            GameObject parent = context.Create("Parent");
            GameObject child = context.Create("Child", parent: parent.Transform);
            var outliner = new OutlinerPanel(context);
            var rect = new Rect(0f, 0f, 300f, 600f);
            void Draw() => outliner.Draw(rect);

            // Selecting the child opens its parent.
            UiFrame(Draw);
            UiFrame(Draw);
            var expanded = (HashSet<GameObject>)typeof(OutlinerPanel).GetField("_expanded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(outliner)!;
            Assert.Contains(parent, expanded);

            // The parent's row, found by feeling down the left edge, where its arrow is.
            int row = Ui.Id(RuntimeHelpers.GetHashCode(parent), "row");
            Vector2? arrow = null;
            for (float y = 0f; y < 300f && arrow == null; y += 4f)
            {
                InputSystem.MoveMouse(new Vector2(20f, y));
                UiFrame(Draw);
                UiFrame(Draw);
                if (Ui.HotId == row) arrow = new Vector2(20f, y);
            }

            Assert.NotNull(arrow);
            InputSystem.PressButton(MouseButton.Left);
            UiFrame(Draw);
            InputSystem.ReleaseButton(MouseButton.Left);
            UiFrame(Draw);
            UiFrame(Draw);

            Assert.DoesNotContain(parent, expanded);
            Assert.Same(child, context.Selection.Object);

            // The selection's highlight goes with it straight away, not once the rows have finished sliding shut.
            Assert.Equal(-1f, Ui.Recall(Ui.Id("outliner.selectedRow"), -1f));
        }

        [Fact]
        public void Big_sharp_screens_make_the_editor_bigger_unless_the_system_already_does()
        {
            Assert.Equal(1f, EditorApp.AutoScale(1f, 1080f));
            Assert.Equal(1.25f, EditorApp.AutoScale(1f, 1440f));
            Assert.Equal(1.5f, EditorApp.AutoScale(1f, 2160f));
            Assert.Equal(1f, EditorApp.AutoScale(1.5f, 2160f));
        }

        [Fact]
        public void The_assets_right_click_menus_keep_what_can_be_made_in_sections()
        {
            Project.Create(Path.Combine(_folder, "Game" + Project.Extension), "Game");
            var context = new EditorContext();
            context.OpenProject(_folder);
            string root = context.Assets!.Root;
            var panel = new AssetsPanel(context);
            typeof(AssetsPanel).GetField("_folder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(panel, root);
            List<MenuItem> Call(string method, object argument) =>
                (List<MenuItem>)typeof(AssetsPanel).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(panel, [argument])!;

            static IEnumerable<string> Makes(IEnumerable<MenuItem> items) =>
                items.SelectMany(item => item.Children != null ? Makes(item.Children) : item.IsSeparator || item.Action == null ? [] : [item.Label]);

            string[] everything = ["New Folder", "New Scene", "C# Script", "Visual Script", "Material", "Shader Graph", "Post Effect", "Post Effect Graph",
                "Animation", "Animation Graph", "VFX Graph", "Audio Graph", "Behavior Tree"];

            // Right-clicking the folder: a short list, the rest in sections beside it.
            List<MenuItem> folder = Call("FolderMenu", root);
            Assert.True(folder.Count(item => !item.IsSeparator) <= 10, string.Join(", ", folder.Select(item => item.Label)));
            Assert.Subset(Makes(folder).ToHashSet(), everything.ToHashSet());
            Assert.All(folder.Where(item => item.Children != null).SelectMany(item => item.Children!), child => Assert.Null(child.Children));

            // Right-clicking a file: its own choices, and everything that can be made under Create, one menu deep.
            string material = context.CreateMaterial(root, "Rust");
            List<MenuItem> file = Call("EntryMenu", new AssetEntry(material, AssetKind.Material));
            Assert.Equal(["Rename", "Duplicate", "Delete"], file.Take(3).Select(item => item.Label));
            MenuItem create = Assert.Single(file, item => item.Children != null);
            Assert.Equal("Create", create.Label);
            Assert.All(create.Children!, child => Assert.Null(child.Children));
            Assert.Subset(Makes(create.Children!).ToHashSet(), everything.ToHashSet());
        }
    }
}
