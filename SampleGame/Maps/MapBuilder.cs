using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// One piece of a <see cref="MapBuilder.CurvedRamp"/>'s path: <see cref="Length"/> units straight on, or round a curve
    /// that turns <see cref="Turn"/> degrees over that length (positive turns left).
    /// </summary>
    public readonly record struct RampSegment(float Length, float Turn = 0f);

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
        /// <remarks>
        /// <paramref name="boosters"/> puts a <see cref="SurfBooster"/> at each of those fractions of the way along (0.5 is
        /// halfway), marked with arrows in <paramref name="boosterColor"/>.
        /// <paramref name="height"/> makes a smaller ramp than the usual <see cref="RampHeight"/>, at the same angle; boosters are
        /// sized for full-height ramps.
        /// </remarks>
        public static GameObject Ramp(string name, Vector3 ridgeStart, float length, float drop, Texture texture, float heading = 0f,
            ReadOnlySpan<float> boosters = default, Color? boosterColor = null, float height = RampHeight)
        {
            float slopedLength = MathF.Sqrt(length * length + drop * drop);
            Mesh mesh = Primitives.CreatePrism(CrossSection(height), slopedLength, TexturesPerUnit);

            // Tilt the far end down, turn it to face its heading, then place it so the middle of the ridge lands halfway along.
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.Atan2(drop, length));
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.ToRadians(heading));
            Quaternion rotation = Quaternion.Concatenate(tilt, turn);
            Vector3 ridgeMiddle = ridgeStart + Vector3.Transform(new Vector3(0f, -drop * 0.5f, -length * 0.5f), turn);

            var ramp = new GameObject(name);
            ramp.Transform.Position = ridgeMiddle - Vector3.Transform(new Vector3(0f, height, 0f), rotation);
            ramp.Transform.Rotation = rotation;

            MeshRenderer renderer = ramp.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.2f;

            ramp.AddComponent<ConvexCollider>().Mesh = mesh;

            // The ramp's own space runs from its near end at +Z to its far end at -Z.
            foreach (float along in boosters)
            {
                AddBooster(ramp, new Vector3(0f, 0f, slopedLength * (0.5f - along)), Quaternion.Identity, boosterColor ?? _boosterColor);
            }

            return ramp;
        }

        /// <summary>
        /// A surf ramp that follows a path of straight runs and curves, starting with its ridge at
        /// <paramref name="ridgeStart"/>, heading <paramref name="heading"/> (degrees, as for <see cref="Ramp"/>). The ridge
        /// falls steadily by <paramref name="drop"/> over the whole path. Its collider is the exact curved surface, so
        /// surfing round the bend is as smooth as the straight parts. In a curve, the inside face is the one to surf: it
        /// banks into the turn and holds you on it.
        /// </summary>
        /// <param name="boosters">Fractions of the way along to put a <see cref="SurfBooster"/>, as for <see cref="Ramp"/>.</param>
        public static GameObject CurvedRamp(string name, Vector3 ridgeStart, float heading, float drop, Texture texture,
            ReadOnlySpan<RampSegment> segments, ReadOnlySpan<float> boosters = default, Color? boosterColor = null)
        {
            // Walk the path in short steps: every metre and a half on the straight, and every 3 degrees round a curve.
            var points = new List<(Vector3 Position, float Heading, float Distance)> { (Vector3.Zero, heading, 0f) };
            Vector3 position = Vector3.Zero;
            float distance = 0f;
            foreach (RampSegment segment in segments)
            {
                int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(segment.Length / 1.5f, MathF.Abs(segment.Turn) / 3f)));
                float stepTurn = segment.Turn / steps;
                float stepLength = segment.Length / steps;
                for (int i = 0; i < steps; i++)
                {
                    // Round a curve, each step is a chord of the circle, so it stays exactly on it.
                    float chord = segment.Turn == 0f ? stepLength
                        : 2f * (segment.Length / MathHelper.ToRadians(MathF.Abs(segment.Turn))) * MathF.Sin(MathHelper.ToRadians(MathF.Abs(stepTurn)) * 0.5f);
                    position += HeadingDirection(heading + stepTurn * 0.5f) * chord;
                    heading += stepTurn;
                    distance += stepLength;
                    points.Add((position, heading, distance));
                }
            }

            // Each frame stands the cross-section upright at its point, square to the way the path heads there.
            var frames = new SweepFrame[points.Count];
            for (int i = 0; i < frames.Length; i++)
            {
                (Vector3 point, float pointHeading, float pointDistance) = points[i];
                Vector3 forward = HeadingDirection(pointHeading);
                var ridge = point with { Y = -drop * pointDistance / distance };
                frames[i] = new SweepFrame(ridge - new Vector3(0f, RampHeight, 0f), new Vector3(-forward.Z, 0f, forward.X), Vector3.UnitY);
            }

            Mesh mesh = Primitives.CreateSweep(CrossSection(RampHeight), frames, TexturesPerUnit);

            var ramp = new GameObject(name);
            ramp.Transform.Position = ridgeStart;

            MeshRenderer renderer = ramp.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Texture = texture;
            renderer.Material.Specular = 0.2f;

            ramp.AddComponent<MeshCollider>().Mesh = mesh;

            foreach (float along in boosters)
            {
                int index = 0;
                while (index < points.Count - 1 && points[index].Distance < along * distance) index++;
                SweepFrame frame = frames[index];
                var orientation = new Matrix4x4(
                    frame.Right.X, frame.Right.Y, frame.Right.Z, 0f,
                    frame.Up.X, frame.Up.Y, frame.Up.Z, 0f,
                    -frame.Forward.X, -frame.Forward.Y, -frame.Forward.Z, 0f,
                    0f, 0f, 0f, 1f);
                AddBooster(ramp, frame.Position, Quaternion.CreateFromRotationMatrix(orientation), boosterColor ?? _boosterColor);
            }

            return ramp;
        }

        private static readonly Color _boosterColor = new(1f, 0.55f, 0.1f);

        // One chevron, pointing along +X: two slanted bars meeting at the tip.
        private static readonly Vector2[] _chevron = [new(0.5f, 0f), new(-0.1f, 0.6f), new(-0.4f, 0.6f), new(0.2f, 0f), new(-0.4f, -0.6f), new(-0.1f, -0.6f)];

        private static float RampHalfWidth => RampHeight / MathF.Tan(MathHelper.ToRadians(RampAngle));

        // A surf ramp's end-on shape: the ridge on top, a slope down each side.
        private static Vector2[] CrossSection(float height)
        {
            float halfWidth = height / MathF.Tan(MathHelper.ToRadians(RampAngle));
            return [new(-halfWidth, 0f), new(halfWidth, 0f), new(0f, height)];
        }

        /// <summary>The flat direction a heading points: 0 is -Z, 90 is -X, and turning left adds to it.</summary>
        private static Vector3 HeadingDirection(float heading)
        {
            float radians = MathHelper.ToRadians(heading);
            return new Vector3(-MathF.Sin(radians), 0f, -MathF.Cos(radians));
        }

        /// <summary>
        /// A booster on a ramp, at <paramref name="position"/> (the middle of the ramp's base there) in the ramp's own space,
        /// turned by <paramref name="rotation"/> so its forward runs along the ramp. A trigger round the ramp there, and
        /// three glowing arrows on each face pointing along it.
        /// </summary>
        private static void AddBooster(GameObject ramp, Vector3 position, Quaternion rotation, Color color)
        {
            var booster = new GameObject("Booster");
            booster.Transform.Parent = ramp.Transform;
            booster.Transform.Position = position;
            booster.Transform.Rotation = rotation;

            // Covers both faces, with room for a surfer pressed against either one.
            BoxCollider trigger = booster.AddComponent<BoxCollider>();
            trigger.Size = new Vector3(RampHalfWidth * 2f + 3f, RampHeight + 3f, 7f);
            trigger.Center = new Vector3(0f, RampHeight * 0.5f, 0f);
            trigger.IsTrigger = true;

            MeshRenderer renderer = booster.AddComponent<MeshRenderer>();
            renderer.Mesh = CreateArrows();
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
            renderer.Material.Emission = color;
            renderer.CastShadows = false;

            booster.AddComponent<SurfBooster>().Arrows = renderer.Material;
        }

        /// <summary>Chevrons pointing along -Z, three down the middle of each face of a ramp, just off its surface.</summary>
        private static Mesh CreateArrows()
        {
            var vertices = new List<MeshVertex>();
            var indices = new List<uint>();

            var ridge = new Vector2(0f, RampHeight);

            foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
            {
                var corner = new Vector2(side * RampHalfWidth, 0f);
                Vector2 down = Vector2.Normalize(corner - ridge);
                Vector2 outward = Vector2.Normalize(new Vector2(side * RampHeight, RampHalfWidth));
                Vector2 middle = ridge + down * Vector2.Distance(ridge, corner) * 0.4f + outward * 0.05f;

                var normal = new Vector3(outward, 0f);
                var across = new Vector3(down, 0f);
                Vector3 along = -Vector3.UnitZ;

                foreach (float offset in (ReadOnlySpan<float>)[-2.2f, 0f, 2.2f])
                {
                    Vector3 centre = new Vector3(middle, 0f) + along * offset;
                    Vector3 Corner(int i) => centre + along * (_chevron[i].X * 1.6f) + across * (_chevron[i].Y * 2.2f);

                    // Each bar is a four-sided shape: tip, outer end, inner end, the notch.
                    AddQuad(Corner(0), Corner(1), Corner(2), Corner(3));
                    AddQuad(Corner(0), Corner(3), Corner(4), Corner(5));

                    void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
                    {
                        uint start = (uint)vertices.Count;
                        foreach (Vector3 point in (ReadOnlySpan<Vector3>)[a, b, c, d]) vertices.Add(new MeshVertex(point, normal, Vector2.Zero));

                        // Wound to face out from the ramp.
                        bool facesOut = Vector3.Dot(Vector3.Cross(b - a, c - a), normal) >= 0f;
                        indices.AddRange(facesOut ? [start, start + 1, start + 2, start, start + 2, start + 3] : [start, start + 2, start + 1, start, start + 3, start + 2]);
                    }
                }
            }

            return new Mesh(vertices.ToArray(), indices.ToArray());
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
            renderer.Material.Emission = color;
            renderer.Material.EmissionStrength = 0.8f;
            return glow;
        }

        /// <summary>
        /// A glowing pad that sends the player to <paramref name="destination"/>, and into <paramref name="zone"/> if
        /// given. With <paramref name="onPress"/>, standing on it waits for E; without, it goes the moment it's
        /// touched.
        /// </summary>
        public static GameObject TeleportPad(string name, Vector3 position, SpawnPoint destination, Zone? zone, Color color, bool onPress = false)
        {
            var size = new Vector3(3f, 0.2f, 3f);

            var pad = new GameObject(name);
            pad.Transform.Position = position + new Vector3(0f, size.Y * 0.5f, 0f);

            MeshRenderer renderer = pad.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.CreateBox(size);
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = color;
            renderer.Material.Emission = color;
            renderer.Material.EmissionStrength = 1.5f;

            // Motes drifting up out of it, like a portal.
            var motes = new GameObject("Motes");
            motes.Transform.Parent = pad.Transform;
            motes.Transform.Position = new Vector3(0f, 0.15f, 0f);
            ParticleSystem particles = motes.AddComponent<ParticleSystem>();
            particles.Rate = 10f;
            particles.Area = new Vector3(2.6f, 0f, 2.6f);
            particles.Spread = 5f;
            particles.Speed = new FloatRange(0.3f, 0.9f);
            particles.Lifetime = new FloatRange(1.2f, 2.2f);
            particles.Size = new FloatRange(0.04f, 0.08f);
            particles.EndSize = 0.2f;
            particles.StartColor = color;
            particles.EndColor = color with { A = 0f };
            particles.Glow = 2f;

            // Taller than the pad, so the player is inside it standing on the pad.
            BoxCollider trigger = pad.AddComponent<BoxCollider>();
            trigger.Size = new Vector3(size.X, 2f, size.Z);
            trigger.Center = new Vector3(0f, 1f, 0f);
            trigger.IsTrigger = true;

            Teleporter teleporter = pad.AddComponent<Teleporter>();
            teleporter.Destination = destination.Position;
            teleporter.DestinationYaw = destination.Yaw;
            teleporter.Zone = zone;
            teleporter.OnPress = onPress;
            teleporter.Glow = renderer.Material;
            return pad;
        }

        /// <summary>
        /// A glowing pad that throws the player <paramref name="height"/> metres up. With a <paramref name="reach"/>, it
        /// also throws them forward, whichever way they're facing, at least fast enough to land that far away and
        /// <paramref name="landingRise"/> higher than the pad at normal gravity, without air strafing; anyone arriving
        /// faster keeps their speed. Without one, it bounces them straight up.
        /// </summary>
        public static GameObject BouncePad(string name, Vector3 position, Color color, float height, float reach = 0f, float landingRise = 0f)
        {
            var pad = new GameObject(name);
            pad.Transform.Position = position;

            var dark = new Color(0.18f, 0.19f, 0.22f);
            AddVisual(pad, "Rim", Primitives.CreateBox(new Vector3(3.4f, 0.06f, 3.4f)), new Vector3(0f, 0.03f, 0f), Vector3.One, color, unlit: true)
                .GetComponent<MeshRenderer>()!.Material.Emission = color;
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
            bouncePad.Color = color;
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

        /// <summary>A glowing orb at <paramref name="position"/> to grapple onto (see <see cref="Grapple"/>). Not solid.</summary>
        public static GameObject GrapplePoint(string name, Vector3 position)
        {
            var point = new GameObject(name);
            point.Transform.Position = position;
            point.AddComponent<global::SampleGame.GrapplePoint>();
            return point;
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
