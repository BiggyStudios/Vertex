using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// The night city all the maps float in: skyscrapers rising out of the haze far below, all the way round, with lit
    /// windows, neon trim along their roofs, glowing signs down their sides and red lights on the tallest. It's only
    /// backdrop: nothing in it is solid, and it stays clear of every map. The same seed builds the same city every time,
    /// so everyone in a room sees the same skyline.
    /// <para>
    /// For speed it's a handful of meshes, not thousands of objects: every tower in one, lit by a window texture that
    /// needs no lighting, and each neon colour in one more, glowing into the bloom.
    /// </para>
    /// </summary>
    public static class CityBackdrop
    {
        // Everything the maps use, with room to spare: no tower stands inside it.
        private const float _clearMinX = -380f, _clearMaxX = 330f, _clearMinZ = -410f, _clearMaxZ = 660f;

        // How far out the city goes, and how far apart the towers stand.
        private const float _cityMinX = -1050f, _cityMaxX = 1000f, _cityMinZ = -1100f, _cityMaxZ = 1350f;
        private const float _spacing = 62f;

        // The towers start this far down, lost in the haze, so they rise out of nowhere.
        private const float _street = -420f;

        // One window texture tile covers this many metres, 16 windows across and 16 floors up.
        private const float _tileSize = 64f;

        private static readonly Color[] _neonColors =
        [
            new(1f, 0.2f, 0.6f),    // hot pink
            new(0.1f, 0.9f, 1f),    // cyan
            new(0.65f, 0.3f, 1f),   // purple
            new(1f, 0.75f, 0.2f),   // amber
            new(1f, 0.2f, 0.25f)    // red
        ];

        private static readonly Color _warningRed = new(1f, 0.1f, 0.08f);

        /// <summary>
        /// The middle of the <paramref name="n"/>th street running north to south, between two rows of towers: nothing
        /// stands within 6 m of it. <see cref="StreetZ"/> is the same for the streets running east to west.
        /// </summary>
        public static float StreetX(int n) => _cityMinX + (n + 0.5f) * _spacing;
        public static float StreetZ(int n) => _cityMinZ + (n + 0.5f) * _spacing;

        /// <summary>The city's edges, for lanes to run from one to the other.</summary>
        public static Vector2 Min => new(_cityMinX - 200f, _cityMinZ - 200f);
        public static Vector2 Max => new(_cityMaxX + 200f, _cityMaxZ + 200f);


        public static void Build()
        {
            var random = new Random(2077);
            var towers = new MeshBuilder();
            var neon = _neonColors.Select(_ => new MeshBuilder()).ToArray();
            var warning = new MeshBuilder();

            for (float x = _cityMinX; x <= _cityMaxX; x += _spacing)
            {
                for (float z = _cityMinZ; z <= _cityMaxZ; z += _spacing)
                {
                    // A few gaps, so it isn't a perfect grid.
                    if (random.NextDouble() < 0.12) continue;

                    // Each tower stays within its square of the grid, so a street runs clear down the middle between rows:
                    // see <see cref="StreetX"/>. The flying cars use them.
                    float width = 20f + random.NextSingle() * 20f;
                    float depth = 20f + random.NextSingle() * 20f;
                    var centre = new Vector2(x + (random.NextSingle() - 0.5f) * 10f, z + (random.NextSingle() - 0.5f) * 10f);
                    if (Overlaps(centre, width, depth)) continue;

                    // Mostly mid-height, with the odd giant towering over the rest. Further out, a little taller, so the
                    // skyline climbs away from the maps.
                    float outward = Math.Clamp(DistanceOutside(centre) / 500f, 0f, 1f);
                    float top = -40f + MathF.Pow(random.NextSingle(), 1.7f) * 330f + outward * 80f;
                    if (random.NextDouble() < 0.06) top += 150f;

                    int color = random.Next(_neonColors.Length);
                    AddTower(towers, neon[color], warning, random, centre, width, depth, top);
                }
            }

            var city = new GameObject("City");
            towers.Finish(city, "City Towers", Color.White, glow: 0f, WindowTexture(random));
            for (int i = 0; i < neon.Length; i++) neon[i].Finish(city, "City Neon", _neonColors[i], glow: 2.2f);
            warning.Finish(city, "City Warning Lights", _warningRed, glow: 3f);
        }

        /// <summary>
        /// One tower: a block from the street up to <paramref name="top"/>, sometimes with a narrower one on top, then its
        /// neon: a strip round the roof, perhaps a sign down one side, perhaps bands round it. Red lights on the tallest.
        /// </summary>
        private static void AddTower(MeshBuilder towers, MeshBuilder neon, MeshBuilder warning, Random random, Vector2 centre, float width, float depth, float top)
        {
            // Each tower's windows start somewhere different in the texture, a whole window along, so neighbours differ.
            var windows = new Vector2(random.Next(16), random.Next(16)) * (_tileSize / 16f);
            towers.AddBox(new Vector3(centre.X, (_street + top) / 2f, centre.Y), new Vector3(width, top - _street, depth), windows);

            float roof = top;
            float roofWidth = width, roofDepth = depth;
            if (random.NextDouble() < 0.4)
            {
                // A setback: a narrower block on top, with its own trim where the lower one stops.
                AddRim(neon, centre, width, depth, top);
                roofWidth = width * (0.5f + random.NextSingle() * 0.25f);
                roofDepth = depth * (0.5f + random.NextSingle() * 0.25f);
                float height = 20f + random.NextSingle() * 60f;
                towers.AddBox(new Vector3(centre.X, top + height / 2f, centre.Y), new Vector3(roofWidth, height, roofDepth), windows);
                roof = top + height;
            }

            if (random.NextDouble() < 0.7) AddRim(neon, centre, roofWidth, roofDepth, roof);

            // A tall sign down the middle of one face, standing just off it.
            if (random.NextDouble() < 0.4)
            {
                float length = 25f + random.NextSingle() * 50f;
                float signTop = top - 6f - random.NextSingle() * 20f;
                int face = random.Next(4);
                Vector2 outward = face switch { 0 => new(1f, 0f), 1 => new(-1f, 0f), 2 => new(0f, 1f), _ => new(0f, -1f) };
                float reach = (face < 2 ? width : depth) / 2f + 0.6f;
                var at = new Vector3(centre.X + outward.X * reach, signTop - length / 2f, centre.Y + outward.Y * reach);
                neon.AddBox(at, face < 2 ? new Vector3(0.6f, length, 3f) : new Vector3(3f, length, 0.6f), Vector2.Zero);
            }

            // Bands of light round it, every so often up its height.
            if (random.NextDouble() < 0.25)
            {
                float gap = 30f + random.NextSingle() * 30f;
                for (float y = top - gap; y > top - 200f && y > _street; y -= gap) AddRim(neon, centre, width, depth, y);
            }

            if (roof > 180f)
            {
                warning.AddBox(new Vector3(centre.X, roof + 4f, centre.Y), new Vector3(1.2f, 8f, 1.2f), Vector2.Zero);
            }
        }

        /// <summary>A thin glowing strip round the edge of a block, at <paramref name="height"/>.</summary>
        private static void AddRim(MeshBuilder neon, Vector2 centre, float width, float depth, float height)
        {
            const float thickness = 0.8f;
            float halfWidth = width / 2f + thickness / 2f, halfDepth = depth / 2f + thickness / 2f;
            float y = height - thickness / 2f;
            neon.AddBox(new Vector3(centre.X, y, centre.Y - halfDepth), new Vector3(width + thickness * 2f, thickness, thickness), Vector2.Zero);
            neon.AddBox(new Vector3(centre.X, y, centre.Y + halfDepth), new Vector3(width + thickness * 2f, thickness, thickness), Vector2.Zero);
            neon.AddBox(new Vector3(centre.X - halfWidth, y, centre.Y), new Vector3(thickness, thickness, depth), Vector2.Zero);
            neon.AddBox(new Vector3(centre.X + halfWidth, y, centre.Y), new Vector3(thickness, thickness, depth), Vector2.Zero);
        }

        private static bool Overlaps(Vector2 centre, float width, float depth) =>
            centre.X + width / 2f > _clearMinX && centre.X - width / 2f < _clearMaxX && centre.Y + depth / 2f > _clearMinZ && centre.Y - depth / 2f < _clearMaxZ;

        /// <summary>How far <paramref name="point"/> is outside the space the maps use.</summary>
        private static float DistanceOutside(Vector2 point)
        {
            float dx = MathF.Max(MathF.Max(_clearMinX - point.X, point.X - _clearMaxX), 0f);
            float dz = MathF.Max(MathF.Max(_clearMinZ - point.Y, point.Y - _clearMaxZ), 0f);
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// A tile of a tower's face at night: dark walls, a grid of windows, some dark and some lit, mostly warm, some
        /// cool, the odd one pink. Drawn as it is, with no lighting, so the windows shine.
        /// </summary>
        private static Texture WindowTexture(Random random)
        {
            const int size = 256;
            const int cell = size / 16;
            var wall = new Color(0.02f, 0.02f, 0.035f);
            var dark = new Color(0.045f, 0.05f, 0.09f);
            Color[] lit = [new(1f, 0.82f, 0.55f), new(1f, 0.9f, 0.7f), new(0.55f, 0.85f, 1f), new(1f, 0.45f, 0.75f)];

            byte[] pixels = new byte[size * size * 4];
            for (int floor = 0; floor < 16; floor++)
            {
                for (int column = 0; column < 16; column++)
                {
                    double roll = random.NextDouble();
                    bool on = roll >= 0.6;
                    Color window = on ? lit[roll < 0.82 ? 0 : roll < 0.9 ? 1 : roll < 0.97 ? 2 : 3] * (0.6f + random.NextSingle() * 0.4f) : dark;
                    for (int y = 0; y < cell; y++)
                    {
                        for (int x = 0; x < cell; x++)
                        {
                            // Each window takes most of its cell, with wall between.
                            bool glass = x >= 3 && x < cell - 3 && y >= 4 && y < cell - 3;
                            Color c = glass ? window : wall;
                            int i = ((floor * cell + y) * size + column * cell + x) * 4;
                            pixels[i] = (byte)(Math.Clamp(c.R, 0f, 1f) * 255f);
                            pixels[i + 1] = (byte)(Math.Clamp(c.G, 0f, 1f) * 255f);
                            pixels[i + 2] = (byte)(Math.Clamp(c.B, 0f, 1f) * 255f);
                            pixels[i + 3] = 255;
                        }
                    }
                }
            }

            return new Texture(size, size, pixels);
        }

        /// <summary>Boxes gathered into one mesh, to draw in one go. The flying cars use it too (see <see cref="Traffic"/>).</summary>
        internal sealed class MeshBuilder
        {
            private readonly List<MeshVertex> _vertices = new();
            private readonly List<uint> _indices = new();

            /// <summary>
            /// A box. Its faces' texture coordinates follow the world, one texture tile every <see cref="_tileSize"/>
            /// metres, starting <paramref name="offset"/> along it; its bottom and top have none.
            /// </summary>
            public void AddBox(Vector3 centre, Vector3 size, Vector2 offset)
            {
                Vector3 half = size / 2f;
                AddFace(centre, new Vector3(0f, 0f, half.Z), new Vector3(half.X, 0f, 0f), new Vector3(0f, half.Y, 0f), Vector3.UnitZ, size.X, size.Y, offset);
                AddFace(centre, new Vector3(0f, 0f, -half.Z), new Vector3(-half.X, 0f, 0f), new Vector3(0f, half.Y, 0f), -Vector3.UnitZ, size.X, size.Y, offset);
                AddFace(centre, new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, -half.Z), new Vector3(0f, half.Y, 0f), Vector3.UnitX, size.Z, size.Y, offset);
                AddFace(centre, new Vector3(-half.X, 0f, 0f), new Vector3(0f, 0f, half.Z), new Vector3(0f, half.Y, 0f), -Vector3.UnitX, size.Z, size.Y, offset);
                AddFace(centre, new Vector3(0f, half.Y, 0f), new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, -half.Z), Vector3.UnitY, 0f, 0f, offset);
                AddFace(centre, new Vector3(0f, -half.Y, 0f), new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, half.Z), -Vector3.UnitY, 0f, 0f, offset);
            }

            /// <summary>One face: centred <paramref name="out"/> from the box's middle, spanning <paramref name="right"/> and <paramref name="up"/> each way.</summary>
            private void AddFace(Vector3 centre, Vector3 @out, Vector3 right, Vector3 up, Vector3 normal, float width, float height, Vector2 offset)
            {
                uint start = (uint)_vertices.Count;
                Vector2 uvSize = new Vector2(width, height) / _tileSize;
                Vector2 uvStart = offset / _tileSize;
                Vector3 middle = centre + @out;
                _vertices.Add(new MeshVertex(middle - right - up, normal, uvStart));
                _vertices.Add(new MeshVertex(middle + right - up, normal, uvStart + new Vector2(uvSize.X, 0f)));
                _vertices.Add(new MeshVertex(middle + right + up, normal, uvStart + uvSize));
                _vertices.Add(new MeshVertex(middle - right + up, normal, uvStart + new Vector2(0f, uvSize.Y)));

                // Wound to face out, whichever way round the corners came.
                bool facesOut = Vector3.Dot(Vector3.Cross(right, up), normal) >= 0f;
                _indices.AddRange(facesOut ? [start, start + 1, start + 2, start, start + 2, start + 3] : [start, start + 2, start + 1, start, start + 3, start + 2]);
            }

            public Mesh ToMesh() => new(_vertices.ToArray(), _indices.ToArray());

            /// <summary>Makes the mesh a child of <paramref name="parent"/>, drawn without lighting, glowing <paramref name="glow"/> into the bloom.</summary>
            public void Finish(GameObject parent, string name, Color color, float glow, Texture? texture = null)
            {
                if (_vertices.Count == 0) return;

                var part = new GameObject(name);
                part.Transform.Parent = parent.Transform;
                MeshRenderer renderer = part.AddComponent<MeshRenderer>();
                renderer.Mesh = ToMesh();
                renderer.Material.Shading = ShadingModel.Unlit;
                renderer.Material.Color = color;
                renderer.Material.Texture = texture;
                renderer.Material.Emission = glow > 0f ? color : Color.Black;
                renderer.Material.EmissionStrength = glow;
                renderer.CastShadows = false;
            }
        }
    }
}
