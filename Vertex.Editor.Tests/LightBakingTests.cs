using System.Diagnostics;
using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Editor.Tests
{
    /// <summary>Baking a scene's light from the editor: in the background, saved beside the scene, and taken away again.</summary>
    public class LightBakingTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public override void Dispose()
        {
            base.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        /// <summary>A saved scene with a static floor, a moving ball and a baked lamp, opened in the editor.</summary>
        private EditorContext Room()
        {
            var context = new EditorContext();
            context.NewScene();
            GameObject floor = GameObject.CreateCube(new Vector3(0f, -0.5f, 0f), Color.White, "Floor");
            floor.Transform.Scale = new Vector3(6f, 1f, 6f);
            floor.GetComponent<MeshRenderer>()!.Static = true;
            GameObject.CreateSphere(new Vector3(0f, 1f, 0f), Color.White, "Ball");
            var lamp = new GameObject("Lamp");
            lamp.Transform.Position = new Vector3(0f, 3f, 0f);
            Light light = lamp.AddComponent<Light>();
            light.Type = LightType.Point;
            light.Mode = LightMode.Baked;
            context.Scene.Lighting.Baking.Quality = BakeQuality.Preview;
            context.Scene.Lighting.Baking.ProbeSpacing = 3f;

            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "Room.scene");
            Vertex.Serialization.SceneFile.Save(context.Scene, path);
            context.OpenScene(path);
            return context;
        }

        private static void WaitForBake(EditorContext context)
        {
            var waited = Stopwatch.StartNew();
            while (context.Baking)
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "The bake didn't finish.");
                Thread.Sleep(5);
                context.Update();
            }
        }

        [Fact]
        public void Baking_saves_the_light_beside_the_scene_and_the_scene_with_where_each_static_mesh_is_in_it()
        {
            EditorContext context = Room();
            context.BakeLighting();
            Assert.True(context.Baking);
            WaitForBake(context);

            string bakeFile = Path.Combine(_folder, "Room.bake");
            Assert.True(File.Exists(bakeFile));
            Assert.False(context.Dirty);
            Assert.Equal(0, context.Scene.Find("Floor")!.GetComponent<MeshRenderer>()!.LightmapIndex);
            Assert.Equal(-1, context.Scene.Find("Ball")!.GetComponent<MeshRenderer>()!.LightmapIndex);
            Assert.NotNull(context.Scene.Lighting.Baked);

            // The saved scene opens with it.
            context.OpenScene(Path.Combine(_folder, "Room.scene"));
            Assert.NotNull(context.Scene.Lighting.Baked?.MeshFor(context.Scene.Find("Floor")!.GetComponent<MeshRenderer>()!));

            context.ClearBakedLighting();
            Assert.False(File.Exists(bakeFile));
            Assert.Equal("", context.Scene.Lighting.BakedLightingFile);
            Assert.Equal(-1, context.Scene.Find("Floor")!.GetComponent<MeshRenderer>()!.LightmapIndex);
        }

        [Fact]
        public void A_stopped_bake_leaves_the_scene_as_it_was()
        {
            EditorContext context = Room();
            context.Scene.Lighting.Baking.Quality = BakeQuality.High;
            context.Scene.Lighting.Baking.Detail = 40f;
            context.BakeLighting();
            context.CancelBake();
            WaitForBake(context);

            Assert.False(File.Exists(Path.Combine(_folder, "Room.bake")));
            Assert.Equal("", context.Scene.Lighting.BakedLightingFile);
            Assert.Equal(-1, context.Scene.Find("Floor")!.GetComponent<MeshRenderer>()!.LightmapIndex);
        }
    }
}
