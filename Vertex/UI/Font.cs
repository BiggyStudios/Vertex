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
            fonts.AddFont(File.ReadAllBytes(path));
            foreach (string fallback in fallbackPaths) fonts.AddFont(File.ReadAllBytes(fallback));
            return new Font(fonts);
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

        public void Dispose() => _fonts.Dispose();

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
