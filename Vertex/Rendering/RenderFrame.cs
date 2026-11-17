using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>One mesh to draw with a material at a world transform.</summary>
    public readonly record struct DrawCall(Mesh Mesh, Material Material, Matrix4x4 WorldMatrix, bool CastShadows);

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
            Vignette = Math.Clamp(lighting.Vignette, 0f, 1f)
        };
    }

    /// <summary>A directional light. Color already includes intensity.</summary>
    public readonly record struct DirectionalLightData(Vector3 Direction, Color Color);

    /// <summary>A point light. Color already includes intensity.</summary>
    public readonly record struct PointLightData(Vector3 Position, Color Color, float Range);

    /// <summary>
    /// One particle, facing the camera. Velocity is the stretch: it's drawn that much longer along that direction
    /// (zero for round). Glow is how much it adds to bloom.
    /// </summary>
    public readonly record struct ParticleData(Vector3 Position, Vector3 Velocity, float Size, Color Color, float Glow, bool Additive);

    /// <summary>One corner of a 2D quad: a position in pixels from the top-left of the screen, and a colour already multiplied by its alpha.</summary>
    public readonly record struct CanvasVertex(Vector2 Position, Vector2 TexCoord, Vector4 Color);

    /// <summary>
    /// A 2D shape drawn over the 3D scene, in list order, blended with premultiplied alpha. A null texture means
    /// solid colour. The corners can be anywhere, so text can be rotated or slanted.
    /// </summary>
    public readonly record struct CanvasQuad(Texture? Texture, CanvasVertex TopLeft, CanvasVertex TopRight, CanvasVertex BottomRight, CanvasVertex BottomLeft);

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
        public IReadOnlyList<PointLightData> PointLights => PointLightList;

        /// <summary>Particles, furthest from the camera first, so the nearer ones cover them.</summary>
        public IReadOnlyList<ParticleData> Particles => ParticleList;

        /// <summary>The 2D layer (see <see cref="UI.Canvas"/>), drawn after the 3D scene. Drawn even without a camera.</summary>
        public IReadOnlyList<CanvasQuad> CanvasQuads => CanvasQuadList;

        internal void Clear()
        {
            DrawCallList.Clear();
            DirectionalLightList.Clear();
            PointLightList.Clear();
            ParticleList.Clear();
            CanvasQuadList.Clear();
            foreach (List<DrawCall> casters in ShadowCasterLists) casters.Clear();
            HasShadows = false;
        }
    }
}
