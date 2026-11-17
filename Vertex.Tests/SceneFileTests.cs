using System.Numerics;

using Vertex.Input;
using Vertex.Rendering;
using Vertex.Serialization;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>Saving scenes and building them again, and the other things the editor needs from the engine.</summary>
    public class SceneFileTests : EngineTest
    {
        /// <summary>A script that points at another object's component, as games' scripts do.</summary>
        public class Follower : Component
        {
            public Light? Target { get; set; }
            public float Speed = 3f;
            public string Label { get; set; } = "";
            public GameObject? Home;

            [DontSave]
            public float Runtime { get; set; }
        }

        private static Scene BuildScene()
        {
            Scene scene = Scene.Active;
            scene.Lighting.FogDensity = 0.05f;

            var lamp = new GameObject("Lamp");
            lamp.Transform.Position = new Vector3(1f, 2f, 3f);
            Light light = lamp.AddComponent<Light>();
            light.Type = LightType.Point;
            light.Color = new Color(1f, 0.5f, 0.25f);

            var box = new GameObject("Box");
            box.Transform.Parent = lamp.Transform;
            box.Transform.Scale = new Vector3(2f);
            box.Transform.EulerAngles = new Vector3(0f, 45f, 0f);
            MeshRenderer renderer = box.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Roughness = 0.2f;
            renderer.Material.Texture = new Texture(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]);

            var made = new GameObject("Made in code") { Active = false };
            made.AddComponent<MeshRenderer>().Mesh = new Mesh([new MeshVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero), new MeshVertex(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX),
                new MeshVertex(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY)], [0, 1, 2]);
            Follower follower = made.AddComponent<Follower>();
            follower.Target = light;
            follower.Speed = 7f;
            follower.Label = "hello";
            follower.Home = lamp;
            follower.Runtime = 99f;
            follower.Enabled = false;

            scene.ApplyChanges();
            return scene;
        }

        [Fact]
        public void A_saved_scene_comes_back_the_same()
        {
            string json = SceneFile.Save(BuildScene());

            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded);
            loaded.ApplyChanges();

            Assert.Equal(["Lamp", "Box", "Made in code"], loaded.GameObjects.Select(o => o.Name));
            Assert.Equal(0.05f, loaded.Lighting.FogDensity);

            GameObject lamp = loaded.Find("Lamp")!, box = loaded.Find("Box")!, made = loaded.Find("Made in code")!;
            Assert.Equal(new Vector3(1f, 2f, 3f), lamp.Transform.Position);
            Assert.Same(lamp.Transform, box.Transform.Parent);
            Assert.Equal(new Vector3(2f), box.Transform.Scale);
            Assert.Equal(45f, box.Transform.EulerAngles.Y, 0.01f);
            Assert.False(made.Active);

            Light light = lamp.GetComponent<Light>()!;
            Assert.Equal(LightType.Point, light.Type);
            Assert.Equal(new Color(1f, 0.5f, 0.25f), light.Color);

            MeshRenderer renderer = box.GetComponent<MeshRenderer>()!;
            Assert.Same(Primitives.Cube, renderer.Mesh);
            Assert.Equal(0.2f, renderer.Material.Roughness);
            Assert.Equal([255, 0, 0, 255, 0, 255, 0, 255], renderer.Material.Texture!.Pixels);

            Assert.Equal(3, made.GetComponent<MeshRenderer>()!.Mesh!.Indices.Length);
            Follower follower = made.GetComponent<Follower>()!;
            Assert.Same(light, follower.Target);
            Assert.Same(lamp, follower.Home);
            Assert.Equal((7f, "hello"), (follower.Speed, follower.Label));
            Assert.Equal(0f, follower.Runtime);
            Assert.False(follower.Enabled);
        }

        [Fact]
        public void Loading_runs_nothing_until_the_scene_updates()
        {
            new GameObject("Watched").AddComponent<Recorder>();
            string json = SceneFile.Save(Scene.Active);

            var loaded = new Scene();
            Recorder recorder = SceneFile.LoadJson(json, loaded)[0].GetComponent<Recorder>()!;
            Assert.Empty(recorder.Calls);

            loaded.Update();
            Assert.Equal(["Start", "Update"], recorder.Calls);
        }

        [Fact]
        public void A_snapshot_puts_the_scene_back_sharing_its_meshes_and_textures()
        {
            Scene scene = BuildScene();
            MeshRenderer original = scene.Find("Made in code")!.GetComponent<MeshRenderer>()!;
            SceneFile.Snapshot snapshot = SceneFile.TakeSnapshot(scene);

            var restored = new Scene();
            snapshot.Restore(restored);
            restored.ApplyChanges();

            Assert.Same(original.Mesh, restored.Find("Made in code")!.GetComponent<MeshRenderer>()!.Mesh);
            Assert.Same(scene.Find("Box")!.GetComponent<MeshRenderer>()!.Material.Texture, restored.Find("Box")!.GetComponent<MeshRenderer>()!.Material.Texture);
        }

        [Fact]
        public void A_copy_of_some_objects_takes_what_is_under_them()
        {
            Scene scene = BuildScene();
            List<GameObject> copies = SceneFile.TakeSnapshot(scene, [scene.Find("Lamp")!]).Restore(scene);

            Assert.Equal(["Lamp", "Box"], copies.Select(o => o.Name));
            Assert.Same(copies[0].Transform, copies[1].Transform.Parent);
            Assert.NotSame(scene.Find("Lamp"), copies[0]);
        }

        [Fact]
        public void A_missing_script_is_skipped_and_the_rest_still_loads()
        {
            string json = SceneFile.Save(BuildScene()).Replace("Follower", "NoSuchScript");

            var loaded = new Scene();
            SceneFile.LoadJson(json, loaded);
            loaded.ApplyChanges();

            Assert.Equal(3, loaded.GameObjects.Count);
            Assert.Single(loaded.Find("Made in code")!.Components);
        }

        [Fact]
        public void Children_and_top_level_objects_can_be_put_in_order()
        {
            var parent = new GameObject("Parent");
            var a = new GameObject("A");
            var b = new GameObject("B");
            a.Transform.Parent = parent.Transform;
            b.Transform.Parent = parent.Transform;
            var top = new GameObject("Top");
            Scene.Active.ApplyChanges();

            b.Transform.SiblingIndex = 0;
            Assert.Equal([b.Transform, a.Transform], parent.Transform.Children);

            top.Transform.SiblingIndex = 0;
            Assert.Equal(0, top.Transform.SiblingIndex);
            Assert.Equal(1, parent.Transform.SiblingIndex);
            Assert.Equal(["Top", "Parent"], Scene.Active.GameObjects.Where(o => o.Transform.Parent == null).Select(o => o.Name));
        }

        [Fact]
        public void A_view_draws_into_its_part_of_the_window_as_if_it_were_the_screen()
        {
            Canvas.FixedScale = 1f;
            Canvas.BeginFrame(1000, 800, 800);
            InputSystem.MoveMouse(new Vector2(300f, 250f));

            Canvas.BeginView(new Vector2(200f, 100f), 720, 360);
            Assert.Equal(new Vector2(1440f, 720f), Canvas.Size);
            Assert.Equal(new Vector2(200f, 300f), Canvas.MousePosition);
            Canvas.DrawRect(new Vector2(10f, 20f), new Vector2(30f), Color.White);
            Canvas.DrawRect(new Vector2(2000f, 20f), new Vector2(30f), Color.White);
            Canvas.EndView();

            Canvas.EndFrame();
            Canvas.FixedScale = 0f;

            CanvasQuad quad = Assert.Single(Canvas.ReadyQuads);
            Assert.Equal(new Vector2(205f, 110f), quad.TopLeft.Position);
        }

        [Fact]
        public void Muted_input_reads_as_nothing_but_is_still_kept()
        {
            InputSystem.PressKey(Key.W);
            InputSystem.Muted = true;
            Assert.False(InputSystem.GetKey(Key.W));
            Assert.False(InputSystem.GetKeyDown(Key.W));

            InputSystem.Muted = false;
            Assert.True(InputSystem.GetKey(Key.W));
        }

        public class Fragile : Component
        {
            protected internal override void OnDestroy() => throw new InvalidOperationException("Start never ran");
        }

        [Fact]
        public void A_scene_that_never_ran_is_thrown_away_without_running_its_code()
        {
            Recorder recorder = new GameObject().AddComponent<Recorder>();
            Scene.Active.ApplyChanges();

            Scene.Active.Discard();

            Assert.Empty(Scene.Active.GameObjects);
            Assert.Empty(recorder.Calls);
        }

        [Fact]
        public void One_component_failing_to_clean_up_does_not_stop_the_rest()
        {
            var gameObject = new GameObject();
            gameObject.AddComponent<Fragile>();
            Recorder recorder = gameObject.AddComponent<Recorder>();
            Scene.Active.ApplyChanges();

            Scene.Active.Unload();

            Assert.Equal(["OnDestroy"], recorder.Calls);
        }
    }
}
