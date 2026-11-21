using System.Numerics;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Small icons drawn from lines and circles, so they're sharp at any size and take their colour from the theme.
    /// Each fits a square <paramref name="size"/> UI units across around its centre.
    /// </summary>
    public static partial class Icons
    {
        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = MathF.Cos(radians), s = MathF.Sin(radians);
            return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
        }

        /// <summary>
        /// An arrowhead pointing right, turned <paramref name="angle"/> degrees clockwise: 90 points down. Foldouts turn
        /// it from 0 to 90 as they open.
        /// </summary>
        public static void Chevron(Vector2 centre, float size, Color color, float angle = 0f)
        {
            float radians = angle * MathF.PI / 180f;
            float h = size * 0.22f;
            Vector2 a = centre + Rotate(new Vector2(-h * 0.6f, -h * 1.6f), radians);
            Vector2 b = centre + Rotate(new Vector2(h * 0.9f, 0f), radians);
            Vector2 c = centre + Rotate(new Vector2(-h * 0.6f, h * 1.6f), radians);
            Ui.Line(a, b, color, 1.5f);
            Ui.Line(b, c, color, 1.5f);
            Ui.Circle(b, 0.75f, color);
        }

        public static void Plus(Vector2 centre, float size, Color color)
        {
            float h = size * 0.32f;
            Ui.Line(centre - new Vector2(h, 0f), centre + new Vector2(h, 0f), color, 1.5f);
            Ui.Line(centre - new Vector2(0f, h), centre + new Vector2(0f, h), color, 1.5f);
        }

        public static void Close(Vector2 centre, float size, Color color)
        {
            float h = size * 0.25f;
            Ui.Line(centre + new Vector2(-h, -h), centre + new Vector2(h, h), color, 1.5f);
            Ui.Line(centre + new Vector2(h, -h), centre + new Vector2(-h, h), color, 1.5f);
        }

        public static void Search(Vector2 centre, float size, Color color)
        {
            float r = size * 0.26f;
            Vector2 lens = centre + new Vector2(-size * 0.06f, -size * 0.06f);
            Vector2 edge = lens + new Vector2(r * 0.72f, r * 0.72f);
            Ring(lens, r, color);
            Ui.Line(edge, centre + new Vector2(size * 0.32f, size * 0.32f), color, 1.6f);
        }

        public static void Check(Vector2 centre, float size, Color color, float progress = 1f)
        {
            if (progress <= 0f) return;

            Vector2 a = centre + new Vector2(-size * 0.28f, size * 0.02f);
            Vector2 b = centre + new Vector2(-size * 0.08f, size * 0.22f);
            Vector2 c = centre + new Vector2(size * 0.3f, -size * 0.2f);

            // Drawn on stroke by stroke as it appears.
            float first = Math.Clamp(progress / 0.4f, 0f, 1f);
            float second = Math.Clamp((progress - 0.4f) / 0.6f, 0f, 1f);
            Ui.Line(a, Vector2.Lerp(a, b, first), color, 1.8f);
            if (second > 0f) Ui.Line(b, Vector2.Lerp(b, c, second), color, 1.8f);
        }

        /// <summary>Three dots in a row: "more options".</summary>
        public static void More(Vector2 centre, float size, Color color)
        {
            float gap = size * 0.24f;
            for (int i = -1; i <= 1; i++) Ui.Circle(centre + new Vector2(gap * i, 0f), 1.4f, color);
        }

        /// <summary>A cube seen from a corner: an object with a mesh.</summary>
        public static void Cube(Vector2 centre, float size, Color color)
        {
            float r = size * 0.36f;
            Vector2 Corner(int i)
            {
                float a = (i * 60f - 90f) * MathF.PI / 180f;
                return centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }

            for (int i = 0; i < 6; i++) Ui.Line(Corner(i), Corner(i + 1), color, 1.2f);
            Ui.Line(centre, Corner(1), color, 1.2f);
            Ui.Line(centre, Corner(3), color, 1.2f);
            Ui.Line(centre, Corner(5), color, 1.2f);
        }

        /// <summary>A solid cube seen from a corner: a prefab, an object saved to be copied.</summary>
        /// <summary>Two nodes joined by a wire: a visual script, or any node graph.</summary>
        public static void Graph(Vector2 centre, float size, Color color)
        {
            float box = size * 0.26f;
            var left = new Vector2(centre.X - size * 0.3f, centre.Y - size * 0.16f);
            var right = new Vector2(centre.X + size * 0.3f, centre.Y + size * 0.16f);
            Ui.Rect(new Rect(left.X - box * 0.5f, left.Y - box * 0.5f, box, box), color, box * 0.25f);
            Ui.Rect(new Rect(right.X - box * 0.5f, right.Y - box * 0.5f, box, box), color, box * 0.25f);

            // An S of a wire from one to the other.
            Vector2 from = left + new Vector2(box * 0.5f, 0f), to = right - new Vector2(box * 0.5f, 0f);
            Span<Vector2> points = stackalloc Vector2[9];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (points.Length - 1f);
                float s = t * t * (3f - 2f * t);
                points[i] = new Vector2(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * s);
            }

            Vertex.UI.Canvas.DrawPolyline(points, MathF.Max(size * 0.09f, 1.3f), Ui.Fade(color));
        }

        public static void Prefab(Vector2 centre, float size, Color color)
        {
            float r = size * 0.38f;
            Vector2 Corner(int i)
            {
                float a = (i * 60f - 90f) * MathF.PI / 180f;
                return centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }

            // Three faces, lit differently, so it reads as solid.
            Vertex.UI.Canvas.DrawQuad(Corner(0), Corner(1), centre, Corner(5), Ui.Fade(color));
            Vertex.UI.Canvas.DrawQuad(centre, Corner(1), Corner(2), Corner(3), Ui.Fade(color * 0.75f));
            Vertex.UI.Canvas.DrawQuad(Corner(5), centre, Corner(3), Corner(4), Ui.Fade(color * 0.55f));
        }

        /// <summary>A window with a title bar: a piece of UI.</summary>
        public static void UI(Vector2 centre, float size, Color color)
        {
            var frame = new Rect(centre.X - size * 0.36f, centre.Y - size * 0.3f, size * 0.72f, size * 0.6f);
            Ui.Outline(frame, color, 2f, 1.2f);
            Ui.Line(new Vector2(frame.X, frame.Y + size * 0.18f), new Vector2(frame.Right, frame.Y + size * 0.18f), color, 1.2f);
        }

        /// <summary>A prompt and a line of text: the Console.</summary>
        public static void Console(Vector2 centre, float size, Color color)
        {
            var frame = new Rect(centre.X - size * 0.4f, centre.Y - size * 0.32f, size * 0.8f, size * 0.64f);
            Ui.Outline(frame, color, 2f, 1.2f);
            Vector2 at = new(frame.X + size * 0.16f, centre.Y);
            Ui.Line(at + new Vector2(0f, -size * 0.1f), at + new Vector2(size * 0.1f, 0f), color, 1.2f);
            Ui.Line(at + new Vector2(size * 0.1f, 0f), at + new Vector2(0f, size * 0.1f), color, 1.2f);
            Ui.Line(at + new Vector2(size * 0.18f, size * 0.11f), at + new Vector2(size * 0.4f, size * 0.11f), color, 1.2f);
        }

        /// <summary>Lines under one another, indented like a tree: the outliner.</summary>
        public static void Tree(Vector2 centre, float size, Color color)
        {
            float left = centre.X - size * 0.34f, right = centre.X + size * 0.34f, step = size * 0.24f;
            Ui.Line(new Vector2(left, centre.Y - step), new Vector2(right, centre.Y - step), color, 1.3f);
            Ui.Line(new Vector2(left + size * 0.18f, centre.Y), new Vector2(right, centre.Y), color, 1.3f);
            Ui.Line(new Vector2(left + size * 0.18f, centre.Y + step), new Vector2(right, centre.Y + step), color, 1.3f);
        }

        /// <summary>Two sliders: the inspector, where values are changed.</summary>
        public static void Sliders(Vector2 centre, float size, Color color)
        {
            float left = centre.X - size * 0.34f, right = centre.X + size * 0.34f, step = size * 0.17f;
            Ui.Line(new Vector2(left, centre.Y - step), new Vector2(right, centre.Y - step), color, 1.2f);
            Ui.Line(new Vector2(left, centre.Y + step), new Vector2(right, centre.Y + step), color, 1.2f);
            Ui.Circle(new Vector2(centre.X - size * 0.1f, centre.Y - step), size * 0.09f, color);
            Ui.Circle(new Vector2(centre.X + size * 0.16f, centre.Y + step), size * 0.09f, color);
        }

        /// <summary>A sun: a light.</summary>
        public static void Light(Vector2 centre, float size, Color color)
        {
            float r = size * 0.17f;
            Ui.Circle(centre, r, color);
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4f;
                var direction = new Vector2(MathF.Cos(a), MathF.Sin(a));
                Ui.Line(centre + direction * (r + size * 0.08f), centre + direction * (r + size * 0.2f), color, 1.2f);
            }
        }

        /// <summary>A film camera from the side: a camera.</summary>
        public static void Camera(Vector2 centre, float size, Color color)
        {
            var body = new Rect(centre.X - size * 0.36f, centre.Y - size * 0.2f, size * 0.46f, size * 0.4f);
            Ui.Outline(body, color, 2f, 1.2f);
            Vector2 lens = new(body.Right, centre.Y);
            Ui.Line(lens + new Vector2(0f, -size * 0.06f), lens + new Vector2(size * 0.24f, -size * 0.18f), color, 1.2f);
            Ui.Line(lens + new Vector2(0f, size * 0.06f), lens + new Vector2(size * 0.24f, size * 0.18f), color, 1.2f);
            Ui.Line(lens + new Vector2(size * 0.24f, -size * 0.18f), lens + new Vector2(size * 0.24f, size * 0.18f), color, 1.2f);
        }

        /// <summary>An empty ring: an object with nothing but a transform.</summary>
        public static void Empty(Vector2 centre, float size, Color color) => Ring(centre, size * 0.22f, color);

        /// <summary>A globe: the scene itself.</summary>
        public static void Scene(Vector2 centre, float size, Color color)
        {
            float r = size * 0.34f;
            Ring(centre, r, color);
            Ui.Line(centre - new Vector2(r, 0f), centre + new Vector2(r, 0f), color, 1.1f);
            Vertex.UI.Canvas.DrawArc(centre, r * 0.5f, -90f, 180f, 1.1f, Ui.Fade(color));
            Vertex.UI.Canvas.DrawArc(centre, r * 0.5f, 90f, 180f, 1.1f, Ui.Fade(color));
        }

        /// <summary>A gear: a script or any other component.</summary>
        public static void Component(Vector2 centre, float size, Color color)
        {
            float r = size * 0.22f;
            Ring(centre, r, color);
            for (int i = 0; i < 6; i++)
            {
                float a = i * MathF.PI / 3f;
                var direction = new Vector2(MathF.Cos(a), MathF.Sin(a));
                Ui.Line(centre + direction * r, centre + direction * (r + size * 0.13f), color, 2f);
            }
        }

        /// <summary>Crossed arrows: the move tool.</summary>
        public static void Move(Vector2 centre, float size, Color color)
        {
            float r = size * 0.36f;
            Ui.Line(centre - new Vector2(r, 0f), centre + new Vector2(r, 0f), color, 1.3f);
            Ui.Line(centre - new Vector2(0f, r), centre + new Vector2(0f, r), color, 1.3f);
            for (int i = 0; i < 4; i++) Chevron(centre + Rotate(new Vector2(r - size * 0.05f, 0f), i * MathF.PI / 2f), size * 0.5f, color, i * 90f);
        }

        /// <summary>A circling arrow: the rotate tool.</summary>
        public static void Rotate(Vector2 centre, float size, Color color)
        {
            // Most of a circle, with an arrowhead where it ends pointing on round.
            float r = size * 0.28f;
            const float start = -40f, sweep = 270f;
            Vertex.UI.Canvas.DrawArc(centre, r, start, sweep, 1.5f, Ui.Fade(color));

            float end = (start + sweep) * MathF.PI / 180f;
            var radial = new Vector2(MathF.Cos(end), MathF.Sin(end));
            var along = new Vector2(-radial.Y, radial.X);
            Vector2 at = centre + radial * r;
            Vertex.UI.Canvas.DrawTriangle(at + along * size * 0.16f, at + radial * size * 0.13f - along * size * 0.02f,
                at - radial * size * 0.13f - along * size * 0.02f, Ui.Fade(color));
        }

        /// <summary>A box with a corner pulled out: the scale tool.</summary>
        public static void Scale(Vector2 centre, float size, Color color)
        {
            float r = size * 0.32f;
            Ui.Outline(new Rect(centre.X - r, centre.Y - r * 0.2f, r * 1.2f, r * 1.2f), color, 1.5f, 1.3f);
            Ui.Line(centre + new Vector2(-r * 0.1f, r * 0.1f), centre + new Vector2(r, -r), color, 1.3f);
            Ui.Line(centre + new Vector2(r * 0.35f, -r), centre + new Vector2(r, -r), color, 1.3f);
            Ui.Line(centre + new Vector2(r, -r * 0.35f), centre + new Vector2(r, -r), color, 1.3f);
        }

        /// <summary>A small grid: the scene view's ground grid.</summary>
        public static void Grid(Vector2 centre, float size, Color color)
        {
            float h = size * 0.34f;
            for (int i = -1; i <= 1; i += 2)
            {
                float o = h * i / 3f;
                Ui.Line(centre + new Vector2(o, -h), centre + new Vector2(o, h), color, 1.2f);
                Ui.Line(centre + new Vector2(-h, o), centre + new Vector2(h, o), color, 1.2f);
            }

            Ui.Outline(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, 2f, 1.2f);
        }

        public static void Undo(Vector2 centre, float size, Color color)
        {
            float r = size * 0.26f;
            Vertex.UI.Canvas.DrawArc(centre + new Vector2(size * 0.04f, size * 0.04f), r, 180f, 200f, 1.4f, Ui.Fade(color));
            Chevron(centre + new Vector2(-r + size * 0.04f, 0f), size * 0.5f, color, -90f);
        }

        public static void Redo(Vector2 centre, float size, Color color)
        {
            float r = size * 0.26f;
            Vertex.UI.Canvas.DrawArc(centre + new Vector2(-size * 0.04f, size * 0.04f), r, -20f, -200f, 1.4f, Ui.Fade(color));
            Chevron(centre + new Vector2(r - size * 0.04f, 0f), size * 0.5f, color, -90f);
        }

        /// <summary>One line fanning out into three: a port split into its parts.</summary>
        public static void Split(Vector2 centre, float size, Color color) => Fan(centre, size, color, -1f);

        /// <summary>Three lines coming together into one: parts joined back into a whole.</summary>
        public static void Join(Vector2 centre, float size, Color color) => Fan(centre, size, color, 1f);

        // Three ends on one side, meeting at a point and going on as one; facing left (joining) or right (splitting).
        private static void Fan(Vector2 centre, float size, Color color, float facing)
        {
            float w = size * 0.32f, h = size * 0.26f;
            Vector2 meet = centre + new Vector2(facing * w * 0.15f, 0f);
            Vector2 single = centre + new Vector2(facing * w, 0f);
            Ui.Line(meet, single, color, 1.4f);
            foreach (float y in (ReadOnlySpan<float>)[-h, 0f, h])
            {
                Vector2 end = centre + new Vector2(-facing * w, y);
                Ui.Line(end, meet, color, 1.4f);
                Ui.Circle(end, size * 0.06f, color);
            }
        }

        public static void Trash(Vector2 centre, float size, Color color)
        {
            float w = size * 0.26f;
            Ui.Line(centre + new Vector2(-w * 1.25f, -size * 0.22f), centre + new Vector2(w * 1.25f, -size * 0.22f), color, 1.3f);
            Ui.Line(centre + new Vector2(-w * 0.4f, -size * 0.3f), centre + new Vector2(w * 0.4f, -size * 0.3f), color, 1.3f);
            Ui.Outline(new Rect(centre.X - w, centre.Y - size * 0.16f, w * 2f, size * 0.46f), color, 2f, 1.3f);
        }

        public static void Folder(Vector2 centre, float size, Color color)
        {
            float w = size * 0.42f, h = size * 0.3f;
            var body = new Rect(centre.X - w, centre.Y - h * 0.75f, w * 2f, h * 1.85f);
            Ui.Rect(new Rect(body.X, body.Y - size * 0.1f, w * 0.85f, size * 0.2f), color, 2f);
            Ui.Rect(body, color, 2.5f);
        }

        /// <summary>A picture: a frame with a hill and a sun.</summary>
        public static void Image(Vector2 centre, float size, Color color)
        {
            float w = size * 0.4f, h = size * 0.32f;
            Ui.Outline(new Rect(centre.X - w, centre.Y - h, w * 2f, h * 2f), color, 2f, 1.3f);
            Ui.Line(centre + new Vector2(-w * 0.7f, h * 0.6f), centre + new Vector2(-w * 0.15f, -h * 0.1f), color, 1.3f);
            Ui.Line(centre + new Vector2(-w * 0.15f, -h * 0.1f), centre + new Vector2(w * 0.7f, h * 0.6f), color, 1.3f);
            Ui.Circle(centre + new Vector2(w * 0.45f, -h * 0.45f), size * 0.06f, color);
        }

        /// <summary>A sound wave.</summary>
        public static void Sound(Vector2 centre, float size, Color color)
        {
            float[] heights = [0.3f, 0.65f, 1f, 0.55f, 0.8f, 0.4f];
            for (int i = 0; i < heights.Length; i++)
            {
                float x = centre.X + (i - 2.5f) * size * 0.14f;
                float h = heights[i] * size * 0.32f;
                Ui.Line(new Vector2(x, centre.Y - h), new Vector2(x, centre.Y + h), color, 1.6f);
            }
        }

        /// <summary>A page with a folded corner: any other file.</summary>
        public static void File(Vector2 centre, float size, Color color)
        {
            float w = size * 0.3f, h = size * 0.4f, fold = size * 0.14f;
            Vector2 tl = centre + new Vector2(-w, -h), tr = centre + new Vector2(w - fold, -h), corner = centre + new Vector2(w, -h + fold);
            Vector2 br = centre + new Vector2(w, h), bl = centre + new Vector2(-w, h);
            Ui.Line(tl, tr, color, 1.3f);
            Ui.Line(tr, corner, color, 1.3f);
            Ui.Line(corner, br, color, 1.3f);
            Ui.Line(br, bl, color, 1.3f);
            Ui.Line(bl, tl, color, 1.3f);
        }

        public static void Play(Vector2 centre, float size, Color color)
        {
            float h = size * 0.3f;
            Vertex.UI.Canvas.DrawTriangle(centre + new Vector2(-h * 0.8f, -h), centre + new Vector2(h * 1.05f, 0f), centre + new Vector2(-h * 0.8f, h), Ui.Fade(color));
        }

        public static void Pause(Vector2 centre, float size, Color color)
        {
            float h = size * 0.28f, w = size * 0.1f;
            Ui.Rect(new Rect(centre.X - w * 2.2f, centre.Y - h, w * 1.6f, h * 2f), color, 1f);
            Ui.Rect(new Rect(centre.X + w * 0.6f, centre.Y - h, w * 1.6f, h * 2f), color, 1f);
        }

        /// <summary>A round dot: recording.</summary>
        public static void Record(Vector2 centre, float size, Color color) => Ui.Circle(centre, size * 0.26f, color);

        /// <summary>A closed box with a band round it: a game built to hand to players.</summary>
        public static void Package(Vector2 centre, float size, Color color)
        {
            float w = size * 0.34f, h = size * 0.3f, lid = size * 0.12f;
            Vector2 topLeft = centre + new Vector2(-w, -h), topRight = centre + new Vector2(w, -h);
            Vector2 bottomLeft = centre + new Vector2(-w, h), bottomRight = centre + new Vector2(w, h);
            Ui.Line(topLeft, topRight, color, 1.3f);
            Ui.Line(topRight, bottomRight, color, 1.3f);
            Ui.Line(bottomRight, bottomLeft, color, 1.3f);
            Ui.Line(bottomLeft, topLeft, color, 1.3f);
            Ui.Line(topLeft + new Vector2(0f, lid), topRight + new Vector2(0f, lid), color, 1.3f);
            Ui.Rect(new Rect(centre.X - size * 0.06f, centre.Y - h, size * 0.12f, h + lid), color, 1f);
        }

        /// <summary>An S-shaped curve between two points: an animation's values drawn as curves.</summary>
        public static void Curve(Vector2 centre, float size, Color color)
        {
            Vector2 start = centre + new Vector2(-size * 0.36f, size * 0.26f), end = centre + new Vector2(size * 0.36f, -size * 0.26f);
            Vector2 previous = start;
            for (int i = 1; i <= 10; i++)
            {
                float t = i / 10f;
                float eased = t * t * (3f - 2f * t);
                var point = new Vector2(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * eased);
                Ui.Line(previous, point, color, 1.4f);
                previous = point;
            }

            Ui.Circle(start, size * 0.07f, color);
            Ui.Circle(end, size * 0.07f, color);
        }

        /// <summary>Boxes joined by an arrow: a state machine, its states and the ways between them.</summary>
        public static void States(Vector2 centre, float size, Color color)
        {
            var a = new Rect(centre.X - size * 0.42f, centre.Y - size * 0.32f, size * 0.36f, size * 0.24f);
            var b = new Rect(centre.X + size * 0.06f, centre.Y + size * 0.08f, size * 0.36f, size * 0.24f);
            Ui.Outline(a, color, 2f, 1.2f);
            Ui.Rect(b, color, 2f);
            Vector2 from = new(a.Centre.X, a.Bottom), to = new(b.X, b.Centre.Y);
            Vector2 corner = new(from.X, to.Y);
            Ui.Line(from, corner, color, 1.2f);
            Ui.Line(corner, to - new Vector2(size * 0.04f, 0f), color, 1.2f);
            Vertex.UI.Canvas.DrawTriangle(to, to + new Vector2(-size * 0.1f, -size * 0.07f), to + new Vector2(-size * 0.1f, size * 0.07f), Ui.Fade(color));
        }

        /// <summary>A bar with a triangle pointing back to it: back to the start.</summary>
        public static void ToStart(Vector2 centre, float size, Color color)
        {
            float h = size * 0.28f;
            Ui.Rect(new Rect(centre.X - h * 1.1f, centre.Y - h, size * 0.09f, h * 2f), color, 1f);
            Vertex.UI.Canvas.DrawTriangle(centre + new Vector2(h * 0.9f, -h), centre + new Vector2(h * 0.9f, h), centre + new Vector2(-h * 0.7f, 0f), Ui.Fade(color));
        }

        public static void Stop(Vector2 centre, float size, Color color)
        {
            float h = size * 0.25f;
            Ui.Rect(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, 2f);
        }

        public static void Minimize(Vector2 centre, float size, Color color) =>
            Ui.Line(centre + new Vector2(-size * 0.28f, 0f), centre + new Vector2(size * 0.28f, 0f), color, 1.2f);

        public static void Maximize(Vector2 centre, float size, Color color)
        {
            float h = size * 0.26f;
            Ui.Outline(new Rect(centre.X - h, centre.Y - h, h * 2f, h * 2f), color, 1.5f, 1.2f);
        }

        /// <summary>Two overlapping windows: restore a maximised window.</summary>
        public static void Restore(Vector2 centre, float size, Color color)
        {
            float h = size * 0.21f, o = size * 0.08f;
            Ui.Outline(new Rect(centre.X - h - o, centre.Y - h + o, h * 2f, h * 2f), color, 1.5f, 1.2f);
            Ui.Line(centre + new Vector2(-h + o, -h - o), centre + new Vector2(h + o, -h - o), color, 1.2f);
            Ui.Line(centre + new Vector2(h + o, -h - o), centre + new Vector2(h + o, h - o), color, 1.2f);
        }

        /// <summary>A horseshoe magnet: snapping.</summary>
        public static void Magnet(Vector2 centre, float size, Color color)
        {
            float r = size * 0.22f;
            Vertex.UI.Canvas.DrawArc(centre + new Vector2(0f, size * 0.04f), r, 0f, 180f, size * 0.13f, Ui.Fade(color));
            Vector2 left = centre + new Vector2(-r, size * 0.04f), right = centre + new Vector2(r, size * 0.04f);
            Ui.Line(left, left - new Vector2(0f, size * 0.3f), color, size * 0.13f);
            Ui.Line(right, right - new Vector2(0f, size * 0.3f), color, size * 0.13f);
        }

        // A ring, thin enough for an icon.
        public static void Ring(Vector2 centre, float radius, Color color) => Vertex.UI.Canvas.DrawCircleOutline(centre, radius, Ui.Fade(color), 1.3f);
    }
}
