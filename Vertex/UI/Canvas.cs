using System.Numerics;

using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.UI
{
    /// <summary>
    /// Draws 2D shapes and text over the 3D scene, for HUDs, menus and chat. Nothing stays on screen by itself:
    /// call the Draw methods from Update on every frame something should show. Later draws go on top of earlier ones.
    /// <para>
    /// Positions and sizes are in UI units, measured from the top-left corner. A UI unit is a pixel on a screen
    /// <see cref="ReferenceHeight"/> pixels tall; on other screens everything scales to match, so the UI takes up
    /// the same share of the screen at any resolution. Wider screens simply have more room left and right.
    /// </para>
    /// </summary>
    public static class Canvas
    {
        // Filled during Update, then handed over whole to the renderer, so a frame drawn twice never shows half a UI.
        private static List<CanvasQuad> _drawing = new();
        private static List<CanvasQuad> _ready = new();

        /// <summary>The screen height the UI is laid out for. At this height, one UI unit is one pixel.</summary>
        public static float ReferenceHeight { get; set; } = 720f;

        /// <summary>An extra size multiplier on top of the automatic scaling, for a "UI size" setting.</summary>
        public static float UiScale { get; set; } = 1f;

        /// <summary>Screen pixels per UI unit this frame.</summary>
        public static float Scale { get; private set; } = 1f;

        /// <summary>The screen size in UI units.</summary>
        public static float Width { get; private set; }
        public static float Height { get; private set; }
        public static Vector2 Size => new(Width, Height);

        /// <summary>The mouse pointer's position in UI units. Only meaningful while the cursor isn't locked.</summary>
        public static Vector2 MousePosition => InputSystem.MousePosition * _windowToPixels / Scale;

        // Mouse positions come in window coordinates, which on high-DPI screens differ from pixels.
        private static float _windowToPixels = 1f;

        public static void DrawRect(Vector2 position, Vector2 size, Color color) => DrawImage(null, position, size, color);

        /// <summary>
        /// Draws <paramref name="texture"/> stretched over a rectangle, tinted by <paramref name="color"/> (white leaves it
        /// as it is). The texture's colours should be premultiplied by its alpha, as the font's are.
        /// </summary>
        public static void DrawImage(Texture? texture, Vector2 position, Vector2 size, Color color)
        {
            Vector4 c = Premultiply(color);
            Vector2 topLeft = position * Scale;
            Vector2 bottomRight = (position + size) * Scale;

            _drawing.Add(new CanvasQuad(texture,
                new CanvasVertex(topLeft, Vector2.Zero, c),
                new CanvasVertex(new Vector2(bottomRight.X, topLeft.Y), new Vector2(1f, 0f), c),
                new CanvasVertex(bottomRight, Vector2.One, c),
                new CanvasVertex(new Vector2(topLeft.X, bottomRight.Y), new Vector2(0f, 1f), c)));
        }

        /// <summary>A straight line from <paramref name="from"/> to <paramref name="to"/>, <paramref name="thickness"/> UI units wide.</summary>
        public static void DrawLine(Vector2 from, Vector2 to, float thickness, Color color)
        {
            Vector2 along = to - from;
            float length = along.Length();
            if (length < 0.0001f) return;

            Vector2 side = new Vector2(-along.Y, along.X) / length * (thickness * 0.5f);
            AddShape(from - side, to - side, to + side, from + side, color);
        }

        /// <summary>
        /// Part of a ring round <paramref name="centre"/>, <paramref name="radius"/> out to the middle of its line, which is
        /// <paramref name="thickness"/> wide. It starts <paramref name="startAngle"/> degrees round from the right and runs
        /// <paramref name="sweep"/> degrees clockwise; a sweep of 360 is a whole circle.
        /// </summary>
        public static void DrawArc(Vector2 centre, float radius, float startAngle, float sweep, float thickness, Color color)
        {
            if (sweep == 0f || radius <= 0f) return;

            // Short enough pieces that the ring looks round at any size.
            int segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweep) / 360f * MathF.Max(radius * Scale * 0.5f, 12f)), 1, 128);
            float inner = MathF.Max(radius - thickness * 0.5f, 0f);
            float outer = radius + thickness * 0.5f;
            Vector2 Point(float angle, float distance)
            {
                float radians = angle * MathF.PI / 180f;
                return centre + new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * distance;
            }

            for (int i = 0; i < segments; i++)
            {
                float a = startAngle + sweep * i / segments;
                float b = startAngle + sweep * (i + 1) / segments;
                AddShape(Point(a, outer), Point(b, outer), Point(b, inner), Point(a, inner), color);
            }
        }

        // A solid four-cornered shape, corners in UI units.
        private static void AddShape(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            Vector4 colour = Premultiply(color);
            _drawing.Add(new CanvasQuad(null,
                new CanvasVertex(a * Scale, Vector2.Zero, colour),
                new CanvasVertex(b * Scale, new Vector2(1f, 0f), colour),
                new CanvasVertex(c * Scale, Vector2.One, colour),
                new CanvasVertex(d * Scale, new Vector2(0f, 1f), colour)));
        }

        /// <summary>
        /// Draws text with its top-left corner at <paramref name="position"/>, <paramref name="size"/> UI units tall.
        /// A shadow makes it readable over a busy scene. Returns the width drawn, so pieces in different colours can
        /// be put side by side.
        /// </summary>
        public static float DrawText(Font font, string text, Vector2 position, Color color, float size = 18f, bool shadow = false)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            // Drawn at the real pixel size, so text stays sharp however much the UI is scaled.
            float pixelSize = PixelSize(size);

            if (shadow)
            {
                float offset = MathF.Max(1f, pixelSize / 16f);
                font.Draw(text, position * Scale + new Vector2(offset), Premultiply(new Color(0f, 0f, 0f, color.A * 0.75f)), pixelSize);
            }

            font.Draw(text, position * Scale, Premultiply(color), pixelSize);
            return font.Measure(text, pixelSize).X / Scale;
        }

        /// <summary>The width and height of <paramref name="text"/> in UI units.</summary>
        public static Vector2 MeasureText(Font font, string text, float size = 18f) => font.Measure(text, PixelSize(size)) / Scale;

        /// <summary>How far apart lines of text are, in UI units.</summary>
        public static float LineHeight(Font font, float size = 18f) => font.LineHeight(PixelSize(size)) / Scale;

        internal static void AddQuad(in CanvasQuad quad) => _drawing.Add(quad);

        internal static IReadOnlyList<CanvasQuad> ReadyQuads => _ready;

        internal static void BeginFrame(int width, int height, int windowHeight = 0)
        {
            Scale = height > 0 ? MathF.Max(height / ReferenceHeight * UiScale, 0.1f) : 1f;
            Width = width / Scale;
            Height = height / Scale;
            _windowToPixels = windowHeight > 0 ? height / (float)windowHeight : 1f;
        }

        internal static void EndFrame()
        {
            (_ready, _drawing) = (_drawing, _ready);
            _drawing.Clear();
        }

        // Rounded to half pixels: each size gets its own set of letters in the font texture, so while a window is
        // being resized this keeps it from filling up with sizes a fraction apart.
        private static float PixelSize(float size) => MathF.Max(MathF.Round(size * Scale * 2f) / 2f, 1f);

        private static Vector4 Premultiply(Color color) => new(color.R * color.A, color.G * color.A, color.B * color.A, color.A);
    }
}
