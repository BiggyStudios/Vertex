using System.Numerics;

using Vertex.Input;
using Vertex.Rendering;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>Files of the project's that scenes refer to (materials, prefabs) and the UI made of objects.</summary>
    public class AssetFileTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));

        public AssetFileTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*", SearchOption.AllDirectories) : []) Assets.Forget(file);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        [Fact]
        public void A_material_file_is_shared_and_scenes_refer_to_it()
        {
            string path = Path.Combine(_folder, "Brick.material");
            File.WriteAllText(path, SceneFile.SaveMaterial(new Material { Roughness = 0.9f, Color = new Color(0.5f, 0.2f, 0.1f) }, _folder));

            Material brick = Assets.LoadMaterial(path);
            Assert.Same(brick, Assets.LoadMaterial(path));
            Assert.Equal(0.9f, brick.Roughness);

            var wall = new GameObject("Wall");
            wall.AddComponent<MeshRenderer>().Material = brick;
            string json = SceneFile.Save(Scene.Active, new[] { _folder });
            Assert.Contains("\"material\": \"file:Brick.material\"", json);

            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded, _folder);
            loaded.ApplyChanges();
            Assert.Same(brick, loaded.Find("Wall")!.GetComponent<MeshRenderer>()!.Material);
        }

        [Fact]
        public void A_prefab_makes_linked_copies_that_are_saved_as_copies()
        {
            var crate = new GameObject("Crate");
            crate.AddComponent<Light>().Intensity = 3f;
            new GameObject("Lid").Transform.Parent = crate.Transform;
            Scene.Active.ApplyChanges();

            Prefab prefab = Prefab.Save(crate, Path.Combine(_folder, "Crate.prefab"), _folder);
            Assert.Same(prefab, crate.Prefab);
            Assert.DoesNotContain("\"prefab\"", File.ReadAllText(prefab.Source));

            GameObject copy = prefab.Instantiate(new Vector3(5f, 0f, 0f));
            Scene.Active.ApplyChanges();
            Assert.Same(prefab, copy.Prefab);
            Assert.Equal(new Vector3(5f, 0f, 0f), copy.Transform.Position);
            Assert.Equal(3f, copy.GetComponent<Light>()!.Intensity);
            Assert.Equal("Lid", Assert.Single(copy.Transform.Children).GameObject.Name);

            // A saved scene keeps the copies as they are, and which prefab they came from.
            var loaded = new Scene();
            SceneFile.LoadJson(SceneFile.Save(Scene.Active, new[] { _folder }), loaded, _folder);
            loaded.ApplyChanges();
            Assert.All(loaded.GameObjects.Where(o => o.Name == "Crate"), o => Assert.Same(prefab, o.Prefab));
            Assert.Null(loaded.Find("Lid")!.Prefab);
        }

        [Fact]
        public void A_component_can_hold_a_prefab_to_make_copies_of()
        {
            Prefab rocket = Prefab.Save(new GameObject("Rocket"), Path.Combine(_folder, "Rocket.prefab"), _folder);
            var launcher = new GameObject("Launcher");
            launcher.AddComponent<Launcher>().Rocket = rocket;

            var loaded = new Scene();
            SceneFile.LoadJson(SceneFile.Save(Scene.Active, new[] { _folder }), loaded, _folder);
            loaded.ApplyChanges();
            Assert.Same(rocket, loaded.Find("Launcher")!.GetComponent<Launcher>()!.Rocket);
        }

        public sealed class Launcher : Component
        {
            public Prefab? Rocket { get; set; }
        }

        [Fact]
        public void UI_is_laid_out_against_its_parent_by_its_anchors()
        {
            Canvas.FixedScale = 1f;
            Canvas.BeginFrame(1000, 500, 500);

            var canvas = new GameObject("Canvas");
            canvas.AddComponent<UICanvas>();
            var bar = new GameObject("Bar");
            bar.Transform.Parent = canvas.Transform;
            UIRect barRect = bar.AddComponent<UIRect>();
            (barRect.AnchorMin, barRect.AnchorMax, barRect.Pivot) = (new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            barRect.Size = new Vector2(-20f, 60f);
            barRect.Position = new Vector2(0f, -10f);

            var corner = new GameObject("Corner");
            corner.Transform.Parent = bar.Transform;
            UIRect cornerRect = corner.AddComponent<UIRect>();
            (cornerRect.AnchorMin, cornerRect.AnchorMax, cornerRect.Pivot) = (Vector2.One, Vector2.One, Vector2.One);
            cornerRect.Size = new Vector2(40f, 20f);
            Scene.Active.ApplyChanges();

            UISystem.Layout(Scene.Active);
            Canvas.EndFrame();
            Canvas.FixedScale = 0f;

            // A bar stretched across the bottom, 10 in from the sides and up from the bottom.
            Assert.Equal(new Vector2(10f, 430f), barRect.ScreenPosition);
            Assert.Equal(new Vector2(980f, 60f), barRect.ScreenSize);

            // Its corner piece in the bar's bottom right.
            Assert.Equal(new Vector2(950f, 470f), cornerRect.ScreenPosition);

            // Where it is on screen goes back to the same position and size.
            (Vector2 position, Vector2 size) = barRect.PlacementFor(barRect.ScreenPosition, barRect.ScreenSize, Vector2.Zero, new Vector2(1000f, 500f));
            Assert.Equal(barRect.Position, position);
            Assert.Equal(barRect.Size, size);
        }

        [Fact]
        public void A_UI_button_is_clicked_by_pressing_and_letting_go_over_it()
        {
            var canvas = new GameObject("Canvas");
            canvas.AddComponent<UICanvas>();
            var buttonObject = new GameObject("Play");
            buttonObject.Transform.Parent = canvas.Transform;
            buttonObject.AddComponent<UIRect>().Size = new Vector2(200f, 60f);
            UIButton button = buttonObject.AddComponent<UIButton>();
            int clicks = 0;
            button.Clicked += () => clicks++;

            var loop = new GameLoop(new EmptyGame());
            loop.Load();

            // The middle of a 1280 × 720 screen, which is 720 units tall.
            InputSystem.MoveMouse(new Vector2(640f, 360f));
            RunFrames(loop, 1);
            Assert.True(UISystem.MouseOverUI);

            InputSystem.PressButton(MouseButton.Left);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            InputSystem.ReleaseButton(MouseButton.Left);
            RunFrames(loop, 1);
            Assert.Equal(1, clicks);

            // Let go somewhere else, and it isn't a click.
            InputSystem.EndFrame();
            InputSystem.PressButton(MouseButton.Left);
            RunFrames(loop, 1);
            InputSystem.EndFrame();
            InputSystem.MoveMouse(new Vector2(10f, 10f));
            InputSystem.ReleaseButton(MouseButton.Left);
            RunFrames(loop, 1);
            Assert.Equal(1, clicks);
            UISystem.Reset();
        }

        [Fact]
        public void The_engine_has_a_font_of_its_own()
        {
            Assert.True(Font.Default.Measure("Hello", 20f).X > 10f);
            Assert.Null(Font.Default.Source);
        }
    }
}
