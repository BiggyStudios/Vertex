using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Which point and spot lights reach each part of the view, so each pixel only works out the few lights near it
    /// rather than every light in view. The view is cut into a grid of boxes (clusters): <see cref="TilesX"/> by
    /// <see cref="TilesY"/> across the screen, and <see cref="Slices"/> deep, further apart the further they are from the
    /// camera, as things far off cover less of the screen. Each light goes in the clusters its sphere (or its cone's) touches.
    /// <para>
    /// What the shaders read (<see cref="Words"/>): one word for each cluster, the place its list starts (high 23 bits)
    /// and how many lights are in it (low 9 bits); then the lists, four lights to a word, a byte each (light 0 in the
    /// lowest byte).
    /// </para>
    /// </summary>
    internal sealed class LightClusters
    {
        public const int TilesX = 16;
        public const int TilesY = 9;
        public const int Slices = 24;
        public const int Count = TilesX * TilesY * Slices;

        /// <summary>Anything nearer the camera than this is in the first slice.</summary>
        public const float Near = 0.5f;

        private uint[] _words = new uint[Count + 64];
        private readonly int[] _counts = new int[Count];
        private int _entries;
        // For each light and slice, the tiles it reaches there (left, right, bottom, top; right before left for none).
        private (int X0, int X1, int Y0, int Y1)[] _rects = new (int, int, int, int)[64 * Slices];

        /// <summary>The clusters and their lists, as the shaders read them.</summary>
        public ReadOnlySpan<uint> Words => _words.AsSpan(0, Count + (_entries + 3) / 4);

        /// <summary>
        /// How to find a point's slice from how far in front of the camera it is: slice = log(depth) * x + y. z is how
        /// many lights are listed altogether.
        /// </summary>
        public Vector4 Slicing { get; private set; }

        /// <summary>How many lights are in the cluster at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="slice"/>), for tests.</summary>
        public int LightsIn(int x, int y, int slice) => (int)(_words[Index(x, y, slice)] & 511u);

        /// <summary>The lights in that cluster, for tests.</summary>
        public IEnumerable<int> LightIndices(int x, int y, int slice)
        {
            uint word = _words[Index(x, y, slice)];
            int start = (int)(word >> 9);
            for (int k = 0; k < (word & 511u); k++)
            {
                int entry = start + k;
                yield return (int)((_words[Count + entry / 4] >> (entry % 4 * 8)) & 255u);
            }
        }

        private static int Index(int x, int y, int slice) => (slice * TilesY + y) * TilesX + x;

        /// <summary>Sorts <paramref name="lights"/> into the clusters of the view <paramref name="view"/> and <paramref name="projection"/> look through.</summary>
        public void Build(IReadOnlyList<PointLightData> lights, Matrix4x4 view, Matrix4x4 projection)
        {
            int count = Math.Min(lights.Count, Shaders.ShaderLibrary.MaxPointLights);
            if (_rects.Length < count * Slices) _rects = new (int, int, int, int)[count * Slices];
            Array.Clear(_counts);
            _entries = 0;

            // How deep the slices go: as far as the furthest light reaches.
            Span<(Vector3 Centre, float Radius)> pieces = stackalloc (Vector3, float)[MaxPieces];
            float far = Near * 4f;
            for (int i = 0; i < count; i++)
            {
                int n = Pieces(lights[i], pieces);
                for (int p = 0; p < n; p++) far = MathF.Max(far, -Vector3.Transform(pieces[p].Centre, view).Z + pieces[p].Radius);
            }

            float scale = Slices / MathF.Log(far / Near);
            float bias = -MathF.Log(Near) * scale;
            bool orthographic = projection.M44 > 0.5f;

            // Each light, slice by slice: the tiles the parts of it in that slice are seen in.
            for (int i = 0; i < count; i++)
            {
                for (int z = 0; z < Slices; z++) _rects[i * Slices + z] = (TilesX, -1, TilesY, -1);

                int n = Pieces(lights[i], pieces);
                for (int p = 0; p < n; p++)
                {
                    (Vector3 centre, float radius) = pieces[p];
                    Vector3 inView = Vector3.Transform(centre, view);
                    float depth = -inView.Z;
                    if (depth + radius <= 0f) continue;
                    int z0 = SliceOf(depth - radius, scale, bias);
                    int z1 = SliceOf(depth + radius, scale, bias);

                    for (int z = z0; z <= z1; z++)
                    {
                        // Where the sphere and the slice overlap, and how wide the sphere is across there.
                        float near = MathF.Max(z == 0 ? float.MinValue : MathF.Exp((z - bias) / scale), depth - radius);
                        float farEnd = MathF.Min(z == Slices - 1 ? float.MaxValue : MathF.Exp((z + 1 - bias) / scale), depth + radius);
                        float off = depth < near ? near - depth : depth > farEnd ? depth - farEnd : 0f;
                        float across = MathF.Sqrt(MathF.Max(radius * radius - off * off, 0f));

                        // Reaching round the camera, it's all of it.
                        (int, int, int, int)? seen = !orthographic && near <= 0.05f
                            ? (0, TilesX - 1, 0, TilesY - 1)
                            : Rectangle(inView, across, near, farEnd, projection, orthographic);
                        if (seen is not { } r) continue;

                        ref (int X0, int X1, int Y0, int Y1) rect = ref _rects[i * Slices + z];
                        rect = (Math.Min(rect.X0, r.Item1), Math.Max(rect.X1, r.Item2), Math.Min(rect.Y0, r.Item3), Math.Max(rect.Y1, r.Item4));
                    }
                }

                for (int z = 0; z < Slices; z++)
                {
                    (int x0, int x1, int y0, int y1) = _rects[i * Slices + z];
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        _counts[Index(x, y, z)]++;
                }
            }

            // Where each cluster's list starts, then the lists filled in.
            int total = 0;
            for (int c = 0; c < Count; c++)
            {
                _words[c] = (uint)(total << 9) | (uint)_counts[c];
                total += _counts[c];
            }

            int needed = Count + (total + 3) / 4;
            if (_words.Length < needed)
            {
                uint[] grown = new uint[Math.Max(needed, _words.Length * 2)];
                Array.Copy(_words, grown, Count);
                _words = grown;
            }

            Array.Clear(_words, Count, (total + 3) / 4);
            Array.Clear(_counts);
            for (int i = 0; i < count; i++)
            for (int z = 0; z < Slices; z++)
            {
                (int x0, int x1, int y0, int y1) = _rects[i * Slices + z];
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = Index(x, y, z);
                    int entry = (int)(_words[c] >> 9) + _counts[c]++;
                    _words[Count + entry / 4] |= (uint)i << (entry % 4 * 8);
                }
            }

            _entries = total;
            Slicing = new Vector4(scale, bias, total, 0f);
        }

        /// <summary>
        /// The tiles a box <paramref name="across"/> either side of <paramref name="centre"/> (in the view), from
        /// <paramref name="near"/> to <paramref name="far"/> in front of the camera, is seen in; null if it's off the screen.
        /// </summary>
        private static (int, int, int, int)? Rectangle(Vector3 centre, float across, float near, float far, Matrix4x4 projection, bool orthographic)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    centre.X + ((corner & 1) == 0 ? -across : across),
                    centre.Y + ((corner & 2) == 0 ? -across : across),
                    -((corner & 4) == 0 ? near : far));
                Vector4 clip = Vector4.Transform(new Vector4(point, 1f), projection);
                float w = orthographic ? 1f : clip.W;
                minX = MathF.Min(minX, clip.X / w);
                maxX = MathF.Max(maxX, clip.X / w);
                minY = MathF.Min(minY, clip.Y / w);
                maxY = MathF.Max(maxY, clip.Y / w);
            }

            if (maxX < -1f || minX > 1f || maxY < -1f || minY > 1f) return null;
            return (TileOf(minX, TilesX), TileOf(maxX, TilesX), TileOf(minY, TilesY), TileOf(maxY, TilesY));
        }

        private static int SliceOf(float depth, float scale, float bias) =>
            depth <= Near ? 0 : Math.Clamp((int)(MathF.Log(depth) * scale + bias), 0, Slices - 1);

        private static int TileOf(float ndc, int tiles) => Math.Clamp((int)((ndc * 0.5f + 0.5f) * tiles), 0, tiles - 1);

        /// <summary>The most spheres <see cref="Pieces"/> makes for one light.</summary>
        internal const int MaxPieces = 4;

        /// <summary>
        /// Spheres that between them hold all a light reaches, into <paramref name="into"/>; returns how many. A point
        /// light's is one, its range round it. A spot light's are round its cone only: a wide one in one sphere round the
        /// far rim, a narrow one in a few along its beam, each as wide as the beam is there, as one sphere round a long
        /// thin cone would be far wider than it.
        /// </summary>
        internal static int Pieces(in PointLightData light, Span<(Vector3 Centre, float Radius)> into)
        {
            float cosine = Math.Clamp(light.SpotOuter, -1f, 1f);
            if (!light.IsSpot || cosine <= 0f)
            {
                into[0] = (light.Position, light.Range);
                return 1;
            }

            Vector3 direction = Vector3.Normalize(light.Direction);
            float sine = MathF.Sqrt(1f - cosine * cosine);
            if (cosine <= 0.7071f)
            {
                // Wider than a right angle across.
                into[0] = (light.Position + direction * (light.Range * cosine), light.Range * sine);
                return 1;
            }

            // Along the beam in equal steps: each piece as wide as the beam at its far end.
            int count = cosine > 0.866f ? MaxPieces : 2;
            float tangent = sine / cosine;
            float step = light.Range / count;
            for (int k = 0; k < count; k++)
            {
                float across = MathF.Min((k + 1) * step * tangent, light.Range * sine);
                into[k] = (light.Position + direction * ((k + 0.5f) * step), MathF.Sqrt(step * step * 0.25f + across * across));
            }

            return count;
        }
    }
}
