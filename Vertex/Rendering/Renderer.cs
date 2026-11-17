using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    /// <summary>
    /// The engine side of rendering. Each frame it gathers cameras, lights and mesh renderers from the
    /// scene into a <see cref="RenderFrame"/>, then hands that to the <see cref="IRenderBackend"/> to draw.
    /// API-independent work (collecting, and later culling or sorting) belongs here, not in a backend.
    /// </summary>
    public sealed class Renderer : IDisposable
    {
        private readonly RenderFrame _frame = new();

        // Unlit draws, gathered apart this frame to go after the lit ones; kept to reuse.
        private readonly List<DrawCall> _unlitDraws = new();

        // Any order that keeps draws with the same material, and within that the same mesh, next to each other.
        private static readonly Comparison<DrawCall> _byMaterialThenMesh = (a, b) =>
        {
            int material = RuntimeHelpers.GetHashCode(a.Material).CompareTo(RuntimeHelpers.GetHashCode(b.Material));
            return material != 0 ? material : RuntimeHelpers.GetHashCode(a.Mesh).CompareTo(RuntimeHelpers.GetHashCode(b.Mesh));
        };

        public IRenderBackend Backend { get; }

        /// <summary>Framebuffer size in pixels.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        public Renderer(IRenderBackend backend)
        {
            Backend = backend;
        }

        internal WindowOptions ConfigureWindow(WindowOptions options) => Backend.ConfigureWindow(options);

        internal void Initialise(IWindow window)
        {
            Backend.Initialise(window);
            Resize(window.FramebufferSize.X, window.FramebufferSize.Y);
        }

        internal void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            Backend.Resize(width, height);
        }

        internal void Render(Scene scene)
        {
            Collect(scene);
            Backend.Render(_frame);
        }

        /// <summary>Refills the frame from the scene. The frame's lists are reused between frames.</summary>
        private void Collect(Scene scene)
        {
            _frame.Clear();
            _frame.Width = Width;
            _frame.Height = Height;
            _frame.CanvasQuadList.AddRange(UI.Canvas.ReadyQuads);

            Lighting lighting = scene.Lighting;
            _frame.Environment = new EnvironmentData(lighting.SkyColor, lighting.HorizonColor, lighting.GroundColor,
                lighting.AmbientIntensity, lighting.FogDensity, lighting.Exposure, lighting.DrawSky, Math.Clamp(lighting.CelShading, 0f, 1f),
                Math.Clamp(lighting.Outlines, 0f, 1f));
            _frame.ShadowDistance = lighting.ShadowDistance;
            _frame.ShadowLightIndex = -1;
            _frame.Bloom = lighting.Bloom;

            Camera? camera = Camera.Main;
            _frame.ClearColor = camera?.ClearColor ?? Color.Black;
            _frame.HasCamera = camera != null && Width > 0 && Height > 0;
            if (camera == null || !_frame.HasCamera) return;

            _frame.ViewMatrix = camera.ViewMatrix;
            _frame.ProjectionMatrix = camera.GetProjectionMatrix((float)Width / Height);
            _frame.CameraPosition = camera.Transform.WorldPosition;
            _frame.CameraForward = camera.Transform.Forward;

            foreach (Light light in scene.FindComponents<Light>())
            {
                Color color = light.Color * light.Intensity;

                if (light.Type == LightType.Directional)
                {
                    if (light.CastShadows && _frame.ShadowLightIndex < 0) _frame.ShadowLightIndex = _frame.DirectionalLightList.Count;
                    _frame.DirectionalLightList.Add(new DirectionalLightData(light.Transform.Forward, color));
                }
                else
                {
                    _frame.PointLightList.Add(new PointLightData(light.Transform.WorldPosition, color, light.Range));
                }
            }

            // Lit draws first, then unlit: the backend sets up each shader once when it switches to it, which costs a
            // lot more than a draw, so switching back and forth between them for every glowing strip adds up.
            foreach (MeshRenderer meshRenderer in scene.FindComponents<MeshRenderer>())
            {
                if (meshRenderer.Mesh == null) continue;

                bool lit = meshRenderer.Material.Shading == ShadingModel.Lit;
                var draw = new DrawCall(meshRenderer.Mesh, meshRenderer.Material, meshRenderer.Transform.WorldMatrix, meshRenderer.CastShadows && lit);
                (lit ? _frame.DrawCallList : _unlitDraws).Add(draw);
            }

            // And within each, the same material and mesh together, so the backend sets each up once for all its draws.
            _frame.DrawCallList.Sort(_byMaterialThenMesh);
            _unlitDraws.Sort(_byMaterialThenMesh);
            _frame.DrawCallList.AddRange(_unlitDraws);
            _unlitDraws.Clear();

            foreach (ParticleSystem particles in scene.FindComponents<ParticleSystem>()) particles.Collect(_frame.ParticleList);

            // Furthest first, so nearer dust and smoke covers what's behind it. Additive ones don't mind the order.
            Vector3 eye = _frame.CameraPosition;
            Vector3 forward = _frame.CameraForward;
            _frame.ParticleList.Sort((a, b) => Vector3.Dot(b.Position - eye, forward).CompareTo(Vector3.Dot(a.Position - eye, forward)));
        }

        public void Dispose() => Backend.Dispose();
    }
}
