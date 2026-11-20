using System.Numerics;

using Vertex.Rendering.Baking;
using Vertex.Rendering.Shaders;

namespace Vertex.Rendering
{
    /// <summary>What a bake made: the baked light, and the static renderers it's for, in the order of their <see cref="MeshRenderer.LightmapIndex"/>.</summary>
    public sealed class LightBakeResult
    {
        public required BakedLighting Lighting { get; init; }
        public required IReadOnlyList<MeshRenderer> Renderers { get; init; }

        /// <summary>The lights with a channel of the shadow mask (see <see cref="BakedLighting.ShadowMask"/>), and which.</summary>
        public IReadOnlyList<(Light Light, int Channel)> MaskChannels { get; init; } = [];

        /// <summary>The detail the lightmap was made at: <see cref="BakeSettings.Detail"/>, or less if it wouldn't fit.</summary>
        public float Detail { get; init; }

        /// <summary>How many of the lightmap's pixels are on a surface.</summary>
        public int Texels { get; init; }

        /// <summary>
        /// Saves the baked light to <paramref name="file"/> and gives it to <paramref name="scene"/>: each static renderer
        /// gets its place in it, and the scene's <see cref="Lighting.BakedLightingFile"/> names the file. Call on the
        /// thread the scene's used on.
        /// </summary>
        public void Apply(Scene scene, string file)
        {
            Lighting.Save(file);
            Assets.Forget(file);
            foreach (MeshRenderer renderer in scene.FindComponents<MeshRenderer>()) renderer.LightmapIndex = -1;
            for (int i = 0; i < Renderers.Count; i++) Renderers[i].LightmapIndex = i;
            foreach (Light light in scene.FindComponents<Light>()) light.ShadowMaskChannel = -1;
            foreach ((Light light, int channel) in MaskChannels) light.ShadowMaskChannel = channel;
            scene.Lighting.BakedLightingFile = Assets.RelativePath(file);
        }
    }

    /// <summary>
    /// Bakes a scene's light: the light falling on its static meshes (see <see cref="MeshRenderer.Static"/>) into a
    /// lightmap, with soft shadows from <see cref="LightMode.Baked"/> lights, the sky's light kept out of where it can't
    /// reach, and light bounced off everything (including from <see cref="LightMode.Mixed"/> lights and glowing
    /// materials); and the light in the space between them into light probes, for things that move. Worked out by
    /// sending rays round the scene on all the computer's cores.
    /// <para>
    /// <see cref="Prepare"/> copies what it needs from the scene, on the thread that uses it; <see cref="Run"/> can then
    /// go on any thread, while the scene carries on; <see cref="LightBakeResult.Apply"/> gives the scene the result.
    /// </para>
    /// </summary>
    public sealed class LightBaker
    {
        private sealed class Surface
        {
            public bool Lit;
            public Vector3 Color;
            public Vector3 Emission;
            public float Metallic;
            public byte[]? Pixels;
            public int Width, Height;
            public Vector2 Scale = Vector2.One, Offset;
        }

        private sealed class BakeObject
        {
            public required MeshRenderer Renderer;
            public required Mesh Mesh;
            public required Matrix4x4 World;
            public required Surface Surface;
            public LightmapUnwrap? Unwrap;
            public Vector2[] Texel = [];
            public int FirstChart;

            // For unlit ones, which aren't in the lightmap: the mesh as it stands.
            public Vector3[] Positions = [];
            public uint[] Indices = [];
        }

        // Channel: which of the shadow mask's it has (see BakedLighting.ShadowMask), or -1.
        private readonly record struct BakeLight(Light Source, LightType Type, Vector3 Position, Vector3 Direction, Vector3 Color, float Range,
            float SpotScale, float SpotOffset, float Size, bool Direct, int Channel);

        private readonly List<BakeObject> _objects = new();
        private readonly List<BakeLight> _lights = new();
        private Vector3 _skyMiddle, _skyChange;
        private float _detail;
        private int _bounces;
        private BakeQuality _quality;
        private float _probeSpacing;
        private int _maxSize;

        private LightBaker() {}

        /// <summary>How many objects' light will be baked into the lightmap.</summary>
        public int StaticObjects => _objects.Count(o => o.Surface.Lit);

        /// <summary>Copies what baking needs from <paramref name="scene"/>: its static meshes, its lights that bake, its sky and its settings.</summary>
        public static LightBaker Prepare(Scene scene)
        {
            var baker = new LightBaker();
            Lighting lighting = scene.Lighting;
            baker._detail = Math.Clamp(lighting.Baking.Detail, 0.05f, 256f);
            baker._bounces = Math.Clamp(lighting.Baking.Bounces, 0, 8);
            baker._quality = lighting.Baking.Quality;
            baker._probeSpacing = MathF.Max(lighting.Baking.ProbeSpacing, 0.25f);
            baker._maxSize = Math.Clamp(lighting.Baking.MaxLightmapSize, 64, 8192);

            foreach (MeshRenderer renderer in scene.FindComponents<MeshRenderer>())
            {
                if (!renderer.Static || !renderer.Enabled || !renderer.GameObject.ActiveInHierarchy || renderer.Mesh is not { } mesh) continue;
                if (mesh.Indices.Length < 3 || renderer is SkinnedMeshRenderer) continue;

                Material material = renderer.Material;
                var surface = new Surface
                {
                    Lit = material.Shading == ShadingModel.Lit,
                    Color = FrameUniforms.ToLinear(material.Color),
                    Emission = FrameUniforms.ToLinear(material.Emission) * MathF.Max(material.EmissionStrength, 0f),
                    Metallic = Math.Clamp(material.Metallic, 0f, 1f),
                    Scale = material.TextureScale,
                    Offset = material.TextureOffset
                };
                if (material.Texture is { } texture && texture is not RenderTexture && texture.Pixels.Length >= texture.Width * texture.Height * 4)
                {
                    surface.Pixels = texture.Pixels;
                    surface.Width = texture.Width;
                    surface.Height = texture.Height;
                }

                baker._objects.Add(new BakeObject { Renderer = renderer, Mesh = mesh, World = renderer.Transform.WorldMatrix, Surface = surface });
            }

            // The sun, as the renderer has it: the first directional light that casts shadows, or else the first.
            Light? sun = null;
            foreach (Light light in scene.FindComponents<Light>())
            {
                if (!light.Enabled || light.Type != LightType.Directional) continue;
                if (sun == null || (light.CastShadows && !sun.CastShadows)) sun = light;
            }

            EnvironmentData environment = EnvironmentData.From(lighting);
            Vector3 towardSun = sun != null ? -Vector3.Normalize(sun.Transform.Forward) : Vector3.UnitY;
            Vector3 sunColor = sun != null ? new Vector3(sun.Color.R, sun.Color.G, sun.Color.B) * sun.Intensity : Vector3.Zero;
            bool atmosphere = environment.Sky == SkyStyle.Atmosphere && sun != null;
            (Vector3 skyLight, Vector3 groundLight, _, _) = FrameUniforms.Sky(environment, sun != null, towardSun, sunColor);

            // The sky's light from each way: from the ground's below to the sky's above, steeper than ambientLight's
            // blend so that an open surface gathers just what the realtime sky gives it.
            baker._skyMiddle = (skyLight + groundLight) * 0.5f * environment.AmbientIntensity;
            baker._skyChange = (skyLight - groundLight) * 0.75f * environment.AmbientIntensity;

            foreach (Light light in scene.FindComponents<Light>())
            {
                if (!light.Enabled || !light.GameObject.ActiveInHierarchy || light.Mode == LightMode.Realtime) continue;
                var color = new Vector3(light.Color.R, light.Color.G, light.Color.B) * light.Intensity;
                if (light == sun && atmosphere) color *= Atmosphere.SunlightTint(towardSun.Y);
                if (color == Vector3.Zero) continue;

                float scale = 0f, offset = 1f;
                if (light.Type == LightType.Spot)
                {
                    float half = Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f * MathF.PI / 180f;
                    float inner = half * (1f - Math.Clamp(light.SpotSoftness, 0f, 1f));
                    float cosOuter = MathF.Cos(half), cosInner = MathF.Max(MathF.Cos(inner), cosOuter + 1e-4f);
                    scale = 1f / (cosInner - cosOuter);
                    offset = -cosOuter * scale;
                }

                baker._lights.Add(new BakeLight(light, light.Type, light.Transform.WorldPosition, Vector3.Normalize(light.Transform.Forward), color,
                    MathF.Max(light.Range, 0.01f), scale, offset, MathF.Max(light.Size, 0f), light.Mode == LightMode.Baked, -1));
            }

            baker.ShareMaskChannels();
            return baker;
        }

        /// <summary>
        /// Gives each baked light that casts shadows a channel of the shadow mask, so things that move can cast shadows from
        /// it: lights whose light could meet need different ones, and there are four. The sun first (it reaches everything),
        /// then the brightest and furthest-reaching; those left over get none, and only cast their baked shadows.
        /// </summary>
        private void ShareMaskChannels()
        {
            var order = new List<int>();
            for (int i = 0; i < _lights.Count; i++)
            {
                if (_lights[i].Direct && _lights[i].Source.CastShadows) order.Add(i);
            }

            static float Importance(in BakeLight light) => light.Type == LightType.Directional
                ? float.MaxValue
                : (light.Color.X + light.Color.Y + light.Color.Z) * light.Range;
            order.Sort((a, b) => Importance(_lights[b]).CompareTo(Importance(_lights[a])));

            static bool Meet(in BakeLight a, in BakeLight b) => a.Type == LightType.Directional || b.Type == LightType.Directional ||
                Vector3.Distance(a.Position, b.Position) < a.Range + b.Range;

            Span<bool> taken = stackalloc bool[4];
            foreach (int i in order)
            {
                taken.Clear();
                foreach (int j in order)
                {
                    if (j != i && _lights[j].Channel >= 0 && Meet(_lights[i], _lights[j])) taken[_lights[j].Channel] = true;
                }

                int channel = taken.IndexOf(false);
                if (channel >= 0) _lights[i] = _lights[i] with { Channel = channel };
            }
        }

        /// <summary>Whether any light has a channel of the shadow mask.</summary>
        private bool Masked => _lights.Any(l => l.Channel >= 0);

        // How many rays for each part, by quality.
        private (int Direct, int Gather, int Probe) Samples => _quality switch
        {
            BakeQuality.Preview => (4, 48, 96),
            BakeQuality.High => (48, 512, 768),
            _ => (16, 160, 256)
        };

        private const int _padding = 2;

        // The lightmap as it's worked out.
        private int _width, _height;
        private Vector3[] _position = [];
        private Vector3[] _normal = [];
        private Vector3[] _face = [];
        private int[] _chart = [];
        private bool[] _valid = [];
        private float[] _coverage = [];
        private TriangleTree _tree = null!;
        private readonly List<(int Object, int Triangle)> _triangles = new();
        private float _offset;
        private float _usedDetail = 1f;

        /// <summary>
        /// Bakes. Slow (seconds to minutes, by the scene's size, <see cref="BakeSettings.Detail"/> and
        /// <see cref="BakeSettings.Quality"/>), so run it off the main thread: <paramref name="progress"/> hears what it's
        /// doing and how far through it is (0 to 1), and <paramref name="cancel"/> stops it.
        /// </summary>
        public LightBakeResult Run(Action<string, float>? progress = null, CancellationToken cancel = default)
        {
            progress?.Invoke("Laying out the lightmap", 0f);
            foreach (BakeObject o in _objects)
            {
                if (o.Surface.Lit) o.Unwrap = LightmapUnwrap.Of(o.Mesh, o.World);
                else
                {
                    o.Positions = o.Mesh.Vertices.Select(v => Vector3.Transform(v.Position, o.World)).ToArray();
                    o.Indices = o.Mesh.Indices;
                }
                cancel.ThrowIfCancellationRequested();
            }

            float detail = Pack();
            _usedDetail = detail;
            _offset = 0.002f + 0.1f / detail;
            progress?.Invoke("Finding the surfaces", 0.05f);
            Rasterise();
            BuildTree();
            cancel.ThrowIfCancellationRequested();

            (int directSamples, int gatherRays, int probeRays) = Samples;
            int count = _width * _height;

            // Straight from the lights: the baked ones' into the lightmap, and every baking light's for what bounces.
            progress?.Invoke("Lighting from the lights", 0.1f);
            var direct = new Vector3[count];
            var directAll = new Vector3[count];

            // And how much of each light in the shadow mask gets past what's in its way: channels 0 to 2 in one, 3 in the
            // other's x, so they're smoothed as the light is.
            bool masked = Masked;
            Vector3[] maskLow = masked ? new Vector3[count] : [], maskHigh = masked ? new Vector3[count] : [];
            ParallelTexels(cancel, i =>
            {
                var random = new Random32((uint)i * 9781u + 17u);
                Span<float> mask = [1f, 1f, 1f, 1f];
                (direct[i], directAll[i]) = DirectAt(_position[i], _normal[i], _face[i], directSamples, ref random, mask);
                if (!masked) return;
                maskLow[i] = new Vector3(mask[0], mask[1], mask[2]);
                maskHigh[i] = new Vector3(mask[3], 0f, 0f);
            });

            // Light gathered from all round: the sky, glowing things, and the light on everything else, bounce by bounce.
            var indirect = new Vector3[count];
            var source = new Vector3[count];
            int passes = Math.Max(_bounces, 1);
            for (int pass = 0; pass < passes; pass++)
            {
                progress?.Invoke(_bounces == 0 ? "Lighting from the sky" : $"Bouncing light ({pass + 1} of {passes})", 0.2f + 0.6f * pass / passes);
                for (int i = 0; i < count; i++) source[i] = _bounces == 0 ? Vector3.Zero : directAll[i] + indirect[i];
                Dilate(source, 2);

                var gathered = new Vector3[count];
                var inside = new bool[count];
                int thisPass = pass;
                ParallelTexels(cancel, i =>
                {
                    var random = new Random32((uint)i * 7919u + (uint)thisPass * 104729u + 3u);
                    (gathered[i], float backfaces) = Gather(_position[i], _normal[i], _face[i], gatherRays, source, ref random);
                    inside[i] = backfaces > 0.35f;
                });

                indirect = gathered;
                if (pass == 0)
                {
                    // Pixels whose rays mostly hit the backs of things are inside something (where a wall meets the
                    // floor, say): they're filled in from their neighbours instead.
                    for (int i = 0; i < count; i++) if (inside[i]) _valid[i] = false;
                }
            }

            progress?.Invoke("Smoothing", 0.8f);
            Smooth(indirect, 2);
            Smooth(direct, 1);
            var light = new Vector3[count];
            for (int i = 0; i < count; i++) light[i] = _valid[i] ? direct[i] + indirect[i] : Vector3.Zero;
            Dilate(light, _padding + 2);

            var pixels = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                (byte r, byte g, byte b, byte a) = BakedLighting.Encode(light[i]);
                pixels[i * 4] = r;
                pixels[i * 4 + 1] = g;
                pixels[i * 4 + 2] = b;
                pixels[i * 4 + 3] = a;
            }

            var baked = new BakedLighting();
            var renderers = new List<MeshRenderer>();
            if (_objects.Any(o => o.Unwrap != null))
            {
                baked.Lightmap = new Texture(_width, _height, pixels);
                if (masked) baked.ShadowMask = new Texture(_width, _height, MaskPixels(maskLow, maskHigh));
                foreach (BakeObject o in _objects)
                {
                    if (o.Unwrap is not { } unwrap) continue;
                    var uvs = new Vector2[o.Texel.Length];
                    for (int v = 0; v < uvs.Length; v++) uvs[v] = o.Texel[v] / new Vector2(_width, _height);
                    baked.Meshes.Add(new BakedMesh(unwrap.SourceVertices, unwrap.Indices, uvs, BakedMesh.Hash(o.Mesh)));
                    renderers.Add(o.Renderer);
                }
            }

            // The light in the space between, for things that move: what each probe sees all round it.
            progress?.Invoke("Light probes", 0.85f);
            for (int i = 0; i < count; i++) source[i] = directAll[i] + indirect[i];
            Dilate(source, 2);
            baked.Probes = BakeProbes(source, probeRays, cancel);

            progress?.Invoke("Done", 1f);
            int texels = 0;
            for (int i = 0; i < count; i++) if (_valid[i]) texels++;
            var channels = new List<(Light, int)>();
            if (baked.ShadowMask != null)
            {
                foreach (BakeLight bakeLight in _lights) if (bakeLight.Channel >= 0) channels.Add((bakeLight.Source, bakeLight.Channel));
            }

            return new LightBakeResult { Lighting = baked, Renderers = renderers, MaskChannels = channels, Detail = detail, Texels = texels };
        }

        /// <summary>The shadow mask's pixels, smoothed and spread over the gaps as the light is.</summary>
        private byte[] MaskPixels(Vector3[] low, Vector3[] high)
        {
            int count = _width * _height;
            Smooth(low, 1);
            Smooth(high, 1);
            for (int i = 0; i < count; i++)
            {
                if (_valid[i]) continue;
                low[i] = Vector3.One;
                high[i] = Vector3.One;
            }

            Dilate(low, _padding + 2);
            Dilate(high, _padding + 2);

            static byte Byte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
            var pixels = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                pixels[i * 4] = Byte(low[i].X);
                pixels[i * 4 + 1] = Byte(low[i].Y);
                pixels[i * 4 + 2] = Byte(low[i].Z);
                pixels[i * 4 + 3] = Byte(high[i].X);
            }

            return pixels;
        }

        // ---- Laying out the lightmap ----------------------------------------------------------------------------

        /// <summary>
        /// Places every piece of every static mesh in the lightmap, side by side in rows, tallest first, with a gap
        /// round each so they don't bleed into each other. If they don't fit at <see cref="BakeSettings.Detail"/>,
        /// with less. Returns the detail they're at.
        /// </summary>
        private float Pack()
        {
            var pieces = new List<(BakeObject Object, int Chart, int Width, int Height)>();
            float detail = _detail;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                pieces.Clear();
                long area = 0;
                foreach (BakeObject o in _objects)
                {
                    if (o.Unwrap is not { } unwrap) continue;
                    for (int c = 0; c < unwrap.Charts.Count; c++)
                    {
                        Vector2 size = unwrap.Charts[c].Size * detail;
                        int width = (int)MathF.Ceiling(size.X) + 1 + 2 * _padding;
                        int height = (int)MathF.Ceiling(size.Y) + 1 + 2 * _padding;
                        pieces.Add((o, c, width, height));
                        area += (long)width * height;
                    }
                }

                int side = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(area / 0.82f) / 4f) * 4, 16, _maxSize);
                pieces.Sort((a, b) => b.Height != a.Height ? b.Height.CompareTo(a.Height) : b.Width.CompareTo(a.Width));

                int x = 0, y = 0, row = 0;
                bool fits = true;
                var placed = new Dictionary<(BakeObject, int), (int X, int Y)>();
                foreach ((BakeObject o, int chart, int width, int height) in pieces)
                {
                    if (width > side)
                    {
                        fits = false;
                        break;
                    }

                    if (x + width > side)
                    {
                        x = 0;
                        y += row;
                        row = 0;
                    }

                    placed[(o, chart)] = (x, y);
                    x += width;
                    row = Math.Max(row, height);
                }

                int used = y + row;
                if (fits && used <= _maxSize)
                {
                    _width = side;
                    _height = Math.Max((used + 3) / 4 * 4, 4);
                    int chartIndex = 0;
                    foreach (BakeObject o in _objects)
                    {
                        if (o.Unwrap is not { } unwrap) continue;
                        o.FirstChart = chartIndex;
                        o.Texel = new Vector2[unwrap.Flat.Length];
                        for (int v = 0; v < o.Texel.Length; v++)
                        {
                            (int px, int py) = placed[(o, unwrap.VertexChart[v])];
                            o.Texel[v] = new Vector2(px + _padding + 0.5f, py + _padding + 0.5f) + unwrap.Flat[v] * detail;
                        }

                        chartIndex += unwrap.Charts.Count;
                    }

                    return detail;
                }

                // Too big: less detail, by how much too big it was.
                float over = MathF.Max(used / (float)_maxSize, area / (0.82f * _maxSize * _maxSize));
                detail /= MathF.Max(MathF.Sqrt(over) * 1.05f, 1.1f);
            }

            throw new InvalidOperationException("The static meshes don't fit in the lightmap: make it bigger, or the detail less.");
        }

        /// <summary>
        /// Finds what's under each of the lightmap's pixels: where it is in the world and which way it faces. Pixels whose
        /// middle is just off a piece's edge take its nearest point, so the edges are lit too.
        /// </summary>
        private void Rasterise()
        {
            int count = _width * _height;
            _position = new Vector3[count];
            _normal = new Vector3[count];
            _face = new Vector3[count];
            _chart = new int[count];
            _valid = new bool[count];
            _coverage = new float[count];
            Array.Fill(_coverage, float.MaxValue);

            foreach (BakeObject o in _objects)
            {
                if (o.Unwrap is not { } unwrap) continue;
                uint[] indices = unwrap.Indices;
                for (int t = 0; t < indices.Length / 3; t++)
                {
                    int i0 = (int)indices[t * 3], i1 = (int)indices[t * 3 + 1], i2 = (int)indices[t * 3 + 2];
                    Vector2 a = o.Texel[i0], b = o.Texel[i1], c = o.Texel[i2];
                    Vector3 pa = unwrap.Positions[i0], pb = unwrap.Positions[i1], pc = unwrap.Positions[i2];
                    Vector3 face = Vector3.Cross(pb - pa, pc - pa);
                    Vector3 average = unwrap.Normals[i0] + unwrap.Normals[i1] + unwrap.Normals[i2];
                    face = face.LengthSquared() > 1e-20f ? Vector3.Normalize(face) : Vector3.Normalize(average + new Vector3(0f, 1e-6f, 0f));
                    if (Vector3.Dot(face, average) < 0f) face = -face;

                    int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))) - 1, 0);
                    int x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))) + 1, _width - 1);
                    int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))) - 1, 0);
                    int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))) + 1, _height - 1);
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var centre = new Vector2(x + 0.5f, y + 0.5f);
                        (Vector3 weights, float distance) = Nearest(centre, a, b, c);
                        if (distance > 0.75f) continue;

                        int i = y * _width + x;
                        if (distance >= _coverage[i]) continue;
                        _coverage[i] = distance;
                        _valid[i] = true;
                        _chart[i] = o.FirstChart + unwrap.VertexChart[i0];
                        _position[i] = pa * weights.X + pb * weights.Y + pc * weights.Z;
                        Vector3 normal = unwrap.Normals[i0] * weights.X + unwrap.Normals[i1] * weights.Y + unwrap.Normals[i2] * weights.Z;
                        _normal[i] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : face;
                        _face[i] = face;
                    }
                }
            }
        }

        /// <summary>The point of triangle abc nearest <paramref name="p"/> (as weights of its corners), and how far it is.</summary>
        private static (Vector3 Weights, float Distance) Nearest(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector2.Dot(v0, v0), d01 = Vector2.Dot(v0, v1), d11 = Vector2.Dot(v1, v1);
            float denominator = d00 * d11 - d01 * d01;
            if (MathF.Abs(denominator) > 1e-12f)
            {
                float d20 = Vector2.Dot(v2, v0), d21 = Vector2.Dot(v2, v1);
                float v = (d11 * d20 - d01 * d21) / denominator;
                float w = (d00 * d21 - d01 * d20) / denominator;
                if (v >= 0f && w >= 0f && v + w <= 1f) return (new Vector3(1f - v - w, v, w), 0f);
            }

            // Outside: the nearest point on its edges.
            (Vector3, float) best = (new Vector3(1f, 0f, 0f), Vector2.Distance(p, a));
            void Edge(Vector2 from, Vector2 to, int i, int j)
            {
                Vector2 edge = to - from;
                float along = edge.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(p - from, edge) / edge.LengthSquared(), 0f, 1f) : 0f;
                float distance = Vector2.Distance(p, from + edge * along);
                if (distance >= best.Item2) return;
                var weights = Vector3.Zero;
                weights = Set(weights, i, 1f - along);
                weights = Set(weights, j, along);
                best = (weights, distance);
            }

            Edge(a, b, 0, 1);
            Edge(b, c, 1, 2);
            Edge(c, a, 2, 0);

            // A little way in from the edge: right on it, it can be exactly where another surface meets this one (a wall
            // standing on a floor), and rays from there would slip through that one.
            Vector2 onEdge = a * best.Item1.X + b * best.Item1.Y + c * best.Item1.Z;
            Vector2 middle = (a + b + c) / 3f;
            float inward = Vector2.Distance(onEdge, middle);
            if (inward > 1e-6f && MathF.Abs(denominator) > 1e-12f)
            {
                Vector2 q = onEdge + (middle - onEdge) * (MathF.Min(0.25f, inward) / inward) - a;
                float d20 = Vector2.Dot(q, v0), d21 = Vector2.Dot(q, v1);
                float v = (d11 * d20 - d01 * d21) / denominator;
                float w = (d00 * d21 - d01 * d20) / denominator;
                return (new Vector3(1f - v - w, v, w), best.Item2);
            }

            return best;
        }

        private static Vector3 Set(Vector3 v, int i, float value) => i switch
        {
            0 => v with { X = value },
            1 => v with { Y = value },
            _ => v with { Z = value }
        };

        private void BuildTree()
        {
            var triangles = new List<(Vector3, Vector3, Vector3)>();
            var facing = new List<Vector3>();
            _triangles.Clear();
            for (int o = 0; o < _objects.Count; o++)
            {
                BakeObject bakeObject = _objects[o];
                Vector3[] positions = bakeObject.Unwrap?.Positions ?? bakeObject.Positions;
                uint[] indices = bakeObject.Unwrap?.Indices ?? bakeObject.Indices;
                Vector3[]? normals = bakeObject.Unwrap?.Normals;
                for (int t = 0; t < indices.Length / 3; t++)
                {
                    Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
                    triangles.Add((a, b, c));
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    facing.Add(normals != null ? normals[indices[t * 3]] + normals[indices[t * 3 + 1]] + normals[indices[t * 3 + 2]] : cross);
                    _triangles.Add((o, t));
                }
            }

            _tree = new TriangleTree(triangles, facing);
        }

        private void ParallelTexels(CancellationToken cancel, Action<int> each)
        {
            var options = new ParallelOptions { CancellationToken = cancel };
            Parallel.For(0, _height, options, y =>
            {
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    if (_valid[i]) each(i);
                }
            });
        }

        // ---- Light ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The light reaching a point straight from the lights, in their light's units (a white surface facing one fully
        /// gets its colour): from the baked lights only, and from every light that bakes (for bouncing on). Each light's
        /// seen from points over its size, so its shadows are soft. How much of each light with a channel in the shadow
        /// mask gets through goes in <paramref name="mask"/>.
        /// </summary>
        private (Vector3 Baked, Vector3 All) DirectAt(Vector3 position, Vector3 normal, Vector3 face, int samples, ref Random32 random, Span<float> mask)
        {
            Vector3 baked = Vector3.Zero, all = Vector3.Zero;
            Vector3 origin = position + face * _offset;
            foreach (BakeLight light in _lights)
            {
                Vector3 arriving = Arriving(light, position, normal);
                if (arriving == Vector3.Zero) continue;

                float lit = Visible(light, origin, samples, ref random);
                if (light.Channel >= 0) mask[light.Channel] = lit;
                if (lit <= 0f) continue;
                all += arriving * lit;
                if (light.Direct) baked += arriving * lit;
            }

            return (baked, all);
        }

        /// <summary>How much of a light reaches a point on a surface facing <paramref name="normal"/>, before shadows: as the shaders work it out.</summary>
        private static Vector3 Arriving(in BakeLight light, Vector3 position, Vector3 normal)
        {
            if (light.Type == LightType.Directional) return light.Color * MathF.Max(Vector3.Dot(normal, -light.Direction), 0f);

            Vector3 toLight = light.Position - position;
            float distance = toLight.Length();
            if (distance >= light.Range || distance < 1e-5f) return Vector3.Zero;

            Vector3 l = toLight / distance;
            float falloff = 1f - distance / light.Range;
            float cone = light.Type == LightType.Spot ? Math.Clamp(Vector3.Dot(-l, light.Direction) * light.SpotScale + light.SpotOffset, 0f, 1f) : 1f;
            return light.Color * (falloff * falloff * cone * cone * MathF.Max(Vector3.Dot(normal, l), 0f));
        }

        /// <summary>How much of a light can be seen from <paramref name="origin"/>, from 0 (none) to 1, over points across its size.</summary>
        private float Visible(in BakeLight light, Vector3 origin, int samples, ref Random32 random)
        {
            int count = light.Size > 0f ? samples : 1;
            int seen = 0;
            for (int s = 0; s < count; s++)
            {
                Vector3 direction;
                float distance;
                if (light.Type == LightType.Directional)
                {
                    // Towards somewhere on the sun's disc.
                    float angle = light.Size * 0.5f * MathF.PI / 180f;
                    direction = InCone(-light.Direction, angle, ref random);
                    distance = 1e6f;
                }
                else
                {
                    Vector3 target = light.Position + (count > 1 ? OnSphere(ref random) * light.Size : Vector3.Zero);
                    Vector3 toLight = target - origin;
                    distance = toLight.Length();
                    if (distance < 1e-5f)
                    {
                        seen++;
                        continue;
                    }

                    direction = toLight / distance;
                    distance -= 1e-3f;
                }

                if (!_tree.Occluded(origin, direction, distance)) seen++;
            }

            return seen / (float)count;
        }

        /// <summary>
        /// The light arriving from all round a point on a surface: rays sent out over the side it faces (more where they
        /// matter more), each seeing the sky, something glowing, or a surface lit by <paramref name="source"/>. Also how
        /// many hit the backs of things (from inside something, that's most).
        /// </summary>
        private (Vector3 Light, float Backfaces) Gather(Vector3 position, Vector3 normal, Vector3 face, int rays, Vector3[] source, ref Random32 random)
        {
            Vector3 origin = position + face * _offset;
            Vector3 total = Vector3.Zero;
            int backfaces = 0;
            for (int r = 0; r < rays; r++)
            {
                Vector3 direction = Cosine(normal, ref random);
                if (Vector3.Dot(direction, face) <= 0f) direction = Vector3.Normalize(direction - face * (2f * Vector3.Dot(direction, face)));
                total += Seen(origin, direction, source, ref backfaces);
            }

            return (total / rays, backfaces / (float)rays);
        }

        /// <summary>The light coming back along a ray: the sky's if it misses, or what it hits gives off.</summary>
        private Vector3 Seen(Vector3 origin, Vector3 direction, Vector3[] source, ref int backfaces)
        {
            if (!_tree.Intersect(origin, direction, 1e6f, out RayHit hit)) return Sky(direction);
            if (hit.Backface)
            {
                backfaces++;
                return Vector3.Zero;
            }

            (int o, int t) = _triangles[hit.Triangle];
            BakeObject bakeObject = _objects[o];
            Surface surface = bakeObject.Surface;
            float w = 1f - hit.U - hit.V;
            if (!surface.Lit) return surface.Color * TextureAt(bakeObject, t, w, hit.U, hit.V) + surface.Emission;

            LightmapUnwrap unwrap = bakeObject.Unwrap!;
            int i0 = (int)unwrap.Indices[t * 3], i1 = (int)unwrap.Indices[t * 3 + 1], i2 = (int)unwrap.Indices[t * 3 + 2];
            Vector2 texel = bakeObject.Texel[i0] * w + bakeObject.Texel[i1] * hit.U + bakeObject.Texel[i2] * hit.V;
            int x = Math.Clamp((int)texel.X, 0, _width - 1), y = Math.Clamp((int)texel.Y, 0, _height - 1);
            Vector3 albedo = surface.Color * TextureAt(bakeObject, t, w, hit.U, hit.V) * (1f - surface.Metallic);
            return albedo * source[y * _width + x] + surface.Emission;
        }

        /// <summary>The sky's light from a way, as the realtime sky lights things.</summary>
        private Vector3 Sky(Vector3 direction) => Vector3.Max(_skyMiddle + _skyChange * direction.Y, Vector3.Zero);

        private static readonly float[] _linear = Enumerable.Range(0, 256).Select(i => MathF.Pow(i / 255f, 2.2f)).ToArray();

        /// <summary>The material's texture where a ray hit, made linear: white with no texture.</summary>
        private static Vector3 TextureAt(BakeObject o, int triangle, float w, float u, float v)
        {
            Surface surface = o.Surface;
            if (surface.Pixels == null) return Vector3.One;

            int[] sources = o.Unwrap?.SourceVertices ?? [];
            uint[] indices = o.Unwrap?.Indices ?? o.Indices;
            int Corner(int k)
            {
                int index = (int)indices[triangle * 3 + k];
                return o.Unwrap != null ? sources[index] : index;
            }

            Vector2 uv = o.Mesh.Vertices[Corner(0)].UV * w + o.Mesh.Vertices[Corner(1)].UV * u + o.Mesh.Vertices[Corner(2)].UV * v;
            uv = uv * surface.Scale + surface.Offset;
            int x = (int)((uv.X - MathF.Floor(uv.X)) * surface.Width) % surface.Width;
            int y = (int)((uv.Y - MathF.Floor(uv.Y)) * surface.Height) % surface.Height;
            int p = (y * surface.Width + x) * 4;
            return new Vector3(_linear[surface.Pixels[p]], _linear[surface.Pixels[p + 1]], _linear[surface.Pixels[p + 2]]);
        }

        // ---- Finishing ------------------------------------------------------------------------------------------

        /// <summary>
        /// Takes the grain out: each pixel averaged with those near it on the same piece that face the same way and are
        /// near it in the world, so shadows' edges and corners stay. <paramref name="rounds"/> times, each reaching further.
        /// </summary>
        private void Smooth(Vector3[] light, int rounds)
        {
            float texelSize = 1f / MathF.Max(_usedDetail, 1e-3f);
            for (int round = 0; round < rounds; round++)
            {
                int step = 1 << round;
                var smoothed = new Vector3[light.Length];
                Parallel.For(0, _height, y =>
                {
                    for (int x = 0; x < _width; x++)
                    {
                        int i = y * _width + x;
                        if (!_valid[i]) continue;

                        Vector3 total = Vector3.Zero;
                        float weights = 0f;
                        for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx * step, ny = y + dy * step;
                            if (nx < 0 || ny < 0 || nx >= _width || ny >= _height) continue;
                            int j = ny * _width + nx;
                            if (!_valid[j] || _chart[j] != _chart[i]) continue;

                            float facing = MathF.Max(Vector3.Dot(_normal[i], _normal[j]), 0f);
                            facing *= facing;
                            facing *= facing;
                            float apart = Vector3.DistanceSquared(_position[i], _position[j]) / (4f * step * step * texelSize * texelSize);
                            float weight = MathF.Exp(-(dx * dx + dy * dy) / 4.5f - apart) * facing * facing;
                            total += light[j] * weight;
                            weights += weight;
                        }

                        smoothed[i] = weights > 0f ? total / weights : light[i];
                    }
                });

                Array.Copy(smoothed, light, light.Length);
            }
        }

        /// <summary>Spreads the edges of the lit pixels out over the gaps round them, so smoothing between pixels doesn't pull in black.</summary>
        private void Dilate(Vector3[] light, int times)
        {
            var filled = (bool[])_valid.Clone();
            for (int round = 0; round < times; round++)
            {
                var next = (bool[])filled.Clone();
                for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    if (filled[i]) continue;

                    Vector3 total = Vector3.Zero;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= _width || ny >= _height || !filled[ny * _width + nx]) continue;
                        total += light[ny * _width + nx];
                        count++;
                    }

                    if (count == 0) continue;
                    light[i] = total / count;
                    next[i] = true;
                }

                filled = next;
            }
        }

        // ---- Light probes ---------------------------------------------------------------------------------------

        private LightProbeGrid? BakeProbes(Vector3[] source, int rays, CancellationToken cancel)
        {
            if (_triangles.Count == 0) return null;

            // An even grid over everything static, a little beyond it.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (BakeObject o in _objects)
            {
                foreach (Vector3 p in o.Unwrap?.Positions ?? o.Positions)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }

            float spacing = _probeSpacing;
            Vector3 size = max - min;
            int Count(float length) => Math.Max((int)MathF.Ceiling(length / spacing) + 1, 2);
            while ((long)Count(size.X) * Count(size.Y) * Count(size.Z) > 65536) spacing *= 1.25f;

            var grid = new LightProbeGrid(min, spacing, Count(size.X), Count(size.Y), Count(size.Z));
            int total = grid.Light.Length;
            (int directSamples, _, _) = Samples;
            Parallel.For(0, total, new ParallelOptions { CancellationToken = cancel }, index =>
            {
                int x = index % grid.CountX, y = index / grid.CountX % grid.CountY, z = index / (grid.CountX * grid.CountY);
                Vector3 position = grid.PositionOf(x, y, z);
                var random = new Random32((uint)index * 2654435761u + 11u);

                // The light from every way: its average, and how it changes across (see ProbeLight).
                Vector3 average = Vector3.Zero, rx = Vector3.Zero, ry = Vector3.Zero, rz = Vector3.Zero;
                int backfaces = 0;
                for (int r = 0; r < rays; r++)
                {
                    Vector3 direction = OnSphere(ref random);
                    Vector3 seen = Seen(position, direction, source, ref backfaces);
                    average += seen;
                    rx += seen * direction.X;
                    ry += seen * direction.Y;
                    rz += seen * direction.Z;
                }

                average /= rays;
                float spread = 2f / rays;
                rx *= spread;
                ry *= spread;
                rz *= spread;

                // Straight from the baked lights, which things that move don't get as the game runs.
                foreach (BakeLight light in _lights)
                {
                    if (!light.Direct) continue;
                    Vector3 toward = light.Type == LightType.Directional ? -light.Direction : Vector3.Normalize(light.Position - position);
                    Vector3 arriving = Arriving(light, position, toward);
                    if (arriving == Vector3.Zero) continue;
                    arriving *= Visible(light, position, Math.Max(directSamples / 2, 1), ref random);
                    average += arriving * 0.25f;
                    rx += arriving * (0.5f * toward.X);
                    ry += arriving * (0.5f * toward.Y);
                    rz += arriving * (0.5f * toward.Z);
                }

                grid.Valid[index] = backfaces < rays * 0.25f;

                // How far it can see along each axis, so it doesn't light things through walls and floors.
                for (int k = 0; k < 6; k++)
                {
                    Vector3 axis = (k / 2) switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ } * (k % 2 == 0 ? 1f : -1f);
                    grid.Reach[index * 6 + k] = _tree.Intersect(position, axis, spacing * 2f, out RayHit wall) ? wall.Distance : float.MaxValue;
                }
                grid.Light[index] = new ProbeLight(
                    new Vector4(average.X, rx.X, ry.X, rz.X),
                    new Vector4(average.Y, rx.Y, ry.Y, rz.Y),
                    new Vector4(average.Z, rx.Z, ry.Z, rz.Z));
            });

            return grid;
        }

        // ---- Random directions ----------------------------------------------------------------------------------

        /// <summary>A quick random number source of its own for each pixel, so baking gives the same result every time.</summary>
        private struct Random32(uint seed)
        {
            private uint _state = seed == 0 ? 1u : seed;

            public float Next()
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (_state >> 8) * (1f / 16777216f);
            }
        }

        private static Vector3 OnSphere(ref Random32 random)
        {
            float z = random.Next() * 2f - 1f;
            float angle = random.Next() * MathF.Tau;
            float r = MathF.Sqrt(MathF.Max(1f - z * z, 0f));
            return new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
        }

        /// <summary>A way out from a surface facing <paramref name="normal"/>: more often straight out than along it, as light from there counts for more.</summary>
        private static Vector3 Cosine(Vector3 normal, ref Random32 random)
        {
            float r = MathF.Sqrt(random.Next());
            float angle = random.Next() * MathF.Tau;
            float x = r * MathF.Cos(angle), y = r * MathF.Sin(angle);
            float z = MathF.Sqrt(MathF.Max(1f - x * x - y * y, 0f));
            (Vector3 tangent, Vector3 bitangent) = Basis(normal);
            return Vector3.Normalize(tangent * x + bitangent * y + normal * z);
        }

        /// <summary>A way within <paramref name="angle"/> (radians) of <paramref name="axis"/>.</summary>
        private static Vector3 InCone(Vector3 axis, float angle, ref Random32 random)
        {
            if (angle <= 0f) return axis;
            float cosine = 1f - random.Next() * (1f - MathF.Cos(angle));
            float sine = MathF.Sqrt(MathF.Max(1f - cosine * cosine, 0f));
            float turn = random.Next() * MathF.Tau;
            (Vector3 tangent, Vector3 bitangent) = Basis(axis);
            return Vector3.Normalize(tangent * (sine * MathF.Cos(turn)) + bitangent * (sine * MathF.Sin(turn)) + axis * cosine);
        }

        private static (Vector3, Vector3) Basis(Vector3 n)
        {
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, n));
            return (tangent, Vector3.Cross(n, tangent));
        }
    }
}
