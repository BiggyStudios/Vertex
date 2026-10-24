using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A small surf map beside the sandbox: a start platform, three ramps with gaps between them and an end platform.
    /// Falling off lands in the water, which sends you back to the start.
    /// <para>
    /// The ramps are steeper than the player can stand on, so standing on one slides you down. To surf, hold the strafe key
    /// towards the ramp (A on the left side, D on the right) and keep the mouse turned the same way, so the slope holds you
    /// up while gravity speeds you along it.
    /// </para>
    /// </summary>
    public static class SurfMap
    {
        /// <summary>Surf ramps are usually 50 to 60 degrees; anything past 45.57 is too steep to stand on.</summary>
        private const float _rampAngle = 55f;
        private const float _rampHeight = 10f;

        // One checker square every 2 units, so speed is easy to judge.
        private const float _texturesPerUnit = 0.25f;

        public static Vector3 Start { get; } = new(60f, 40f, 6f);
        public const float StartYaw = 0f;

        /// <param name="exit">Where the pad on the end platform sends the player.</param>
        public static void Build(Vector3 exit, float exitYaw)
        {
            Texture rampTexture = Texture.CreateCheckerboard(new Color(0.55f, 0.65f, 0.8f), new Color(0.42f, 0.5f, 0.66f), squares: 2);
            Texture platformTexture = Texture.CreateCheckerboard(new Color(0.5f, 0.7f, 0.45f), new Color(0.38f, 0.56f, 0.35f), squares: 2);

            CreateBlock("Surf Start", new Vector3(60f, 39.5f, 4f), new Vector3(12f, 1f, 12f), platformTexture);

            // Each ramp runs downhill, which is where the speed comes from, and starts a little lower than the last one
            // ended, shifted sideways, with a 12-unit gap to fly across.
            CreateRamp("Surf Ramp 1", ridgeStart: new Vector3(60f, 37f, -6f), length: 80f, drop: 8f, rampTexture);
            CreateRamp("Surf Ramp 2", ridgeStart: new Vector3(66f, 28f, -98f), length: 80f, drop: 8f, rampTexture);
            CreateRamp("Surf Ramp 3", ridgeStart: new Vector3(60f, 15f, -190f), length: 80f, drop: 6f, rampTexture);

            CreateBlock("Surf End", new Vector3(62f, -0.5f, -295f), new Vector3(30f, 1f, 30f), platformTexture);
            CreateTeleportPad("Surf Exit", new Vector3(62f, 0f, -302f), exit, exitYaw, airAcceleration: 10f);

            // Well below everything, and kept clear of the sandbox at x -15 to 15.
            var water = new GameObject("Surf Water");
            water.Transform.Position = new Vector3(65f, -10f, -150f);
            water.Transform.Scale = new Vector3(60f, 1f, 340f);
            MeshRenderer waterRenderer = water.AddComponent<MeshRenderer>();
            waterRenderer.Mesh = Primitives.Plane;
            waterRenderer.Material.Color = new Color(0.12f, 0.25f, 0.45f);
            waterRenderer.Material.Specular = 0.6f;

            var reset = new GameObject("Surf Reset");
            reset.Transform.Position = new Vector3(65f, -12f, -150f);
            BoxCollider resetTrigger = reset.AddComponent<BoxCollider>();
            resetTrigger.Size = new Vector3(60f, 4f, 340f);
            resetTrigger.IsTrigger = true;
            Teleporter resetTeleporter = reset.AddComponent<Teleporter>();
            resetTeleporter.Destination = Start;
            resetTeleporter.DestinationYaw = StartYaw;
        }

        /// <summary>A glowing pad that sends the player to <paramref name="destination"/> when they step on it.</summary>
        public static void CreateTeleportPad(string name, Vector3 position, Vector3 destination, float destinationYaw, float? airAcceleration = null)
        {
            var size = new Vector3(3f, 0.2f, 3f);

            var pad = new GameObject(name);
            pad.Transform.Position = position + new Vector3(0f, size.Y * 0.5f, 0f);

            MeshRenderer renderer = pad.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size);
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = new Color(0.2f, 0.9f, 1f);

            // Taller than the pad, so the player is inside it standing on the pad.
            BoxCollider trigger = pad.AddComponent<BoxCollider>();
            trigger.Size = new Vector3(size.X, 2f, size.Z);
            trigger.Center = new Vector3(0f, 1f, 0f);
            trigger.IsTrigger = true;

            Teleporter teleporter = pad.AddComponent<Teleporter>();
            teleporter.Destination = destination;
            teleporter.DestinationYaw = destinationYaw;
            teleporter.AirAcceleration = airAcceleration;
        }

        /// <summary>
        /// A double-sided ramp running towards -Z from <paramref name="ridgeStart"/>, the top of its near end.
        /// Its ridge falls by <paramref name="drop"/> over <paramref name="length"/>.
        /// </summary>
        private static void CreateRamp(string name, Vector3 ridgeStart, float length, float drop, Texture texture)
        {
            float halfWidth = _rampHeight / MathF.Tan(MathHelper.ToRadians(_rampAngle));
            float slopedLength = MathF.Sqrt(length * length + drop * drop);
            Mesh mesh = Primitives.CreatePrism([new Vector2(-halfWidth, 0f), new Vector2(halfWidth, 0f), new Vector2(0f, _rampHeight)], slopedLength, _texturesPerUnit);

            // Tilt the far end down, then place it so the middle of the ridge lands halfway along.
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.Atan2(drop, length));
            Vector3 ridgeMiddle = ridgeStart + new Vector3(0f, -drop * 0.5f, -length * 0.5f);

            var ramp = new GameObject(name);
            ramp.Transform.Position = ridgeMiddle - Vector3.Transform(new Vector3(0f, _rampHeight, 0f), tilt);
            ramp.Transform.Rotation = tilt;

            MeshRenderer renderer = ramp.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.2f;

            ramp.AddComponent<ConvexCollider>().Mesh = mesh;
        }

        private static void CreateBlock(string name, Vector3 centre, Vector3 size, Texture texture)
        {
            var block = new GameObject(name);
            block.Transform.Position = centre;

            MeshRenderer renderer = block.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size, _texturesPerUnit);
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.1f;

            block.AddComponent<BoxCollider>().Size = size;
        }
    }
}
