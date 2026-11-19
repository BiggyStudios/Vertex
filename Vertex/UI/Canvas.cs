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
        public static Vector2 MousePosition => (InputSystem.MousePosition * _windowToPixels - _origin) / Scale;

        /// <summary>How many screen pixels make up one unit of <see cref="InputSystem.MousePosition"/>; more than 1 on some high-DPI screens.</summary>
        public static float WindowToPixels => _windowToPixels;

        // Mouse positions come in window coordinates, which on high-DPI screens differ from pixels.
        private static float _windowToPixels = 1f;

        // Where drawing lands in the window, in pixels: the top-left of the view while one is open (see BeginView).
        private static Vector2 _origin;

        private readonly record struct ViewState(float Scale, float Width, float Height, Vector2 Origin, Vector4[] Clips);
        private static readonly Stack<ViewState> _views = new();

        /// <summary>
        /// Draws into a rectangle of the window as if it were a whole screen, until <see cref="EndView"/>: positions, sizes
        /// and the mouse are measured from its top-left, the UI scales to its height as it would on a screen that size, and
        /// nothing spills out of it. For an editor showing a game's own UI in its game view.
        /// </summary>
        internal static void BeginView(Vector2 pixelOrigin, int width, int height)
        {
            _views.Push(new ViewState(Scale, Width, Height, _origin, _clips.ToArray()));
            _origin = pixelOrigin;
            Scale = height > 0 ? MathF.Max(height / ReferenceHeight * UiScale, 0.1f) : 1f;
            Width = width / Scale;
            Height = height / Scale;
            _clips.Clear();
            _clips.Add(new Vector4(pixelOrigin.X, pixelOrigin.Y, pixelOrigin.X + width, pixelOrigin.Y + height));
        }

        /// <summary>
        /// Draws into <paramref name="size"/> of the current view at <paramref name="position"/> (both in UI units),
        /// magnified by <paramref name="zoom"/> and scrolled so <paramref name="offset"/> is at its top-left, until
        /// <see cref="EndView"/>: for an editor's zoomable canvas (a node graph). Everything drawn inside, text too, scales
        /// with it and stays sharp; nothing spills out.
        /// </summary>
        internal static void BeginView(Vector2 position, Vector2 size, Vector2 offset, float zoom)
        {
            var clip = new Vector4(position.X * Scale + _origin.X, position.Y * Scale + _origin.Y, (position.X + size.X) * Scale + _origin.X, (position.Y + size.Y) * Scale + _origin.Y);
            if (_clips.Count > 0)
            {
                Vector4 outer = _clips[^1];
                clip = new Vector4(MathF.Max(clip.X, outer.X), MathF.Max(clip.Y, outer.Y), MathF.Min(clip.Z, outer.Z), MathF.Min(clip.W, outer.W));
            }

            if (clip.Z <= clip.X || clip.W <= clip.Y) clip = new Vector4(-2f, -2f, -1f, -1f);

            _views.Push(new ViewState(Scale, Width, Height, _origin, _clips.ToArray()));
            _origin = (position - offset * zoom) * Scale + _origin;
            Scale *= zoom;
            Width = size.X / zoom;
            Height = size.Y / zoom;
            _clips.Clear();
            _clips.Add(clip);
        }

        /// <summary>Goes back to drawing as before <c>BeginView</c>.</summary>
        internal static void EndView()
        {
            if (_views.Count == 0) return;

            ViewState state = _views.Pop();
            (Scale, Width, Height, _origin) = (state.Scale, state.Width, state.Height, state.Origin);
            _clips.Clear();
            _clips.AddRange(state.Clips);
        }

        /// <summary>
        /// Keeps everything drawn from now until <see cref="PopClip"/> inside this rectangle, as well as inside any clip
        /// already pushed. For panels whose contents scroll past their edges.
        /// </summary>
        public static void PushClip(Vector2 position, Vector2 size)
        {
            var clip = new Vector4(position.X * Scale + _origin.X, position.Y * Scale + _origin.Y, (position.X + size.X) * Scale + _origin.X, (position.Y + size.Y) * Scale + _origin.Y);
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
                return ((new Vector2(clip.X, clip.Y) - _origin) / Scale, new Vector2(clip.Z - clip.X, clip.W - clip.Y) / Scale);
            }
        }

        /// <summary>Whether <paramref name="point"/> (in UI units) is inside the current clip, so can be seen and clicked.</summary>
        public static bool InClip(Vector2 point)
        {
            if (_clips.Count == 0) return true;
            Vector4 clip = _clips[^1];
            Vector2 pixel = point * Scale + _origin;
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

        /// <summary>
        /// A four-cornered piece of <paramref name="texture"/>: corners <paramref name="a"/> to <paramref name="d"/> in UI
        /// units, in order round it, each with where in the texture it shows (0 to 1 across it). Two corners can be the
        /// same, for a triangle: a slice of a radial fill.
        /// </summary>
        public static void DrawImageQuad(Texture? texture, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, Color color)
        {
            Vector4 colour = Premultiply(color);
            Add(new CanvasQuad(texture,
                new CanvasVertex(a * Scale, uvA, colour),
                new CanvasVertex(b * Scale, uvB, colour),
                new CanvasVertex(c * Scale, uvC, colour),
                new CanvasVertex(d * Scale, uvD, colour)));
        }

        /// <summary>
        /// A straight line from <paramref name="from"/> to <paramref name="to"/>, <paramref name="thickness"/> UI units wide,
        /// with smooth edges and round ends.
        /// </summary>
        public static void DrawLine(Vector2 from, Vector2 to, float thickness, Color color)
        {
            Vector2 a = from * Scale, b = to * Scale;
            float radius = thickness * 0.5f * Scale;

            // Thinner than a pixel draws a pixel wide, fainter, which looks the same and doesn't break up.
            if (radius < 0.5f)
            {
                color.A *= MathF.Max(radius * 2f, 0f);
                radius = 0.5f;
            }

            Vector2 along = b - a;
            float length = along.Length();
            Vector2 direction = length > 1e-4f ? along / length : Vector2.UnitX;
            Vector2 side = new Vector2(-direction.Y, direction.X) * (radius + 1f);
            Vector2 end = direction * (radius + 1f);
            Vector4 c = Premultiply(color);

            Add(new CanvasQuad(null,
                new CanvasVertex(a - end - side, Vector2.Zero, c),
                new CanvasVertex(b + end - side, new Vector2(1f, 0f), c),
                new CanvasVertex(b + end + side, Vector2.One, c),
                new CanvasVertex(a - end + side, new Vector2(0f, 1f), c))
            {
                Shape = new Vector4(a, b.X, b.Y),
                ShapeStyle = new Vector4(radius, 0f, 1f, 1f)
            });
        }

        /// <summary>Lines joining <paramref name="points"/> in order, smooth and round where they meet.</summary>
        public static void DrawPolyline(ReadOnlySpan<Vector2> points, float thickness, Color color)
        {
            for (int i = 0; i + 1 < points.Length; i++) DrawLine(points[i], points[i + 1], thickness, color);
        }

        /// <summary>A four-cornered shape with hard edges, corners in UI units, in order round it.</summary>
        public static void DrawQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color) => AddShape(a, b, c, d, color);

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

            // Thin rings as smooth lines; thick ones as filled pieces, which meet without overlapping.
            if (thickness * Scale <= 4f)
            {
                for (int i = 0; i < segments; i++)
                {
                    float a = startAngle + sweep * i / segments;
                    float b = startAngle + sweep * (i + 1) / segments;
                    DrawLine(Point(a, radius), Point(b, radius), thickness, color);
                }

                return;
            }

            for (int i = 0; i < segments; i++)
            {
                float a = startAngle + sweep * i / segments;
                float b = startAngle + sweep * (i + 1) / segments;
                AddShape(Point(a, outer), Point(b, outer), Point(b, inner), Point(a, inner), color);
            }
        }

        /// <summary>A filled triangle with smooth edges, corners in UI units: arrowheads and markers.</summary>
        public static void DrawTriangle(Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            Vector2 pa = a * Scale, pb = b * Scale, pc = c * Scale;
            Vector2 min = Vector2.Min(pa, Vector2.Min(pb, pc)) - Vector2.One;
            Vector2 max = Vector2.Max(pa, Vector2.Max(pb, pc)) + Vector2.One;
            Vector4 colour = Premultiply(color);

            Add(new CanvasQuad(null,
                new CanvasVertex(min, Vector2.Zero, colour),
                new CanvasVertex(new Vector2(max.X, min.Y), new Vector2(1f, 0f), colour),
                new CanvasVertex(max, Vector2.One, colour),
                new CanvasVertex(new Vector2(min.X, max.Y), new Vector2(0f, 1f), colour))
            {
                Shape = new Vector4(pa, pb.X, pb.Y),
                ShapeStyle = new Vector4(pc.X, pc.Y, 0f, 2f)
            });
        }

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
            if (_origin != Vector2.Zero) quad = Moved(quad, _origin);

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

        // The quad and its shape, moved by `by` pixels: into a view (see BeginView).
        private static CanvasQuad Moved(CanvasQuad quad, Vector2 by)
        {
            CanvasVertex Move(CanvasVertex v) => v with { Position = v.Position + by };
            var offset = new Vector4(by, by.X, by.Y);
            Vector4 shape = quad.Shape, style = quad.ShapeStyle;

            switch (style.W)
            {
                // A line: both its ends.
                case > 0.5f and < 1.5f: shape += offset; break;

                // A triangle: two corners in the shape, the third in the style.
                case >= 1.5f:
                    shape += offset;
                    style += new Vector4(by, 0f, 0f);
                    break;

                // A rounded rectangle: its centre.
                default:
                    if (shape.Z > 0f) shape += new Vector4(by, 0f, 0f);
                    break;
            }

            return quad with
            {
                TopLeft = Move(quad.TopLeft),
                TopRight = Move(quad.TopRight),
                BottomRight = Move(quad.BottomRight),
                BottomLeft = Move(quad.BottomLeft),
                Shape = shape,
                ShapeStyle = style
            };
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
            _views.Clear();
            _origin = Vector2.Zero;
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
