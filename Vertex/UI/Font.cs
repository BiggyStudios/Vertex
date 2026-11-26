using System.Drawing;
using System.Numerics;

using FontStashSharp;
using FontStashSharp.Interfaces;

using Vertex.Rendering;

namespace Vertex.UI
{
    /// <summary>
    /// A TrueType or OpenType font for <see cref="Canvas.DrawText"/>, sharp at any size. Letters are drawn into a
    /// texture the first time each one is used at each size, so any character the font has just works.
    /// </summary>
    public sealed class Font : IDisposable
    {
        private readonly FontSystem _fonts;

        private Font(FontSystem fonts)
        {
            _fonts = fonts;
        }

        /// <summary>
        /// Loads a .ttf or .otf file. Fallback files are used for characters the first one doesn't have,
        /// such as a Japanese font behind a Latin one.
        /// </summary>
        public static Font Load(string path, params string[] fallbackPaths)
        {
            var fonts = new FontSystem();
            fonts.AddFont(AssetFiles.ReadAllBytes(path));
            foreach (string fallback in fallbackPaths) fonts.AddFont(AssetFiles.ReadAllBytes(fallback));
            return new Font(fonts) { Source = Path.GetFullPath(path) };
        }

        /// <summary>A font from the bytes of a .ttf or .otf file.</summary>
        public static Font FromBytes(byte[] file)
        {
            var fonts = new FontSystem();
            fonts.AddFont(file);
            return new Font(fonts);
        }

        /// <summary>The file it was loaded from, if it was: how saved scenes refer to it.</summary>
        public string? Source { get; internal set; }

        private static Font? _default, _defaultBold;

        /// <summary>
        /// Noto Sans, which comes with the engine: for text with no font of its own (a UI text that hasn't been given
        /// one), so there's always something to draw with. Under the SIL Open Font License (see UI/Fonts/OFL.txt).
        /// </summary>
        public static Font Default => _default ??= Embedded("NotoSans-Regular.ttf");

        /// <summary>The bold Noto Sans that comes with the engine.</summary>
        public static Font DefaultBold => _defaultBold ??= Embedded("NotoSans-Bold.ttf");

        private static Font Embedded(string name)
        {
            using Stream stream = typeof(Font).Assembly.GetManifestResourceStream("Vertex.Fonts." + name)
                                  ?? throw new InvalidOperationException($"The engine's font {name} is missing.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return FromBytes(memory.ToArray());
        }

        /// <summary>The width and height of <paramref name="text"/> at <paramref name="size"/> pixels.</summary>
        public Vector2 Measure(string text, float size) => string.IsNullOrEmpty(text) ? Vector2.Zero : _fonts.GetFont(size).MeasureString(text);

        /// <summary>How far apart lines of text are at <paramref name="size"/> pixels.</summary>
        public float LineHeight(float size) => _fonts.GetFont(size).LineHeight;

        internal void Draw(string text, Vector2 position, Vector4 premultipliedColor, float size)
        {
            var color = new FSColor(premultipliedColor.X, premultipliedColor.Y, premultipliedColor.Z, premultipliedColor.W);
            _fonts.GetFont(size).DrawText(FontRenderer.Instance, text, position, color);
        }

        /// <summary>
        /// One letter laid out by <see cref="Layout"/>: its corners in pixels from the line's top-left (y down), and where
        /// it is in the letters' texture. The texture is premultiplied, white where there's ink.
        /// </summary>
        internal readonly record struct Glyph(Texture Texture, Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft, Vector2 UvMin, Vector2 UvMax);

        /// <summary>
        /// Lays out one line of <paramref name="text"/> at <paramref name="size"/> pixels into <paramref name="into"/>, without
        /// drawing it: for text placed in the world rather than on the screen.
        /// </summary>
        internal void Layout(string text, float size, List<Glyph> into)
        {
            if (string.IsNullOrEmpty(text)) return;

            GlyphCollector.Instance.Into = into;
            _fonts.GetFont(size).DrawText(GlyphCollector.Instance, text, Vector2.Zero, FSColor.White);
            GlyphCollector.Instance.Into = null;
        }

        public void Dispose() => _fonts.Dispose();

        /// <summary>Takes the letters FontStashSharp lays out as glyphs, rather than canvas quads. Shares the canvas's textures.</summary>
        private sealed class GlyphCollector : IFontStashRenderer2
        {
            public static readonly GlyphCollector Instance = new();

            public List<Glyph>? Into;

            public ITexture2DManager TextureManager => FontRenderer.Instance;

            public void DrawQuad(object texture, ref VertexPositionColorTexture topLeft, ref VertexPositionColorTexture topRight,
                ref VertexPositionColorTexture bottomLeft, ref VertexPositionColorTexture bottomRight)
            {
                Into?.Add(new Glyph((Texture)texture, Flat(topLeft), Flat(topRight), Flat(bottomRight), Flat(bottomLeft),
                    topLeft.TextureCoordinate, bottomRight.TextureCoordinate));
            }

            private static Vector2 Flat(in VertexPositionColorTexture vertex) => new(vertex.Position.X, vertex.Position.Y);
        }

        /// <summary>
        /// How FontStashSharp gets at the engine: its glyph textures are ordinary <see cref="Texture"/>s,
        /// and each letter it lays out becomes a quad on the <see cref="Canvas"/>.
        /// </summary>
        private sealed class FontRenderer : IFontStashRenderer2, ITexture2DManager
        {
            public static readonly FontRenderer Instance = new();

            public ITexture2DManager TextureManager => this;

            public object CreateTexture(int width, int height) => new Texture(width, height, new byte[width * height * 4]);

            public Point GetTextureSize(object texture)
            {
                var t = (Texture)texture;
                return new Point(t.Width, t.Height);
            }

            public void SetTextureData(object texture, Rectangle bounds, byte[] data)
            {
                // FontStashSharp reuses one buffer for every glyph, so it's often bigger than this one needs.
                ((Texture)texture).SetPixels(bounds.X, bounds.Y, bounds.Width, bounds.Height, data.AsSpan(0, bounds.Width * bounds.Height * 4));
            }

            public void DrawQuad(object texture, ref VertexPositionColorTexture topLeft, ref VertexPositionColorTexture topRight,
                ref VertexPositionColorTexture bottomLeft, ref VertexPositionColorTexture bottomRight)
            {
                Canvas.AddQuad(new CanvasQuad((Texture)texture, ToVertex(topLeft), ToVertex(topRight), ToVertex(bottomRight), ToVertex(bottomLeft)));
            }

            private static CanvasVertex ToVertex(in VertexPositionColorTexture vertex) =>
                new(new Vector2(vertex.Position.X, vertex.Position.Y), vertex.TextureCoordinate, vertex.Color.ToVector4());
        }
    }
}
