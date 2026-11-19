using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace NeonCity.Builder
{
    // The textures, made from noise and saved as PNG files next to the scene, and the materials everything's made of.
    internal static partial class CityBuilder
    {
        private static Texture _concrete = null!, _asphalt = null!, _tiles = null!, _palette = null!, _advert = null!, _screens = null!,
            _wood = null!, _vending = null!;

        private static Material _wallMaterial = null!, _darkWall = null!, _metal = null!, _darkMetal = null!, _glass = null!, _road = null!,
            _sidewalk = null!, _puddle = null!, _marking = null!, _windows = null!, _signPanel = null!, _woodMaterial = null!, _darkWood = null!,
            _steel = null!, _redCloth = null!, _vinyl = null!, _floorTiles = null!, _plaster = null!, _ceramic = null!, _ground = null!;

        /// <summary>
        /// The window colours, each a window picture in the windows texture, which every window shows one of: lit ones first,
        /// warm, cool and a few coloured by signs and screens inside; then dark ones.
        /// </summary>
        private static readonly Color[] _windowColors =
        [
            new(1f, 0.74f, 0.42f), new(0.95f, 0.66f, 0.36f), new(1f, 0.84f, 0.6f), new(0.66f, 0.8f, 1f), new(0.5f, 0.7f, 1f),
            new(0.3f, 0.95f, 0.9f), new(1f, 0.42f, 0.78f), new(0.7f, 0.45f, 1f), new(0.5f, 0.36f, 0.22f), new(0.4f, 0.5f, 0.7f),
            new(0.05f, 0.06f, 0.08f), new(0.04f, 0.05f, 0.07f), new(0.07f, 0.07f, 0.09f), new(0.05f, 0.07f, 0.12f), new(0.08f, 0.06f, 0.07f),
            new(0.03f, 0.04f, 0.05f)
        ];

        private const int _litColors = 10;

        private static void MakeTextures()
        {
            _concrete = SaveTexture("Concrete", 256, 256, (x, y) =>
            {
                // Weathered panels: blotchy, with seams between them and streaks of dirt running down from each seam.
                float n = Layered(x / 32f, y / 32f, 8) * 0.6f + Layered(x / 8f, y / 8f, 32) * 0.25f;
                float seam = (y % 128 < 2 || x % 128 < 2) ? 0.55f : 1f;
                float streak = 1f - 0.25f * MathF.Exp(-(y % 128) / 40f) * Layered(x / 4f, 0f, 64);
                return Grey((0.55f + n * 0.45f) * seam * streak);
            });

            _asphalt = SaveTexture("Asphalt", 256, 256, (x, y) =>
            {
                float n = Layered(x / 4f, y / 4f, 64) * 0.5f + Layered(x / 32f, y / 32f, 8) * 0.5f;
                float speck = Hash(x, y, 256) > 0.93f ? 0.25f : 0f;
                return Grey(0.4f + n * 0.5f + speck);
            });

            _tiles = SaveTexture("Tiles", 256, 256, (x, y) =>
            {
                // Square tiles, each a slightly different shade, with dark grout between.
                bool grout = x % 64 < 3 || y % 64 < 3;
                float shade = 0.8f + Hash(x / 64, y / 64, 4) * 0.2f + Layered(x / 16f, y / 16f, 16) * 0.08f;
                return Grey(grout ? 0.25f : shade);
            });

            _wood = SaveTexture("Wood", 256, 256, (x, y) =>
            {
                // Planks along x, with grain.
                int plank = y / 32;
                float grain = MathF.Sin((x + Hash(plank, 0, 64) * 300f) * (MathF.Tau * 8f / 256f) + Layered(x / 32f, y / 4f, 8) * 6f) * 0.5f + 0.5f;
                float shade = 0.7f + grain * 0.25f + Hash(plank, 1, 64) * 0.1f;
                if (y % 32 < 1) shade *= 0.5f;
                return new Color(shade, shade * 0.82f, shade * 0.66f);
            });

            _palette = SaveTexture("Windows", _windowColors.Length * _windowCell, _windowCell, (x, y) =>
            {
                // A window of each colour side by side: a frame and a bar down the middle, blinds part way down lit
                // ones, which are brighter lower down where the room's lamps are; a pale streak of reflection on dark ones.
                int index = x / _windowCell, lx = x % _windowCell;
                Color color = _windowColors[index];
                bool frame = lx < 2 || lx >= _windowCell - 2 || y < 2 || y >= _windowCell - 2 || lx == _windowCell / 2 || lx == _windowCell / 2 - 1;
                if (frame) return new Color(0.03f, 0.03f, 0.035f);

                if (index < _litColors)
                {
                    float shade = 0.7f + 0.3f * y / _windowCell;
                    if (y < 12 && y % 3 == 0) shade *= 0.55f;
                    return color * shade;
                }

                float streak = MathF.Abs(lx - y * 0.6f - 4f) < 2f ? 0.06f : 0f;
                return new Color(color.R + streak, color.G + streak, color.B + streak * 1.3f);
            });

            _advert = SaveTexture("Advert", 128, 256, (x, y) =>
            {
                // A face in profile isn't something noise can draw, so: a sunset of bands behind a big ring and stripes,
                // the kind of thing that sells a drink. White, so the colour comes from the material it's tinted by.
                float u = x / 128f, v = y / 256f;
                float bands = 0.35f + 0.65f * (1f - v);
                float ring = MathF.Abs(MathF.Sqrt((u - 0.5f) * (u - 0.5f) * 4f + (v - 0.38f) * (v - 0.38f) * 16f) - 0.8f) < 0.08f ? 1f : 0f;
                float stripes = v > 0.62f && v < 0.9f && (int)(v * 40f) % 2 == 0 ? 0.9f : 0f;
                float scan = (y % 4 == 0) ? 0.75f : 1f;
                float value = MathF.Max(MathF.Max(bands * 0.45f, ring), stripes) * scan;
                return Grey(value);
            });

            _screens = SaveTexture("Screens", 64, 64, (x, y) =>
            {
                // Chunky game pixels: a dark sky, a row of blocks, a little ship.
                int px = x / 4, py = y / 4;
                Color sky = new(0.05f, 0.02f + py * 0.012f, 0.18f);
                if (py > 12) return new Color(0.2f, 0.9f, 0.4f);
                if (py == 2 && px % 3 != 0) return new Color(1f, 0.3f, 0.6f);
                if (py == 4 && px % 4 == 1) return new Color(1f, 0.85f, 0.2f);
                if (py >= 9 && py <= 10 && px >= 7 && px <= 9) return new Color(0.4f, 0.85f, 1f);
                return Hash(px, py, 16) > 0.95f ? new Color(0.9f, 0.9f, 1f) : sky;
            });

            _vending = SaveTexture("Vending", 64, 128, (x, y) =>
            {
                // Rows of brightly coloured cans behind glass, with a lit panel along the top.
                if (y < 18) return new Color(0.95f, 0.95f, 1f);
                int column = x / 12, row = (y - 18) / 18;
                bool can = x % 12 > 2 && x % 12 < 10 && (y - 18) % 18 > 3 && (y - 18) % 18 < 15 && column < 5;
                if (!can) return new Color(0.75f, 0.85f, 0.95f);
                Color[] cans = [new(1f, 0.2f, 0.2f), new(0.2f, 0.6f, 1f), new(1f, 0.8f, 0.1f), new(0.3f, 0.9f, 0.4f), new(1f, 0.4f, 0.8f)];
                return cans[(column + row) % cans.Length];
            });
        }

        private static void MakeMaterials()
        {
            _wallMaterial = Lit(new Color(0.34f, 0.33f, 0.35f), 0.85f, texture: _concrete);
            _darkWall = Lit(new Color(0.16f, 0.16f, 0.18f), 0.8f, texture: _concrete);
            _metal = Lit(new Color(0.35f, 0.36f, 0.38f), 0.45f, metallic: 0.8f);
            _darkMetal = Lit(new Color(0.07f, 0.07f, 0.08f), 0.4f, metallic: 0.6f);
            _glass = Lit(new Color(0.015f, 0.02f, 0.03f), 0.06f, metallic: 0.3f);
            _road = Lit(new Color(0.2f, 0.2f, 0.22f), 0.3f, texture: _asphalt);
            _sidewalk = Lit(new Color(0.3f, 0.3f, 0.32f), 0.45f, texture: _tiles);
            _puddle = Lit(new Color(0.05f, 0.05f, 0.06f), 0.06f);
            _marking = Lit(new Color(0.75f, 0.72f, 0.6f), 0.5f);
            _signPanel = Lit(new Color(0.04f, 0.04f, 0.05f), 0.5f, metallic: 0.4f);
            _woodMaterial = Lit(new Color(0.75f, 0.6f, 0.45f), 0.55f, texture: _wood);
            _darkWood = Lit(new Color(0.3f, 0.18f, 0.12f), 0.5f, texture: _wood);
            _steel = Lit(new Color(0.62f, 0.64f, 0.67f), 0.3f, metallic: 0.9f);
            _redCloth = Lit(new Color(0.6f, 0.06f, 0.07f), 0.9f);
            _vinyl = Lit(new Color(0.65f, 0.08f, 0.1f), 0.35f);
            _floorTiles = Lit(new Color(0.42f, 0.4f, 0.38f), 0.22f, texture: _tiles);
            _plaster = Lit(new Color(0.3f, 0.26f, 0.24f), 0.85f, texture: _concrete);
            _ceramic = Lit(new Color(0.9f, 0.88f, 0.84f), 0.25f);
            _ground = Lit(new Color(0.08f, 0.08f, 0.09f), 0.6f, texture: _asphalt);

            // Windows light themselves: each takes its colour from the palette, a little dimmer than full.
            _windows = new Material { Shading = ShadingModel.Unlit, Color = new Color(0.62f, 0.62f, 0.62f), Texture = _palette };
        }

        private static Material Lit(Color color, float roughness, float metallic = 0f, Texture? texture = null) =>
            new() { Color = color, Roughness = roughness, Metallic = metallic, Texture = texture };

        /// <summary>Glowing, like a neon tube: dark itself, but giving off its colour brightly enough to bloom.</summary>
        private static Material Glow(Color color, float strength) =>
            new() { Color = new Color(0.05f, 0.05f, 0.05f), Roughness = 0.3f, Emission = color, EmissionStrength = strength };

        private static Material Picture(Texture texture, Color tint) =>
            new() { Shading = ShadingModel.Unlit, Color = tint, Texture = texture };

        /// <summary>The corners of window picture <paramref name="index"/> in the windows texture (see <see cref="_windowColors"/>).</summary>
        private static (Vector2 Min, Vector2 Max) WindowPicture(int index) =>
            (new Vector2((index * _windowCell + 0.5f) / (_windowColors.Length * _windowCell), 0f),
             new Vector2(((index + 1) * _windowCell - 0.5f) / (_windowColors.Length * _windowCell), 1f));

        private const int _windowCell = 32;

        private static Texture SaveTexture(string name, int width, int height, Func<int, int, Color> pixel)
        {
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color c = pixel(x, y);
                    int i = (y * width + x) * 4;
                    rgba[i] = (byte)Math.Clamp(c.R * 255f, 0f, 255f);
                    rgba[i + 1] = (byte)Math.Clamp(c.G * 255f, 0f, 255f);
                    rgba[i + 2] = (byte)Math.Clamp(c.B * 255f, 0f, 255f);
                    rgba[i + 3] = 255;
                }
            }

            string path = Path.Combine(_assets, "Textures", name + ".png");
            Png.Save(path, width, height, rgba);
            return Assets.LoadTexture(path);
        }

        private static Color Grey(float value) => new(value, value, value);

        // ---------------------------------------------------------------- Noise that tiles

        private static float Hash(int x, int y, int period)
        {
            uint qx = (uint)(((x % period) + period) % period), qy = (uint)(((y % period) + period) % period);
            uint h = (qx * 1597334673u) ^ (qy * 3812015801u);
            h = (h ^ (h >> 16)) * 2246822519u;
            h ^= h >> 13;
            return h / 4294967295f;
        }

        private static float Noise(float x, float y, int period)
        {
            int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = float.Lerp(Hash(ix, iy, period), Hash(ix + 1, iy, period), fx);
            float b = float.Lerp(Hash(ix, iy + 1, period), Hash(ix + 1, iy + 1, period), fx);
            return float.Lerp(a, b, fy);
        }

        // Three layers of noise, each finer, repeating every `period` units of the first.
        private static float Layered(float x, float y, int period) =>
            Noise(x, y, period) * 0.55f + Noise(x * 2f, y * 2f, period * 2) * 0.3f + Noise(x * 4f, y * 4f, period * 4) * 0.15f;
    }
}
