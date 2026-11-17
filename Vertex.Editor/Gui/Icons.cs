using System.Numerics;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Small icons drawn from lines and circles, so they're sharp at any size and take their colour from the theme.
    /// Each fits a square <paramref name="size"/> UI units across around its centre.
    /// </summary>
    public static class Icons
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
            float r = size * 0.3f;
            Vertex.UI.Canvas.DrawArc(centre, r, -60f, 290f, 1.4f, Ui.Fade(color));
            Vector2 end = centre + new Vector2(MathF.Cos(-60f * MathF.PI / 180f), MathF.Sin(-60f * MathF.PI / 180f)) * r;
            Chevron(end + new Vector2(0f, size * 0.02f), size * 0.5f, color, -150f);
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

        public static void Trash(Vector2 centre, float size, Color color)
        {
            float w = size * 0.26f;
            Ui.Line(centre + new Vector2(-w * 1.25f, -size * 0.22f), centre + new Vector2(w * 1.25f, -size * 0.22f), color, 1.3f);
            Ui.Line(centre + new Vector2(-w * 0.4f, -size * 0.3f), centre + new Vector2(w * 0.4f, -size * 0.3f), color, 1.3f);
            Ui.Outline(new Rect(centre.X - w, centre.Y - size * 0.16f, w * 2f, size * 0.46f), color, 2f, 1.3f);
        }

        // A ring, thin enough for an icon.
        private static void Ring(Vector2 centre, float radius, Color color) => Vertex.UI.Canvas.DrawCircleOutline(centre, radius, Ui.Fade(color), 1.3f);
    }
}
