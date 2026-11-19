using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vertex.Rendering.Shaders
{
    /// <summary>
    /// Everything the scene shaders need about the view being drawn, laid out as <see cref="ShaderLibrary.Frame"/>'s
    /// FrameData (std140: only matrices and four-component vectors, so it matches field for field). Both backends upload
    /// it as it is.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameUniforms
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 InverseViewProjection;
        public Matrices4 ShadowMatrices;
        public Vector4 CascadeSplits;
        public Vector4 CascadeTexels;
        public Vector4 Shadow;
        public Vector4 CameraPosition;
        public Vector4 CameraForward;
        public Vector4 SkyLight;
        public Vector4 GroundLight;
        public Vector4 Horizon;
        public Vector4 Zenith;
        public Vector4 GroundColor;
        public Vector4 SunDirection;
        public Vector4 SunColor;
        public Vector4 ClearColor;
        public int DirectionalCount, PointCount, Unused0, Unused1;
        public Vectors4 DirectionalDirection;
        public Vectors4 DirectionalColor;
        public Vectors8 PointPositionRange;
        public Vectors8 PointColor;

        /// <summary>
        /// The uniforms for <paramref name="frame"/>. <paramref name="cascades"/> are where the shadow maps were drawn, or
        /// null if nothing casts shadows this view.
        /// </summary>
        public static FrameUniforms Create(RenderFrame frame, ShadowCascades? cascades)
        {
            EnvironmentData environment = frame.Environment;
            Matrix4x4.Invert(frame.ViewMatrix * frame.ProjectionMatrix, out Matrix4x4 inverseViewProjection);

            var data = new FrameUniforms
            {
                View = frame.ViewMatrix,
                Projection = frame.ProjectionMatrix,
                InverseViewProjection = inverseViewProjection,
                CameraPosition = new Vector4(frame.CameraPosition, frame.Time),
                CameraForward = new Vector4(Vector3.Normalize(frame.CameraForward), 0f),
                ClearColor = Linear(frame.ClearColor, 0f),
                Shadow = new Vector4(1f / ShadowCascades.DefaultResolution, -1f, 0f, 0f)
            };

            if (cascades != null)
            {
                for (int i = 0; i < ShadowCascades.Count; i++) data.ShadowMatrices[i] = cascades.Matrices[i];
                data.CascadeSplits = new Vector4(cascades.Splits[0], cascades.Splits[1], cascades.Splits[2], cascades.Splits[3]);
                data.CascadeTexels = new Vector4(cascades.TexelSizes[0], cascades.TexelSizes[1], cascades.TexelSizes[2], cascades.TexelSizes[3]);
                data.Shadow = new Vector4(1f / cascades.Resolution, frame.ShadowLightIndex, ShadowCascades.Count, 0f);
            }

            // The sun is where the shadow-casting light (or else the first directional light) comes from.
            int sunIndex = frame.ShadowLightIndex >= 0 ? frame.ShadowLightIndex : 0;
            bool hasSun = frame.DirectionalLights.Count > sunIndex;
            Vector3 sun = Vector3.UnitY;
            Vector3 sunColor = Vector3.Zero;
            if (hasSun)
            {
                DirectionalLightData light = frame.DirectionalLights[sunIndex];
                sun = -Vector3.Normalize(light.Direction);
                sunColor = new Vector3(light.Color.R, light.Color.G, light.Color.B);
            }

            data.SunDirection = new Vector4(sun, hasSun ? 1f : 0f);
            data.SunColor = new Vector4(sunColor, Atmosphere.Brightness);

            // The light from the sky and ground. The atmosphere's needs a sun to work from; without one, the gradient's.
            Vector3 groundColor = ToLinear(environment.GroundColor);
            bool atmosphere = environment.Sky == SkyStyle.Atmosphere && hasSun;
            Vector3 skyLight, groundLight, horizon, zenith;
            if (atmosphere)
            {
                (skyLight, horizon) = Atmosphere.Ambient(sun, sunColor);
                zenith = Atmosphere.Radiance(Vector3.UnitY, sun, sunColor);
                groundLight = groundColor * (sunColor * Atmosphere.SunlightTint(sun.Y) * MathF.Max(sun.Y, 0f) + skyLight);
            }
            else
            {
                zenith = ToLinear(environment.SkyColor);
                horizon = ToLinear(environment.HorizonColor);
                skyLight = (zenith + horizon) * 0.5f;
                groundLight = groundColor;
            }

            float kind = !environment.DrawSky ? 0f : atmosphere ? 2f : 1f;
            data.SkyLight = new Vector4(skyLight, environment.AmbientIntensity);
            data.GroundLight = new Vector4(groundLight, environment.FogDensity);
            data.Horizon = new Vector4(horizon, environment.CelShading);
            data.Zenith = new Vector4(zenith, kind);
            data.GroundColor = new Vector4(groundColor, environment.CloudCover);

            data.DirectionalCount = Math.Min(frame.DirectionalLights.Count, ShaderLibrary.MaxDirectionalLights);
            for (int i = 0; i < data.DirectionalCount; i++)
            {
                DirectionalLightData light = frame.DirectionalLights[i];
                var color = new Vector3(light.Color.R, light.Color.G, light.Color.B);

                // The sun turns golden as it gets low, and goes out once it's set, through the same air the sky is made of.
                if (atmosphere && i == sunIndex) color *= Atmosphere.SunlightTint(sun.Y);

                data.DirectionalDirection[i] = new Vector4(light.Direction, 0f);
                data.DirectionalColor[i] = new Vector4(color, 1f);
            }

            data.PointCount = Math.Min(frame.PointLights.Count, ShaderLibrary.MaxPointLights);
            for (int i = 0; i < data.PointCount; i++)
            {
                PointLightData light = frame.PointLights[i];
                data.PointPositionRange[i] = new Vector4(light.Position, light.Range);
                data.PointColor[i] = new Vector4(light.Color.R, light.Color.G, light.Color.B, 1f);
            }

            return data;
        }

        public static Vector3 ToLinear(Color color) =>
            new(MathF.Pow(MathF.Max(color.R, 0f), 2.2f), MathF.Pow(MathF.Max(color.G, 0f), 2.2f), MathF.Pow(MathF.Max(color.B, 0f), 2.2f));

        public static Vector4 Linear(Color color, float w) => new(ToLinear(color), w);
    }

    [InlineArray(ShaderLibrary.MaxCascades)]
    internal struct Matrices4 { private Matrix4x4 _first; }

    [InlineArray(ShaderLibrary.MaxDirectionalLights)]
    internal struct Vectors4 { private Vector4 _first; }

    [InlineArray(ShaderLibrary.MaxPointLights)]
    internal struct Vectors8 { private Vector4 _first; }

    /// <summary>A material as <see cref="ShaderLibrary.Material"/>'s MaterialData has it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MaterialUniforms
    {
        public Vector4 Color;
        public Vector4 Emission;
        public Vector4 Surface;
        public Vector4 UVTransform;
        public Vector4 NormalParams;

        public static MaterialUniforms Create(Material material) => new()
        {
            Color = FrameUniforms.Linear(material.Color, material.Color.A),
            Emission = new Vector4(FrameUniforms.ToLinear(material.Emission) * MathF.Max(material.EmissionStrength, 0f), 0f),
            Surface = new Vector4(Math.Clamp(material.Roughness, 0f, 1f), Math.Clamp(material.Metallic, 0f, 1f),
                Math.Clamp(material.Specular, 0f, 1f), material.Texture != null ? 1f : 0f),
            UVTransform = new Vector4(material.TextureScale, material.TextureOffset.X, material.TextureOffset.Y),
            NormalParams = new Vector4(material.NormalMap != null ? 1f : 0f, material.NormalStrength, 0f, 0f)
        };
    }

    /// <summary>
    /// The post-processing passes' settings, as <see cref="ShaderLibrary.Post"/>'s PostBlock has them: push constants in
    /// Vulkan, a uniform struct in OpenGL.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PostUniforms
    {
        public Vector2 TexelSize;
        public float BloomStrength;
        public float Outlines;
        public Vector4 Projection;
        public Vector2 OutlineStep;
        public float Exposure;
        public float Vignette;
        public float Occlusion;
        public float OcclusionRadius;
        public float Threshold;
        public float Time;
        public Vector2 Direction;
        public float Orthographic;   // 1 for an orthographic camera: depth is then linear
        public float Pad1;

        /// <summary>
        /// How bright (after exposure) something must be before it blooms: brighter than a white surface in full sun, so
        /// only the sun and glints off shiny things do, not everything sunlit. Glowing things bloom through the glow they
        /// draw as well (see ShaderLibrary's GlowColor), so they keep their colour.
        /// </summary>
        public const float BloomThreshold = 2.2f;

        /// <summary>How strongly the blurred bright parts go back over the picture, for <see cref="Lighting.Bloom"/>, shared across the bloom levels (each adds once).</summary>
        public static float BloomScale(float bloom, int levels) => bloom * 0.5f / levels;

        /// <summary>Everything about <paramref name="frame"/> that stays the same through its post passes.</summary>
        public static PostUniforms ForView(RenderFrame frame)
        {
            Matrix4x4 projection = frame.ProjectionMatrix;
            EnvironmentData environment = frame.Environment;
            return new PostUniforms
            {
                Projection = new Vector4(projection.M11, projection.M22, projection.M33, projection.M43),
                Orthographic = projection.M44 > 0.5f ? 1f : 0f,
                Exposure = environment.Exposure,
                Outlines = environment.Outlines,
                Vignette = environment.Vignette,
                OcclusionRadius = environment.AmbientOcclusionRadius,
                Time = frame.Time,
                // One pixel wide at 720 lines, wider on bigger screens.
                OutlineStep = new Vector2(1f / frame.Width, 1f / frame.Height) * MathF.Max(1f, MathF.Round(frame.Height / 720f))
            };
        }
    }
}
