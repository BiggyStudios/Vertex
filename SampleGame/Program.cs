using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.UI;

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
            // hold right mouse to scope, V for noclip. B to change class, Escape for the menu, ` for the console.
            var player = new Player();
            Settings.Current.Apply(player);

            // F1-F5 to host or join; see MultiplayerManager.
            var multiplayerObject = new GameObject("Multiplayer");
            MultiplayerManager multiplayer = multiplayerObject.AddComponent<MultiplayerManager>();
            multiplayer.LocalPlayer = player;

            // B, or Change class in the menu, to pick a class. It also opens as the game starts.
            ClassMenu classMenu = multiplayerObject.AddComponent<ClassMenu>();
            classMenu.Player = player;

            PauseMenu menu = multiplayerObject.AddComponent<PauseMenu>();
            menu.Player = player;
            menu.Multiplayer = multiplayer;
            menu.ClassMenu = classMenu;
            menu.Quit = () => _window?.Close();

            DevConsole.Font = Fonts.Regular;
            GameCommands.Register(player, multiplayer);

            // Slightly warm sunlight, which reads nicely against the cooler light from the sky.
            var sun = new GameObject("Sun");
            Light sunLight = sun.AddComponent<Light>();
            sunLight.Color = new Color(1f, 0.95f, 0.87f);
            sunLight.Intensity = 1.1f;
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

            // Two glowing pads either side of the spawn: cyan (right) to the practice surf course, purple (left) to the
            // surf_vertex combat arena. Pads in each bring you back. In both, air control comes from sv_surf_airaccelerate
            // (150, as on surf servers) instead of sv_airaccelerate.
            var home = new SpawnPoint(Player.SpawnPosition, 0f);
            SurfMap.Build(exit: home);
            MapBuilder.TeleportPad("To Surf", new Vector3(6f, 0f, 2f), new SpawnPoint(SurfMap.Start, SurfMap.StartYaw), SurfMap.Zone, new Color(0.2f, 0.9f, 1f));

            CombatSurfMap.Build(exit: home);
            MapBuilder.TeleportPad("To surf_vertex", new Vector3(-8f, 0f, 4f), CombatSurfMap.RedSpawn, CombatSurfMap.Zone, new Color(0.75f, 0.4f, 1f))
                .GetComponent<Teleporter>()!.ToRandomSpawn = true;

            // Bounce pads: the yellow one behind the spawn throws you straight up, and the green one past the cyan pad
            // throws you forward the way you face, just far enough to reach the lookout in the far corner.
            MapBuilder.BouncePad("Bounce Pad", new Vector3(-3f, 0f, 12f), new Color(1f, 0.8f, 0.2f), height: 10f);
            var lookout = new Vector3(12f, 6f, -12f);
            MapBuilder.Block("Lookout", lookout - new Vector3(0f, 3f, 0f), new Vector3(5f, 6f, 5f), new Color(0.6f, 0.62f, 0.66f),
                MapBuilder.Checker(new Color(0.62f, 0.62f, 0.6f), new Color(0.52f, 0.52f, 0.5f)));
            MapBuilder.Glow("Lookout Edge", lookout + new Vector3(0f, -0.3f, 0f), new Vector3(5.1f, 0.2f, 5.1f), new Color(0.3f, 1f, 0.45f));
            var lookoutPad = new Vector3(10f, 0f, -1f);
            MapBuilder.BouncePad("Lookout Pad", lookoutPad, new Color(0.3f, 1f, 0.45f), height: 13f,
                reach: Vector2.Distance(new Vector2(lookoutPad.X, lookoutPad.Z), new Vector2(lookout.X, lookout.Z)), landingRise: lookout.Y - lookoutPad.Y);
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
