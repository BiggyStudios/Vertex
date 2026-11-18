using System.Numerics;

using Vertex.Physics;
using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>Making a game in code, and using what's made in the editor from code.</summary>
    public class WorkflowTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));
        private readonly string[] _assetFolders = Application.AssetFolders;

        public WorkflowTests()
        {
            Directory.CreateDirectory(Path.Combine(_folder, "Scenes"));
            Application.AssetFolders = [_folder];
        }

        public override void Dispose()
        {
            base.Dispose();
            Application.AssetFolders = _assetFolders;
            Directory.Delete(_folder, recursive: true);
        }

        /// <summary>A scene file made "in the editor", with one object in it.</summary>
        private void SaveScene(string name, string objectName)
        {
            var scene = new Scene();
            _ = new GameObject(objectName, scene);
            scene.Lighting.FogDensity = 0.02f;
            scene.ApplyChanges();
            SceneFile.Save(scene, Path.Combine(_folder, "Scenes", name), _folder);
        }

        [Fact]
        public void A_scene_made_in_the_editor_opens_from_code_in_place_of_the_active_one()
        {
            SaveScene("Arena.scene", "Floor");
            Scene before = Scene.Active;
            var recorder = new GameObject("Old").AddComponent<Recorder>();
            before.Update();

            Scene arena = Scene.Load("Scenes/Arena.scene");
            Assert.Same(arena, Scene.Active);
            Assert.Contains("OnDestroy", recorder.Calls);
            arena.ApplyChanges();
            Assert.Equal(["Floor"], arena.GameObjects.Select(o => o.Name));
            Assert.Equal(0.02f, arena.Lighting.FogDensity);
            Assert.Same(arena.Find("Floor"), GameObject.Find("Floor"));

            Assert.Throws<FileNotFoundException>(() => Scene.Load("Scenes/Missing.scene"));
        }

        [Fact]
        public void A_scene_file_can_be_added_to_a_world_built_in_code()
        {
            SaveScene("Room.scene", "Table");
            var made = new GameObject("Made in code");
            List<GameObject> added = Scene.Active.Add("Scenes/Room.scene");
            Scene.Active.ApplyChanges();

            Assert.Equal(["Table"], added.Select(o => o.Name));
            Assert.Equal(["Made in code", "Table"], Scene.Active.GameObjects.Select(o => o.Name));
            Assert.False(made.IsDestroyed);
        }

        [Fact]
        public void A_game_with_a_start_scene_opens_it_when_it_loads()
        {
            SaveScene("Main.scene", "Spawn");
            var game = new Game { StartScene = "Scenes/Main.scene" };
            var loop = new GameLoop(game);
            loop.Load();
            RunFrames(loop, 1);

            Assert.Equal(["Spawn"], Scene.Active.GameObjects.Select(o => o.Name));
        }

        [Fact]
        public void Shapes_made_in_code_are_drawn_and_solid()
        {
            GameObject cube = GameObject.CreateCube(new Vector3(0f, 3f, 0f), Color.Red);
            GameObject ball = GameObject.CreateSphere(Vector3.Zero, name: "Ball");
            GameObject floor = GameObject.CreatePlane(Vector3.Zero, size: 20f);

            Assert.Equal(new Vector3(0f, 3f, 0f), cube.Transform.Position);
            Assert.Same(Primitives.Cube, cube.GetComponent<MeshRenderer>()!.Mesh);
            Assert.Equal(Color.Red, cube.GetComponent<MeshRenderer>()!.Material.Color);
            Assert.NotNull(cube.GetComponent<BoxCollider>());
            Assert.Equal("Ball", ball.Name);
            Assert.NotNull(ball.GetComponent<SphereCollider>());
            Assert.Equal(new Vector3(20f, 1f, 20f), floor.Transform.Scale);

            // A box dropped on the floor comes to rest on it.
            GameObject box = GameObject.CreateCube(new Vector3(0f, 2f, 0f));
            box.AddComponent<Rigidbody>(body => body.Mass = 2f);
            Assert.Equal(2f, box.GetComponent<Rigidbody>()!.Mass);
            cube.Destroy();
            ball.Destroy();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 180);
            Assert.InRange(box.Transform.Position.Y, 0.4f, 0.6f);
        }
    }
}
