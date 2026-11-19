using System.Numerics;

using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.UI
{
    /// <summary>
    /// The top of a screen's worth of UI: every object under it with a <see cref="UIRect"/> is laid out on the screen and
    /// drawn over the game, each frame, as the scene runs. Several can be on screen at once; higher
    /// <see cref="Order"/>s draw on top.
    /// <para>
    /// This UI is made of objects and components, so it can be built and arranged in the editor and saved with the
    /// scene. For UI drawn straight from code every frame, there's <see cref="Canvas"/> and <see cref="Gui"/>.
    /// </para>
    /// </summary>
    public sealed class UICanvas : Component
    {
        /// <summary>Which canvas draws over which: higher on top.</summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// Where a piece of UI goes: a rectangle placed against its parent's (or the screen's, at the top of a
    /// <see cref="UICanvas"/>). Positions are in UI units, as <see cref="Canvas"/> uses, with y going down.
    /// <para>
    /// The <see cref="AnchorMin"/> and <see cref="AnchorMax"/> are points in the parent, from (0, 0) at its top-left to
    /// (1, 1) at its bottom-right. Together they say what the rectangle keeps to when the parent changes size: both at the
    /// top-left keeps it in that corner, both at (0.5, 0.5) keeps it in the middle, and (0, 0) and (1, 1) stretches it with
    /// the parent. <see cref="Position"/> is how far its <see cref="Pivot"/> is from the anchors, and <see cref="Size"/>
    /// its size (added to the anchors' span, when they're apart).
    /// </para>
    /// </summary>
    public sealed class UIRect : Component
    {
        [Tooltip("The point of the parent the rectangle's top-left keeps to: (0, 0) its top-left, (1, 1) its bottom-right")]
        public Vector2 AnchorMin { get; set; } = new(0.5f);

        [Tooltip("The point of the parent the rectangle's bottom-right keeps to: the same as Anchor Min, unless it stretches")]
        public Vector2 AnchorMax { get; set; } = new(0.5f);

        [Tooltip("The point of the rectangle that Position places, from (0, 0) at its top-left to (1, 1) at its bottom-right")]
        public Vector2 Pivot { get; set; } = new(0.5f);

        [Tooltip("How far the pivot is from the anchors, in UI units")]
        public Vector2 Position { get; set; }

        [Tooltip("The size in UI units; when the anchors are apart, added to the space between them")]
        public Vector2 Size { get; set; } = new(200f, 60f);

        /// <summary>Where it was laid out last, in UI units of the screen it's on: its top-left corner.</summary>
        [DontSave, HideInInspector]
        public Vector2 ScreenPosition { get; internal set; }

        /// <summary>Its size as laid out last, in UI units.</summary>
        [DontSave, HideInInspector]
        public Vector2 ScreenSize { get; internal set; }

        /// <summary>Whether a point (in UI units) is inside it, as last laid out.</summary>
        public bool Contains(Vector2 point) =>
            point.X >= ScreenPosition.X && point.Y >= ScreenPosition.Y && point.X < ScreenPosition.X + ScreenSize.X && point.Y < ScreenPosition.Y + ScreenSize.Y;

        /// <summary>Lays it out in <paramref name="parentPosition"/>/<paramref name="parentSize"/> (its parent's rectangle).</summary>
        internal void Layout(Vector2 parentPosition, Vector2 parentSize)
        {
            Vector2 anchorsFrom = parentPosition + AnchorMin * parentSize;
            Vector2 anchorsTo = parentPosition + AnchorMax * parentSize;
            Vector2 size = Vector2.Max(anchorsTo - anchorsFrom + Size, Vector2.Zero);
            Vector2 pivot = anchorsFrom + (anchorsTo - anchorsFrom) * Pivot + Position;
            ScreenPosition = pivot - size * Pivot;
            ScreenSize = size;
        }

        /// <summary>Puts it straight at a screen rectangle, ignoring its anchors: what a <see cref="UILayoutGroup"/> does.</summary>
        internal void Place(Vector2 screenPosition, Vector2 screenSize)
        {
            ScreenPosition = screenPosition;
            ScreenSize = Vector2.Max(screenSize, Vector2.Zero);
        }

        /// <summary>
        /// The <see cref="Position"/> and <see cref="Size"/> that put it at <paramref name="screenPosition"/>,
        /// <paramref name="screenSize"/> inside <paramref name="parentPosition"/>/<paramref name="parentSize"/>, keeping its
        /// anchors and pivot: for an editor dragging it about.
        /// </summary>
        public (Vector2 Position, Vector2 Size) PlacementFor(Vector2 screenPosition, Vector2 screenSize, Vector2 parentPosition, Vector2 parentSize)
        {
            Vector2 anchorsFrom = parentPosition + AnchorMin * parentSize;
            Vector2 anchorsTo = parentPosition + AnchorMax * parentSize;
            Vector2 size = screenSize - (anchorsTo - anchorsFrom);
            Vector2 pivot = screenPosition + screenSize * Pivot;
            return (pivot - anchorsFrom - (anchorsTo - anchorsFrom) * Pivot, size);
        }
    }

    /// <summary>Something that draws inside its object's <see cref="UIRect"/>. The kinds of UI are made from these.</summary>
    public abstract class UIGraphic : Component
    {
        /// <summary>Draws it in <paramref name="position"/>/<paramref name="size"/>, in UI units. <paramref name="hovered"/> and <paramref name="pressed"/> are the mouse's.</summary>
        internal abstract void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed);

        /// <summary>Whether it takes the mouse now (a button, a slider): the topmost one under the pointer gets it.</summary>
        internal virtual bool TakesMouse => false;

        /// <summary>The mouse went down on it, at <paramref name="mouse"/> (UI units); it's laid out at <paramref name="position"/>/<paramref name="size"/>.</summary>
        internal virtual void Press(Vector2 mouse, Vector2 position, Vector2 size) {}

        /// <summary>The mouse moved while held after pressing it, wherever it is now.</summary>
        internal virtual void Drag(Vector2 mouse, Vector2 position, Vector2 size) {}

        /// <summary>The mouse came back up after pressing it; <paramref name="over"/> if it's still over it (a click).</summary>
        internal virtual void Release(bool over) {}

        /// <summary>Starts the visual scripts' <paramref name="name"/> event nodes on this object: On Clicked, On Value Changed.</summary>
        private protected void TellScripts(string name, params (string Port, object? Value)[] outputs)
        {
            foreach (Scripting.VisualScript script in GameObject.GetComponents<Scripting.VisualScript>().ToList()) script.Fire(name, outputs);
        }
    }

    /// <summary>A coloured rectangle, with rounded corners and an outline if wanted: the backing of a menu or a bar.</summary>
    public sealed class UIPanel : UIGraphic
    {
        public Color Color { get; set; } = new(0.1f, 0.11f, 0.13f, 0.85f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 8f;

        public Color OutlineColor { get; set; } = new(1f, 1f, 1f, 0f);

        [Range(0f, 8f)]
        public float OutlineWidth { get; set; } = 1f;

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            Canvas.DrawRect(position, size, Color, CornerRadius);
            if (OutlineColor.A > 0f && OutlineWidth > 0f) Canvas.DrawRectOutline(position, size, OutlineColor, OutlineWidth, CornerRadius);
        }
    }

    /// <summary>How a <see cref="UIImage"/> covers its rectangle.</summary>
    public enum ImageMode
    {
        /// <summary>Stretched to fill the rectangle, whatever its shape.</summary>
        Stretch,

        /// <summary>As big as fits inside the rectangle, in its own proportions.</summary>
        Fit,

        /// <summary>Covers the whole rectangle in its own proportions, trimming what spills over.</summary>
        Fill,

        /// <summary>
        /// Cut in nine by <see cref="UIImage.SliceBorder"/>: the corners keep their size, the edges stretch one way and the
        /// middle both, so a framed panel or button looks right at any size.
        /// </summary>
        Sliced,

        /// <summary>Repeated across the rectangle, like floor tiles.</summary>
        Tiled,

        /// <summary>Only part shown, by <see cref="UIImage.FillAmount"/>: a health bar, a cooldown going round.</summary>
        Filled
    }

    /// <summary>Which way a <see cref="ImageMode.Filled"/> image fills.</summary>
    public enum FillMethod
    {
        /// <summary>From the left edge across.</summary>
        Horizontal,

        /// <summary>From the bottom edge up.</summary>
        Vertical,

        /// <summary>Round from the top, like a clock's hand.</summary>
        Radial
    }

    /// <summary>
    /// A picture: a texture, tinted by <see cref="Color"/>, shown in its rectangle by <see cref="Mode"/> (stretched, fitted,
    /// sliced into a frame, tiled, or part-filled). With no texture, it's a rectangle of its colour.
    /// </summary>
    public sealed class UIImage : UIGraphic
    {
        public Texture? Texture { get; set; }

        /// <summary>Multiplied with the texture; with no texture, it's a plain rectangle of this colour.</summary>
        public Color Color { get; set; } = Color.White;

        [Tooltip("How it covers the rectangle: stretched, fitted in proportion, filling it, sliced into a frame, tiled, or part-filled")]
        public ImageMode Mode { get; set; } = ImageMode.Fit;

        [Tooltip("Rounds its corners (Stretch, Fit and Fill)")]
        [Range(0f, 64f)]
        public float CornerRadius { get; set; }

        [Header("Picture")]
        [Tooltip("Shows it mirrored left to right")]
        public bool FlipX { get; set; }

        [Tooltip("Shows it upside down")]
        public bool FlipY { get; set; }

        /// <summary>The part of the texture shown, from its top-left (0, 0) to its bottom-right (1, 1): one picture of a sheet of them.</summary>
        [Tooltip("The top-left of the part of the texture shown, from (0, 0) at its top-left to (1, 1) at its bottom-right")]
        public Vector2 RegionMin { get; set; } = Vector2.Zero;

        [Tooltip("The bottom-right of the part of the texture shown")]
        public Vector2 RegionMax { get; set; } = Vector2.One;

        [Header("Sliced and Tiled")]
        [Tooltip("Sliced: how far in from each edge of the texture the frame goes, in the texture's pixels")]
        public float SliceBorder { get; set; } = 16f;

        [Tooltip("Sliced and Tiled: UI units per pixel of the texture. 1 shows it at its own size")]
        public float PixelScale { get; set; } = 1f;

        [Header("Filled")]
        [Tooltip("Which way it fills: across, up, or round like a clock's hand")]
        public FillMethod FillMethod { get; set; } = FillMethod.Horizontal;

        [Tooltip("How much of it shows: 0 none, 1 all")]
        [Range(0f, 1f)]
        public float FillAmount { get; set; } = 1f;

        [Tooltip("Fills the other way: from the right, from the top, or anticlockwise")]
        public bool FillReversed { get; set; }

        /// <summary>The setting older scenes saved before <see cref="Mode"/>: on is <see cref="ImageMode.Fit"/>, off <see cref="ImageMode.Stretch"/>.</summary>
        [HideInInspector, Serialization.DontSave]
        public bool KeepAspect
        {
            get => Mode == ImageMode.Fit;
            set
            {
                if (Mode is ImageMode.Fit or ImageMode.Stretch) Mode = value ? ImageMode.Fit : ImageMode.Stretch;
            }
        }

        /// <summary>The size in pixels of the part of the texture shown.</summary>
        private Vector2 RegionPixels => Texture is { } texture ? Vector2.Abs(RegionMax - RegionMin) * new Vector2(texture.Width, texture.Height) : Vector2.Zero;

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            if (size.X <= 0f || size.Y <= 0f) return;

            // Flipping swaps which side of the texture each edge shows.
            Vector2 uv0 = RegionMin, uv1 = RegionMax;
            if (FlipX) (uv0.X, uv1.X) = (uv1.X, uv0.X);
            if (FlipY) (uv0.Y, uv1.Y) = (uv1.Y, uv0.Y);

            Vector2 pixels = RegionPixels;
            bool hasPicture = Texture != null && pixels.X > 0f && pixels.Y > 0f;
            ImageMode mode = hasPicture || Mode == ImageMode.Filled ? Mode : ImageMode.Stretch;

            switch (mode)
            {
                case ImageMode.Fit:
                {
                    float aspect = pixels.X / pixels.Y;
                    Vector2 fit = size.X / size.Y > aspect ? new Vector2(size.Y * aspect, size.Y) : new Vector2(size.X, size.X / aspect);
                    Canvas.DrawImage(Texture, position + (size - fit) * 0.5f, fit, uv0, uv1, Color, CornerRadius);
                    break;
                }

                case ImageMode.Fill:
                {
                    // The middle part of the picture that has the rectangle's proportions.
                    float aspect = pixels.X / pixels.Y, rectAspect = size.X / size.Y;
                    Vector2 shown = rectAspect > aspect ? new Vector2(1f, aspect / rectAspect) : new Vector2(rectAspect / aspect, 1f);
                    Vector2 middle = (uv0 + uv1) * 0.5f, half = (uv1 - uv0) * shown * 0.5f;
                    Canvas.DrawImage(Texture, position, size, middle - half, middle + half, Color, CornerRadius);
                    break;
                }

                case ImageMode.Sliced:
                    DrawSliced(position, size, uv0, uv1, pixels);
                    break;

                case ImageMode.Tiled:
                    DrawTiled(position, size, uv0, uv1, pixels);
                    break;

                case ImageMode.Filled:
                    DrawFilled(position, size, uv0, uv1);
                    break;

                default:
                    Canvas.DrawImage(Texture, position, size, uv0, uv1, Color, CornerRadius);
                    break;
            }
        }

        /// <summary>Nine pieces: corners at their own size, edges stretched along, the middle stretched both ways.</summary>
        private void DrawSliced(Vector2 position, Vector2 size, Vector2 uv0, Vector2 uv1, Vector2 pixels)
        {
            float border = Math.Clamp(SliceBorder, 0f, MathF.Min(pixels.X, pixels.Y) * 0.5f);
            Vector2 uvBorder = (uv1 - uv0) * (new Vector2(border) / pixels);
            float onScreen = MathF.Min(border * MathF.Max(PixelScale, 0f), MathF.Min(size.X, size.Y) * 0.5f);

            Span<float> xs = [position.X, position.X + onScreen, position.X + size.X - onScreen, position.X + size.X];
            Span<float> ys = [position.Y, position.Y + onScreen, position.Y + size.Y - onScreen, position.Y + size.Y];
            Span<float> us = [uv0.X, uv0.X + uvBorder.X, uv1.X - uvBorder.X, uv1.X];
            Span<float> vs = [uv0.Y, uv0.Y + uvBorder.Y, uv1.Y - uvBorder.Y, uv1.Y];

            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    if (xs[column + 1] <= xs[column] || ys[row + 1] <= ys[row]) continue;
                    Piece(new Vector2(xs[column], ys[row]), new Vector2(xs[column + 1], ys[row + 1]), new Vector2(us[column], vs[row]), new Vector2(us[column + 1], vs[row + 1]));
                }
            }
        }

        /// <summary>The picture over and over, at its own size times <see cref="PixelScale"/>; the last row and column cut short.</summary>
        private void DrawTiled(Vector2 position, Vector2 size, Vector2 uv0, Vector2 uv1, Vector2 pixels)
        {
            // No smaller than a 64th of the rectangle, so a tiny tile on a huge rectangle can't ask for millions of pieces.
            Vector2 tile = Vector2.Max(pixels * MathF.Max(PixelScale, 0.01f), size / 64f);

            for (float y = 0f; y < size.Y - 0.001f; y += tile.Y)
            {
                for (float x = 0f; x < size.X - 0.001f; x += tile.X)
                {
                    Vector2 shown = Vector2.Min(tile, size - new Vector2(x, y));
                    Vector2 at = position + new Vector2(x, y);
                    Piece(at, at + shown, uv0, uv0 + (uv1 - uv0) * (shown / tile));
                }
            }
        }

        private void DrawFilled(Vector2 position, Vector2 size, Vector2 uv0, Vector2 uv1)
        {
            float amount = Math.Clamp(FillAmount, 0f, 1f);
            if (amount <= 0f) return;

            switch (FillMethod)
            {
                case FillMethod.Horizontal:
                {
                    float from = FillReversed ? 1f - amount : 0f, to = FillReversed ? 1f : amount;
                    Piece(position + new Vector2(size.X * from, 0f), position + new Vector2(size.X * to, size.Y),
                        new Vector2(Lerp(uv0.X, uv1.X, from), uv0.Y), new Vector2(Lerp(uv0.X, uv1.X, to), uv1.Y));
                    break;
                }

                case FillMethod.Vertical:
                {
                    // Up from the bottom (or down from the top, reversed).
                    float from = FillReversed ? 0f : 1f - amount, to = FillReversed ? amount : 1f;
                    Piece(position + new Vector2(0f, size.Y * from), position + new Vector2(size.X, size.Y * to),
                        new Vector2(uv0.X, Lerp(uv0.Y, uv1.Y, from)), new Vector2(uv1.X, Lerp(uv0.Y, uv1.Y, to)));
                    break;
                }

                default:
                    DrawRadial(position, size, uv0, uv1, amount);
                    break;
            }
        }

        /// <summary>
        /// A slice of the rectangle round from the top, like a clock's hand sweeping: triangles from the middle out to the
        /// edge, split wherever the sweep passes a corner or the middle of an edge, so each lies along one edge.
        /// </summary>
        private void DrawRadial(Vector2 position, Vector2 size, Vector2 uv0, Vector2 uv1, float amount)
        {
            Vector2 half = size * 0.5f;
            Vector2 centre = position + half;
            float corner = MathF.Atan2(half.Y, half.X) * 180f / MathF.PI;
            Span<float> stops = [-90f, -corner, 0f, corner, 90f, 180f - corner, 180f, 180f + corner, 270f];
            float end = -90f + amount * 360f;

            // Where a line from the middle at this angle (degrees clockwise from the right) meets the edge; mirrored, for anticlockwise.
            Vector2 Edge(float degrees)
            {
                float radians = degrees * MathF.PI / 180f;
                var direction = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
                float reach = MathF.Min(MathF.Abs(direction.X) > 1e-6f ? half.X / MathF.Abs(direction.X) : float.MaxValue,
                    MathF.Abs(direction.Y) > 1e-6f ? half.Y / MathF.Abs(direction.Y) : float.MaxValue);
                Vector2 offset = direction * reach;
                if (FillReversed) offset.X = -offset.X;
                return centre + offset;
            }

            Vector2 Uv(Vector2 point) => uv0 + (uv1 - uv0) * ((point - position) / size);
            Vector2 middleUv = Uv(centre);

            for (int i = 0; i + 1 < stops.Length; i++)
            {
                float from = stops[i], to = MathF.Min(stops[i + 1], end);
                if (to <= from) break;

                Vector2 a = Edge(from), b = Edge(to);
                Canvas.DrawImageQuad(Texture, centre, a, b, b, middleUv, Uv(a), Uv(b), Uv(b), Color);
            }
        }

        /// <summary>One rectangle of the picture, from corner <paramref name="from"/> to <paramref name="to"/>, showing <paramref name="uvFrom"/> to <paramref name="uvTo"/>.</summary>
        private void Piece(Vector2 from, Vector2 to, Vector2 uvFrom, Vector2 uvTo) =>
            Canvas.DrawImageQuad(Texture, from, new Vector2(to.X, from.Y), to, new Vector2(from.X, to.Y),
                uvFrom, new Vector2(uvTo.X, uvFrom.Y), uvTo, new Vector2(uvFrom.X, uvTo.Y), Color);

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    public enum TextAlign
    {
        Left,
        Centre,
        Right
    }

    public enum TextVerticalAlign
    {
        Top,
        Middle,
        Bottom
    }

    /// <summary>Words, laid out in the rectangle: on one line, or wrapped onto as many as it takes.</summary>
    public sealed class UIText : UIGraphic
    {
        public string Text { get; set; } = "Text";

        /// <summary>The font; with none, the engine's own (<see cref="UI.Font.Default"/>).</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 24f;

        public Color Color { get; set; } = Color.White;

        public TextAlign Align { get; set; } = TextAlign.Centre;

        public TextVerticalAlign VerticalAlign { get; set; } = TextVerticalAlign.Middle;

        /// <summary>Breaks the text onto more lines when it's wider than the rectangle.</summary>
        public bool Wrap { get; set; } = true;

        /// <summary>A soft dark copy just behind it, so it reads over busy backgrounds.</summary>
        public bool Shadow { get; set; }

        [Header("More")]
        [Tooltip("How far apart its lines are: 1 as the font has them, 1.5 half as far again")]
        [Range(0.5f, 3f)]
        public float LineSpacing { get; set; } = 1f;

        [Tooltip("A line round each letter, this many UI units thick: 0 for none")]
        [Range(0f, 8f)]
        public float OutlineWidth { get; set; }

        [Tooltip("The colour of the line round each letter")]
        public Color OutlineColor { get; set; } = Color.Black;

        [Tooltip("Makes the text smaller, as far as it needs to, to fit in the rectangle")]
        public bool ShrinkToFit { get; set; }

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            Font font = Font ?? UI.Font.Default;
            float fontSize = ShrinkToFit ? FittingSize(font, size) : FontSize;
            DrawText(font, Text, fontSize, Color, Align, VerticalAlign, Wrap, Shadow, position, size, LineSpacing, OutlineWidth, OutlineColor);
        }

        /// <summary>The largest size, no bigger than <see cref="FontSize"/>, at which the text fits in <paramref name="size"/>.</summary>
        internal float FittingSize(Font font, Vector2 size)
        {
            bool Fits(float fontSize)
            {
                List<string> lines = Wrap ? WrapLines(font, Text, fontSize, size.X) : SplitLines(Text);
                if (lines.Count * Canvas.LineHeight(font, fontSize) * LineSpacing > size.Y + 0.5f) return false;
                return lines.All(line => Canvas.MeasureText(font, line, fontSize).X <= size.X + 0.5f);
            }

            if (string.IsNullOrEmpty(Text) || Fits(FontSize)) return FontSize;

            // Halving the gap a few times is close enough to the size that just fits.
            float low = 4f, high = FontSize;
            for (int i = 0; i < 7; i++)
            {
                float middle = (low + high) * 0.5f;
                if (Fits(middle)) low = middle;
                else high = middle;
            }

            return low;
        }

        internal static void DrawText(Font font, string text, float fontSize, Color color, TextAlign align, TextVerticalAlign verticalAlign, bool wrap,
            bool shadow, Vector2 position, Vector2 size, float lineSpacing = 1f, float outlineWidth = 0f, Color outlineColor = default)
        {
            if (string.IsNullOrEmpty(text)) return;

            List<string> lines = wrap ? WrapLines(font, text, fontSize, size.X) : SplitLines(text);
            float lineHeight = Canvas.LineHeight(font, fontSize) * lineSpacing;
            float height = lines.Count * lineHeight;
            float y = verticalAlign switch
            {
                TextVerticalAlign.Top => position.Y,
                TextVerticalAlign.Bottom => position.Y + size.Y - height,
                _ => position.Y + (size.Y - height) * 0.5f
            };

            foreach (string line in lines)
            {
                float width = Canvas.MeasureText(font, line, fontSize).X;
                float x = align switch
                {
                    TextAlign.Left => position.X,
                    TextAlign.Right => position.X + size.X - width,
                    _ => position.X + (size.X - width) * 0.5f
                };

                // An outline is the text drawn round about itself in the outline's colour, then the text over the top.
                if (outlineWidth > 0f && outlineColor.A > 0f)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        float angle = i * MathF.PI / 4f;
                        Canvas.DrawText(font, line, new Vector2(x + MathF.Cos(angle) * outlineWidth, y + MathF.Sin(angle) * outlineWidth), outlineColor, fontSize);
                    }
                }

                Canvas.DrawText(font, line, new Vector2(x, y), color, fontSize, shadow);
                y += lineHeight;
            }
        }

        private static List<string> SplitLines(string text) => [.. text.Replace("\r", "").Split('\n')];

        private static List<string> WrapLines(Font font, string text, float fontSize, float width)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Canvas.MeasureText(font, candidate, fontSize).X > width)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }

                lines.Add(line);
            }

            return lines;
        }
    }

    /// <summary>
    /// Something to click: a rounded rectangle that lights up under the mouse, with a label. Code finds out about
    /// clicks through <see cref="Clicked"/>, and visual scripts on its object through On Clicked:
    /// <code>
    /// GetComponent&lt;UIButton&gt;()!.Clicked += () => StartGame();
    /// </code>
    /// </summary>
    public sealed class UIButton : UIGraphic
    {
        public string Label { get; set; } = "Button";

        /// <summary>The label's font; with none, the engine's own.</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 22f;

        public Color LabelColor { get; set; } = Color.White;

        public Color Color { get; set; } = new(0.2f, 0.22f, 0.26f, 0.95f);
        public Color HoverColor { get; set; } = new(0.27f, 0.3f, 0.36f, 1f);
        public Color PressedColor { get; set; } = new(0.17f, 0.18f, 0.21f, 1f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 8f;

        /// <summary>Whether it can be clicked. A button that can't is drawn faded.</summary>
        public bool Interactable { get; set; } = true;

        /// <summary>Raised when it's clicked: pressed and let go with the mouse still over it.</summary>
        public event Action? Clicked;

        // Eases between its colours, so it doesn't snap.
        private float _hover;

        /// <summary>Clicks it from code, as if the mouse had: what listens to its clicks hears this one.</summary>
        public void Click()
        {
            Clicked?.Invoke();
            TellScripts("Clicked");
        }

        internal override bool TakesMouse => Interactable;

        internal override void Release(bool over)
        {
            if (over) Click();
        }

        internal static Color Faded(Color color) => new(color.R, color.G, color.B, color.A * 0.5f);

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            float target = Interactable && hovered ? 1f : 0f;
            _hover += (target - _hover) * (1f - MathF.Exp(-18f * Time.DeltaTime));

            Color color = !Interactable ? Faded(Color)
                : pressed ? PressedColor
                : Color.Lerp(Color, HoverColor, _hover);
            Canvas.DrawRect(position, size, color, CornerRadius);

            Color label = Interactable ? LabelColor : Faded(LabelColor);
            UIText.DrawText(Font ?? UI.Font.Default, Label, FontSize, label, TextAlign.Centre, TextVerticalAlign.Middle, wrap: false, shadow: false, position, size);
        }
    }
}
