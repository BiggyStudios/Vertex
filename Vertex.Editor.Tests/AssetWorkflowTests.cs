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
        }

        [Fact]
        public void Big_sharp_screens_make_the_editor_bigger_unless_the_system_already_does()
        {
            Assert.Equal(1f, EditorApp.AutoScale(1f, 1080f));
            Assert.Equal(1.25f, EditorApp.AutoScale(1f, 1440f));
            Assert.Equal(1.5f, EditorApp.AutoScale(1f, 2160f));
            Assert.Equal(1f, EditorApp.AutoScale(1.5f, 2160f));
        }
    }
}
