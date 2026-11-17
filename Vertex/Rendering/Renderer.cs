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

        // Cameras drawing into textures this frame; kept to reuse.
        private readonly List<Camera> _targetCameras = new();

        private readonly GpuParticleManager _gpuParticles = new();

        // The GPU particles' simulation run here instead, for backends without compute shaders. Null for the frame if
        // there's nothing to simulate here.
        private CpuParticlePool? _cpuParticlePool;
        private CpuParticlePool? _cpuParticles;

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

        /// <summary>
        /// Draws the scene: first from every camera with a <see cref="Camera.Target"/> into its texture, then from
        /// <see cref="Camera.Main"/> to the screen with the <see cref="UI.Canvas"/> over it.
        /// </summary>
        internal void Render(Scene scene)
        {
            Backend.BeginFrame();

            // GPU particles move once a frame, whatever number of views draw them: on the GPU if the backend can,
            // otherwise here, and then they're handed over with the other particles.
            GpuParticleUpdate particles = _gpuParticles.Gather(scene, Time.TotalTime);
            _cpuParticles = null;
            if (Backend.SupportsGpuParticles)
            {
                Backend.UpdateParticles(particles);
            }
            else if (particles.PoolSize > 0)
            {
                _cpuParticles = _cpuParticlePool ??= new CpuParticlePool();
                _cpuParticles.Apply(particles);
            }

            // Gathered first, so the scene isn't being searched while the backend draws.
            foreach (Camera camera in scene.FindComponents<Camera>())
            {
                if (camera.Target != null) _targetCameras.Add(camera);
            }

            foreach (Camera camera in _targetCameras)
            {
                RenderTexture target = camera.Target!;
                Collect(scene, camera, target.Width, target.Height, target);
                Backend.Render(_frame);
            }

            _targetCameras.Clear();

            Collect(scene, Camera.Main, Width, Height, target: null);
            Backend.Render(_frame);
            Backend.EndFrame();
        }

        /// <summary>Refills the frame from the scene as <paramref name="camera"/> sees it. The frame's lists are reused between frames.</summary>
        private void Collect(Scene scene, Camera? camera, int width, int height, RenderTexture? target)
        {
            _frame.Clear();
            _frame.Width = width;
            _frame.Height = height;
            _frame.Target = target;

            // The 2D layer only goes on the screen.
            if (target == null) _frame.CanvasQuadList.AddRange(UI.Canvas.ReadyQuads);

            Lighting lighting = scene.Lighting;
            _frame.Environment = new EnvironmentData(lighting.SkyColor, lighting.HorizonColor, lighting.GroundColor,
                lighting.AmbientIntensity, lighting.FogDensity, lighting.Exposure, lighting.DrawSky, Math.Clamp(lighting.CelShading, 0f, 1f),
                Math.Clamp(lighting.Outlines, 0f, 1f));
            _frame.ShadowDistance = lighting.ShadowDistance;
            _frame.ShadowLightIndex = -1;
            _frame.Bloom = lighting.Bloom;

            _frame.ClearColor = camera?.ClearColor ?? Color.Black;
            _frame.HasCamera = camera != null && width > 0 && height > 0;
            if (camera == null || !_frame.HasCamera) return;

            _frame.ViewMatrix = camera.ViewMatrix;
            _frame.ProjectionMatrix = camera.GetProjectionMatrix((float)width / height);
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
            _cpuParticles?.Collect(_frame.ParticleList);

            // Furthest first, so nearer dust and smoke covers what's behind it. Additive ones don't mind the order.
            Vector3 eye = _frame.CameraPosition;
            Vector3 forward = _frame.CameraForward;
            _frame.ParticleList.Sort((a, b) => Vector3.Dot(b.Position - eye, forward).CompareTo(Vector3.Dot(a.Position - eye, forward)));
        }

        public void Dispose() => Backend.Dispose();
    }
}
