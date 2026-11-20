using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>One mesh to draw with a material at a world transform.</summary>
    public readonly record struct DrawCall(Mesh Mesh, Material Material, Matrix4x4 WorldMatrix, bool CastShadows)
    {
        /// <summary>The baked light it gets instead of the sky's: see <see cref="BakedLight"/>.</summary>
        public BakedLight Baked { get; init; }
    }

    /// <summary>
    /// The baked light a draw gets (see <see cref="BakedLighting"/>), in place of the plain light from the sky: from the
    /// scene's lightmap, for a static mesh drawn with its <see cref="MeshVertex.LightmapUV"/>s, or from the light probes
    /// round it, for anything else. Neither, it's lit by the sky as usual.
    /// </summary>
    public readonly record struct BakedLight
    {
        /// <summary>Lit from the lightmap, where its mesh's lightmap coordinates say.</summary>
        public bool Lightmapped { get; init; }

        /// <summary>Lit by <see cref="Probe"/>.</summary>
        public bool FromProbes { get; init; }

        /// <summary>The light round it from the probes: see <see cref="ProbeLight"/>.</summary>
        public ProbeLight Probe { get; init; }
    }

    /// <summary>
    /// The light arriving at a point from all round, as a few numbers for each of red, green and blue: how much a
    /// surface facing way n gets is x + dot(yzw, n) (low-order spherical harmonics, ready for that). Linear light.
    /// </summary>
    public readonly record struct ProbeLight(Vector4 Red, Vector4 Green, Vector4 Blue)
    {
        /// <summary>
        /// What a surface facing <paramref name="normal"/> gets, as the shaders work it out: the average, brighter towards
        /// where the light comes from, on a curve that never goes below nothing (Hazel's, from Geomerics).
        /// </summary>
        public Vector3 Toward(Vector3 normal) => new(Channel(Red, normal), Channel(Green, normal), Channel(Blue, normal));

        private static float Channel(Vector4 c, Vector3 n)
        {
            float average = MathF.Max(c.X, 0f);
            Vector3 lean = new Vector3(c.Y, c.Z, c.W) * 0.5f;
            float leaning = lean.Length();
            if (average <= 1e-6f || leaning <= 1e-6f) return average;

            float ratio = MathF.Min(leaning / average, 1f);
            float q = 0.5f * (1f + Vector3.Dot(lean / leaning, n));
            float p = 1f + 2f * ratio;
            float a = (1f - ratio) / (1f + ratio);
            return average * (a + (1f - a) * (p + 1f) * MathF.Pow(MathF.Max(q, 0f), p));
        }

        public static ProbeLight operator +(ProbeLight a, ProbeLight b) => new(a.Red + b.Red, a.Green + b.Green, a.Blue + b.Blue);
        public static ProbeLight operator *(ProbeLight a, float b) => new(a.Red * b, a.Green * b, a.Blue * b);
    }

    /// <summary>The scene's <see cref="Rendering.Lighting"/> settings for this frame. Colours are as authored (sRGB).</summary>
    public readonly record struct EnvironmentData
    {
        public SkyStyle Sky { get; init; }
        public Color SkyColor { get; init; }
        public Color HorizonColor { get; init; }
        public Color GroundColor { get; init; }
        public float CloudCover { get; init; }
        public float AmbientIntensity { get; init; }
        public float FogDensity { get; init; }
        public float Exposure { get; init; }
        public bool DrawSky { get; init; }
        public float CelShading { get; init; }
        public float Outlines { get; init; }
        public float AmbientOcclusion { get; init; }
        public float AmbientOcclusionRadius { get; init; }
        public bool AntiAliasing { get; init; }
        public float Vignette { get; init; }
        public bool VolumetricFog { get; init; }
        public float VolumetricDensity { get; init; }
        public Color VolumetricColor { get; init; }
        public float VolumetricHeight { get; init; }
        public float VolumetricFalloff { get; init; }
        public float VolumetricNoise { get; init; }
        public Vector3 VolumetricWind { get; init; }
        public float VolumetricForwardScattering { get; init; }
        public float VolumetricAmbient { get; init; }
        public float VolumetricDistance { get; init; }
        public float VertexSnapping { get; init; }
        public float AffineTextures { get; init; }
        public bool PixelatedTextures { get; init; }

        /// <summary>A snapshot of <paramref name="lighting"/>, with its settings kept in range.</summary>
        public static EnvironmentData From(Lighting lighting) => new()
        {
            Sky = lighting.Sky,
            SkyColor = lighting.SkyColor,
            HorizonColor = lighting.HorizonColor,
            GroundColor = lighting.GroundColor,
            CloudCover = Math.Clamp(lighting.CloudCover, 0f, 1f),
            AmbientIntensity = MathF.Max(lighting.AmbientIntensity, 0f),
            FogDensity = MathF.Max(lighting.FogDensity, 0f),
            Exposure = MathF.Max(lighting.Exposure, 0f),
            DrawSky = lighting.DrawSky,
            CelShading = Math.Clamp(lighting.CelShading, 0f, 1f),
            Outlines = Math.Clamp(lighting.Outlines, 0f, 1f),
            AmbientOcclusion = Math.Clamp(lighting.AmbientOcclusion, 0f, 1f),
            AmbientOcclusionRadius = MathF.Max(lighting.AmbientOcclusionRadius, 0.01f),
            AntiAliasing = lighting.AntiAliasing,
            Vignette = Math.Clamp(lighting.Vignette, 0f, 1f),
            VolumetricFog = lighting.VolumetricFog,
            VolumetricDensity = MathF.Max(lighting.VolumetricDensity, 0f),
            VolumetricColor = lighting.VolumetricColor,
            VolumetricHeight = lighting.VolumetricHeight,
            VolumetricFalloff = MathF.Max(lighting.VolumetricFalloff, 0f),
            VolumetricNoise = Math.Clamp(lighting.VolumetricNoise, 0f, 1f),
            VolumetricWind = lighting.VolumetricWind,
            VolumetricForwardScattering = Math.Clamp(lighting.VolumetricForwardScattering, 0f, 0.95f),
            VolumetricAmbient = MathF.Max(lighting.VolumetricAmbient, 0f),
            VolumetricDistance = Math.Clamp(lighting.VolumetricDistance, 1f, 2000f),
            VertexSnapping = MathF.Max(lighting.VertexSnapping, 0f),
            AffineTextures = Math.Clamp(lighting.AffineTextures, 0f, 1f),
            PixelatedTextures = lighting.PixelatedTextures
        };
    }

    /// <summary>A directional light. Color already includes intensity.</summary>
    public readonly record struct DirectionalLightData(Vector3 Direction, Color Color)
    {
        /// <summary>Baked (see <see cref="LightMode.Baked"/>): it doesn't light surfaces as the game runs, only the sky and fog.</summary>
        public bool Baked { get; init; }

        /// <summary>
        /// For a baked light, which channel of the shadow mask it has (see <see cref="BakedLighting.ShadowMask"/>), so things
        /// that move can cast shadows from it; -1 if none.
        /// </summary>
        public int ShadowMask { get; init; } = -1;

        /// <summary>How brightly it shows in volumetric fog, 1 as bright as it lights surfaces.</summary>
        public float FogStrength { get; init; } = 1f;
    }

    /// <summary>
    /// A point or spot light. Color already includes intensity. A spot light shines along <see cref="Direction"/> in a
    /// cone: fully inside <see cref="SpotInner"/> and not at all outside <see cref="SpotOuter"/> (the cosines of the
    /// angles from its middle). A point light has a <see cref="SpotOuter"/> below -1, so it shines every way.
    /// </summary>
    public readonly record struct PointLightData(Vector3 Position, Color Color, float Range)
    {
        public Vector3 Direction { get; init; } = -Vector3.UnitZ;
        public float SpotOuter { get; init; } = -2f;
        public float SpotInner { get; init; } = -1f;

        /// <summary>
        /// Baked (see <see cref="LightMode.Baked"/>): it doesn't light surfaces as the game runs, only volumetric fog; and
        /// with a <see cref="ShadowMask"/> channel, things that move take its light back out of the lightmap where they block it.
        /// </summary>
        public bool Baked { get; init; }

        /// <summary>
        /// For a baked light, which channel of the shadow mask it has (see <see cref="BakedLighting.ShadowMask"/>), so things
        /// that move can cast shadows from it; -1 if none.
        /// </summary>
        public int ShadowMask { get; init; } = -1;

        /// <summary>Whether it casts shadows (see <see cref="Light.CastShadows"/>): the nearest that do get them.</summary>
        public bool CastsShadows { get; init; }

        /// <summary>What it's from (its <see cref="Light"/>), so its shadow can be kept from frame to frame.</summary>
        internal object? Owner { get; init; }

        /// <summary>How brightly it shows in volumetric fog, 1 as bright as it lights surfaces.</summary>
        public float FogStrength { get; init; } = 1f;

        public bool IsSpot => SpotOuter >= -1f;
    }

    /// <summary>
    /// A box of thicker or thinner volumetric fog (see <see cref="FogVolume"/>): <see cref="WorldToBox"/> takes the world
    /// into the box, which is then from -0.5 to 0.5 on every axis.
    /// </summary>
    public readonly record struct FogVolumeData(Matrix4x4 WorldToBox, float Density, float Softness);

    /// <summary>
    /// A post effect to run on the finished picture (see <see cref="PostEffect"/>): its shader, its settings in the order
    /// the shader has them (see <see cref="ShaderProperty.Slot"/>), and its textures (<see cref="Shader.MaxTextures"/>
    /// of them, plain ones where it has fewer).
    /// </summary>
    public readonly record struct PostEffectData(Shader Shader, Vector4[] Values, Texture[] Textures);

    /// <summary>
    /// One particle, facing the camera. Velocity is the stretch: it's drawn that much longer along that direction
    /// (zero for round). Glow is how much it adds to bloom. Rotation turns it (in radians) where it faces the camera, and
    /// Look is the shape drawn.
    /// </summary>
    public readonly record struct ParticleData(Vector3 Position, Vector3 Velocity, float Size, Color Color, float Glow, bool Additive,
        float Rotation = 0f, ParticleLook Look = ParticleLook.Soft);

    /// <summary>
    /// A flat, four-cornered piece of picture in the world, drawn over the scene with its see-through parts blended: a
    /// sprite, or one letter of world text. Corners go round from the top-left; <see cref="UvMin"/> is the texture at the
    /// top-left corner and <see cref="UvMax"/> at the bottom-right. <see cref="Color"/> is straight (not premultiplied)
    /// sRGB, and tints the texture; with no texture it is a plain colour.
    /// </summary>
    public readonly record struct WorldQuad(Texture? Texture, Vector3 TopLeft, Vector3 TopRight, Vector3 BottomRight, Vector3 BottomLeft,
        Vector2 UvMin, Vector2 UvMax, Color Color)
    {
        /// <summary>Whether the texture's colours are already multiplied by its alpha (the font's are; image files' aren't).</summary>
        public bool PremultipliedTexture { get; init; }

        /// <summary>Drawn with sharp, square pixels instead of smoothed between them: for pixel art.</summary>
        public bool Pixelated { get; init; }

        /// <summary>The floats each corner has on the GPU: position, texture coordinates, colour, and whether the texture is premultiplied.</summary>
        internal const int FloatsPerVertex = 10;

        /// <summary>
        /// Writes its two triangles into <paramref name="into"/> from vertex <paramref name="vertex"/>, as the sprite
        /// shaders take them; returns the vertex after.
        /// </summary>
        internal int Write(float[] into, int vertex)
        {
            WriteCorner(into, vertex++, TopLeft, UvMin);
            WriteCorner(into, vertex++, TopRight, new Vector2(UvMax.X, UvMin.Y));
            WriteCorner(into, vertex++, BottomRight, UvMax);
            WriteCorner(into, vertex++, TopLeft, UvMin);
            WriteCorner(into, vertex++, BottomRight, UvMax);
            WriteCorner(into, vertex++, BottomLeft, new Vector2(UvMin.X, UvMax.Y));
            return vertex;
        }

        private void WriteCorner(float[] into, int vertex, Vector3 position, Vector2 uv)
        {
            Span<float> v = into.AsSpan(vertex * FloatsPerVertex, FloatsPerVertex);
            v[0] = position.X;
            v[1] = position.Y;
            v[2] = position.Z;
            v[3] = uv.X;
            v[4] = uv.Y;
            v[5] = Color.R;
            v[6] = Color.G;
            v[7] = Color.B;
            v[8] = Color.A;
            v[9] = PremultipliedTexture ? 1f : 0f;
        }
    }

    /// <summary>One corner of a 2D quad: a position in pixels from the top-left of the screen, and a colour already multiplied by its alpha.</summary>
    public readonly record struct CanvasVertex(Vector2 Position, Vector2 TexCoord, Vector4 Color);

    /// <summary>
    /// A 2D shape drawn over the 3D scene, in list order, blended with premultiplied alpha. A null texture means
    /// solid colour. The corners can be anywhere, so text can be rotated or slanted.
    /// </summary>
    public readonly record struct CanvasQuad(Texture? Texture, CanvasVertex TopLeft, CanvasVertex TopRight, CanvasVertex BottomRight, CanvasVertex BottomLeft)
    {
        /// <summary>
        /// Cuts the quad to a rounded rectangle, smooth-edged: its centre and half size in pixels (x, y, half width, half
        /// height). Zero half size (the default) draws the whole quad. See <see cref="ShapeStyle"/>.
        /// </summary>
        public Vector4 Shape { get; init; }

        /// <summary>
        /// The <see cref="Shape"/>'s corner radius, outline width (0 fills it) and edge softness, in pixels. A softness of
        /// about one is a crisp edge; more blurs it, for shadows and glows.
        /// </summary>
        public Vector4 ShapeStyle { get; init; }

        /// <summary>Only the part inside this rectangle is drawn: left, top, right and bottom in pixels. Empty (the default) draws it all.</summary>
        public Vector4 Clip { get; init; }

        /// <summary>The floats each of the quad's corners has on the GPU: see <see cref="WriteVertex"/>.</summary>
        internal const int FloatsPerVertex = 20;

        /// <summary>
        /// Writes one corner as the canvas shaders take it: position, texture coordinates, colour, shape, shape style
        /// and clip, in that order.
        /// </summary>
        internal void WriteVertex(Span<float> into, in CanvasVertex corner)
        {
            into[0] = corner.Position.X;
            into[1] = corner.Position.Y;
            into[2] = corner.TexCoord.X;
            into[3] = corner.TexCoord.Y;
            into[4] = corner.Color.X;
            into[5] = corner.Color.Y;
            into[6] = corner.Color.Z;
            into[7] = corner.Color.W;
            into[8] = Shape.X;
            into[9] = Shape.Y;
            into[10] = Shape.Z;
            into[11] = Shape.W;
            into[12] = ShapeStyle.X;
            into[13] = ShapeStyle.Y;
            into[14] = ShapeStyle.Z;
            into[15] = ShapeStyle.W;
            into[16] = Clip.X;
            into[17] = Clip.Y;
            into[18] = Clip.Z;
            into[19] = Clip.W;
        }
    }

    /// <summary>
    /// A snapshot of everything that needs to be drawn this frame, filled in by <see cref="Renderer"/>.
    /// Backends only read this; they never look at GameObjects or components directly.
    /// </summary>
    public class RenderFrame
    {
        internal readonly List<DrawCall> DrawCallList = new();
        internal readonly List<DirectionalLightData> DirectionalLightList = new();
        internal readonly List<PointLightData> PointLightList = new();
        internal readonly List<ParticleData> ParticleList = new();
        internal readonly List<CanvasQuad> CanvasQuadList = new();
        internal readonly List<WorldQuad> WorldQuadList = new();
        internal readonly List<FogVolumeData> FogVolumeList = new();
        internal readonly List<PostEffectData> PostEffectList = new();

        /// <summary>The scene's baked light, which draws with <see cref="BakedLight.Lightmapped"/> on read; null if it has none.</summary>
        public Texture? Lightmap { get; internal set; }

        /// <summary>The scene's shadow mask (see <see cref="BakedLighting.ShadowMask"/>), read with the lightmap; null if it has none.</summary>
        public Texture? ShadowMask { get; internal set; }

        /// <summary>Which of <see cref="PointLights"/> reach each part of the view.</summary>
        internal readonly LightClusters Clusters = new();

        internal readonly List<LocalShadowData> LocalShadowList = new();
        internal readonly List<LocalShadowDraw> LocalShadowDrawList = new();
        internal readonly List<DrawCall> LocalShadowCasterList = new();

        /// <summary>The point and spot lights' shadows this view, as the shaders read them (see <see cref="LocalShadows"/>).</summary>
        public IReadOnlyList<LocalShadowData> LocalShadows => LocalShadowList;

        /// <summary>The layers of the point and spot lights' shadow map to draw again before this view: only what's changed.</summary>
        public IReadOnlyList<LocalShadowDraw> LocalShadowDraws => LocalShadowDrawList;

        /// <summary>What to draw into each of <see cref="LocalShadowDraws"/>, from its <see cref="LocalShadowDraw.FirstCaster"/>.</summary>
        public IReadOnlyList<DrawCall> LocalShadowCasters => LocalShadowCasterList;

        /// <summary>Where the sun's shadow cascades are this view, when <see cref="HasShadows"/>.</summary>
        internal readonly Shaders.ShadowCascades Cascades = new();

        // What casts a shadow into each cascade: only what's inside it.
        internal readonly List<DrawCall>[] ShadowCasterLists = CreateCasterLists();

        private static List<DrawCall>[] CreateCasterLists()
        {
            var lists = new List<DrawCall>[Shaders.ShadowCascades.Count];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<DrawCall>();
            return lists;
        }

        /// <summary>Size in pixels of what's being drawn into: the screen, or <see cref="Target"/>.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        /// <summary>
        /// The texture to draw into, or null for the screen. Its top row should end up first, as in a
        /// <see cref="Texture"/> loaded from a file, so it can be used the same way. Only the screen gets <see cref="CanvasQuads"/>.
        /// </summary>
        public RenderTexture? Target { get; internal set; }

        /// <summary>False when there is no camera or the window is minimised; only clear the screen.</summary>
        public bool HasCamera { get; internal set; }

        /// <summary>Whether to draw the editor's grid on the ground: see <see cref="RenderView.Grid"/>.</summary>
        public bool Grid { get; internal set; }

        /// <summary>Whether the grid stands in the X-Y plane, for 2D, rather than lying on the ground.</summary>
        public bool Grid2D { get; internal set; }

        public Color ClearColor { get; internal set; }
        public EnvironmentData Environment { get; internal set; }

        /// <summary>How strongly glowing things bloom; see <see cref="Lighting.Bloom"/>. 0 for none.</summary>
        public float Bloom { get; internal set; }

        /// <summary>Which of <see cref="DirectionalLights"/> casts shadows, or -1 for none.</summary>
        public int ShadowLightIndex { get; internal set; } = -1;

        /// <summary>
        /// Whether the sun casts shadows this view: <see cref="ShadowLightIndex"/> is a light the shaders can take, and
        /// the cascades have been placed for it.
        /// </summary>
        public bool HasShadows { get; internal set; }

        /// <summary>What to draw into shadow cascade <paramref name="cascade"/>: what casts shadows and is inside it.</summary>
        public IReadOnlyList<DrawCall> ShadowCasters(int cascade) => ShadowCasterLists[cascade];

        /// <summary>How far around the camera shadows reach.</summary>
        public float ShadowDistance { get; internal set; }

        public Matrix4x4 ViewMatrix { get; internal set; }

        /// <summary>
        /// Right-handed perspective projection from System.Numerics (clip-space depth 0 to 1, +Y up).
        /// Backends with different clip-space conventions should adjust it.
        /// </summary>
        public Matrix4x4 ProjectionMatrix { get; internal set; }

        public Vector3 CameraPosition { get; internal set; }
        public Vector3 CameraForward { get; internal set; }

        /// <summary>Seconds since the game started, for things that move on their own, like clouds.</summary>
        public float Time { get; internal set; }

        public IReadOnlyList<DrawCall> DrawCalls => DrawCallList;
        public IReadOnlyList<DirectionalLightData> DirectionalLights => DirectionalLightList;
        /// <summary>Point and spot lights that reach into the view: the nearest <see cref="Shaders.ShaderLibrary.MaxPointLights"/>, if there are more.</summary>
        public IReadOnlyList<PointLightData> PointLights => PointLightList;

        /// <summary>Boxes of thicker or thinner volumetric fog, nearest the camera first.</summary>
        public IReadOnlyList<FogVolumeData> FogVolumes => FogVolumeList;

        /// <summary>Post effects to run on the finished picture, in order, each on what the one before made.</summary>
        public IReadOnlyList<PostEffectData> PostEffects => PostEffectList;

        /// <summary>Particles, furthest from the camera first, so the nearer ones cover them.</summary>
        public IReadOnlyList<ParticleData> Particles => ParticleList;

        /// <summary>
        /// Sprites and letters in the world, in the order to draw them: by sorting order, then furthest first, so the
        /// nearer cover what's behind. Drawn after the scene's meshes, tested against their depth without writing it.
        /// </summary>
        public IReadOnlyList<WorldQuad> WorldQuads => WorldQuadList;

        /// <summary>The 2D layer (see <see cref="UI.Canvas"/>), drawn after the 3D scene. Drawn even without a camera.</summary>
        public IReadOnlyList<CanvasQuad> CanvasQuads => CanvasQuadList;

        internal void Clear()
        {
            DrawCallList.Clear();
            DirectionalLightList.Clear();
            PointLightList.Clear();
            ParticleList.Clear();
            CanvasQuadList.Clear();
            WorldQuadList.Clear();
            FogVolumeList.Clear();
            PostEffectList.Clear();
            foreach (List<DrawCall> casters in ShadowCasterLists) casters.Clear();
            LocalShadowList.Clear();
            LocalShadowDrawList.Clear();
            LocalShadowCasterList.Clear();
            HasShadows = false;
            Lightmap = null;
            ShadowMask = null;
            Grid = false;
            Grid2D = false;
        }
    }
}
