using System.Collections.Concurrent;

using StbImageSharp;

using Vertex.Rendering;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Small copies of images for the asset browser and pickers, made in the background so a folder of big textures
    /// doesn't hold anything up: ask with <see cref="Get"/>, which gives null until it's ready.
    /// </summary>
    public static class Thumbnails
    {
        public const int Size = 128;

        private static readonly ConcurrentDictionary<string, Texture?> _made = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> _making = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _workers = new(2);

        /// <summary>The thumbnail of the image at <paramref name="path"/>, or null while it's being made (or if it can't be read).</summary>
        public static Texture? Get(string path)
        {
            if (_made.TryGetValue(path, out Texture? made)) return made;
            if (!_making.TryAdd(path, 0)) return null;

            Task.Run(async () =>
            {
                await _workers.WaitAsync();
                try
                {
                    _made[path] = Make(path);
                }
                catch (Exception)
                {
                    _made[path] = null;
                }
                finally
                {
                    _workers.Release();
                    _making.TryRemove(path, out _);
                }
            });

            return null;
        }

        /// <summary>Forgets a thumbnail, so it's made again: the file changed.</summary>
        public static void Forget(string path) => _made.TryRemove(path, out _);

        private static Texture Make(string path)
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // Averaging boxes of pixels, so detail doesn't shimmer away.
            float scale = MathF.Min(1f, (float)Size / MathF.Max(image.Width, image.Height));
            int width = Math.Max((int)(image.Width * scale), 1), height = Math.Max((int)(image.Height * scale), 1);
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                int y0 = y * image.Height / height, y1 = Math.Max((y + 1) * image.Height / height, y0 + 1);
                for (int x = 0; x < width; x++)
                {
                    int x0 = x * image.Width / width, x1 = Math.Max((x + 1) * image.Width / width, x0 + 1);
                    float r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sy * image.Width + sx) * 4;
                            float alpha = image.Data[i + 3] / 255f;
                            r += image.Data[i] * alpha;
                            g += image.Data[i + 1] * alpha;
                            b += image.Data[i + 2] * alpha;
                            a += image.Data[i + 3];
                            count++;
                        }
                    }

                    // Colours multiplied by their alpha, as the 2D layer draws them.
                    int o = (y * width + x) * 4;
                    pixels[o] = (byte)(r / count);
                    pixels[o + 1] = (byte)(g / count);
                    pixels[o + 2] = (byte)(b / count);
                    pixels[o + 3] = (byte)(a / count);
                }
            }

            return new Texture(width, height, pixels);
        }
    }
}
