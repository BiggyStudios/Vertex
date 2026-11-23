using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.OpenGL;
using Silk.NET.Windowing;

using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// OpenGL 3.3 implementation of <see cref="IRenderBackend"/>, for computers without Vulkan. It draws exactly what the
    /// Vulkan backend does, with the same shaders (see <see cref="ShaderLibrary"/>): the sun's shadow cascades, the sky,
    /// physically based lighting with fog, particles, ambient occlusion, bloom, tone mapping and anti-aliasing, then the
    /// 2D canvas.
    /// </summary>
    public class GLRenderBackend : IRenderBackend
    {
        // Where each shader finds its data.
        private const uint _frameBinding = 0;
        private const uint _materialBinding = 1;
        private const int _textureUnit = 0;
        private const int _shadowUnit = 1;
        private const int _normalUnit = 2;
        private const int _cloudUnit = 3;

        /// <summary>Where the scene's shaders find the volumetric fog in front of what they draw.</summary>
        internal const int FogUnit = 5;

        // Where the fog passes find what the other pass (or last frame) made.
        private const int _fogSourceUnit = 6;

        // Where the scene's and fog's shaders find which lights reach each part of the view (see LightClusters).
        private const int _clusterUnit = 7;

        // Where the scene's shaders find its baked light.
        private const int _lightmapUnit = 12;

        // Where they find the point and spot lights' shadows.
        private const int _localShadowUnit = 13;

        // And how much of the baked lights reached each pixel of the lightmap (see BakedLighting.ShadowMask).
        private const int _shadowMaskUnit = 14;

        // Where a game's shaders (surface shaders and post effects) find their textures: four, one after another.
        private const int _shaderTextureUnit = 8;

        private GL _gl = null!;
        private GLShader _fogLight = null!;
        private GLShader _fogTotal = null!;
        private GLShader _litShader = null!;
        private GLShader _unlitShader = null!;
        private GLShader _skyShader = null!;
        private GLShader _gridShader = null!;
        private GLShader _shadowShader = null!;
        private GLShader _bloomDown = null!;
        private GLShader _bloomUp = null!;
        private GLShader _occlusion = null!;
        private GLShader _occlusionBlur = null!;
        private GLShader _composite = null!;
        private GLShader _antiAlias = null!;

        // Games' post effects (see PostEffect), built the first time they're used and again when their code changes; null
        // for one that won't compile, until it changes.
        private readonly Dictionary<Shader, (int Version, GLShader? Program)> _effectShaders = new();
        // Materials' surface shaders, lit and unlit, built the first time and again when their code changes.
        private readonly Dictionary<(Shader Shader, bool Lit), (int Version, GLShader? Program)> _surfaceShaders = new();
        private readonly List<(PostEffectData Effect, GLShader Program)> _effects = new();
        private GLShadowMaps _shadowMaps = null!;
        private GLShadowMaps _localShadowMaps = null!;
        private GLViewTargets _screenViews = null!;
        private GLCanvas _canvas = null!;
        private GLParticles _particles = null!;
        private GLSprites _sprites = null!;
        private GLTexture _white = null!;

        private uint _frameBuffer;
        private uint _clusterBuffer;
        private uint _clusterTexture;
        private uint _materialBuffer;
        private int _uniformAlignment = 256;
        private byte[] _materialData = new byte[4096];
        private readonly Dictionary<Material, int> _materialOffsets = new();

        // The sky and post passes make their triangle in the vertex shader, but OpenGL still wants a vertex array bound to draw.
        private uint _emptyVertexArray;

        // GPU copies of meshes/textures. Weak keys, so dropping a Mesh doesn't keep it alive here.
        private readonly ConditionalWeakTable<Mesh, GLMesh> _meshes = new();
        private readonly ConditionalWeakTable<Texture, GLTexture> _textures = new();
        private readonly ConditionalWeakTable<RenderTexture, GLRenderTarget> _renderTargets = new();

        public string Name => "OpenGL";

        // OpenGL 3.3 has no compute shaders, so the engine runs GPU particles on the CPU and sends them as ordinary ones.
        public bool SupportsGpuParticles => false;

        private GLGpuTimer _timer = null!;
        private readonly List<GpuPassTime> _gpuTimes = new();
        private bool _gpuTimesNew;

        public bool MeasureGpuTime { get; set; }

        public void BeginFrame()
        {
            _timer.Enabled = MeasureGpuTime;
            if (_timer.BeginFrame(_gpuTimes)) _gpuTimesNew = true;
            FinishReadBacks();
        }

        // Pictures being read back (see ReadBack): each waits in a buffer of its own until the GPU's drawn it, so reading
        // it never stalls for the GPU to catch up.
        private readonly List<(uint Buffer, nint Fence, int Size, Action<byte[]> Done)> _readBacks = new();

        public unsafe void ReadBack(RenderTexture texture, Action<byte[]> done)
        {
            if (!_renderTargets.TryGetValue(texture, out GLRenderTarget? target)) return;

            int size = texture.Width * texture.Height * 4;
            uint buffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, buffer);
            _gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)size, null, BufferUsageARB.StreamRead);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Framebuffer);
            _gl.ReadPixels(0, 0, (uint)texture.Width, (uint)texture.Height, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            _readBacks.Add((buffer, _gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None), size, done));
        }

        /// <summary>Hands over the pictures read back that the GPU has finished drawing.</summary>
        private unsafe void FinishReadBacks()
        {
            for (int i = 0; i < _readBacks.Count; i++)
            {
                (uint buffer, nint fence, int size, Action<byte[]> done) = _readBacks[i];
                GLEnum state = _gl.ClientWaitSync(fence, (SyncObjectMask)0, 0);
                if (state is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)) continue;

                byte[] pixels = new byte[size];
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, buffer);
                fixed (byte* data = pixels) _gl.GetBufferSubData(BufferTargetARB.PixelPackBuffer, 0, (nuint)size, data);
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
                _gl.DeleteBuffer(buffer);
                _gl.DeleteSync(fence);
                _readBacks.RemoveAt(i--);
                done(pixels);
            }
        }

        public void ReadGpuTimes(List<GpuPassTime> into)
        {
            if (!_gpuTimesNew) return;

            into.Clear();
            into.AddRange(_gpuTimes);
            _gpuTimesNew = false;
        }

        private void Mark(string pass) => _timer.Mark(pass);

        public void UpdateParticles(GpuParticleUpdate update) {}

        // OpenGL shows the frame itself once the window's Render event returns.
        public void EndFrame() => Mark("");

        public WindowOptions ConfigureWindow(WindowOptions options)
        {
            return options with
            {
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
                PreferredDepthBufferBits = 24
            };
        }

        public void Initialise(IWindow window)
        {
            _gl = window.CreateOpenGL();
            Log.Info($"Rendering with OpenGL on {_gl.GetStringS(StringName.Renderer)}.");

            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(TriangleFace.Back);

            if (_gl.IsExtensionPresent("GL_EXT_texture_filter_anisotropic") || _gl.IsExtensionPresent("GL_ARB_texture_filter_anisotropic"))
            {
                _gl.GetFloat((GetPName)GLTexture.MaxTextureMaxAnisotropy, out float max);
                GLTexture.MaxAnisotropy = MathF.Min(8f, max);
            }

            _litShader = SceneShader(ShaderLibrary.MeshVertex, ShaderLibrary.LitFragment);
            _unlitShader = SceneShader(ShaderLibrary.MeshVertex, ShaderLibrary.UnlitFragment);
            _skyShader = SceneShader(ShaderLibrary.SkyVertex, ShaderLibrary.SkyFragment);
            _gridShader = SceneShader(ShaderLibrary.GridVertex, ShaderLibrary.GridFragment);
            _shadowShader = GLShader.FromLibrary(_gl, ShaderLibrary.ShadowVertex, ShaderLibrary.EmptyFragment);
            _fogLight = FogShader(ShaderLibrary.FogLightFragment);
            _fogTotal = FogShader(ShaderLibrary.FogTotalFragment);

            _bloomDown = PostShader(ShaderLibrary.BloomDownsampleFragment);
            _bloomUp = PostShader(ShaderLibrary.BloomUpsampleFragment);
            _occlusion = PostShader(ShaderLibrary.OcclusionFragment);
            _occlusionBlur = PostShader(ShaderLibrary.OcclusionBlurFragment);
            _composite = PostShader(ShaderLibrary.CompositeFragment);
            _antiAlias = PostShader(ShaderLibrary.AntiAliasFragment);

            _gl.GetInteger(GetPName.UniformBufferOffsetAlignment, out int alignment);
            if (alignment > 0) _uniformAlignment = alignment;
            _frameBuffer = _gl.GenBuffer();
            _materialBuffer = _gl.GenBuffer();
            _clusterBuffer = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, _clusterBuffer);
            _gl.BufferData(BufferTargetARB.TextureBuffer, 4, ReadOnlySpan<uint>.Empty, BufferUsageARB.StreamDraw);
            _clusterTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureBuffer, _clusterTexture);
            _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.R32ui, _clusterBuffer);

            _shadowMaps = new GLShadowMaps(_gl, ShadowCascades.DefaultResolution);
            _localShadowMaps = new GLShadowMaps(_gl, LocalShadows.Resolution, LocalShadows.Layers);
            _screenViews = new GLViewTargets(_gl);
            _canvas = new GLCanvas(_gl);
            _emptyVertexArray = _gl.GenVertexArray();
            _particles = new GLParticles(_gl);
            _sprites = new GLSprites(_gl);
            _white = new GLTexture(_gl, new Texture(1, 1, [255, 255, 255, 255]));
            _timer = new GLGpuTimer(_gl);
        }

        private GLShader SceneShader(string vertex, string fragment)
        {
            GLShader shader = GLShader.FromLibrary(_gl, vertex, fragment);
            shader.BindBlock("FrameData", _frameBinding);
            shader.BindBlock("MaterialData", _materialBinding);
            shader.SetSamplers(("materialTexture", _textureUnit), ("shadowMap", _shadowUnit), ("normalMap", _normalUnit), ("cloudNoise", _cloudUnit),
                ("fogVolume", FogUnit), ("lightClusterWords", _clusterUnit), ("lightmap", _lightmapUnit), ("localShadowMap", _localShadowUnit), ("shadowMask", _shadowMaskUnit));
            SetShaderTextureUnits(shader);
            return shader;
        }

        private GLShader FogShader(string fragment)
        {
            GLShader shader = GLShader.FromLibrary(_gl, ShaderLibrary.FullscreenVertex, fragment);
            shader.BindBlock("FrameData", _frameBinding);
            shader.SetSamplers(("shadowMap", _shadowUnit), ("cloudNoise", _cloudUnit), ("fogHistory", _fogSourceUnit), ("fogLight", _fogSourceUnit), ("lightClusterWords", _clusterUnit), ("localShadowMap", _localShadowUnit));
            return shader;
        }

        private GLShader PostShader(string fragment)
        {
            GLShader shader = GLShader.FromLibrary(_gl, ShaderLibrary.FullscreenVertex, fragment);
            shader.SetSamplers(("source", 0), ("bloomImage", 1), ("depthImage", 2), ("occlusionImage", 3), ("ambientImage", 4));
            return shader;
        }

        /// <summary>
        /// A game's post effect, built the first time and again when its code changes. Null if it won't compile; what the
        /// compiler said goes in the shader's <see cref="Shader.Problem"/> and the log.
        /// </summary>
        private GLShader? EffectShader(Shader shader)
        {
            if (_effectShaders.TryGetValue(shader, out (int Version, GLShader? Program) built) && built.Version == shader.Version) return built.Program;

            built.Program?.Dispose();
            GLShader? program = null;
            try
            {
                program = PostShader(ShaderLibrary.EffectFragment(shader));
                SetShaderTextureUnits(program);
            }
            catch (Exception exception)
            {
                shader.CompileProblem = $"Shader {shader.Name} didn't compile:\n{ShaderLibrary.WithFileName(exception.Message, shader.Name)}";
                Log.Error(shader.CompileProblem);
            }

            _effectShaders[shader] = (shader.Version, program);
            return program;
        }

        /// <summary>
        /// The scene program for a material: its surface shader's own (lit or unlit as the material is), built the first
        /// time and again when its code changes, or the standard one. A surface shader that won't compile is drawn the
        /// standard way; what the compiler said goes in its <see cref="Shader.Problem"/> and the log.
        /// </summary>
        private GLShader SceneProgram(Material material, Shader? surface)
        {
            bool lit = material.Shading != ShadingModel.Unlit;
            if (surface == null) return lit ? _litShader : _unlitShader;

            if (!_surfaceShaders.TryGetValue((surface, lit), out (int Version, GLShader? Program) built) || built.Version != surface.Version)
            {
                built.Program?.Dispose();
                built = (surface.Version, null);
                try
                {
                    built.Program = SceneShader(ShaderLibrary.MeshVertexWith(surface), lit ? ShaderLibrary.LitFragmentWith(surface) : ShaderLibrary.UnlitFragmentWith(surface));
                }
                catch (Exception exception)
                {
                    surface.CompileProblem = $"Shader {surface.Name} didn't compile:\n{ShaderLibrary.WithFileName(exception.Message, surface.Name)}";
                    Log.Error(surface.CompileProblem);
                }

                _surfaceShaders[(surface, lit)] = built;
            }

            return built.Program ?? (lit ? _litShader : _unlitShader);
        }

        private static void SetShaderTextureUnits(GLShader shader) => shader.SetSamplers(
            ("shaderTexture0", _shaderTextureUnit), ("shaderTexture1", _shaderTextureUnit + 1),
            ("shaderTexture2", _shaderTextureUnit + 2), ("shaderTexture3", _shaderTextureUnit + 3));

        /// <summary>A game shader's textures, each on its unit (see <see cref="Shader.PackTextures"/>).</summary>
        private void BindShaderTextures(Texture[] textures)
        {
            for (int i = 0; i < textures.Length; i++) GetTexture(textures[i]).Bind(_shaderTextureUnit + i);
        }

        public void Resize(int width, int height)
        {
            _gl.Viewport(0, 0, (uint)width, (uint)height);
        }

        public unsafe void Render(RenderFrame frame)
        {
            // Where the finished picture goes: the screen, or a render texture's framebuffer. Each has its own view
            // images, sized to match.
            uint output = 0;
            GLViewTargets views = _screenViews;
            bool toTexture = frame.Target != null;
            if (toTexture)
            {
                GLRenderTarget target = GetRenderTarget(frame.Target!);
                output = target.Framebuffer;
                views = target.Views;
            }

            if (frame.HasCamera)
            {
                bool shadows = frame.HasShadows;
                if (shadows)
                {
                    Mark("Shadows");
                    DrawShadows(frame);
                }

                if (frame.LocalShadowDraws.Count > 0)
                {
                    Mark("Light shadows");
                    DrawLocalShadows(frame);
                }

                FrameUniforms uniforms = FrameUniforms.Create(frame, shadows ? frame.Cascades : null);
                views.Ensure(frame.Width, frame.Height);

                bool fog = uniforms.FogReach.Z > 0.5f;
                if (fog)
                {
                    (int fogWidth, int fogHeight) = VolumetricFog.GridSize(frame.Width, frame.Height);
                    bool fresh = views.EnsureFog(fogWidth, fogHeight);
                    views.Fog.Prepare(ref uniforms, frame, fogWidth, fogHeight, fresh);
                }
                else
                {
                    views.Fog.Forget();
                }

                _gl.BindBuffer(BufferTargetARB.UniformBuffer, _frameBuffer);
                _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)sizeof(FrameUniforms), &uniforms, BufferUsageARB.StreamDraw);
                _gl.BindBufferBase(BufferTargetARB.UniformBuffer, _frameBinding, _frameBuffer);

                // Which lights reach each part of the view.
                ReadOnlySpan<uint> clusters = frame.Clusters.Words;
                _gl.BindBuffer(BufferTargetARB.TextureBuffer, _clusterBuffer);
                fixed (uint* words = clusters) _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)(clusters.Length * sizeof(uint)), words, BufferUsageARB.StreamDraw);
                _gl.ActiveTexture(TextureUnit.Texture0 + _clusterUnit);
                _gl.BindTexture(TextureTarget.TextureBuffer, _clusterTexture);
                (frame.Lightmap != null ? GetTexture(frame.Lightmap) : _white).Bind(_lightmapUnit);
                (frame.ShadowMask != null ? GetTexture(frame.ShadowMask) : _white).Bind(_shadowMaskUnit);
                _localShadowMaps.Bind(_localShadowUnit);

                if (fog)
                {
                    Mark("Volumetric fog");
                    DrawFog(views);
                }

                // What the scene's shaders read the fog from; nothing when there's none (they don't look then).
                _gl.ActiveTexture(TextureUnit.Texture0 + FogUnit);
                _gl.BindTexture(TextureTarget.Texture2DArray, fog ? views.FogTotal : 0);

                UploadMaterials(frame);
                Mark("Scene");
                DrawScene(frame, views, uniforms.ClearColor);
                FinishView(frame, views, output, toTexture);
            }
            else
            {
                Color clear = frame.ClearColor;
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, output);
                _gl.Viewport(0, 0, (uint)frame.Width, (uint)frame.Height);
                _gl.ClearColor(clear.R, clear.G, clear.B, clear.A);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            }

            if (toTexture)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                return;
            }

            Mark("Canvas");
            // The 2D layer goes on top of the screen, camera or not.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)frame.Width, (uint)frame.Height);
            _canvas.Draw(frame.CanvasQuads, frame.Width, frame.Height, GetTexture);
        }

        /// <summary>The sun's view of everything that casts shadows, into each cascade in turn (see <see cref="ShadowCascades"/>).</summary>
        private void DrawShadows(RenderFrame frame)
        {
            // Made again at a new size if the resolution's been changed.
            int size = frame.Cascades.Resolution;
            if (_shadowMaps.Size != size)
            {
                _shadowMaps.Dispose();
                _shadowMaps = new GLShadowMaps(_gl, size);
            }

            // Drawing back faces only keeps lit surfaces from shadowing themselves.
            _gl.CullFace(TriangleFace.Front);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _shadowShader.Use();

            for (int cascade = 0; cascade < ShadowCascades.Count; cascade++)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowMaps.Framebuffer(cascade));
                _gl.Viewport(0, 0, (uint)size, (uint)size);
                _gl.Clear(ClearBufferMask.DepthBufferBit);

                Matrix4x4 lightSpace = frame.Cascades.Matrices[cascade];
                foreach (DrawCall draw in frame.ShadowCasters(cascade))
                {
                    _shadowShader.SetMatrix4("draw.lightModel", draw.WorldMatrix * lightSpace);
                    GetMesh(draw.Mesh).Draw();
                }
            }

            _gl.CullFace(TriangleFace.Back);
        }

        /// <summary>The layers of the point and spot lights' shadow map that have changed (see <see cref="LocalShadows"/>).</summary>
        private void DrawLocalShadows(RenderFrame frame)
        {
            _gl.CullFace(TriangleFace.Front);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _shadowShader.Use();

            int size = LocalShadows.Resolution;
            foreach (LocalShadowDraw layer in frame.LocalShadowDraws)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _localShadowMaps.Framebuffer(layer.Layer));
                _gl.Viewport(0, 0, (uint)size, (uint)size);
                _gl.Clear(ClearBufferMask.DepthBufferBit);
                for (int i = layer.FirstCaster; i < layer.FirstCaster + layer.CasterCount; i++)
                {
                    DrawCall draw = frame.LocalShadowCasters[i];
                    _shadowShader.SetMatrix4("draw.lightModel", draw.WorldMatrix * layer.ViewProjection);
                    GetMesh(draw.Mesh).Draw();
                }
            }

            _gl.CullFace(TriangleFace.Back);
        }

        /// <summary>
        /// The volumetric fog over the view (see <see cref="ShaderLibrary.FogLightFragment"/>): the lit fog in each cell,
        /// carried on from last frame's, then added up from the camera out, a few slices a pass.
        /// </summary>
        private void DrawFog(GLViewTargets views)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.Blend);
            _gl.BindVertexArray(_emptyVertexArray);
            _shadowMaps.Bind(_shadowUnit);
            GetTexture(CloudNoise.Texture).Bind(_cloudUnit);

            // Nothing the passes write may be bound where the scene's shaders read the fog.
            _gl.ActiveTexture(TextureUnit.Texture0 + FogUnit);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);

            int current = views.Fog.Current;
            RunFogPasses(_fogLight, views, views.FogLightFramebuffers[current], views.FogLight[1 - current]);
            RunFogPasses(_fogTotal, views, views.FogTotalFramebuffers, views.FogLight[current]);

            _gl.ActiveTexture(TextureUnit.Texture0 + _fogSourceUnit);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
        }

        private void RunFogPasses(GLShader shader, GLViewTargets views, uint[] framebuffers, uint source)
        {
            shader.Use();
            _gl.ActiveTexture(TextureUnit.Texture0 + _fogSourceUnit);
            _gl.BindTexture(TextureTarget.Texture2DArray, source);

            for (int pass = 0; pass < framebuffers.Length; pass++)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffers[pass]);
                _gl.Viewport(0, 0, (uint)views.FogWidth, (uint)views.FogHeight);
                shader.SetVector4("fogPass.settings", new Vector4(pass * ShaderLibrary.FogSlicesPerPass, 0f, 0f, 0f));
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
        }

        /// <summary>
        /// Every material this view draws with, written one after another into one uniform buffer, each where a draw
        /// can be pointed at it.
        /// </summary>
        private unsafe void UploadMaterials(RenderFrame frame)
        {
            _materialOffsets.Clear();
            int stride = (sizeof(MaterialUniforms) + _uniformAlignment - 1) / _uniformAlignment * _uniformAlignment;
            int size = 0;

            foreach (DrawCall draw in frame.DrawCalls)
            {
                if (_materialOffsets.ContainsKey(draw.Material)) continue;

                if (size + stride > _materialData.Length) Array.Resize(ref _materialData, Math.Max(_materialData.Length * 2, size + stride));
                MaterialUniforms data = MaterialUniforms.Create(draw.Material, Renderer.SurfaceShader(draw.Material));
                MemoryMarshal.Write(_materialData.AsSpan(size), in data);
                _materialOffsets.Add(draw.Material, size);
                size += stride;
            }

            if (size == 0) return;

            _gl.BindBuffer(BufferTargetARB.UniformBuffer, _materialBuffer);
            fixed (byte* data = _materialData) _gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)size, data, BufferUsageARB.StreamDraw);
        }

        private unsafe void DrawScene(RenderFrame frame, GLViewTargets views, Vector4 clearColor)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, views.SceneFramebuffer);
            _gl.Viewport(0, 0, (uint)frame.Width, (uint)frame.Height);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.DepthMask(true);

            float* color = stackalloc float[4] { clearColor.X, clearColor.Y, clearColor.Z, 1f };
            float* none = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _gl.ClearBuffer(BufferKind.Color, 0, color);
            _gl.ClearBuffer(BufferKind.Color, 1, none);
            _gl.ClearBuffer(BufferKind.Color, 2, none);
            _gl.Clear(ClearBufferMask.DepthBufferBit);

            _shadowMaps.Bind(_shadowUnit);

            GLShader? currentShader = null;
            Material? currentMaterial = null;
            Mesh? currentMesh = null;
            GLMesh? glMesh = null;

            // The draws come grouped by shader, then material, then mesh (see Renderer), so each is only set up when it
            // changes: many draws in a row share a material, and some a mesh too.
            IReadOnlyList<DrawCall> draws = frame.DrawCalls;
            for (int i = 0; i < draws.Count; i++)
            {
                DrawCall draw = draws[i];
                Material material = draw.Material;

                if (material != currentMaterial)
                {
                    Shader? surface = Renderer.SurfaceShader(material);
                    GLShader program = SceneProgram(material, surface);
                    if (program != currentShader)
                    {
                        program.Use();
                        currentShader = program;
                    }

                    _gl.BindBufferRange(BufferTargetARB.UniformBuffer, _materialBinding, _materialBuffer, _materialOffsets[material],
                        (nuint)sizeof(MaterialUniforms));
                    (material.Texture != null ? GetTexture(material.Texture) : _white).Bind(_textureUnit);
                    (material.NormalMap != null ? GetTexture(material.NormalMap) : _white).Bind(_normalUnit);
                    if (surface != null) BindShaderTextures(surface.PackTextures(material.Values));
                    currentMaterial = material;
                }

                GLShader shader = currentShader!;
                shader.SetMatrix4("draw.model", draw.WorldMatrix);
                BakedLight baked = draw.Baked;
                shader.SetVector4("draw.lightmap", new Vector4(baked.Lightmapped ? 1f : 0f, baked.FromProbes ? 1f : 0f, 0f, 0f));
                shader.SetVector4("draw.probe[0]", baked.Probe.Red);
                shader.SetVector4("draw.probe[1]", baked.Probe.Green);
                shader.SetVector4("draw.probe[2]", baked.Probe.Blue);

                if (draw.Mesh != currentMesh)
                {
                    glMesh = GetMesh(draw.Mesh);
                    currentMesh = draw.Mesh;
                }

                glMesh!.Draw();
            }

            if (frame.Environment.DrawSky)
            {
                // Behind everything: drawn after the meshes at the far plane, only where none of them are, so the sky's
                // clouds aren't worked out for pixels that are then covered.
                GetTexture(CloudNoise.Texture).Bind(_cloudUnit);
                _skyShader.Use();
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.DepthMask(false);
                _gl.BindVertexArray(_emptyVertexArray);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                _gl.DepthMask(true);
                _gl.DepthFunc(DepthFunction.Less);
            }

            if (frame.Grid)
            {
                // The editor's ground grid: blended over the scene and tested against it, but hiding nothing.
                _gridShader.Use();
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                _gl.Disable(EnableCap.CullFace);
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.DepthMask(false);
                _gl.BindVertexArray(_emptyVertexArray);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                _gl.DepthMask(true);
                _gl.DepthFunc(DepthFunction.Less);
                _gl.Enable(EnableCap.CullFace);
                _gl.Disable(EnableCap.Blend);
            }

            _sprites.Draw(frame, texture => texture != null ? GetTexture(texture) : _white);
            _particles.Draw(frame);
        }

        /// <summary>
        /// The view's post passes, as the Vulkan backend's: ambient occlusion, bloom, then the finished picture into
        /// <paramref name="output"/>, anti-aliased on the way if wanted.
        /// </summary>
        private void FinishView(RenderFrame frame, GLViewTargets views, uint output, bool toTexture)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.Blend);
            _gl.BindVertexArray(_emptyVertexArray);

            EnvironmentData environment = frame.Environment;
            PostUniforms post = PostUniforms.ForView(frame);
            int width = views.Width, height = views.Height;

            bool occlusion = environment.AmbientOcclusion > 0f;
            if (occlusion)
            {
                Mark("Ambient occlusion");
                int occlusionWidth = views.OcclusionWidth, occlusionHeight = views.OcclusionHeight;
                RunPost(_occlusion, views.OcclusionFramebuffer, occlusionWidth, occlusionHeight, post, _white.Handle, depth: views.Depth);
                post.TexelSize = new Vector2(1f / occlusionWidth, 1f / occlusionHeight);
                post.Direction = Vector2.UnitX;
                RunPost(_occlusionBlur, views.OcclusionBlurFramebuffer, occlusionWidth, occlusionHeight, post, views.Occlusion);
                post.Direction = Vector2.UnitY;
                RunPost(_occlusionBlur, views.OcclusionFramebuffer, occlusionWidth, occlusionHeight, post, views.OcclusionBlur);
                post.Direction = Vector2.Zero;
            }

            Mark("Bloom");
            bool bloom = frame.Bloom > 0f && views.BloomLevels.Count > 0;
            if (bloom)
            {
                // Down, keeping only the bright parts on the first step; then back up, each size added onto the next.
                uint source = views.Color;
                int sourceWidth = width, sourceHeight = height;
                post.Threshold = PostUniforms.BloomThreshold;
                foreach ((uint framebuffer, uint texture, int levelWidth, int levelHeight) in views.BloomLevels)
                {
                    post.TexelSize = new Vector2(1f / sourceWidth, 1f / sourceHeight);
                    RunPost(_bloomDown, framebuffer, levelWidth, levelHeight, post, source, bloom: views.Glow);
                    source = texture;
                    sourceWidth = levelWidth;
                    sourceHeight = levelHeight;
                    post.Threshold = 0f;
                }

                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                for (int i = views.BloomLevels.Count - 1; i > 0; i--)
                {
                    (_, uint texture, int levelWidth, int levelHeight) = views.BloomLevels[i];
                    (uint target, _, int targetWidth, int targetHeight) = views.BloomLevels[i - 1];
                    post.TexelSize = new Vector2(1f / levelWidth, 1f / levelHeight);
                    RunPost(_bloomUp, target, targetWidth, targetHeight, post, texture);
                }
                _gl.Disable(EnableCap.Blend);
            }

            post.Occlusion = occlusion ? environment.AmbientOcclusion : 0f;
            post.BloomStrength = bloom ? PostUniforms.BloomScale(frame.Bloom, views.BloomLevels.Count) : 0f;
            uint bloomImage = bloom ? views.BloomLevels[0].Texture : _white.Handle;
            uint occlusionImage = occlusion ? views.Occlusion : _white.Handle;

            // A game's post effects (see PostEffect), those that build, go after everything else: the finished picture
            // goes into an image of their own for the first to read.
            _effects.Clear();
            foreach (PostEffectData effect in frame.PostEffects)
            {
                if (EffectShader(effect.Shader) is { } program) _effects.Add((effect, program));
            }

            bool effects = _effects.Count > 0;
            if (effects) views.EnsureEffects();
            uint finished = effects ? views.EffectFramebuffers[0] : output;

            // A render texture wants its top row first, as one loaded from a file has it; OpenGL's start at the bottom.
            if (environment.AntiAliasing)
            {
                Mark("Composite");
                RunPost(_composite, views.FinishedFramebuffer, width, height, post, views.Color, bloomImage, views.Depth, occlusionImage, views.Ambient);
                post.TexelSize = new Vector2(1f / width, 1f / height);
                Mark("Anti-aliasing");
                RunPost(_antiAlias, finished, frame.Width, frame.Height, post, views.Finished, flipY: toTexture && !effects);
            }
            else
            {
                Mark("Composite");
                RunPost(_composite, finished, frame.Width, frame.Height, post, views.Color, bloomImage, views.Depth, occlusionImage, views.Ambient,
                    flipY: toTexture && !effects);
            }

            if (effects)
            {
                // Each reads what the one before made and draws into the other image, the last into the output.
                post.TexelSize = new Vector2(1f / width, 1f / height);
                uint source = views.Effects[0];
                for (int i = 0; i < _effects.Count; i++)
                {
                    (PostEffectData effect, GLShader program) = _effects[i];
                    Mark(effect.Shader.Name);
                    bool last = i == _effects.Count - 1;
                    program.Use();
                    program.SetVector4Array("shaderValues", effect.Values);
                    BindShaderTextures(effect.Textures);
                    RunPost(program, last ? output : views.EffectFramebuffers[(i + 1) % 2], frame.Width, frame.Height, post, source, depth: views.Depth,
                        flipY: last && toTexture);
                    source = views.Effects[(i + 1) % 2];
                }
            }

            // The view's images are drawn into next frame, so they mustn't be left where a shader could read them.
            for (int unit = 4; unit >= 0; unit--) BindTexture(unit, 0);

            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
        }

        private void RunPost(GLShader shader, uint framebuffer, int width, int height, in PostUniforms post, uint source,
            uint bloom = 0, uint depth = 0, uint occlusion = 0, uint ambient = 0, bool flipY = false)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            shader.Use();
            shader.SetPost(post);
            shader.SetBool("flipY", flipY);

            uint white = _white.Handle;
            BindTexture(0, source);
            BindTexture(1, bloom != 0 ? bloom : white);
            BindTexture(2, depth != 0 ? depth : white);
            BindTexture(3, occlusion != 0 ? occlusion : white);
            BindTexture(4, ambient != 0 ? ambient : white);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void BindTexture(int unit, uint texture)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
        }

        private GLMesh GetMesh(Mesh mesh)
        {
            if (!_meshes.TryGetValue(mesh, out GLMesh? glMesh))
            {
                glMesh = new GLMesh(_gl, mesh);
                _meshes.Add(mesh, glMesh);
                mesh.Disposed += OnMeshDisposed;
            }
            else if (glMesh.Version != mesh.Version)
            {
                glMesh.UpdateVertices(mesh);
            }

            return glMesh;
        }

        private GLRenderTarget GetRenderTarget(RenderTexture texture)
        {
            if (!_renderTargets.TryGetValue(texture, out GLRenderTarget? target))
            {
                target = new GLRenderTarget(_gl, texture);
                _renderTargets.Add(texture, target);
                texture.Disposed += OnTextureDisposed;
            }
            else
            {
                target.Update(texture);
            }

            return target;
        }

        private GLTexture GetTexture(Texture texture)
        {
            // Drawn into by a camera, so there are no pixels to upload: use what it drew.
            if (texture is RenderTexture renderTexture) return GetRenderTarget(renderTexture).Color;

            if (!_textures.TryGetValue(texture, out GLTexture? glTexture))
            {
                glTexture = new GLTexture(_gl, texture);
                _textures.Add(texture, glTexture);
                texture.Disposed += OnTextureDisposed;
            }
            else if (glTexture.Version != texture.Version)
            {
                // Changed since it was uploaded (a font atlas that gained new letters, say).
                glTexture.Upload(texture);
            }

            return glTexture;
        }

        private void OnMeshDisposed(Mesh mesh)
        {
            if (_meshes.TryGetValue(mesh, out GLMesh? glMesh))
            {
                glMesh.Dispose();
                _meshes.Remove(mesh);
            }
        }

        private void OnTextureDisposed(Texture texture)
        {
            if (_textures.TryGetValue(texture, out GLTexture? glTexture))
            {
                glTexture.Dispose();
                _textures.Remove(texture);
            }

            if (texture is RenderTexture renderTexture && _renderTargets.TryGetValue(renderTexture, out GLRenderTarget? target))
            {
                target.Dispose();
                _renderTargets.Remove(renderTexture);
            }
        }

        public void Dispose()
        {
            // Never initialised (the window didn't open): nothing was made.
            if (_gl == null) return;

            foreach ((uint buffer, nint fence, int _, Action<byte[]> _) in _readBacks)
            {
                _gl.DeleteBuffer(buffer);
                _gl.DeleteSync(fence);
            }
            _readBacks.Clear();

            foreach (KeyValuePair<Mesh, GLMesh> entry in _meshes) entry.Value.Dispose();
            foreach (KeyValuePair<Texture, GLTexture> entry in _textures) entry.Value.Dispose();
            foreach (KeyValuePair<RenderTexture, GLRenderTarget> entry in _renderTargets) entry.Value.Dispose();
            _meshes.Clear();
            _textures.Clear();
            _renderTargets.Clear();

            foreach (GLShader shader in new[] { _fogLight, _fogTotal, _litShader, _unlitShader, _skyShader, _gridShader, _shadowShader, _bloomDown, _bloomUp, _occlusion, _occlusionBlur, _composite, _antiAlias })
                shader.Dispose();
            foreach ((int _, GLShader? program) in _effectShaders.Values) program?.Dispose();
            foreach ((int _, GLShader? program) in _surfaceShaders.Values) program?.Dispose();
            _shadowMaps.Dispose();
            _screenViews.Dispose();
            _canvas.Dispose();
            _particles.Dispose();
            _sprites.Dispose();
            _white.Dispose();
            _timer.Dispose();
            _gl.DeleteBuffer(_frameBuffer);
            _gl.DeleteBuffer(_materialBuffer);
            _gl.DeleteTexture(_clusterTexture);
            _localShadowMaps.Dispose();
            _gl.DeleteBuffer(_clusterBuffer);
            _gl.DeleteVertexArray(_emptyVertexArray);
        }
    }
}
