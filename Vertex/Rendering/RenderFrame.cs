using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>One mesh to draw with a material at a world transform.</summary>
    public readonly record struct DrawCall(Mesh Mesh, Material Material, Matrix4x4 WorldMatrix, bool CastShadows);

    /// <summary>The scene's <see cref="Rendering.Lighting"/> settings for this frame. Colours are as authored (sRGB).</summary>
    public readonly record struct EnvironmentData(Color SkyColor, Color HorizonColor, Color GroundColor,
        float AmbientIntensity, float FogDensity, float Exposure, bool DrawSky);

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

        /// <summary>Framebuffer size in pixels.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }

        /// <summary>False when there is no camera or the window is minimised; only clear the screen.</summary>
        public bool HasCamera { get; internal set; }

        public Color ClearColor { get; internal set; }
        public EnvironmentData Environment { get; internal set; }

        /// <summary>How strongly glowing things bloom; see <see cref="Lighting.Bloom"/>. 0 for none.</summary>
        public float Bloom { get; internal set; }

        /// <summary>Which of <see cref="DirectionalLights"/> casts shadows, or -1 for none.</summary>
        public int ShadowLightIndex { get; internal set; } = -1;

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
        }
    }
}
