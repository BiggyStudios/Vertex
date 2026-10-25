using StbImageSharp;

namespace Vertex.Rendering
{
    /// <summary>
    /// A 2D RGBA texture. Pure data: the active renderer makes its own GPU copy the first time it is used.
    /// </summary>
    public class Texture : IDisposable
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Width * Height * 4 bytes (RGBA), top row first.</summary>
        public byte[] Pixels { get; }

        /// <summary>Raised by <see cref="Dispose"/> so renderers can free their GPU copy.</summary>
        public event Action<Texture>? Disposed;

        /// <summary>Bumped by <see cref="SetPixels"/>, so renderers know to upload their copy again.</summary>
        public int Version { get; private set; }

        /// <param name="rgbaPixels">Width * Height * 4 bytes, top row first.</param>
        public Texture(int width, int height, byte[] rgbaPixels)
        {
            if (rgbaPixels.Length != width * height * 4)
                throw new ArgumentException("Pixel data must be width * height * 4 bytes.", nameof(rgbaPixels));

            Width = width;
            Height = height;
            Pixels = rgbaPixels;
        }

        /// <summary>Loads a PNG, JPG, BMP, TGA, GIF or HDR file.</summary>
        public static Texture Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return new Texture(image.Width, image.Height, image.Data);
        }

        /// <summary>Decodes an encoded image (PNG, JPG, ...) held in memory.</summary>
        public static Texture FromMemory(byte[] encodedImage)
        {
            ImageResult image = ImageResult.FromMemory(encodedImage, ColorComponents.RedGreenBlueAlpha);
            return new Texture(image.Width, image.Height, image.Data);
        }

        /// <summary>A checkerboard, handy for prototyping floors and walls.</summary>
        public static Texture CreateCheckerboard(Color a, Color b, int squares = 8, int squareSize = 16)
        {
            int size = squares * squareSize;
            byte[] pixels = new byte[size * size * 4];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Color c = ((x / squareSize) + (y / squareSize)) % 2 == 0 ? a : b;
                    int i = (y * size + x) * 4;
                    pixels[i] = (byte)(c.R * 255);
                    pixels[i + 1] = (byte)(c.G * 255);
                    pixels[i + 2] = (byte)(c.B * 255);
                    pixels[i + 3] = (byte)(c.A * 255);
                }
            }

            return new Texture(size, size, pixels);
        }

        /// <summary>
        /// Replaces a rectangle of pixels (RGBA, top row first). The renderer re-uploads the texture before it's next drawn.
        /// </summary>
        public void SetPixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgbaPixels)
        {
            if (x < 0 || y < 0 || x + width > Width || y + height > Height)
                throw new ArgumentOutOfRangeException(nameof(x), "The rectangle must fit inside the texture.");
            if (rgbaPixels.Length != width * height * 4)
                throw new ArgumentException("Pixel data must be width * height * 4 bytes.", nameof(rgbaPixels));

            for (int row = 0; row < height; row++)
            {
                rgbaPixels.Slice(row * width * 4, width * 4).CopyTo(Pixels.AsSpan(((y + row) * Width + x) * 4));
            }

            Version++;
        }

        /// <summary>Frees the GPU copy. The texture is uploaded again if it is used afterwards.</summary>
        public void Dispose()
        {
            Disposed?.Invoke(this);
            Disposed = null;
        }
    }
}
