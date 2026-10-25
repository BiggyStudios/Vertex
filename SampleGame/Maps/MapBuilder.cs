using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>The building blocks the maps are made of: surf ramps, solid blocks, teleport and bounce pads, water and triggers.</summary>
    public static class MapBuilder
    {
        /// <summary>Surf ramps are usually 50 to 60 degrees; anything past 45.57 is too steep to stand on.</summary>
        public const float RampAngle = 55f;
        public const float RampHeight = 10f;

        // One checker square every 2 units, so speed is easy to judge.
        public const float TexturesPerUnit = 0.25f;

        public static Texture Checker(Color a, Color b) => Texture.CreateCheckerboard(a, b, squares: 2);

        /// <summary>
        /// A double-sided surf ramp starting at <paramref name="ridgeStart"/>, the top of its near end, and running
        /// <paramref name="length"/> units in the direction <paramref name="heading"/> (degrees; 0 runs towards -Z,
        /// 180 towards +Z). Its ridge falls by <paramref name="drop"/> along the way, which is where the speed comes from.
        /// </summary>
        public static GameObject Ramp(string name, Vector3 ridgeStart, float length, float drop, Texture texture, float heading = 0f)
        {
            float halfWidth = RampHeight / MathF.Tan(MathHelper.ToRadians(RampAngle));
            float slopedLength = MathF.Sqrt(length * length + drop * drop);
            Mesh mesh = Primitives.CreatePrism([new Vector2(-halfWidth, 0f), new Vector2(halfWidth, 0f), new Vector2(0f, RampHeight)], slopedLength, TexturesPerUnit);

            // Tilt the far end down, turn it to face its heading, then place it so the middle of the ridge lands halfway along.
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.Atan2(drop, length));
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.ToRadians(heading));
            Quaternion rotation = Quaternion.Concatenate(tilt, turn);
            Vector3 ridgeMiddle = ridgeStart + Vector3.Transform(new Vector3(0f, -drop * 0.5f, -length * 0.5f), turn);

            var ramp = new GameObject(name);
            ramp.Transform.Position = ridgeMiddle - Vector3.Transform(new Vector3(0f, RampHeight, 0f), rotation);
            ramp.Transform.Rotation = rotation;

            MeshRenderer renderer = ramp.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.2f;

            ramp.AddComponent<ConvexCollider>().Mesh = mesh;
            return ramp;
        }

        /// <summary>A solid box. The texture, if any, keeps the same scale whatever the size.</summary>
        public static GameObject Block(string name, Vector3 centre, Vector3 size, Color color, Texture? texture = null)
        {
            var block = new GameObject(name);
            block.Transform.Position = centre;

            MeshRenderer renderer = block.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size, TexturesPerUnit);
            renderer.Material.Color = color;
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.1f;

            block.AddComponent<BoxCollider>().Size = size;
            return block;
        }

        /// <summary>A glowing strip for decoration and team colours. Not solid.</summary>
        public static GameObject Glow(string name, Vector3 centre, Vector3 size, Color color)
        {
            var glow = new GameObject(name);
            glow.Transform.Position = centre;

            MeshRenderer renderer = glow.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size);
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
            return glow;
        }

        /// <summary>
        /// A glowing pad that sends the player to <paramref name="destination"/> when they step on it, and into
        /// <paramref name="zone"/> if given.
        /// </summary>
        public static GameObject TeleportPad(string name, Vector3 position, SpawnPoint destination, Zone? zone, Color color)
        {
            var size = new Vector3(3f, 0.2f, 3f);

            var pad = new GameObject(name);
            pad.Transform.Position = position + new Vector3(0f, size.Y * 0.5f, 0f);

            MeshRenderer renderer = pad.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size);
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;

            // Taller than the pad, so the player is inside it standing on the pad.
            BoxCollider trigger = pad.AddComponent<BoxCollider>();
            trigger.Size = new Vector3(size.X, 2f, size.Z);
            trigger.Center = new Vector3(0f, 1f, 0f);
            trigger.IsTrigger = true;

            Teleporter teleporter = pad.AddComponent<Teleporter>();
            teleporter.Destination = destination.Position;
            teleporter.DestinationYaw = destination.Yaw;
            teleporter.Zone = zone;
            return pad;
        }

        /// <summary>
        /// A glowing pad that throws the player <paramref name="height"/> metres up. With a <paramref name="reach"/>, it
        /// also throws them forward, whichever way they're facing, fast enough to land that far away and
        /// <paramref name="landingRise"/> higher than the pad at normal gravity, without air strafing. Without one, it
        /// bounces them straight up.
        /// </summary>
        public static GameObject BouncePad(string name, Vector3 position, Color color, float height, float reach = 0f, float landingRise = 0f)
        {
            var pad = new GameObject(name);
            pad.Transform.Position = position;

            var dark = new Color(0.18f, 0.19f, 0.22f);
            AddVisual(pad, "Rim", Primitives.CreateBox(new Vector3(3.4f, 0.06f, 3.4f)), new Vector3(0f, 0.03f, 0f), Vector3.One, color, unlit: true);
            AddVisual(pad, "Base", Primitives.CreateBox(new Vector3(3.1f, 0.14f, 3.1f)), new Vector3(0f, 0.07f, 0f), Vector3.One, dark, unlit: false);

            // Scaled rather than sized, so the BouncePad can squash it when someone uses it.
            GameObject top = AddVisual(pad, "Top", Primitives.Cube, new Vector3(0f, 0.17f, 0f), new Vector3(2.4f, 0.06f, 2.4f), color, unlit: true);

            // Not solid, like the teleport pads: it's thin enough that you stand on the floor beneath it.
            BoxCollider trigger = pad.AddComponent<BoxCollider>();
            trigger.Size = new Vector3(3f, 1f, 3f);
            trigger.Center = new Vector3(0f, 0.5f, 0f);
            trigger.IsTrigger = true;

            BouncePad bouncePad = pad.AddComponent<BouncePad>();
            bouncePad.Height = height;
            if (reach > 0f) bouncePad.ForwardSpeed = global::SampleGame.BouncePad.SpeedToReach(reach, landingRise, height, Player.DefaultGravity);
            bouncePad.Top = top;
            return pad;
        }

        private static GameObject AddVisual(GameObject parent, string name, Mesh mesh, Vector3 position, Vector3 scale, Color color, bool unlit)
        {
            var visual = new GameObject(name);
            visual.Transform.Parent = parent.Transform;
            visual.Transform.Position = position;
            visual.Transform.Scale = scale;

            MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Color = color;
            renderer.Material.Specular = 0.1f;
            if (unlit) renderer.Material.Shading = ShadingModel.Unlit;
            return visual;
        }

        /// <summary>A flat water surface, for looks. Pair it with a trigger just below to catch players.</summary>
        public static GameObject Water(string name, Vector3 centre, Vector2 size)
        {
            var water = new GameObject(name);
            water.Transform.Position = centre;
            water.Transform.Scale = new Vector3(size.X, 1f, size.Y);

            MeshRenderer renderer = water.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Plane;
            renderer.Material.Color = new Color(0.12f, 0.25f, 0.45f);
            renderer.Material.Specular = 0.6f;
            return water;
        }

        /// <summary>An invisible trigger box. Add a Teleporter, KillTrigger or similar to make it do something.</summary>
        public static GameObject Trigger(string name, Vector3 centre, Vector3 size)
        {
            var trigger = new GameObject(name);
            trigger.Transform.Position = centre;

            BoxCollider collider = trigger.AddComponent<BoxCollider>();
            collider.Size = size;
            collider.IsTrigger = true;
            return trigger;
        }
    }
}
