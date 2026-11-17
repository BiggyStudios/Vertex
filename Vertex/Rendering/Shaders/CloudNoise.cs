namespace Vertex.Rendering.Shaders
{
    /// <summary>
    /// Smooth random values that tile, for the sky's clouds (see ShaderLibrary.SkyFragment). Worked out once here and
    /// looked up, rather than made from scratch for every pixel of sky every frame: one texture lookup per layer of
    /// detail instead of four random numbers and the blending between them.
    /// </summary>
    internal static class CloudNoise
    {
        /// <summary>How many random values across before it repeats. The shader divides by this.</summary>
        public const int Period = 64;

        // Texels between each pair of values, so the texture's own blending between texels follows the smooth curve
        // between values closely.
        private const int _texelsPerCell = 4;

        public const int Size = Period * _texelsPerCell;

        /// <summary>The noise, in red (and green and blue), from 0 to 255.</summary>
        public static Texture Texture { get; } = Create();

        private static Texture Create()
        {
            var pixels = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Value noise at the middle of this texel, as the shader worked it out before.
                    float px = (x + 0.5f) / _texelsPerCell, py = (y + 0.5f) / _texelsPerCell;
                    int ix = (int)MathF.Floor(px), iy = (int)MathF.Floor(py);
                    float fx = px - ix, fy = py - iy;
                    float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);

                    float bottom = float.Lerp(Hash(ix, iy), Hash(ix + 1, iy), ux);
                    float top = float.Lerp(Hash(ix, iy + 1), Hash(ix + 1, iy + 1), ux);
                    byte value = (byte)Math.Clamp(MathF.Round(float.Lerp(bottom, top, uy) * 255f), 0f, 255f);

                    int i = (y * Size + x) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                    pixels[i + 3] = 255;
                }
            }

            return new Texture(Size, Size, pixels);
        }

        // A random value from 0 to 1 for each whole-number point, repeating every Period.
        private static float Hash(int x, int y)
        {
            uint qx = (uint)(((x % Period) + Period) % Period), qy = (uint)(((y % Period) + Period) % Period);
            uint h = (qx * 1597334673u) ^ (qy * 3812015801u);
            h = (h ^ (h >> 16)) * 2246822519u;
            h ^= h >> 13;
            return h / 4294967295f;
        }
    }
}
