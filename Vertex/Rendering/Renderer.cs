using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.Windowing;

namespace Vertex.Rendering
{
    /// <summary>
    /// The engine side of rendering. Each frame it gathers cameras, lights and mesh renderers from the
    /// scene into a <see cref="RenderFrame"/>, then hands that to the <see cref="IRenderBackend"/> to draw.
    /// API-independent work (collecting, culling what can't be seen, sorting) belongs here, not in a backend.
    /// </summary>
    /// <summary>
    /// A picture of the scene from a camera, drawn into a texture: see <see cref="Renderer.Render(Scene, Camera?, ReadOnlySpan{RenderView})"/>.
    /// With <paramref name="Grid"/>, a grid on the ground (y = 0) shows where things stand, as in an editor; with
    /// <paramref name="Grid2D"/> as well, it stands in the X-Y plane (z = 0) instead, behind a 2D game.
    /// </summary>
    public readonly record struct RenderView(Camera Camera, RenderTexture Target, bool Grid = false, bool Grid2D = false);

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

        private static readonly Comparison<DrawCall> _byMesh = (a, b) =>
            RuntimeHelpers.GetHashCode(a.Mesh).CompareTo(RuntimeHelpers.GetHashCode(b.Mesh));

        private readonly System.Diagnostics.Stopwatch _cpuTime = new();

        public IRenderBackend Backend { get; }

        /// <summary>What drawing the last frame took: draws, culling and time. See <see cref="MeasureGpuTime"/> for the GPU's side.</summary>
        public RenderStats Stats { get; } = new();

        /// <summary>
        /// Whether to time each part of the frame on the GPU, for <see cref="RenderStats.GpuPasses"/>. Costs next to
        /// nothing, but is off unless wanted; the console's <c>r_showstats</c> turns it on.
        /// </summary>
        public bool MeasureGpuTime
        {
            get => Backend.MeasureGpuTime;
            set => Backend.MeasureGpuTime = value;
        }

        /// <summary>
        /// How many texels across each of the sun's shadow maps is: 2048 by default. Halving it to 1024 makes shadows
        /// a little softer but takes a quarter of the drawing, which helps on integrated graphics. Kept between 256 and 4096.
        /// </summary>
        public int ShadowResolution { get; set; } = Shaders.ShadowCascades.DefaultResolution;

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
        internal void Render(Scene scene) => Render(scene, Camera.Main, []);

        /// <summary>
        /// Draws the scene for a host that chooses its own cameras, such as an editor: first from every camera in the scene
        /// with a <see cref="Camera.Target"/>, then each of <paramref name="views"/> into its texture, then the screen from
        /// <paramref name="screenCamera"/> (null leaves it cleared) with the <see cref="UI.Canvas"/> over it. The views' cameras
        /// needn't be in the scene: an editor keeps its own apart, so the game never sees it.
        /// </summary>
        public void Render(Scene scene, Camera? screenCamera, ReadOnlySpan<RenderView> views)
        {
            Backend.BeginFrame();

            // Not counting the waits for the screen (in BeginFrame and EndFrame), which aren't work.
            _cpuTime.Restart();
            Stats.Views = 0;
            Stats.DrawCalls = 0;
            Stats.CulledDrawCalls = 0;
            Stats.ShadowDrawCalls = 0;

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

            foreach (RenderView view in views)
            {
                Collect(scene, view.Camera, view.Target.Width, view.Target.Height, view.Target);
                _frame.Grid = view.Grid;
                _frame.Grid2D = view.Grid2D;
                Backend.Render(_frame);
            }

            Collect(scene, screenCamera, Width, Height, target: null);
            Backend.Render(_frame);
            Stats.CpuMilliseconds = _cpuTime.Elapsed.TotalMilliseconds;

            Backend.EndFrame();
            if (Backend.MeasureGpuTime) Backend.ReadGpuTimes(Stats.GpuPassList);
            else Stats.GpuPassList.Clear();
        }

        /// <summary>A light, into the frame's lists of lights (the first directional one that casts shadows gets them).</summary>
        private void AddLight(Light light)
        {
            if (!light.Enabled) return;
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
            _frame.Environment = EnvironmentData.From(lighting);
            _frame.ShadowDistance = lighting.ShadowDistance;
            _frame.ShadowLightIndex = -1;
            _frame.Bloom = MathF.Max(lighting.Bloom, 0f);
            _frame.Time = Time.TotalTime;

            _frame.ClearColor = camera?.ClearColor ?? Color.Black;
            _frame.HasCamera = camera != null && width > 0 && height > 0;
            if (camera == null || !_frame.HasCamera) return;

            Stats.Views++;
            _frame.ViewMatrix = camera.ViewMatrix;
            _frame.ProjectionMatrix = camera.GetProjectionMatrix((float)width / height);
            _frame.CameraPosition = camera.Transform.WorldPosition;
            _frame.CameraForward = camera.Transform.Forward;

            foreach (Light light in scene.FindComponents<Light>()) AddLight(light);
            foreach (Light light in scene.OutsideLights) AddLight(light);

            int sun = _frame.ShadowLightIndex;
            _frame.HasShadows = sun >= 0 && sun < Shaders.ShaderLibrary.MaxDirectionalLights && _frame.ShadowDistance > 0f;
            _frame.Cascades.Resolution = Math.Clamp(ShadowResolution, 256, 4096);
            if (_frame.HasShadows) _frame.Cascades.Place(_frame, Vector3.Normalize(_frame.DirectionalLightList[sun].Direction));

            // Only what the camera can see is drawn, but anything can cast a shadow into view, so shadow casters are
            // checked against each cascade instead.
            var frustum = new Frustum(_frame.ViewMatrix * _frame.ProjectionMatrix);
            List<DrawCall>[] casters = _frame.ShadowCasterLists;

            // Lit draws first, then unlit: the backend sets up each shader once when it switches to it, which costs a
            // lot more than a draw, so switching back and forth between them for every glowing strip adds up.
            foreach (MeshRenderer meshRenderer in scene.FindComponents<MeshRenderer>())
            {
                if (meshRenderer.Mesh == null) continue;

                bool lit = meshRenderer.Material.Shading == ShadingModel.Lit;
                var draw = new DrawCall(meshRenderer.Mesh, meshRenderer.Material, meshRenderer.Transform.WorldMatrix, meshRenderer.CastShadows && lit);
                Culling.WorldBounds(draw.Mesh, draw.WorldMatrix, out Vector3 centre, out Vector3 extents);

                if (frustum.Intersects(centre, extents)) (lit ? _frame.DrawCallList : _unlitDraws).Add(draw);
                else Stats.CulledDrawCalls++;

                if (!draw.CastShadows || !_frame.HasShadows) continue;
                for (int cascade = 0; cascade < casters.Length; cascade++)
                {
                    if (Culling.InOrthographic(_frame.Cascades.Matrices[cascade], centre, extents)) casters[cascade].Add(draw);
                }
            }

            // And within each, the same material and mesh together, so the backend sets each up once for all its draws.
            _frame.DrawCallList.Sort(_byMaterialThenMesh);
            _unlitDraws.Sort(_byMaterialThenMesh);
            _frame.DrawCallList.AddRange(_unlitDraws);
            _unlitDraws.Clear();
            Stats.DrawCalls += _frame.DrawCallList.Count;

            // Shadow maps only need each mesh set up once.
            foreach (List<DrawCall> list in casters)
            {
                list.Sort(_byMesh);
                Stats.ShadowDrawCalls += list.Count;
            }

            CollectFlat(scene, camera, frustum);

            foreach (ParticleSystem particles in scene.FindComponents<ParticleSystem>()) particles.Collect(_frame.ParticleList);
            foreach (Effects.VisualEffect effect in scene.FindComponents<Effects.VisualEffect>()) effect.Collect(_frame.ParticleList);
            _cpuParticles?.Collect(_frame.ParticleList);

            // Furthest first, so nearer dust and smoke covers what's behind it. Additive ones don't mind the order.
            Vector3 eye = _frame.CameraPosition;
            Vector3 forward = _frame.CameraForward;
            _frame.ParticleList.Sort((a, b) => Vector3.Dot(b.Position - eye, forward).CompareTo(Vector3.Dot(a.Position - eye, forward)));
        }

        // Sprites' and world text's quads as they're gathered, and which renderer each run of them came from, to sort.
        private readonly List<WorldQuad> _flatQuads = new();
        private readonly List<(int Order, float Depth, int Start, int Count)> _flatRuns = new();

        private static readonly Comparison<(int Order, float Depth, int Start, int Count)> _flatOrder = (a, b) =>
            a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : a.Depth != b.Depth ? b.Depth.CompareTo(a.Depth)
            : a.Start.CompareTo(b.Start);

        /// <summary>
        /// Sprites and world text the camera can see, into the frame: by sorting order, then furthest first, so the nearer
        /// cover what's behind. Each renderer's quads (a text's letters) stay together, in their own order.
        /// </summary>
        private void CollectFlat(Scene scene, Camera camera, in Frustum frustum)
        {
            _flatQuads.Clear();
            _flatRuns.Clear();
            Transform eye = camera.Transform;
            var view = new ViewAxes(eye.WorldPosition, eye.Right, eye.Up, eye.Forward);

            foreach (FlatRenderer flat in scene.FindComponents<FlatRenderer>())
            {
                int start = _flatQuads.Count;
                flat.Collect(_flatQuads, view);
                int count = _flatQuads.Count - start;
                if (count == 0) continue;

                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                for (int i = start; i < _flatQuads.Count; i++)
                {
                    WorldQuad quad = _flatQuads[i];
                    min = Vector3.Min(min, Vector3.Min(Vector3.Min(quad.TopLeft, quad.TopRight), Vector3.Min(quad.BottomRight, quad.BottomLeft)));
                    max = Vector3.Max(max, Vector3.Max(Vector3.Max(quad.TopLeft, quad.TopRight), Vector3.Max(quad.BottomRight, quad.BottomLeft)));
                }

                if (!frustum.Intersects((min + max) * 0.5f, (max - min) * 0.5f))
                {
                    _flatQuads.RemoveRange(start, count);
                    Stats.CulledDrawCalls++;
                    continue;
                }

                _flatRuns.Add((flat.SortingOrder, Vector3.Dot(flat.Transform.WorldPosition - view.Position, view.Forward), start, count));
            }

            _flatRuns.Sort(_flatOrder);
            foreach ((_, _, int start, int count) in _flatRuns)
            {
                for (int i = start; i < start + count; i++) _frame.WorldQuadList.Add(_flatQuads[i]);
            }

            Stats.DrawCalls += _flatRuns.Count;
        }

        public void Dispose() => Backend.Dispose();
    }
}
