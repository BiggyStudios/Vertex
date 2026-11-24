using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    /// <paramref name="Grid2D"/> as well, it stands in the X-Y plane (z = 0) instead, behind a 2D game. With
    /// <paramref name="EffectsFrom"/>, it has that camera's post effects (see <see cref="PostEffect"/>), so an editor's view can
    /// look as the game will. With <paramref name="Scene"/>, it shows that scene instead of the one being rendered: a
    /// preview of a material on a sphere, in a scene of its own.
    /// <para>
    /// <paramref name="Transparent"/> leaves wherever nothing's drawn see-through (the sky, alpha 0), for a picture that goes
    /// over something else, like an editor's thumbnail of a model. It isn't anti-aliased, so draw it bigger than it's shown.
    /// <paramref name="ReadBack"/> is given the finished picture's pixels (RGBA, top row first, as <see cref="Texture.Pixels"/>)
    /// once the GPU has drawn them, a frame or two later; a backend that can't read pictures back never calls it.
    /// </para>
    /// <para>
    /// <paramref name="Overlay"/> is drawn over the scene in this view only, see-through and behind what's in front of it, like
    /// sprites: an editor's picture of where characters can walk.
    /// </para>
    /// </summary>
    public readonly record struct RenderView(Camera Camera, RenderTexture Target, bool Grid = false, bool Grid2D = false, Camera? EffectsFrom = null,
        Scene? Scene = null, bool Transparent = false, Action<byte[]>? ReadBack = null, IReadOnlyList<WorldQuad>? Overlay = null);

    public sealed class Renderer : IDisposable
    {
        private readonly RenderFrame _frame = new();

        // Unlit draws, gathered apart this frame to go after the lit ones; kept to reuse.
        private readonly List<DrawCall> _unlitDraws = new();

        // Shadows for point and spot lights, kept from frame to frame; and everything that casts shadows, with its bounds.
        private readonly LocalShadows _localShadows = new();
        private readonly List<(DrawCall Draw, Vector3 Centre, Vector3 Extents)> _casters = new();

        // The renderers to draw this frame and their meshes as drawn, then what each chunk of them gave (see Collect).
        private readonly List<MeshRenderer> _renderers = new();
        private readonly List<Mesh> _drawnMeshes = new();
        private readonly List<CollectChunk> _chunks = new();

        /// <summary>What a chunk of renderers gave: kept from frame to frame to reuse the lists.</summary>
        private sealed class CollectChunk(int cascades)
        {
            public readonly List<DrawCall> Lit = new(), Unlit = new();
            public readonly List<(DrawCall Draw, Vector3 Centre, Vector3 Extents)> Casters = new();
            public readonly List<DrawCall>[] Cascades = Enumerable.Range(0, cascades).Select(_ => new List<DrawCall>()).ToArray();
            public int Culled;
        }

        /// <summary>What every chunk needs to know: the view, and the sun's cascades.</summary>
        private readonly record struct CollectJob(Frustum Frustum, bool MovingOnly, bool HasShadows, Matrix4x4 SunTurn, Vector3[] LightScales, Vector3[] LightOffsets);

        /// <summary>
        /// Renderers <paramref name="from"/> to <paramref name="to"/>: where each is, whether the camera sees it, and which of
        /// the sun's cascades it shadows. Runs on any thread, touching only <paramref name="chunk"/>.
        /// </summary>
        private void CollectRange(CollectChunk chunk, int from, int to, CollectJob job)
        {
            chunk.Lit.Clear();
            chunk.Unlit.Clear();
            chunk.Casters.Clear();
            foreach (List<DrawCall> list in chunk.Cascades) list.Clear();
            chunk.Culled = 0;

            for (int i = from; i < to; i++)
            {
                MeshRenderer meshRenderer = _renderers[i];
                Mesh mesh = _drawnMeshes[i];
                Material material = meshRenderer.Material;
                bool lit = material.Shading == ShadingModel.Lit;
                meshRenderer.WorldBounds(mesh, out Matrix4x4 world, out Vector3 centre, out Vector3 extents);

                // Baked light: from the lightmap, drawn with the mesh made for it, or from the probes round it.
                BakedLight bakedLight = default;
                if (lit && _baked != null)
                {
                    if (mesh == meshRenderer.Mesh && _baked.MeshFor(meshRenderer) is { } lightmapped)
                    {
                        mesh = lightmapped;
                        bakedLight = new BakedLight { Lightmapped = true };
                    }
                    else if (_baked.Probes is { } probes && probes.Sample(centre, out ProbeLight probe))
                    {
                        bakedLight = new BakedLight { FromProbes = true, Probe = probe };
                    }
                }

                var draw = new DrawCall(mesh, material, world, meshRenderer.CastShadows && lit)
                {
                    Baked = bakedLight,
                    Stamp = ((long)RuntimeHelpers.GetHashCode(meshRenderer) << 32) | (uint)meshRenderer.Transform.Version
                };

                if (job.Frustum.Intersects(centre, extents)) (lit ? chunk.Lit : chunk.Unlit).Add(draw);
                else chunk.Culled++;

                if (!draw.CastShadows) continue;
                chunk.Casters.Add((draw, centre, extents));
                if (!job.HasShadows || (job.MovingOnly && bakedLight.Lightmapped)) continue;
                Vector3 lightCentre = Vector3.Transform(centre, job.SunTurn), lightExtents = Culling.Extend(extents, job.SunTurn);
                for (int cascade = 0; cascade < chunk.Cascades.Length; cascade++)
                {
                    if (Culling.InCascade(lightCentre, lightExtents, job.LightScales[cascade], job.LightOffsets[cascade])) chunk.Cascades[cascade].Add(draw);
                }
            }
        }

        // Cameras drawing into textures this frame; kept to reuse.
        private readonly List<Camera> _targetCameras = new();

        private readonly GpuParticleManager _gpuParticles = new();

        // The GPU particles' simulation run here instead, for backends without compute shaders. Null for the frame if
        // there's nothing to simulate here.
        private CpuParticlePool? _cpuParticlePool;
        private CpuParticlePool? _cpuParticles;

        // Each draw's place in the order, worked out once: sorting by a number is much quicker than calling a comparison
        // that looks at three objects each time, with thousands of draws.
        private long[] _sortKeys = [];

        /// <summary>
        /// Puts draws with the same surface shader (or none) next to each other, as switching shaders costs the most;
        /// within that the same material, and within that the same mesh. (Any order that keeps them together will do: two
        /// whose numbers happen to match only cost a little switching.)
        /// </summary>
        private void SortByMaterial(List<DrawCall> draws)
        {
            Span<DrawCall> items = CollectionsMarshal.AsSpan(draws);
            Span<long> keys = Keys(items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                DrawCall draw = items[i];
                keys[i] = ((long)(RuntimeHelpers.GetHashCode(draw.Material.Shader) & 0x1FFFFF) << 42)
                          | ((long)(RuntimeHelpers.GetHashCode(draw.Material) & 0x1FFFFF) << 21)
                          | (long)(RuntimeHelpers.GetHashCode(draw.Mesh) & 0x1FFFFF);
            }

            SortByKeys(items, keys);
        }

        /// <summary>Shadow maps only need each mesh set up once: the same mesh's draws together.</summary>
        private void SortByMesh(List<DrawCall> draws)
        {
            Span<DrawCall> items = CollectionsMarshal.AsSpan(draws);
            Span<long> keys = Keys(items.Length);
            for (int i = 0; i < items.Length; i++) keys[i] = RuntimeHelpers.GetHashCode(items[i].Mesh);
            SortByKeys(items, keys);
        }

        private int[] _sortOrder = [];
        private DrawCall[] _sortScratch = [];

        /// <summary>
        /// Puts <paramref name="items"/> in the order of <paramref name="keys"/>. A draw is big (its matrix and baked light),
        /// so the sort moves numbers saying where each goes, and then each draw is moved once.
        /// </summary>
        private void SortByKeys(Span<DrawCall> items, Span<long> keys)
        {
            int count = items.Length;
            if (count < 2) return;
            if (_sortOrder.Length < count)
            {
                _sortOrder = new int[Math.Max(count, _sortOrder.Length * 2)];
                _sortScratch = new DrawCall[_sortOrder.Length];
            }

            Span<int> order = _sortOrder.AsSpan(0, count);
            for (int i = 0; i < count; i++) order[i] = i;
            keys.Sort(order);

            Span<DrawCall> sorted = _sortScratch.AsSpan(0, count);
            for (int i = 0; i < count; i++) sorted[i] = items[order[i]];
            sorted.CopyTo(items);
        }

        private Span<long> Keys(int count)
        {
            if (_sortKeys.Length < count) _sortKeys = new long[Math.Max(count, _sortKeys.Length * 2)];
            return _sortKeys.AsSpan(0, count);
        }


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

        /// <summary>Whether frames wait for the screen's refresh: see <see cref="Screen.VSync"/>, which a game's window follows.</summary>
        public bool VSync
        {
            get => Backend.VSync;
            set => Backend.VSync = value;
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
            _localShadows.NextFrame();

            // Not counting the waits for the screen (in BeginFrame and EndFrame), which aren't work.
            _cpuTime.Restart();
            Stats.Views = 0;
            Stats.DrawCalls = 0;
            Stats.CulledDrawCalls = 0;
            Stats.ShadowDrawCalls = 0;
            Stats.LightShadowLayers = 0;

            // GPU particles move once a frame, whatever number of views draw them: on the GPU if the backend can,
            // otherwise here, and then they're handed over with the other particles.
            GpuParticleUpdate particles = _gpuParticles.Gather(scene, Time.PreciseTotalTime);
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
                Collect(view.Scene ?? scene, view.Camera, view.Target.Width, view.Target.Height, view.Target, view.EffectsFrom);
                _frame.Grid = view.Grid;
                _frame.Grid2D = view.Grid2D;
                if (view.Overlay != null) _frame.WorldQuadList.AddRange(view.Overlay);
                if (view.Transparent)
                {
                    // The anti-aliasing reads how bright each pixel is from alpha, which here says what's drawn instead.
                    // The sky's still drawn, for shiny things to reflect, but it's at the far plane, so it's see-through.
                    _frame.Transparent = true;
                    _frame.Environment = _frame.Environment with { AntiAliasing = false, Vignette = 0f, VolumetricFog = false };
                    _frame.PostEffectList.Clear();
                }

                Backend.Render(_frame);
                if (view.ReadBack != null) Backend.ReadBack(view.Target, view.ReadBack);
            }

            Collect(scene, screenCamera, Width, Height, target: null);
            Backend.Render(_frame);
            Stats.CpuMilliseconds = _cpuTime.Elapsed.TotalMilliseconds;

            Backend.EndFrame();
            if (Backend.MeasureGpuTime) Backend.ReadGpuTimes(Stats.GpuPassList);
            else Stats.GpuPassList.Clear();
        }

        // The scene's baked light, while it's being gathered.
        private BakedLighting? _baked;

        /// <summary>A light, into the frame's lists of lights (the first directional one that casts shadows gets them).</summary>
        private void AddLight(Light light)
        {
            if (!light.Enabled) return;
            Color color = light.Color * light.Intensity;

            // Once the scene's baked, a baked light's light is in the lightmap and probes: it only lights volumetric fog
            // (and for the sun, the sky), and things that move take its light back out of the lightmap where they block it,
            // if it has a channel of the shadow mask. Until then it lights as a realtime one, so it's seen while the
            // scene's made.
            bool baked = light.Mode == LightMode.Baked && _baked != null;
            int mask = baked && light.CastShadows && _baked!.ShadowMask != null ? light.ShadowMaskChannel : -1;
            if (baked && light.Type != LightType.Directional && mask < 0 && (!_frame.Environment.VolumetricFog || light.FogStrength <= 0f)) return;

            if (light.Type == LightType.Directional)
            {
                // A baked sun's shadows are only of what moves (see Collect).
                if (light.CastShadows && (!baked || mask >= 0) && _frame.ShadowLightIndex < 0) _frame.ShadowLightIndex = _frame.DirectionalLightList.Count;
                _frame.DirectionalLightList.Add(new DirectionalLightData(light.Transform.Forward, color)
                {
                    FogStrength = MathF.Max(light.FogStrength, 0f),
                    Baked = baked,
                    ShadowMask = mask
                });
            }
            else if (light.Type == LightType.Spot)
            {
                // Full inside the soft edge, fading to nothing at the cone's edge.
                float half = Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f * MathF.PI / 180f;
                float inner = half * (1f - Math.Clamp(light.SpotSoftness, 0f, 1f));
                _frame.PointLightList.Add(new PointLightData(light.Transform.WorldPosition, color, light.Range)
                {
                    Direction = light.Transform.Forward,
                    SpotOuter = MathF.Cos(half),
                    SpotInner = MathF.Max(MathF.Cos(inner), MathF.Cos(half) + 1e-4f),
                    FogStrength = MathF.Max(light.FogStrength, 0f),
                    Baked = baked,
                    ShadowMask = mask,
                    CastsShadows = light.CastShadows,
                    Owner = light
                });
            }
            else
            {
                _frame.PointLightList.Add(new PointLightData(light.Transform.WorldPosition, color, light.Range)
                {
                    FogStrength = MathF.Max(light.FogStrength, 0f),
                    Baked = baked,
                    ShadowMask = mask,
                    CastsShadows = light.CastShadows,
                    Owner = light
                });
            }
        }

        private readonly List<PointLightData> _nearLights = new();

        /// <summary>
        /// Leaves only the point and spot lights that reach into the view, and of those, if there are more than the shaders
        /// take, the ones that reach nearest the camera.
        /// </summary>
        private void KeepLightsInView(in Frustum frustum)
        {
            List<PointLightData> lights = _frame.PointLightList;
            _nearLights.Clear();
            foreach (PointLightData light in lights)
            {
                if (light.Range > 0f && frustum.Intersects(light.Position, new Vector3(light.Range))) _nearLights.Add(light);
            }

            if (_nearLights.Count > Shaders.ShaderLibrary.MaxPointLights)
            {
                Vector3 eye = _frame.CameraPosition;
                _nearLights.Sort((a, b) => (Vector3.Distance(a.Position, eye) - a.Range).CompareTo(Vector3.Distance(b.Position, eye) - b.Range));
                _nearLights.RemoveRange(Shaders.ShaderLibrary.MaxPointLights, _nearLights.Count - Shaders.ShaderLibrary.MaxPointLights);
            }

            lights.Clear();
            lights.AddRange(_nearLights);
        }

        /// <summary>The post effects on <paramref name="camera"/>'s object that can run: enabled, with a post effect shader that has no problems.</summary>
        private void CollectPostEffects(Camera camera)
        {
            if (camera.GameObject.IsDestroyed || !camera.GameObject.ActiveInHierarchy) return;

            foreach (PostEffect effect in camera.GameObject.GetComponents<PostEffect>())
            {
                if (!effect.Enabled || effect.Shader is not { } shader || !Usable(shader, ShaderPurpose.PostEffect)) continue;
                _frame.PostEffectList.Add(new PostEffectData(shader, shader.PackValues(effect.Values), shader.PackTextures(effect.Values)));
            }
        }

        /// <summary>
        /// Whether a game's shader can be drawn for <paramref name="purpose"/>: it has no problems, and it's for that. What's
        /// wrong is said once for each version of its code; what the compiler said, the backend has logged already.
        /// </summary>
        internal static bool Usable(Shader shader, ShaderPurpose purpose)
        {
            string? problem = shader.Problem;
            if (problem == null && shader.Purpose != purpose)
            {
                problem = purpose == ShaderPurpose.PostEffect
                    ? $"{shader.Name} is a surface shader, for a material: a post effect needs vec4 effect(vec2 uv)"
                    : $"{shader.Name} is a post effect, for a camera: a material needs a surface shader, with void surface(inout Surface s)";
            }

            if (problem == null) return true;
            if (shader.CompileProblem == null && shader.ReportedVersion != shader.Version) Log.Error(problem);
            shader.ReportedVersion = shader.Version;
            return false;
        }

        /// <summary>The surface shader a material is drawn with, or null for the standard one (none, or one that can't be used).</summary>
        internal static Shader? SurfaceShader(Material material) =>
            material.Shader is { } shader && Usable(shader, ShaderPurpose.Surface) ? shader : null;

        /// <summary>The boxes of fog the view reaches into, nearest first, as many as the shaders take.</summary>
        private void CollectFogVolumes(Scene scene, in Frustum frustum)
        {
            if (!_frame.Environment.VolumetricFog) return;

            Vector3 eye = _frame.CameraPosition;
            float reach = _frame.Environment.VolumetricDistance;
            foreach (FogVolume volume in scene.FindComponents<FogVolume>())
            {
                if (!volume.Enabled || volume.Density == 0f) continue;

                Matrix4x4 world = volume.Transform.WorldMatrix;
                Culling.WorldBounds(Primitives.Cube, world, out Vector3 centre, out Vector3 extents);
                if (!frustum.Intersects(centre, extents) || !Matrix4x4.Invert(world, out Matrix4x4 toBox)) continue;

                // Beyond where the fog is worked out, it wouldn't show.
                Vector3 nearest = Vector3.Clamp(eye, centre - extents, centre + extents);
                if (Vector3.Distance(nearest, eye) > reach) continue;

                _frame.FogVolumeList.Add(new FogVolumeData(toBox, volume.Density, Math.Clamp(volume.Softness, 0f, 1f)));
            }

            if (_frame.FogVolumeList.Count > Shaders.ShaderLibrary.MaxFogVolumes)
            {
                _frame.FogVolumeList.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
                _frame.FogVolumeList.RemoveRange(Shaders.ShaderLibrary.MaxFogVolumes, _frame.FogVolumeList.Count - Shaders.ShaderLibrary.MaxFogVolumes);
            }

            float Distance(FogVolumeData volume)
            {
                Matrix4x4.Invert(volume.WorldToBox, out Matrix4x4 world);
                return Vector3.Distance(world.Translation, eye);
            }
        }

        /// <summary>Refills the frame from the scene as <paramref name="camera"/> sees it. The frame's lists are reused between frames.</summary>
        private void Collect(Scene scene, Camera? camera, int width, int height, RenderTexture? target, Camera? effectsFrom = null)
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

            _baked = scene.Lighting.Baked;
            _frame.Lightmap = _baked?.Lightmap;
            _frame.ShadowMask = _baked?.ShadowMask;
            foreach (Light light in scene.FindComponents<Light>()) AddLight(light);
            foreach (Light light in scene.OutsideLights) AddLight(light);

            int sun = _frame.ShadowLightIndex;
            _frame.HasShadows = sun >= 0 && sun < Shaders.ShaderLibrary.MaxDirectionalLights && _frame.ShadowDistance > 0f;
            _frame.Cascades.Resolution = Math.Clamp(ShadowResolution, 256, 4096);
            if (_frame.HasShadows) _frame.Cascades.Place(_frame, Vector3.Normalize(_frame.DirectionalLightList[sun].Direction));

            // A baked sun's static shadows are in the lightmap: its shadow maps only need what isn't.
            bool movingOnly = _frame.HasShadows && _frame.DirectionalLightList[sun].Baked;

            // Only what the camera can see is drawn, but anything can cast a shadow into view, so shadow casters are
            // checked against each cascade instead.
            var frustum = new Frustum(_frame.ViewMatrix * _frame.ProjectionMatrix);
            List<DrawCall>[] casters = _frame.ShadowCasterLists;
            KeepLightsInView(frustum);
            _frame.Clusters.Build(_frame.PointLightList, _frame.ViewMatrix, _frame.ProjectionMatrix);
            CollectFogVolumes(scene, frustum);
            CollectPostEffects(effectsFrom ?? camera);

            Matrix4x4 sunTurn = _frame.Cascades.Rotation;
            Vector3[] lightScales = _frame.Cascades.LightScale, lightOffsets = _frame.Cascades.LightOffset;

            // Each renderer's mesh first, here on this thread: a skinned one bends itself to its bones when asked.
            _renderers.Clear();
            _drawnMeshes.Clear();
            foreach (MeshRenderer meshRenderer in scene.FindComponents<MeshRenderer>())
            {
                if (meshRenderer.DrawnMesh is not { } mesh) continue;
                _renderers.Add(meshRenderer);
                _drawnMeshes.Add(mesh);
            }

            // Then where each is, whether it's seen, and which cascades it shadows: thousands of renderers in a big scene,
            // each on its own, so in chunks on every core. The chunks are put together in order, so the lists come out the
            // same as going through them one by one.
            int count = _renderers.Count;
            int chunks = Math.Clamp((count + 511) / 512, 1, Environment.ProcessorCount * 2);
            while (_chunks.Count < chunks) _chunks.Add(new CollectChunk(casters.Length));
            int chunkSize = (count + chunks - 1) / Math.Max(chunks, 1);
            var job = new CollectJob(frustum, movingOnly, _frame.HasShadows, sunTurn, lightScales, lightOffsets);
            if (chunks == 1) CollectRange(_chunks[0], 0, count, job);
            else Parallel.For(0, chunks, c => CollectRange(_chunks[c], c * chunkSize, Math.Min(count, (c + 1) * chunkSize), job));

            // Lit draws first, then unlit: the backend sets up each shader once when it switches to it, which costs a
            // lot more than a draw, so switching back and forth between them for every glowing strip adds up.
            for (int c = 0; c < chunks; c++)
            {
                CollectChunk chunk = _chunks[c];
                _frame.DrawCallList.AddRange(chunk.Lit);
                _unlitDraws.AddRange(chunk.Unlit);
                _casters.AddRange(chunk.Casters);
                for (int cascade = 0; cascade < casters.Length; cascade++) casters[cascade].AddRange(chunk.Cascades[cascade]);
                Stats.CulledDrawCalls += chunk.Culled;
            }

            // And within each, the same material and mesh together, so the backend sets each up once for all its draws.
            SortByMaterial(_frame.DrawCallList);
            SortByMaterial(_unlitDraws);
            _frame.DrawCallList.AddRange(_unlitDraws);
            _unlitDraws.Clear();
            Stats.DrawCalls += _frame.DrawCallList.Count;

            // Shadow maps only need each mesh set up once.
            foreach (List<DrawCall> list in casters)
            {
                SortByMesh(list);
                Stats.ShadowDrawCalls += list.Count;
            }

            // Point and spot lights' shadows: which get them, and what's changed in them to draw again.
            _localShadows.Plan(_frame, _casters);
            Stats.LightShadowLayers += _frame.LocalShadowDrawList.Count;
            _casters.Clear();

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
