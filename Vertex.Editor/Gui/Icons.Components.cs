using System.Numerics;

using Vertex.UI;

namespace Vertex.Editor.Gui
{
    // The icons of the kinds of component: what's shown on their cards in the inspector and in Add Component's list (see
    // ComponentIcons). Colliders are dashed, like the outline of a shape that's there but not seen.
    public static partial class Icons
    {
        // ---------------------------------------------------------------- Helpers

        /// <summary>Every other piece of a path, so it reads as dashed. <paramref name="closed"/> joins the last point to the first.</summary>
        private static void Dashed(ReadOnlySpan<Vector2> points, Color color, float thickness, bool closed)
        {
            int count = closed ? points.Length : points.Length - 1;
            for (int i = 0; i < count; i += 2) Ui.Line(points[i], points[(i + 1) % points.Length], color, thickness);
        }

        /// <summary>Points round an ellipse, starting at its right and going clockwise.</summary>
        private static void Ellipse(Span<Vector2> into, Vector2 centre, float radiusX, float radiusY)
        {
            for (int i = 0; i < into.Length; i++)
            {
                float a = i * MathF.Tau / into.Length;
                into[i] = centre + new Vector2(MathF.Cos(a) * radiusX, MathF.Sin(a) * radiusY);
            }
        }

        private static Vector2 Hexagon(Vector2 centre, float radius, int corner)
        {
            float a = (corner * 60f - 90f) * MathF.PI / 180f;
            return centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
        }

        // ---------------------------------------------------------------- Physics

        /// <summary>A solid ball with speed lines behind it: something physics moves.</summary>
        public static void Rigidbody(Vector2 centre, float size, Color color)
        {
            Vector2 ball = centre + new Vector2(size * 0.14f, 0f);
            Ui.Circle(ball, size * 0.24f, color);
            float[] lengths = [0.16f, 0.24f, 0.16f];
            for (int i = 0; i < 3; i++)
            {
                float y = centre.Y + (i - 1) * size * 0.16f;
                float right = ball.X - size * 0.3f;
                Ui.Line(new Vector2(right - lengths[i] * size, y), new Vector2(right, y), color, 1.4f);
            }
        }

        /// <summary>A dashed cube: a box-shaped collider.</summary>
        public static void BoxCollider(Vector2 centre, float size, Color color)
        {
            float r = size * 0.36f;
            Span<Vector2> outline = stackalloc Vector2[12];
            for (int i = 0; i < 6; i++)
            {
                outline[i * 2] = Hexagon(centre, r, i);
                outline[i * 2 + 1] = Vector2.Lerp(Hexagon(centre, r, i), Hexagon(centre, r, i + 1), 0.5f);
            }

            Dashed(outline, color, 1.3f, closed: true);
            for (int corner = 1; corner < 6; corner += 2) Ui.Line(centre, Vector2.Lerp(centre, Hexagon(centre, r, corner), 0.55f), color, 1.2f);
        }

        /// <summary>A dashed ball with its equator: a sphere collider.</summary>
        public static void SphereCollider(Vector2 centre, float size, Color color)
        {
            Span<Vector2> round = stackalloc Vector2[16];
            Ellipse(round, centre, size * 0.34f, size * 0.34f);
            Dashed(round, color, 1.3f, closed: true);
            Span<Vector2> equator = stackalloc Vector2[12];
            Ellipse(equator, centre, size * 0.34f, size * 0.12f);
            Dashed(equator, color, 1.1f, closed: true);
        }

        /// <summary>A dashed pill standing up: a capsule collider.</summary>
        public static void CapsuleCollider(Vector2 centre, float size, Color color)
        {
            float r = size * 0.17f, half = size * 0.2f;
            Span<Vector2> points = stackalloc Vector2[18];
            int n = 0;
            for (int i = 0; i <= 6; i++) points[n++] = centre + new Vector2(0f, -half) + Angle(180f + i * 30f) * r;
            for (int i = 0; i <= 6; i++) points[n++] = centre + new Vector2(0f, half) + Angle(i * 30f) * r;
            Dashed(points[..n], color, 1.3f, closed: true);
        }

        /// <summary>A dashed outline of many corners: a convex collider.</summary>
        public static void ConvexCollider(Vector2 centre, float size, Color color)
        {
            Span<Vector2> points = stackalloc Vector2[14];
            for (int i = 0; i < 7; i++)
            {
                Vector2 corner = centre + Angle(i * 360f / 7f - 90f) * size * 0.36f;
                Vector2 next = centre + Angle((i + 1) * 360f / 7f - 90f) * size * 0.36f;
                points[i * 2] = corner;
                points[i * 2 + 1] = Vector2.Lerp(corner, next, 0.5f);
            }

            Dashed(points, color, 1.3f, closed: true);
        }

        /// <summary>A triangle split into triangles: a collider shaped like a mesh.</summary>
        public static void MeshCollider(Vector2 centre, float size, Color color)
        {
            Vector2 top = centre + new Vector2(0f, -size * 0.34f);
            Vector2 left = centre + new Vector2(-size * 0.38f, size * 0.28f);
            Vector2 right = centre + new Vector2(size * 0.38f, size * 0.28f);
            Vector2 a = Vector2.Lerp(top, left, 0.5f), b = Vector2.Lerp(top, right, 0.5f), c = Vector2.Lerp(left, right, 0.5f);
            Ui.Line(top, left, color, 1.3f);
            Ui.Line(left, right, color, 1.3f);
            Ui.Line(right, top, color, 1.3f);
            Ui.Line(a, b, color, 1.1f);
            Ui.Line(b, c, color, 1.1f);
            Ui.Line(c, a, color, 1.1f);
        }

        /// <summary>A person, head and shoulders: something walked about like a player.</summary>
        public static void Character(Vector2 centre, float size, Color color)
        {
            Ui.Circle(centre + new Vector2(0f, -size * 0.17f), size * 0.12f, color);
            Canvas.DrawArc(centre + new Vector2(0f, size * 0.3f), size * 0.24f, 180f, 180f, 1.5f, Ui.Fade(color));
        }

        /// <summary>A flat square in a dashed one: a 2D box collider.</summary>
        public static void BoxCollider2D(Vector2 centre, float size, Color color)
        {
            float h = size * 0.32f;
            Span<Vector2> square = stackalloc Vector2[8];
            square[0] = centre + new Vector2(-h, -h); square[1] = centre + new Vector2(0f, -h);
            square[2] = centre + new Vector2(h, -h); square[3] = centre + new Vector2(h, 0f);
            square[4] = centre + new Vector2(h, h); square[5] = centre + new Vector2(0f, h);
            square[6] = centre + new Vector2(-h, h); square[7] = centre + new Vector2(-h, 0f);
            Dashed(square, color, 1.3f, closed: true);
            Text2D(centre, size, color);
        }

        /// <summary>A dashed circle with "2D" in it: a 2D circle collider.</summary>
        public static void CircleCollider2D(Vector2 centre, float size, Color color)
        {
            Span<Vector2> round = stackalloc Vector2[16];
            Ellipse(round, centre, size * 0.36f, size * 0.36f);
            Dashed(round, color, 1.3f, closed: true);
            Text2D(centre, size, color);
        }

        /// <summary>A dashed pill lying down with "2D" in it: a 2D capsule collider.</summary>
        public static void CapsuleCollider2D(Vector2 centre, float size, Color color)
        {
            float r = size * 0.2f, half = size * 0.16f;
            Span<Vector2> points = stackalloc Vector2[18];
            int n = 0;
            for (int i = 0; i <= 6; i++) points[n++] = centre + new Vector2(half, 0f) + Angle(-90f + i * 30f) * r;
            for (int i = 0; i <= 6; i++) points[n++] = centre + new Vector2(-half, 0f) + Angle(90f + i * 30f) * r;
            Dashed(points[..n], color, 1.3f, closed: true);
            Text2D(centre, size, color);
        }

        /// <summary>A ball on a flat line with "2D" beside it: a rigidbody kept to a flat plane.</summary>
        public static void Rigidbody2D(Vector2 centre, float size, Color color)
        {
            Vector2 ball = centre + new Vector2(-size * 0.1f, -size * 0.04f);
            Ring(ball, size * 0.18f, color);
            Ui.Line(centre + new Vector2(-size * 0.4f, size * 0.24f), centre + new Vector2(size * 0.4f, size * 0.24f), color, 1.3f);
            Ui.Text(new Rect(centre.X + size * 0.06f, centre.Y - size * 0.44f, size * 0.4f, size * 0.4f), "2D", color, 0.5f, size * 0.36f, bold: true);
        }

        private static void Text2D(Vector2 centre, float size, Color color) =>
            Ui.Text(new Rect(centre.X - size * 0.3f, centre.Y - size * 0.25f, size * 0.6f, size * 0.5f), "2D", color, 0.5f, size * 0.34f, bold: true);

        // ---------------------------------------------------------------- Joints

        /// <summary>Two blocks held by a bar: a fixed joint.</summary>
        public static void FixedJoint(Vector2 centre, float size, Color color)
        {
            float b = size * 0.2f;
            Ui.Outline(new Rect(centre.X - size * 0.42f, centre.Y - b * 0.5f, b, b), color, 1.5f, 1.3f);
            Ui.Outline(new Rect(centre.X + size * 0.42f - b, centre.Y - b * 0.5f, b, b), color, 1.5f, 1.3f);
            Ui.Line(centre + new Vector2(-size * 0.22f, 0f), centre + new Vector2(size * 0.22f, 0f), color, size * 0.1f);
        }

        /// <summary>Two leaves turning about a pin, like a door's: a hinge.</summary>
        public static void HingeJoint(Vector2 centre, float size, Color color)
        {
            float pin = size * 0.1f;
            Ring(centre, pin, color);
            Vector2 open = Vector2.Normalize(new Vector2(1f, -1f));
            Ui.Line(centre + new Vector2(-pin, 0f), centre + new Vector2(-size * 0.42f, 0f), color, size * 0.1f);
            Ui.Line(centre + open * pin, centre + open * size * 0.42f, color, size * 0.1f);
            // The way it swings, from one leaf over to the other.
            Canvas.DrawArc(centre, size * 0.26f, 190f, 115f, 1f, Ui.Fade(color.WithAlpha(0.6f)));
        }

        /// <summary>A block on a rail, free to slide: a slider joint.</summary>
        public static void SliderJoint(Vector2 centre, float size, Color color)
        {
            float half = size * 0.42f;
            Ui.Line(centre + new Vector2(-half, size * 0.16f), centre + new Vector2(half, size * 0.16f), color, 1.3f);
            Ui.Line(centre + new Vector2(-half, size * 0.06f), centre + new Vector2(-half, size * 0.26f), color, 1.3f);
            Ui.Line(centre + new Vector2(half, size * 0.06f), centre + new Vector2(half, size * 0.26f), color, 1.3f);
            Ui.Rect(new Rect(centre.X - size * 0.13f, centre.Y - size * 0.14f, size * 0.26f, size * 0.22f), color, 1.5f);
            Chevron(centre + new Vector2(-size * 0.27f, -size * 0.03f), size * 0.45f, color, 180f);
            Chevron(centre + new Vector2(size * 0.27f, -size * 0.03f), size * 0.45f, color);
        }

        /// <summary>A ball in a cup on a stem: a ball-and-socket joint.</summary>
        public static void BallJoint(Vector2 centre, float size, Color color)
        {
            Vector2 ball = centre + new Vector2(0f, -size * 0.08f);
            Ui.Circle(ball, size * 0.14f, color);
            Canvas.DrawArc(ball, size * 0.24f, 20f, 140f, 1.4f, Ui.Fade(color));
            Ui.Line(ball + new Vector2(0f, size * 0.24f), ball + new Vector2(0f, size * 0.44f), color, 1.4f);
        }

        /// <summary>Two weights on a slack rope: a distance joint, which keeps them within a reach of each other.</summary>
        public static void DistanceJoint(Vector2 centre, float size, Color color)
        {
            Vector2 left = centre + new Vector2(-size * 0.34f, -size * 0.12f), right = centre + new Vector2(size * 0.34f, -size * 0.12f);
            Span<Vector2> rope = stackalloc Vector2[9];
            for (int i = 0; i < rope.Length; i++)
            {
                float t = i / (rope.Length - 1f);
                rope[i] = Vector2.Lerp(left, right, t) + new Vector2(0f, MathF.Sin(t * MathF.PI) * size * 0.26f);
            }

            Canvas.DrawPolyline(rope, 1.3f, Ui.Fade(color));
            Ui.Circle(left, size * 0.09f, color);
            Ui.Circle(right, size * 0.09f, color);
        }

        /// <summary>A coil between two weights: a spring joint.</summary>
        public static void SpringJoint(Vector2 centre, float size, Color color)
        {
            Vector2 left = centre + new Vector2(-size * 0.36f, 0f), right = centre + new Vector2(size * 0.36f, 0f);
            Span<Vector2> coil = stackalloc Vector2[10];
            for (int i = 0; i < coil.Length; i++)
            {
                float t = i / (coil.Length - 1f);
                float lift = i == 0 || i == coil.Length - 1 ? 0f : (i % 2 == 0 ? -1f : 1f) * size * 0.16f;
                coil[i] = Vector2.Lerp(left + new Vector2(size * 0.1f, 0f), right - new Vector2(size * 0.1f, 0f), t) + new Vector2(0f, lift);
            }

            Canvas.DrawPolyline(coil, 1.3f, Ui.Fade(color));
            Ui.Circle(left, size * 0.09f, color);
            Ui.Circle(right, size * 0.09f, color);
        }

        // ---------------------------------------------------------------- Sound

        /// <summary>A loudspeaker giving off waves: something that plays sound.</summary>
        public static void Speaker(Vector2 centre, float size, Color color)
        {
            Vector2 o = centre + new Vector2(-size * 0.12f, 0f);
            Ui.Rect(new Rect(o.X - size * 0.24f, o.Y - size * 0.1f, size * 0.12f, size * 0.2f), color, 1f);
            Canvas.DrawQuad(o + new Vector2(-size * 0.12f, -size * 0.1f), o + new Vector2(size * 0.06f, -size * 0.26f),
                o + new Vector2(size * 0.06f, size * 0.26f), o + new Vector2(-size * 0.12f, size * 0.1f), Ui.Fade(color));
            Canvas.DrawArc(o + new Vector2(size * 0.06f, 0f), size * 0.16f, -45f, 90f, 1.3f, Ui.Fade(color));
            Canvas.DrawArc(o + new Vector2(size * 0.06f, 0f), size * 0.3f, -45f, 90f, 1.3f, Ui.Fade(color));
        }

        /// <summary>Headphones: where sound is heard from.</summary>
        public static void Headphones(Vector2 centre, float size, Color color)
        {
            float r = size * 0.3f;
            Canvas.DrawArc(centre + new Vector2(0f, size * 0.06f), r, 180f, 180f, 1.5f, Ui.Fade(color));
            Ui.Rect(new Rect(centre.X - r - size * 0.07f, centre.Y + size * 0.02f, size * 0.15f, size * 0.26f), color, 2f);
            Ui.Rect(new Rect(centre.X + r - size * 0.08f, centre.Y + size * 0.02f, size * 0.15f, size * 0.26f), color, 2f);
        }

        // ---------------------------------------------------------------- Effects and pictures

        /// <summary>Specks thrown up from a point: particles.</summary>
        public static void Particles(Vector2 centre, float size, Color color)
        {
            Vector2 from = centre + new Vector2(0f, size * 0.38f);
            Ui.Line(from + new Vector2(-size * 0.14f, 0f), from + new Vector2(size * 0.14f, 0f), color, 1.6f);
            (float X, float Y, float R)[] specks = [(-0.26f, 0.02f, 0.08f), (-0.1f, -0.26f, 0.1f), (0.16f, -0.34f, 0.07f), (0.28f, -0.06f, 0.1f), (0.04f, 0.04f, 0.065f)];
            foreach ((float x, float y, float r) in specks) Ui.Circle(centre + new Vector2(x, y) * size, r * size, color);
        }

        /// <summary>A four-pointed star and a small one: a visual effect.</summary>
        public static void Sparkle(Vector2 centre, float size, Color color)
        {
            void Star(Vector2 at, float r)
            {
                float w = r * 0.36f;
                Canvas.DrawQuad(at + new Vector2(0f, -r), at + new Vector2(w, 0f), at + new Vector2(0f, r), at + new Vector2(-w, 0f), Ui.Fade(color));
                Canvas.DrawQuad(at + new Vector2(-r, 0f), at + new Vector2(0f, -w), at + new Vector2(r, 0f), at + new Vector2(0f, w), Ui.Fade(color));
            }

            Star(centre + new Vector2(-size * 0.08f, size * 0.07f), size * 0.36f);
            Star(centre + new Vector2(size * 0.27f, -size * 0.26f), size * 0.17f);
        }

        /// <summary>Three drifting wavy lines: fog.</summary>
        public static void Fog(Vector2 centre, float size, Color color)
        {
            const int steps = 10;
            for (int row = 0; row < 3; row++)
            {
                float y = centre.Y + (row - 1) * size * 0.22f;
                float left = centre.X - size * 0.36f + (row == 1 ? size * 0.1f : 0f);
                float width = size * (row == 1 ? 0.62f : 0.6f);
                Vector2 previous = default;
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    var point = new Vector2(left + width * t, y + MathF.Sin(t * MathF.Tau + row * 1.3f) * size * 0.05f);
                    if (i > 0) Ui.Line(previous, point, color, 1.6f);
                    previous = point;
                }
            }
        }

        /// <summary>A frame with a small figure in it: a flat picture placed in the world, a sprite.</summary>
        public static void Sprite(Vector2 centre, float size, Color color)
        {
            float h = size * 0.36f;
            Ui.Outline(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, 3f, 1.3f);
            Ui.Circle(centre + new Vector2(0f, -size * 0.1f), size * 0.08f, color);
            Ui.Rect(new Rect(centre.X - size * 0.13f, centre.Y + size * 0.0f, size * 0.26f, size * 0.2f), color, 3f);
        }

        /// <summary>Frames on a strip of film: something that plays a sprite's frames one after another.</summary>
        public static void Animation(Vector2 centre, float size, Color color)
        {
            var strip = new Rect(centre.X - size * 0.4f, centre.Y - size * 0.26f, size * 0.8f, size * 0.52f);
            Ui.Outline(strip, color, 2f, 1.3f);
            for (int i = 0; i < 3; i++)
            {
                float x = strip.X + size * 0.07f + i * size * 0.24f;
                Ui.Rect(new Rect(x, strip.Y + size * 0.11f, size * 0.18f, size * 0.3f), color.WithAlpha(i == 1 ? 1f : 0.45f), 1.5f);
            }
        }

        /// <summary>A "T" standing on a slab: text placed in the world.</summary>
        public static void WorldText(Vector2 centre, float size, Color color)
        {
            Letter(centre + new Vector2(0f, -size * 0.06f), size * 0.86f, color);
            Ui.Line(centre + new Vector2(-size * 0.36f, size * 0.36f), centre + new Vector2(size * 0.36f, size * 0.36f), color, 1.3f);
        }

        // ---------------------------------------------------------------- Networking

        /// <summary>Three machines joined up: a network game.</summary>
        public static void Network(Vector2 centre, float size, Color color)
        {
            Vector2 top = centre + new Vector2(0f, -size * 0.28f);
            Vector2 left = centre + new Vector2(-size * 0.3f, size * 0.22f), right = centre + new Vector2(size * 0.3f, size * 0.22f);
            Ui.Line(top, left, color, 1.2f);
            Ui.Line(top, right, color, 1.2f);
            Ui.Line(left, right, color, 1.2f);
            foreach (Vector2 machine in (ReadOnlySpan<Vector2>)[top, left, right]) Ui.Circle(machine, size * 0.1f, color);
        }

        /// <summary>A cube giving off a signal: an object shared over the network.</summary>
        public static void NetworkObject(Vector2 centre, float size, Color color)
        {
            Cube(centre + new Vector2(-size * 0.08f, size * 0.08f), size * 0.8f, color);
            Vector2 at = centre + new Vector2(size * 0.2f, -size * 0.2f);
            Canvas.DrawArc(at - new Vector2(size * 0.06f, -size * 0.06f), size * 0.14f, -90f, 90f, 1.2f, Ui.Fade(color));
            Canvas.DrawArc(at - new Vector2(size * 0.06f, -size * 0.06f), size * 0.26f, -90f, 90f, 1.2f, Ui.Fade(color));
        }

        /// <summary>Two arrows going round after each other: kept in step over the network.</summary>
        public static void Sync(Vector2 centre, float size, Color color)
        {
            float r = size * 0.28f;
            for (int half = 0; half < 2; half++)
            {
                float start = half * 180f - 60f;
                Canvas.DrawArc(centre, r, start, 130f, 1.4f, Ui.Fade(color));
                float end = (start + 130f) * MathF.PI / 180f;
                var radial = new Vector2(MathF.Cos(end), MathF.Sin(end));
                var along = new Vector2(-radial.Y, radial.X);
                Vector2 at = centre + radial * r;
                Canvas.DrawTriangle(at + along * size * 0.13f, at + radial * size * 0.1f, at - radial * size * 0.1f, Ui.Fade(color));
            }
        }

        /// <summary>A map pin: a place players start from.</summary>
        public static void Pin(Vector2 centre, float size, Color color)
        {
            Vector2 head = centre + new Vector2(0f, -size * 0.1f);
            float r = size * 0.22f;
            Canvas.DrawArc(head, r, 150f, 240f, 1.4f, Ui.Fade(color));
            Vector2 tip = centre + new Vector2(0f, size * 0.38f);
            Ui.Line(head + Angle(150f) * r, tip, color, 1.4f);
            Ui.Line(head + Angle(30f) * r, tip, color, 1.4f);
            Ui.Circle(head, size * 0.07f, color);
        }

        // ---------------------------------------------------------------- Scripts

        /// <summary>"&lt;/&gt;": a script written in code.</summary>
        public static void Code(Vector2 centre, float size, Color color)
        {
            float h = size * 0.22f;
            Vector2 left = centre + new Vector2(-size * 0.2f, 0f), right = centre + new Vector2(size * 0.2f, 0f);
            Ui.Line(left + new Vector2(-h * 0.8f, 0f), left + new Vector2(0f, -h), color, 1.5f);
            Ui.Line(left + new Vector2(-h * 0.8f, 0f), left + new Vector2(0f, h), color, 1.5f);
            Ui.Line(right + new Vector2(h * 0.8f, 0f), right + new Vector2(0f, -h), color, 1.5f);
            Ui.Line(right + new Vector2(h * 0.8f, 0f), right + new Vector2(0f, h), color, 1.5f);
            Ui.Line(centre + new Vector2(size * 0.07f, -size * 0.3f), centre + new Vector2(-size * 0.07f, size * 0.3f), color, 1.3f);
        }

        // ---------------------------------------------------------------- UI

        /// <summary>A rectangle with handles at its corners: where a piece of UI goes.</summary>
        public static void UIRect(Vector2 centre, float size, Color color)
        {
            var frame = new Rect(centre.X - size * 0.34f, centre.Y - size * 0.26f, size * 0.68f, size * 0.52f);
            Ui.Outline(frame, color.WithAlpha(0.7f), 0f, 1.1f);
            float h = size * 0.12f;
            foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[frame.Position, new Vector2(frame.Right, frame.Y), new Vector2(frame.Right, frame.Bottom), new Vector2(frame.X, frame.Bottom)])
            {
                Ui.Rect(new Rect(corner.X - h * 0.5f, corner.Y - h * 0.5f, h, h), color, 1f);
            }
        }

        /// <summary>A filled rounded rectangle: a panel.</summary>
        public static void Panel(Vector2 centre, float size, Color color)
        {
            var frame = new Rect(centre.X - size * 0.36f, centre.Y - size * 0.28f, size * 0.72f, size * 0.56f);
            Ui.Rect(frame, color.WithAlpha(0.35f), size * 0.1f);
            Ui.Outline(frame, color, size * 0.1f, 1.3f);
        }

        /// <summary>The letter T, drawn: text.</summary>
        public static void Letter(Vector2 centre, float size, Color color)
        {
            float h = size * 0.3f;
            Ui.Line(centre + new Vector2(-h, -h), centre + new Vector2(h, -h), color, 1.8f);
            Ui.Line(centre + new Vector2(0f, -h), centre + new Vector2(0f, h), color, 1.8f);
            Ui.Line(centre + new Vector2(-h * 0.4f, h), centre + new Vector2(h * 0.4f, h), color, 1.3f);
        }

        /// <summary>A rounded button with a pointer on it.</summary>
        public static void Button(Vector2 centre, float size, Color color)
        {
            var pill = new Rect(centre.X - size * 0.4f, centre.Y - size * 0.22f, size * 0.8f, size * 0.36f);
            Ui.Outline(pill, color, size * 0.18f, 1.3f);
            Vector2 tip = centre + new Vector2(size * 0.04f, -size * 0.04f);
            Canvas.DrawTriangle(tip, tip + new Vector2(size * 0.08f, size * 0.36f), tip + new Vector2(size * 0.28f, size * 0.2f), Ui.Fade(color));
        }

        /// <summary>A box with a tick in it: a toggle.</summary>
        public static void Toggle(Vector2 centre, float size, Color color)
        {
            float h = size * 0.3f;
            Ui.Outline(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, size * 0.08f, 1.3f);
            Check(centre, size * 0.9f, color);
        }

        /// <summary>A track with a knob on it: a slider.</summary>
        public static void Slider(Vector2 centre, float size, Color color)
        {
            float half = size * 0.38f;
            Ui.Line(centre + new Vector2(-half, 0f), centre + new Vector2(size * 0.06f, 0f), color, 2.2f);
            Ui.Line(centre + new Vector2(size * 0.06f, 0f), centre + new Vector2(half, 0f), color.WithAlpha(0.45f), 1.4f);
            Ui.Circle(centre + new Vector2(size * 0.06f, 0f), size * 0.13f, color);
        }

        /// <summary>A bar part filled: how far along something is.</summary>
        public static void ProgressBar(Vector2 centre, float size, Color color)
        {
            var bar = new Rect(centre.X - size * 0.4f, centre.Y - size * 0.12f, size * 0.8f, size * 0.24f);
            Ui.Outline(bar, color, size * 0.08f, 1.3f);
            Ui.Rect(new Rect(bar.X + 2.5f, bar.Y + 2.5f, (bar.Width - 5f) * 0.6f, bar.Height - 5f), color, size * 0.05f);
        }

        /// <summary>A box with a text cursor in it: somewhere to type.</summary>
        public static void TextBox(Vector2 centre, float size, Color color)
        {
            var box = new Rect(centre.X - size * 0.4f, centre.Y - size * 0.2f, size * 0.8f, size * 0.4f);
            Ui.Outline(box, color, size * 0.06f, 1.3f);
            float x = box.X + size * 0.2f;
            Ui.Line(new Vector2(x, box.Y + size * 0.09f), new Vector2(x, box.Bottom - size * 0.09f), color, 1.4f);
            Ui.Line(new Vector2(x + size * 0.1f, centre.Y), new Vector2(box.Right - size * 0.1f, centre.Y), color.WithAlpha(0.45f), 1.2f);
        }

        /// <summary>Three bars one under another: lays its children out in a column.</summary>
        public static void Column(Vector2 centre, float size, Color color)
        {
            for (int i = -1; i <= 1; i++) Ui.Rect(new Rect(centre.X - size * 0.32f, centre.Y + i * size * 0.24f - size * 0.08f, size * 0.64f, size * 0.16f), color, 1.5f);
        }

        /// <summary>Three bars side by side: lays its children out in a row.</summary>
        public static void Row(Vector2 centre, float size, Color color)
        {
            for (int i = -1; i <= 1; i++) Ui.Rect(new Rect(centre.X + i * size * 0.24f - size * 0.08f, centre.Y - size * 0.32f, size * 0.16f, size * 0.64f), color, 1.5f);
        }

        /// <summary>Four squares in a grid: lays its children out in a grid.</summary>
        public static void GridLayout(Vector2 centre, float size, Color color)
        {
            float s = size * 0.28f, gap = size * 0.08f;
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    Ui.Rect(new Rect(centre.X - s - gap * 0.5f + column * (s + gap), centre.Y - s - gap * 0.5f + row * (s + gap), s, s), color, 1.5f);
                }
            }
        }

        /// <summary>A frame with only part of a circle showing through: hides what's outside it.</summary>
        public static void Mask(Vector2 centre, float size, Color color)
        {
            var frame = new Rect(centre.X - size * 0.3f, centre.Y - size * 0.3f, size * 0.6f, size * 0.6f);
            Ui.Outline(frame, color, 2f, 1.3f);
            Canvas.DrawArc(frame.Position + new Vector2(frame.Width, frame.Height), size * 0.38f, 180f, 90f, size * 0.12f, Ui.Fade(color.WithAlpha(0.6f)));
        }

        /// <summary>A rectangle with arrows out to its sides: keeps UI the same size on any screen.</summary>
        public static void Fit(Vector2 centre, float size, Color color)
        {
            float h = size * 0.16f;
            Ui.Outline(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, 1.5f, 1.2f);
            for (int i = 0; i < 4; i++)
            {
                Vector2 direction = Angle(i * 90f);
                Ui.Line(centre + direction * size * 0.24f, centre + direction * size * 0.42f, color, 1.2f);
            }
        }

        private static Vector2 Angle(float degrees)
        {
            float radians = degrees * MathF.PI / 180f;
            return new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        }
    }
}
