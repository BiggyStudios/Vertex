using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    public class SampleGame : Game
    {
        private static VertexWindow? _window;

        public static void Main()
        {
            _window = new VertexWindow(1280, 720, new SampleGame(), "Vertex");
            _window.Run();
        }

        public override void Load()
        {
            // The player has the camera: mouse to look, WASD to move, Space or mouse wheel to jump, left mouse to shoot,
            // V for noclip.
            var player = new Player();

            // F1-F5 to host or join; see MultiplayerManager.
            new GameObject("Multiplayer").AddComponent<MultiplayerManager>().LocalPlayer = player;

            var sun = new GameObject("Sun");
            sun.AddComponent<Light>().Intensity = 0.9f;
            sun.Transform.EulerAngles = new Vector3(-50f, 30f, 0f);

            var ground = new GameObject("Ground");
            ground.Transform.Scale = new Vector3(30f, 1f, 30f);
            MeshRenderer groundRenderer = ground.AddComponent<MeshRenderer>();
            groundRenderer.Mesh = Primitives.CreatePlane(uvScale: 15f);
            groundRenderer.Material.Texture = Texture.CreateCheckerboard(new Color(0.75f, 0.75f, 0.75f), new Color(0.55f, 0.55f, 0.55f), squares: 2);
            groundRenderer.Material.Specular = 0.05f;
            // A full-height box whose top sits at y = 0, flush with the plane. Thick ground is harder to fall through.
            ground.AddComponent<BoxCollider>().Center = new Vector3(0f, -0.5f, 0f);

            var cube = new GameObject("Spinning Cube");
            cube.Transform.Position = new Vector3(3f, 1f, 0f);
            MeshRenderer cubeRenderer = cube.AddComponent<MeshRenderer>();
            cubeRenderer.Mesh = Primitives.Cube;
            cubeRenderer.Material.Color = new Color(0.2f, 0.5f, 1f);
            cube.AddComponent<Spinner>().Speed = new Vector3(20f, 45f, 0f);
            // Kinematic, so it keeps spinning from code but knocks falling things away.
            cube.AddComponent<BoxCollider>();
            cube.AddComponent<Rigidbody>().IsKinematic = true;

            var ball = new GameObject("Ball");
            ball.Transform.Position = new Vector3(-1.5f, 6f, -2f);
            ball.Transform.Scale = new Vector3(1.5f);
            MeshRenderer ballRenderer = ball.AddComponent<MeshRenderer>();
            ballRenderer.Mesh = Primitives.Sphere;
            ballRenderer.Material.Color = new Color(0.3f, 0.85f, 0.4f);
            ball.AddComponent<SphereCollider>().Bounciness = 0.7f;
            ball.AddComponent<Rigidbody>();

            for (int i = 0; i < 5; i++)
            {
                CreateCrate(new Vector3(1.5f, 0.5f + i * 1.5f, -3f), new Vector3(0f, i * 17f, 0f));
            }

            GameObject house = Model.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "house.obj"));
            house.Transform.Position = new Vector3(-4.5f, 0f, -1f);
            house.Transform.EulerAngles = new Vector3(0f, 25f, 0f);

            // Step on the glowing pad to go surfing; the pad at the end of the map brings you back here.
            // Surf servers raise sv_airaccelerate from 10 to 150, so the pads switch it on the way there and back.
            SurfMap.Build(exit: Player.SpawnPosition, exitYaw: 0f);
            SurfMap.CreateTeleportPad("To Surf", new Vector3(6f, 0f, 2f), SurfMap.Start, SurfMap.StartYaw, airAcceleration: 150f);
        }

        public override void Update()
        {
            if (InputSystem.GetKeyDown(Key.Escape)) _window?.Close();
        }

        private static GameObject CreateCrate(Vector3 position, Vector3 eulerAngles)
        {
            var crate = new GameObject("Crate");
            crate.Transform.Position = position;
            crate.Transform.EulerAngles = eulerAngles;

            MeshRenderer renderer = crate.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Cube;
            renderer.Material.Color = new Color(0.65f, 0.45f, 0.25f);

            crate.AddComponent<BoxCollider>();
            crate.AddComponent<Rigidbody>().Mass = 2f;
            return crate;
        }
    }
}
