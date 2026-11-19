using System.Numerics;

using Vertex.UI;

namespace Vertex.Rendering
{
    /// <summary>
    /// Words placed in the world at its object: a sign, a name over a player's head (with <see cref="FlatRenderer.Billboard"/>
    /// set to face the camera), damage numbers, a 2D game's labels. Nearer things in front hide it, as they would anything
    /// else in the world. For words on the screen, over the game, there's <see cref="UIText"/>.
    /// </summary>
    public sealed class WorldText : FlatRenderer
    {
        [Tooltip("What it says. New lines start new lines")]
        public string Text { get; set; } = "Text";

        /// <summary>The font; with none, the engine's own.</summary>
        public Font? Font { get; set; }

        [Tooltip("How tall a line of it is, in world units (metres)")]
        public float Size { get; set; } = 0.5f;

        public Color Color { get; set; } = Color.White;

        [Tooltip("Which side of the object's position it lines up on")]
        public TextAlign Align { get; set; } = TextAlign.Centre;

        [Tooltip("Whether the object's position is at its top, middle or bottom")]
        public TextVerticalAlign VerticalAlign { get; set; } = TextVerticalAlign.Middle;

        [Tooltip("How far apart its lines are: 1 as the font has them")]
        [Range(0.5f, 3f)]
        public float LineSpacing { get; set; } = 1f;

        [Tooltip("A copy behind it in this colour, a little down and to the right, so it reads against anything. Clear for none")]
        public Color ShadowColor { get; set; } = new(0f, 0f, 0f, 0f);

        // Letters are drawn at this many pixels high, then scaled to Size: big enough to stay sharp close up.
        private const float _pixelSize = 64f;

        // The letters laid out in the text's own plane (its object's units, before Size), kept until something changes.
        private readonly List<(Texture Texture, Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft, Vector2 UvMin, Vector2 UvMax)> _laidOut = new();
        private (string Text, Font Font, TextAlign Align, TextVerticalAlign VerticalAlign, float LineSpacing)? _laidOutFor;
        private readonly List<Font.Glyph> _glyphs = new();

        /// <summary>How wide and tall it is, in world units, before its object's scale.</summary>
        public Vector2 Measure()
        {
            Font font = Font ?? UI.Font.Default;
            float scale = Size / font.LineHeight(_pixelSize);
            string[] lines = (Text ?? "").Replace("\r", "").Split('\n');
            float width = lines.Max(line => font.Measure(line, _pixelSize).X);
            return new Vector2(width, font.LineHeight(_pixelSize) * (lines.Length - 1) * LineSpacing + font.LineHeight(_pixelSize)) * scale;
        }

        internal override void Collect(List<WorldQuad> into, in ViewAxes view)
        {
            if (string.IsNullOrEmpty(Text) || Size <= 0f) return;

            Font font = Font ?? UI.Font.Default;
            LayOut(font);

            float scale = Size / font.LineHeight(_pixelSize);
            Matrix4x4 world = Matrix4x4.CreateScale(scale, -scale, scale) * DrawMatrix(view);

            if (ShadowColor.A > 0f)
            {
                // Down and to the right by a twentieth of a line, drawn first so the text covers it.
                Matrix4x4 shadow = Matrix4x4.CreateTranslation(_pixelSize * 0.05f, _pixelSize * 0.05f, 0f) * world;
                Add(into, shadow, ShadowColor);
            }

            Add(into, world, Color);
        }

        private void Add(List<WorldQuad> into, in Matrix4x4 world, Color color)
        {
            foreach (var glyph in _laidOut)
            {
                into.Add(new WorldQuad(glyph.Texture,
                    Vector3.Transform(new Vector3(glyph.TopLeft, 0f), world),
                    Vector3.Transform(new Vector3(glyph.TopRight, 0f), world),
                    Vector3.Transform(new Vector3(glyph.BottomRight, 0f), world),
                    Vector3.Transform(new Vector3(glyph.BottomLeft, 0f), world),
                    glyph.UvMin, glyph.UvMax, color) { PremultipliedTexture = true });
            }
        }

        /// <summary>
        /// Lays the letters out in pixels, y down, with the object's position where the alignment puts it: the middle of
        /// the text for centred, its left edge for left-aligned, and so on.
        /// </summary>
        private void LayOut(Font font)
        {
            var key = (Text, font, Align, VerticalAlign, LineSpacing);
            if (_laidOutFor == key) return;
            _laidOutFor = key;
            _laidOut.Clear();

            string[] lines = Text.Replace("\r", "").Split('\n');
            float lineHeight = font.LineHeight(_pixelSize);
            float step = lineHeight * LineSpacing;
            float height = step * (lines.Length - 1) + lineHeight;
            float top = VerticalAlign switch
            {
                TextVerticalAlign.Top => 0f,
                TextVerticalAlign.Bottom => -height,
                _ => -height * 0.5f
            };

            for (int i = 0; i < lines.Length; i++)
            {
                float width = font.Measure(lines[i], _pixelSize).X;
                float left = Align switch { TextAlign.Left => 0f, TextAlign.Right => -width, _ => -width * 0.5f };
                var offset = new Vector2(left, top + step * i);

                _glyphs.Clear();
                font.Layout(lines[i], _pixelSize, _glyphs);
                foreach (Font.Glyph glyph in _glyphs)
                {
                    _laidOut.Add((glyph.Texture, glyph.TopLeft + offset, glyph.TopRight + offset, glyph.BottomRight + offset, glyph.BottomLeft + offset,
                        glyph.UvMin, glyph.UvMax));
                }
            }
        }
    }
}
