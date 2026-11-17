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
    /// <para>
    /// Rectangles can have rounded corners and smooth edges, and <see cref="PushClip"/> keeps whatever's drawn inside a
    /// rectangle, for panels that scroll.
    /// </para>
    /// </summary>
    public static class Canvas
    {
        // Filled during Update, then handed over whole to the renderer, so a frame drawn twice never shows half a UI.
        private static List<CanvasQuad> _drawing = new();
        private static List<CanvasQuad> _ready = new();

        // The clip rectangles pushed, in pixels (left, top, right, bottom); the last is the one in force.
        private static readonly List<Vector4> _clips = new();

        // How soft a smooth edge is, in pixels: enough that curves don't look jagged, little enough that they look sharp.
        private const float _edgeSoftness = 1f;

        /// <summary>The screen height the UI is laid out for. At this height, one UI unit is one pixel.</summary>
        public static float ReferenceHeight { get; set; } = 720f;

        /// <summary>An extra size multiplier on top of the automatic scaling, for a "UI size" setting.</summary>
        public static float UiScale { get; set; } = 1f;

        /// <summary>
        /// Screen pixels per UI unit, fixed instead of following the screen height: a tool like the editor sets it to the
        /// display scale (1.25 for 125%), so things keep their size however big the window. 0, the default, scales with the screen.
        /// </summary>
        public static float FixedScale { get; set; }

        /// <summary>Screen pixels per UI unit this frame.</summary>
        public static float Scale { get; private set; } = 1f;

        /// <summary>The screen size in UI units.</summary>
        public static float Width { get; private set; }
        public static float Height { get; private set; }
        public static Vector2 Size => new(Width, Height);

        /// <summary>The mouse pointer's position in UI units. Only meaningful while the cursor isn't locked.</summary>
        public static Vector2 MousePosition => InputSystem.MousePosition * _windowToPixels / Scale;

        /// <summary>How many screen pixels make up one unit of <see cref="InputSystem.MousePosition"/>; more than 1 on some high-DPI screens.</summary>
        public static float WindowToPixels => _windowToPixels;

        // Mouse positions come in window coordinates, which on high-DPI screens differ from pixels.
        private static float _windowToPixels = 1f;

        /// <summary>
        /// Keeps everything drawn from now until <see cref="PopClip"/> inside this rectangle, as well as inside any clip
        /// already pushed. For panels whose contents scroll past their edges.
        /// </summary>
        public static void PushClip(Vector2 position, Vector2 size)
        {
            var clip = new Vector4(position.X * Scale, position.Y * Scale, (position.X + size.X) * Scale, (position.Y + size.Y) * Scale);
            if (_clips.Count > 0)
            {
                Vector4 outer = _clips[^1];
                clip = new Vector4(MathF.Max(clip.X, outer.X), MathF.Max(clip.Y, outer.Y), MathF.Min(clip.Z, outer.Z), MathF.Min(clip.W, outer.W));
            }

            // Kept non-empty, as an empty clip means "no clip" to the renderer: one that's shut is a zero-width sliver off screen.
            if (clip.Z <= clip.X || clip.W <= clip.Y) clip = new Vector4(-2f, -2f, -1f, -1f);
            _clips.Add(clip);
        }

        /// <summary>Undoes the last <see cref="PushClip"/>.</summary>
        public static void PopClip()
        {
            if (_clips.Count > 0) _clips.RemoveAt(_clips.Count - 1);
        }

        /// <summary>The rectangle drawing is kept inside, in UI units, or null if nothing's been pushed.</summary>
        public static (Vector2 Position, Vector2 Size)? ClipRect
        {
            get
            {
                if (_clips.Count == 0) return null;
                Vector4 clip = _clips[^1];
                return (new Vector2(clip.X, clip.Y) / Scale, new Vector2(clip.Z - clip.X, clip.W - clip.Y) / Scale);
            }
        }

        /// <summary>Whether <paramref name="point"/> (in UI units) is inside the current clip, so can be seen and clicked.</summary>
        public static bool InClip(Vector2 point)
        {
            if (_clips.Count == 0) return true;
            Vector4 clip = _clips[^1];
            Vector2 pixel = point * Scale;
            return pixel.X >= clip.X && pixel.Y >= clip.Y && pixel.X < clip.Z && pixel.Y < clip.W;
        }

        public static void DrawRect(Vector2 position, Vector2 size, Color color) => DrawImage(null, position, size, color);

        /// <summary>A filled rectangle with corners rounded to <paramref name="radius"/> UI units, its edges smooth.</summary>
        public static void DrawRect(Vector2 position, Vector2 size, Color color, float radius)
        {
            if (radius <= 0f)
            {
                DrawRect(position, size, color);
                return;
            }

            AddRoundedQuad(null, position, size, Vector2.Zero, Vector2.One, color, color, color, color, radius, outline: 0f, softness: _edgeSoftness, grow: 0f);
        }

        /// <summary>The outline of a rectangle, <paramref name="thickness"/> UI units wide and inside its edge, with corners rounded to <paramref name="radius"/>.</summary>
        public static void DrawRectOutline(Vector2 position, Vector2 size, Color color, float thickness = 1f, float radius = 0f)
        {
            AddRoundedQuad(null, position, size, Vector2.Zero, Vector2.One, color, color, color, color, MathF.Max(radius, 0f),
                outline: MathF.Max(thickness, 0.01f), softness: _edgeSoftness, grow: 0f);
        }

        /// <summary>A filled circle.</summary>
        public static void DrawCircle(Vector2 centre, float radius, Color color)
        {
            if (radius <= 0f) return;
            AddRoundedQuad(null, centre - new Vector2(radius), new Vector2(radius * 2f), Vector2.Zero, Vector2.One, color, color, color, color,
                radius, outline: 0f, softness: _edgeSoftness, grow: 0f);
        }

        /// <summary>A ring round <paramref name="centre"/>, <paramref name="thickness"/> wide inside <paramref name="radius"/>.</summary>
        public static void DrawCircleOutline(Vector2 centre, float radius, Color color, float thickness = 1f)
        {
            if (radius <= 0f) return;
            AddRoundedQuad(null, centre - new Vector2(radius), new Vector2(radius * 2f), Vector2.Zero, Vector2.One, color, color, color, color,
                radius, outline: MathF.Max(thickness, 0.01f), softness: _edgeSoftness, grow: 0f);
        }

        /// <summary>
        /// A soft shadow under a rectangle: draw it first, then the rectangle over it. <paramref name="blur"/> is how far
        /// the shadow fades out past the rectangle's edge, in UI units.
        /// </summary>
        public static void DrawShadow(Vector2 position, Vector2 size, Color color, float radius, float blur)
        {
            blur = MathF.Max(blur, 0.5f);
            AddRoundedQuad(null, position, size, Vector2.Zero, Vector2.One, color, color, color, color, radius + blur * 0.5f,
                outline: 0f, softness: blur * 2f * Scale, grow: blur);
        }

        /// <summary>
        /// A rectangle shaded from one colour to another: each corner has its own colour, and the colours blend across it.
        /// For colour pickers and fades.
        /// </summary>
        public static void DrawGradient(Vector2 position, Vector2 size, Color topLeft, Color topRight, Color bottomRight, Color bottomLeft, float radius = 0f)
        {
            AddRoundedQuad(null, position, size, Vector2.Zero, Vector2.One, topLeft, topRight, bottomRight, bottomLeft, MathF.Max(radius, 0f),
                outline: 0f, softness: _edgeSoftness, grow: 0f);
        }

        /// <summary>
        /// Draws <paramref name="texture"/> stretched over a rectangle, tinted by <paramref name="color"/> (white leaves it
        /// as it is). The texture's colours should be premultiplied by its alpha, as the font's are.
        /// </summary>
        public static void DrawImage(Texture? texture, Vector2 position, Vector2 size, Color color)
        {
            Vector4 c = Premultiply(color);
            Vector2 topLeft = position * Scale;
            Vector2 bottomRight = (position + size) * Scale;

            Add(new CanvasQuad(texture,
                new CanvasVertex(topLeft, Vector2.Zero, c),
                new CanvasVertex(new Vector2(bottomRight.X, topLeft.Y), new Vector2(1f, 0f), c),
                new CanvasVertex(bottomRight, Vector2.One, c),
                new CanvasVertex(new Vector2(topLeft.X, bottomRight.Y), new Vector2(0f, 1f), c)));
        }

        /// <summary>
        /// Draws the part of <paramref name="texture"/> from <paramref name="uvMin"/> to <paramref name="uvMax"/> (0 to 1
        /// across it) over a rectangle with corners rounded to <paramref name="radius"/>, tinted by <paramref name="color"/>.
        /// </summary>
        public static void DrawImage(Texture? texture, Vector2 position, Vector2 size, Vector2 uvMin, Vector2 uvMax, Color color, float radius = 0f)
        {
            AddRoundedQuad(texture, position, size, uvMin, uvMax, color, color, color, color, MathF.Max(radius, 0f), outline: 0f,
                softness: radius > 0f ? _edgeSoftness : 0f, grow: 0f);
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

        /// <summary>A filled triangle, corners in UI units: arrowheads and markers.</summary>
        public static void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color color) => AddShape(a, b, c, c, color);

        // A solid four-cornered shape, corners in UI units.
        private static void AddShape(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            Vector4 colour = Premultiply(color);
            Add(new CanvasQuad(null,
                new CanvasVertex(a * Scale, Vector2.Zero, colour),
                new CanvasVertex(b * Scale, new Vector2(1f, 0f), colour),
                new CanvasVertex(c * Scale, Vector2.One, colour),
                new CanvasVertex(d * Scale, new Vector2(0f, 1f), colour)));
        }

        // A rectangle cut to a rounded shape on the GPU. The quad is grown by grow UI units each way, for soft shadows
        // that spread past the shape; texture coordinates and colours stretch over the shape itself.
        private static void AddRoundedQuad(Texture? texture, Vector2 position, Vector2 size, Vector2 uvMin, Vector2 uvMax,
            Color topLeft, Color topRight, Color bottomRight, Color bottomLeft, float radius, float outline, float softness, float grow)
        {
            if (size.X <= 0f || size.Y <= 0f) return;

            Vector2 min = (position - new Vector2(grow)) * Scale;
            Vector2 max = (position + size + new Vector2(grow)) * Scale;
            Vector2 uvGrow = grow > 0f ? (uvMax - uvMin) * grow / size : Vector2.Zero;

            var quad = new CanvasQuad(texture,
                new CanvasVertex(min, uvMin - uvGrow, Premultiply(topLeft)),
                new CanvasVertex(new Vector2(max.X, min.Y), new Vector2(uvMax.X + uvGrow.X, uvMin.Y - uvGrow.Y), Premultiply(topRight)),
                new CanvasVertex(max, uvMax + uvGrow, Premultiply(bottomRight)),
                new CanvasVertex(new Vector2(min.X, max.Y), new Vector2(uvMin.X - uvGrow.X, uvMax.Y + uvGrow.Y), Premultiply(bottomLeft)));

            if (radius > 0f || outline > 0f || softness > _edgeSoftness)
            {
                Vector2 centre = (position + size * 0.5f) * Scale;
                Vector2 half = size * 0.5f * Scale;
                quad = quad with
                {
                    Shape = new Vector4(centre, half.X, half.Y),
                    ShapeStyle = new Vector4(radius * Scale, outline * Scale, MathF.Max(softness, 0.001f), 0f)
                };
            }

            Add(quad);
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

            // Letters land on whole pixels, or they go blurry.
            Vector2 pixel = new(MathF.Round(position.X * Scale), MathF.Round(position.Y * Scale));

            if (shadow)
            {
                float offset = MathF.Max(1f, pixelSize / 16f);
                font.Draw(text, pixel + new Vector2(offset), Premultiply(new Color(0f, 0f, 0f, color.A * 0.75f)), pixelSize);
            }

            font.Draw(text, pixel, Premultiply(color), pixelSize);
            return font.Measure(text, pixelSize).X / Scale;
        }

        /// <summary>The width and height of <paramref name="text"/> in UI units.</summary>
        public static Vector2 MeasureText(Font font, string text, float size = 18f) => font.Measure(text, PixelSize(size)) / Scale;

        /// <summary>How far apart lines of text are, in UI units.</summary>
        public static float LineHeight(Font font, float size = 18f) => font.LineHeight(PixelSize(size)) / Scale;

        internal static void AddQuad(in CanvasQuad quad) => Add(quad);

        // Every quad goes through here: it takes the clip in force, and is left out if that hides all of it.
        private static void Add(CanvasQuad quad)
        {
            if (_clips.Count > 0)
            {
                Vector4 clip = _clips[^1];
                float left = MathF.Min(MathF.Min(quad.TopLeft.Position.X, quad.TopRight.Position.X), MathF.Min(quad.BottomRight.Position.X, quad.BottomLeft.Position.X));
                float right = MathF.Max(MathF.Max(quad.TopLeft.Position.X, quad.TopRight.Position.X), MathF.Max(quad.BottomRight.Position.X, quad.BottomLeft.Position.X));
                float top = MathF.Min(MathF.Min(quad.TopLeft.Position.Y, quad.TopRight.Position.Y), MathF.Min(quad.BottomRight.Position.Y, quad.BottomLeft.Position.Y));
                float bottom = MathF.Max(MathF.Max(quad.TopLeft.Position.Y, quad.TopRight.Position.Y), MathF.Max(quad.BottomRight.Position.Y, quad.BottomLeft.Position.Y));
                if (right <= clip.X || left >= clip.Z || bottom <= clip.Y || top >= clip.W) return;

                // Only needs cutting on the GPU if it reaches past the clip.
                if (left < clip.X || right > clip.Z || top < clip.Y || bottom > clip.W) quad = quad with { Clip = clip };
            }

            _drawing.Add(quad);
        }

        internal static IReadOnlyList<CanvasQuad> ReadyQuads => _ready;

        internal static void BeginFrame(int width, int height, int windowHeight = 0)
        {
            Scale = FixedScale > 0f ? FixedScale * UiScale
                : height > 0 ? MathF.Max(height / ReferenceHeight * UiScale, 0.1f) : 1f;
            Width = width / Scale;
            Height = height / Scale;
            _windowToPixels = windowHeight > 0 ? height / (float)windowHeight : 1f;
            _clips.Clear();
        }

        internal static void EndFrame()
        {
            (_ready, _drawing) = (_drawing, _ready);
            _drawing.Clear();
            _clips.Clear();
        }

        // Rounded to half pixels: each size gets its own set of letters in the font texture, so while a window is
        // being resized this keeps it from filling up with sizes a fraction apart.
        private static float PixelSize(float size) => MathF.Max(MathF.Round(size * Scale * 2f) / 2f, 1f);

        private static Vector4 Premultiply(Color color) => new(color.R * color.A, color.G * color.A, color.B * color.A, color.A);
    }
}
