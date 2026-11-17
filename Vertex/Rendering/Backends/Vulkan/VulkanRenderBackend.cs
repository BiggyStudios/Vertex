using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

using Vertex.Rendering.Shaders;

using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Vertex.Rendering.Vulkan
{
    /// <summary>
    /// The Vulkan 1.3 implementation of <see cref="IRenderBackend"/>, and Vertex's main renderer. It draws exactly what the
    /// OpenGL backend does, with the same shaders (see <see cref="ShaderLibrary"/>), into the screen or render textures:
    /// the sun's shadow cascades, the sky, physically based lighting with fog, particles, ambient occlusion, bloom, tone
    /// mapping and anti-aliasing, then the 2D canvas. It also simulates <see cref="GpuParticleSystem"/>s with compute
    /// shaders. Two frames are in flight: the CPU records one while the GPU draws the other.
    /// </summary>
    public sealed unsafe class VulkanRenderBackend : IRenderBackend
    {
        private const int _framesInFlight = 2;
        private const int _particleFloats = 11; // position xyz, corner xy, colour rgba, glow, additive

        [StructLayout(LayoutKind.Sequential)]
        private struct DrawConstants
        {
            public Matrix4x4 Model;
            public Matrix4x4 NormalMatrix;
        }

        /// <summary>The GPU copy of a <see cref="Mesh"/>: vertices then indices, in one buffer.</summary>
        private sealed class VkMesh(VkBufferResource buffer, ulong indexOffset, uint indexCount)
        {
            public readonly VkBufferResource Buffer = buffer;
            public readonly ulong IndexOffset = indexOffset;
            public readonly uint IndexCount = indexCount;
        }

        /// <summary>The GPU copy of a <see cref="Texture"/>, with mipmaps.</summary>
        private sealed class VkTexture(VkImageResource image, int version)
        {
            public VkImageResource Image = image;
            public int Version = version;
        }

        /// <summary>The GPU side of a <see cref="RenderTexture"/>: the finished picture, and the images it's drawn through.</summary>
        private sealed class VkRenderTarget(VkImageResource color, VkViewTargets views, int version)
        {
            public VkImageResource Color = color;
            public readonly VkViewTargets Views = views;
            public int Version = version;
        }

        private VkContext _context = null!;
        private VkShaderCompiler _compiler = null!;
        private VkSwapchain _swapchain = null!;
        private readonly VkFrame[] _frames = new VkFrame[_framesInFlight];
        private VkFrame _frame = null!;
        private long _frameNumber;
        private uint _imageIndex;
        private bool _frameStarted;

        // The next frame's slot is free and its swapchain image acquired, ahead of BeginFrame.
        private bool _acquired;

        // The id given to the last present, and the first given to the current swapchain (see WaitUntilShown).
        private ulong _lastPresentId;
        private ulong _firstPresentIdOnSwapchain = 1;

        private bool _screenDrawn;
        private bool _swapchainOutOfDate;
        private int _width;
        private int _height;

        private DescriptorSetLayout _frameSetLayout;
        private DescriptorSetLayout _materialSetLayout;
        private DescriptorSetLayout _textureSetLayout;
        private DescriptorSetLayout _postSetLayout;
        private PipelineLayout _sceneLayout;
        private PipelineLayout _shadowLayout;
        private PipelineLayout _gpuParticleLayout;
        private PipelineLayout _canvasLayout;
        private PipelineLayout _postLayout;

        private Pipeline _lit, _unlit, _sky, _shadow, _particles, _gpuParticles, _canvas;
        private Pipeline _bloomDown, _bloomUp, _occlusion, _occlusionBlur;
        private Pipeline _compositeToScreen, _compositeToTexture, _antiAliasToScreen, _antiAliasToTexture;
        private readonly List<ShaderModule> _shaders = new();

        private Sampler _repeatSampler;
        private Sampler _clampSampler;
        private Sampler _shadowSampler;

        private VkShadowMaps _shadowMaps = null!;
        private VkViewTargets _screenViews = null!;
        private VkGpuParticles _gpuParticlePool = null!;
        private VkTexture _white = null!;

        private readonly ConditionalWeakTable<Mesh, VkMesh> _meshes = new();
        private readonly ConditionalWeakTable<Texture, VkTexture> _textures = new();
        private readonly ConditionalWeakTable<RenderTexture, VkRenderTarget> _renderTargets = new();

        // Descriptor sets made this frame, to use again for every draw (and view) that wants the same one.
        private readonly Dictionary<Material, DescriptorSet> _materialSets = new();
        private readonly Dictionary<Texture, DescriptorSet> _textureSets = new();

        private float[] _particleVertices = new float[6 * _particleFloats * 256];
        private float[] _canvasVertices = new float[6 * 8 * 256];
        private readonly List<(Texture Texture, int First, int Count)> _canvasBatches = new();
        private bool _uploadedThisView;

        public string Name => "Vulkan";

        public bool SupportsGpuParticles => true;

        /// <inheritdoc/>
        public bool MeasureGpuTime { get; set; }

        // The GPU's times for the latest frame it finished, and whether they're newer than what was last read.
        private readonly List<GpuPassTime> _gpuTimes = new();
        private bool _gpuTimesNew;

        // Whether this frame is being timed.
        private bool _timing;

        /// <summary>
        /// How many finished frames may wait to be shown before the next is held back, which is what input lag is made
        /// of. 0, the default, is the least lag: each frame starts once the last is on screen, about one refresh from
        /// input to screen. 1 adds a refresh of lag but keeps the frame rate steadier in scenes that take nearly a whole
        /// refresh to draw. -1 leaves pacing to the driver (the most lag). Only has an effect where the driver supports
        /// VK_KHR_present_wait, as NVIDIA's do.
        /// </summary>
        public int MaxQueuedFrames { get; set; }

        /// <summary>The GPU it's drawing with.</summary>
        public string DeviceName => _context?.DeviceName ?? "";

        /// <summary>Whether this computer can run it (a Vulkan 1.3 GPU and driver), and if not, why.</summary>
        public static bool IsSupported(out string reason) => VkContext.IsSupported(out reason);

        public WindowOptions ConfigureWindow(WindowOptions options)
        {
            return options with { API = GraphicsAPI.DefaultVulkan };
        }

        public void Initialise(IWindow window)
        {
            _context = new VkContext(window);
            _compiler = new VkShaderCompiler(_context);
            _width = window.FramebufferSize.X;
            _height = window.FramebufferSize.Y;
            _swapchain = new VkSwapchain(_context, _width, _height);
            for (int i = 0; i < _framesInFlight; i++) _frames[i] = new VkFrame(_context);
            _frame = _frames[0];

            _repeatSampler = _context.CreateSampler(Filter.Linear, SamplerAddressMode.Repeat, mipmaps: true, anisotropic: true);
            _clampSampler = _context.CreateSampler(Filter.Linear, SamplerAddressMode.ClampToEdge, mipmaps: false);
            _shadowSampler = _context.CreateSampler(Filter.Linear, SamplerAddressMode.ClampToBorder, mipmaps: false, compare: true);

            CreateLayouts();
            _gpuParticlePool = new VkGpuParticles(_context, _compiler);
            CreatePipelines();

            _shadowMaps = new VkShadowMaps(_context, ShadowCascades.DefaultResolution);
            _screenViews = new VkViewTargets(_context);

            // White, already premultiplied, for untextured canvas shapes and images a pass doesn't use; and the shadow
            // maps start fully lit, so they're ready to be read before anything's drawn into them.
            _white = new VkTexture(_context.CreateImage(1, 1, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit), 0);
            _context.RunNow(commands =>
            {
                Upload(commands, _white.Image, [255, 255, 255, 255], deleteLater: null);

                VkImageResource shadows = _shadowMaps.Array;
                _context.Transition(commands, shadows, ImageLayout.TransferDstOptimal, Stage.None, Stage.TransferWrite, discard: true);
                ClearDepthStencilValue clear = new(1f, 0);
                ImageSubresourceRange range = new(ImageAspectFlags.DepthBit, 0, 1, 0, shadows.Layers);
                _context.Vk.CmdClearDepthStencilImage(commands, shadows.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
                _context.Transition(commands, shadows, ImageLayout.ShaderReadOnlyOptimal, Stage.TransferWrite, Stage.FragmentRead);
            });

            Console.WriteLine($"[Vertex] Rendering with Vulkan on {_context.DeviceName}.");
        }

        private void CreateLayouts()
        {
            const ShaderStageFlags vertexAndFragment = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
            const ShaderStageFlags fragment = ShaderStageFlags.FragmentBit;

            _frameSetLayout = VkDescriptors.CreateLayout(_context,
                (DescriptorType.UniformBuffer, vertexAndFragment),
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment));
            _materialSetLayout = VkDescriptors.CreateLayout(_context,
                (DescriptorType.UniformBuffer, fragment),
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment));
            _textureSetLayout = VkDescriptors.CreateLayout(_context, (DescriptorType.CombinedImageSampler, fragment));
            _postSetLayout = VkDescriptors.CreateLayout(_context,
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment),
                (DescriptorType.CombinedImageSampler, fragment));

            _sceneLayout = VkDescriptors.CreatePipelineLayout(_context, [_frameSetLayout, _materialSetLayout], (uint)sizeof(DrawConstants), ShaderStageFlags.VertexBit);
            _shadowLayout = VkDescriptors.CreatePipelineLayout(_context, [], (uint)sizeof(Matrix4x4), ShaderStageFlags.VertexBit);
            _canvasLayout = VkDescriptors.CreatePipelineLayout(_context, [_textureSetLayout], (uint)sizeof(Vector2), ShaderStageFlags.VertexBit);
            _postLayout = VkDescriptors.CreatePipelineLayout(_context, [_postSetLayout], (uint)sizeof(PostUniforms), fragment);
        }

        /// <summary>One of the shared shaders, built for Vulkan.</summary>
        private ShaderModule Shader(string name, ShaderKind kind, string source) => Module(name, kind, ShaderLibrary.Build(ShaderTarget.Vulkan, source));

        private ShaderModule Module(string name, ShaderKind kind, string fullSource)
        {
            ShaderModule module = _compiler.Create(name, kind, fullSource);
            _shaders.Add(module);
            return module;
        }

        private void CreatePipelines()
        {
            _gpuParticleLayout = VkDescriptors.CreatePipelineLayout(_context, [_frameSetLayout, _gpuParticlePool.DrawSetLayout], 0, 0);

            const ShaderKind vertex = ShaderKind.VertexShader;
            const ShaderKind fragment = ShaderKind.FragmentShader;
            ShaderModule meshVertex = Shader("mesh.vert", vertex, ShaderLibrary.MeshVertex);
            ShaderModule litFragment = Shader("lit.frag", fragment, ShaderLibrary.LitFragment);
            ShaderModule unlitFragment = Shader("unlit.frag", fragment, ShaderLibrary.UnlitFragment);
            ShaderModule shadowVertex = Shader("shadow.vert", vertex, ShaderLibrary.ShadowVertex);
            ShaderModule skyVertex = Shader("sky.vert", vertex, ShaderLibrary.SkyVertex);
            ShaderModule skyFragment = Shader("sky.frag", fragment, ShaderLibrary.SkyFragment);
            ShaderModule particleVertex = Shader("particle.vert", vertex, ShaderLibrary.ParticleVertex);
            ShaderModule gpuParticleVertex = Shader("gpuparticle.vert", vertex, ShaderLibrary.GpuParticleVertex);
            ShaderModule particleFragment = Shader("particle.frag", fragment, ShaderLibrary.ParticleFragment);
            ShaderModule canvasVertex = Module("canvas.vert", vertex, VkShaderSources.CanvasVertex);
            ShaderModule canvasFragment = Module("canvas.frag", fragment, VkShaderSources.CanvasFragment);
            ShaderModule fullscreen = Shader("fullscreen.vert", vertex, ShaderLibrary.FullscreenVertex);
            ShaderModule bloomDown = Shader("bloomdown.frag", fragment, ShaderLibrary.BloomDownsampleFragment);
            ShaderModule bloomUp = Shader("bloomup.frag", fragment, ShaderLibrary.BloomUpsampleFragment);
            ShaderModule occlusion = Shader("occlusion.frag", fragment, ShaderLibrary.OcclusionFragment);
            ShaderModule occlusionBlur = Shader("occlusionblur.frag", fragment, ShaderLibrary.OcclusionBlurFragment);
            ShaderModule composite = Shader("composite.frag", fragment, ShaderLibrary.CompositeFragment);
            ShaderModule antiAlias = Shader("antialias.frag", fragment, ShaderLibrary.AntiAliasFragment);

            Format[] sceneFormats = [VkViewTargets.HdrFormat, _context.LightFormat, _context.LightFormat];
            (uint, Format, uint)[] meshAttributes =
            [
                (0, Format.R32G32B32Sfloat, 0),
                (1, Format.R32G32B32Sfloat, 12),
                (2, Format.R32G32Sfloat, 24)
            ];

            VkPipelineDescription Scene(ShaderModule fragmentShader) => new()
            {
                Layout = _sceneLayout,
                Vertex = meshVertex,
                Fragment = fragmentShader,
                VertexStride = (uint)sizeof(MeshVertex),
                Attributes = meshAttributes,
                Cull = CullModeFlags.BackBit,
                DepthTest = true,
                DepthWrite = true,
                ColorFormats = sceneFormats,
                DepthFormat = _context.DepthFormat
            };

            _lit = VkPipelines.CreateGraphics(_context, Scene(litFragment));
            _unlit = VkPipelines.CreateGraphics(_context, Scene(unlitFragment));

            // Behind everything: drawn after the meshes, at the far plane, only where none of them are, so the sky's
            // clouds aren't worked out for pixels that are then covered.
            _sky = VkPipelines.CreateGraphics(_context, new VkPipelineDescription
            {
                Layout = _sceneLayout,
                Vertex = skyVertex,
                Fragment = skyFragment,
                ColorFormats = sceneFormats,
                DepthTest = true,
                DepthFormat = _context.DepthFormat
            });

            // Depth only. Drawing back faces only keeps lit surfaces from shadowing themselves.
            _shadow = VkPipelines.CreateGraphics(_context, new VkPipelineDescription
            {
                Layout = _shadowLayout,
                Vertex = shadowVertex,
                VertexStride = (uint)sizeof(MeshVertex),
                Attributes = [(0, Format.R32G32B32Sfloat, 0)],
                Cull = CullModeFlags.FrontBit,
                DepthTest = true,
                DepthWrite = true,
                DepthFormat = _context.ShadowDepthFormat
            });

            // Depth tested against the scene but not written, so particles never hide each other.
            _particles = VkPipelines.CreateGraphics(_context, new VkPipelineDescription
            {
                Layout = _sceneLayout,
                Vertex = particleVertex,
                Fragment = particleFragment,
                VertexStride = _particleFloats * sizeof(float),
                Attributes =
                [
                    (0, Format.R32G32B32Sfloat, 0),
                    (1, Format.R32G32Sfloat, 12),
                    (2, Format.R32G32B32A32Sfloat, 20),
                    (3, Format.R32Sfloat, 36),
                    (4, Format.R32Sfloat, 40)
                ],
                DepthTest = true,
                Blend = VkBlend.Premultiplied,
                ColorFormats = sceneFormats,
                DepthFormat = _context.DepthFormat
            });

            _gpuParticles = VkPipelines.CreateGraphics(_context, new VkPipelineDescription
            {
                Layout = _gpuParticleLayout,
                Vertex = gpuParticleVertex,
                Fragment = particleFragment,
                DepthTest = true,
                Blend = VkBlend.Premultiplied,
                ColorFormats = sceneFormats,
                DepthFormat = _context.DepthFormat
            });

            _canvas = VkPipelines.CreateGraphics(_context, new VkPipelineDescription
            {
                Layout = _canvasLayout,
                Vertex = canvasVertex,
                Fragment = canvasFragment,
                VertexStride = 8 * sizeof(float),
                Attributes = [(0, Format.R32G32Sfloat, 0), (1, Format.R32G32Sfloat, 8), (2, Format.R32G32B32A32Sfloat, 16)],
                Blend = VkBlend.Premultiplied,
                ColorFormats = [_swapchain.Format]
            });

            Pipeline Post(ShaderModule fragmentShader, Format format, VkBlend blend = VkBlend.None) =>
                VkPipelines.CreateGraphics(_context, new VkPipelineDescription
                {
                    Layout = _postLayout,
                    Vertex = fullscreen,
                    Fragment = fragmentShader,
                    Blend = blend,
                    ColorFormats = [format]
                });

            _bloomDown = Post(bloomDown, _context.LightFormat);
            _bloomUp = Post(bloomUp, _context.LightFormat, VkBlend.Additive);
            _occlusion = Post(occlusion, VkViewTargets.OcclusionFormat);
            _occlusionBlur = Post(occlusionBlur, VkViewTargets.OcclusionFormat);
            _compositeToScreen = Post(composite, _swapchain.Format);
            _compositeToTexture = Post(composite, VkViewTargets.OutputFormat);
            _antiAliasToScreen = Post(antiAlias, _swapchain.Format);
            _antiAliasToTexture = Post(antiAlias, VkViewTargets.OutputFormat);
        }

        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public void BeginFrame()
        {
            _frameStarted = false;
            _screenDrawn = false;

            // Normally the last frame's EndFrame already waited for this one's turn (see there); only after a
            // minimise, a resize or at the very start is it still to do.
            if (!_acquired && !WaitForNextFrame()) return;
            _acquired = false;

            CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _context.Vk.ResetCommandBuffer(_frame.Commands, 0);
            _context.Vk.BeginCommandBuffer(_frame.Commands, &begin);

            _timing = MeasureGpuTime && _context.TimestampPeriod > 0f;
            if (_timing) _context.Vk.CmdResetQueryPool(_frame.Commands, _frame.Timestamps, 0, VkFrame.MaxTimestamps);
            _frameStarted = true;
        }

        /// <summary>
        /// Waits until the next frame can be drawn: its frame-in-flight slot is free and the swapchain has an image for
        /// it. This is where the CPU is held back to the screen's refresh. False if there's nothing to draw into
        /// (minimised, or the swapchain needs making again).
        /// </summary>
        private bool WaitForNextFrame()
        {
            if (_width <= 0 || _height <= 0) return false;

            if (_swapchainOutOfDate || _swapchain.RequestedWidth != _width || _swapchain.RequestedHeight != _height)
            {
                _swapchain.Recreate(_width, _height);
                _swapchainOutOfDate = false;

                // Ids presented to the old swapchain can't be waited on with the new one.
                _firstPresentIdOnSwapchain = _lastPresentId + 1;
            }
            else
            {
                WaitUntilShown();
            }

            _frame = _frames[_frameNumber % _framesInFlight];
            _frame.WaitAndReset();
            ReadTimestamps();
            _materialSets.Clear();
            _textureSets.Clear();

            uint? image = _swapchain.Acquire(_frame.ImageAvailable);
            if (image == null)
            {
                _swapchainOutOfDate = true;
                return false;
            }

            _imageIndex = image.Value;
            _acquired = true;
            return true;
        }

        /// <summary>
        /// Holds the next frame back until the last one (or <see cref="MaxQueuedFrames"/> before it) has actually reached
        /// the screen. Without this, the driver lets finished frames pile up in front of the screen (on laptops with two
        /// GPUs especially, where each is copied across to the one the screen is on) and each is a refresh of input lag:
        /// measured at 34.7 ms from input to screen on a 144 Hz laptop, against 7.5 ms with it. Needs VK_KHR_present_wait;
        /// without it, frames are paced by the driver.
        /// </summary>
        private void WaitUntilShown()
        {
            if (_context.PresentWait is not { } presentWait || MaxQueuedFrames < 0 ||
                _lastPresentId < _firstPresentIdOnSwapchain + (ulong)MaxQueuedFrames) return;

            Result result = presentWait.WaitForPresent(_context.Device, _swapchain.Handle, _lastPresentId - (ulong)MaxQueuedFrames, 50_000_000);
            if (result is Result.ErrorOutOfDateKhr or Result.ErrorSurfaceLostKhr) _swapchainOutOfDate = true;
        }

        /// <summary>Notes the time on the GPU as <paramref name="pass"/> begins, if timing. An empty name marks the frame's end.</summary>
        private void Mark(CommandBuffer commands, string pass)
        {
            if (!_timing || _frame.TimestampNames.Count >= VkFrame.MaxTimestamps) return;

            _context.Vk.CmdWriteTimestamp2(commands, PipelineStageFlags2.AllCommandsBit, _frame.Timestamps, (uint)_frame.TimestampNames.Count);
            _frame.TimestampNames.Add(pass);
        }

        /// <summary>Turns the timestamps this frame slot took last time round, now finished, into each pass's time.</summary>
        private void ReadTimestamps()
        {
            List<string> names = _frame.TimestampNames;
            if (names.Count >= 2)
            {
                ulong* ticks = stackalloc ulong[VkFrame.MaxTimestamps];
                Result result = _context.Vk.GetQueryPoolResults(_context.Device, _frame.Timestamps, 0, (uint)names.Count,
                    (nuint)(names.Count * sizeof(ulong)), ticks, sizeof(ulong), QueryResultFlags.Result64Bit);

                if (result == Result.Success)
                {
                    GpuTimestamps.Measure(_gpuTimes, names, new ReadOnlySpan<ulong>(ticks, names.Count), _context.TimestampPeriod / 1e6);
                    _gpuTimesNew = true;
                }
            }

            names.Clear();
        }

        public void ReadGpuTimes(List<GpuPassTime> into)
        {
            if (!_gpuTimesNew) return;

            into.Clear();
            into.AddRange(_gpuTimes);
            _gpuTimesNew = false;
        }

        public void UpdateParticles(GpuParticleUpdate update)
        {
            if (!_frameStarted) return;
            Mark(_frame.Commands, "Particles");
            _gpuParticlePool.Update(_frame.Commands, _frame, update);
        }

        public void Render(RenderFrame frame)
        {
            if (!_frameStarted || frame.Width <= 0 || frame.Height <= 0) return;

            CommandBuffer commands = _frame.Commands;
            Mark(commands, "Uploads");
            Prepare(commands, frame);

            VkImageResource output;
            VkViewTargets views;
            bool toScreen = frame.Target == null;
            if (!toScreen)
            {
                VkRenderTarget target = GetRenderTarget(frame.Target!);
                output = target.Color;
                views = target.Views;
            }
            else
            {
                output = _swapchain.Images[_imageIndex];
                views = _screenViews;
                _screenDrawn = true;
            }

            // The screen's image waits for the swapchain to hand it over (the submit waits at colour output for that);
            // a render texture waits for anything still reading what it showed before.
            Stage outputAfter = toScreen ? new Stage(PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None) : Stage.FragmentRead;

            // The view is drawn at the frame's size, but the swapchain's images can come out a slightly different size
            // than the window asked for; the finished picture is stretched to whatever the output really is.
            int outputWidth = output.Width, outputHeight = output.Height;

            if (frame.HasCamera)
            {
                bool shadows = frame.HasShadows;
                if (shadows)
                {
                    Mark(commands, "Shadows");
                    DrawShadows(commands, frame);
                }

                FrameUniforms uniforms = FrameUniforms.Create(frame, shadows ? frame.Cascades : null);
                DescriptorSet frameSet = WriteFrameData(in uniforms);
                views.Ensure(frame.Width, frame.Height, _frame.DeleteLater);
                Mark(commands, "Scene");
                DrawScene(commands, frame, views, frameSet, in uniforms);

                EnvironmentData environment = frame.Environment;
                PostUniforms post = PostUniforms.ForView(frame);
                bool occlusion = environment.AmbientOcclusion > 0f;
                if (occlusion)
                {
                    Mark(commands, "Ambient occlusion");
                    Occlusion(commands, views, post);
                }

                Mark(commands, "Bloom");
                bool bloom = Bloom(commands, frame, views, post);

                post.Occlusion = occlusion ? environment.AmbientOcclusion : 0f;
                post.BloomStrength = bloom ? PostUniforms.BloomScale(frame.Bloom, views.BloomLevels.Count) : 0f;
                VkImageResource bloomImage = bloom ? views.BloomLevels[0] : _white.Image;
                VkImageResource occlusionImage = occlusion ? views.Occlusion! : _white.Image;

                if (environment.AntiAliasing)
                {
                    // Finished into an image of its own first, which the anti-aliasing reads to smooth into the output.
                    Mark(commands, "Composite");
                    RunPost(commands, _compositeToTexture, views.Finished!, post, views.Color!, bloomImage, views.Depth, occlusionImage, views.Ambient);

                    post.TexelSize = new Vector2(1f / views.Finished!.Width, 1f / views.Finished.Height);
                    Mark(commands, "Anti-aliasing");
                    BeginOutput(commands, output, outputAfter, null);
                    PostDraw(commands, toScreen ? _antiAliasToScreen : _antiAliasToTexture, outputWidth, outputHeight, post, views.Finished);
                }
                else
                {
                    Mark(commands, "Composite");
                    BeginOutput(commands, output, outputAfter, null);
                    PostDraw(commands, toScreen ? _compositeToScreen : _compositeToTexture, outputWidth, outputHeight, post,
                        views.Color!, bloomImage, views.Depth, occlusionImage, views.Ambient);
                }
            }
            else
            {
                Color clear = frame.ClearColor;
                BeginOutput(commands, output, outputAfter, new ClearColorValue(clear.R, clear.G, clear.B, clear.A));
            }

            // The 2D layer goes on top of the screen, camera or not.
            if (toScreen)
            {
                Mark(commands, "Canvas");
                DrawCanvas(commands, frame, outputWidth, outputHeight);
            }
            _context.Vk.CmdEndRendering(commands);

            if (!toScreen)
                _context.Transition(commands, output, ImageLayout.ShaderReadOnlyOptimal, Stage.ColorOutput, Stage.FragmentRead);
        }

        private void BeginOutput(CommandBuffer commands, VkImageResource output, Stage after, ClearColorValue? clear)
        {
            _context.Transition(commands, output, ImageLayout.ColorAttachmentOptimal, after, Stage.ColorOutput, discard: true);
            VkPipelines.BeginRendering(_context, commands, output.Width, output.Height, [(output, clear)]);
        }

        public void EndFrame()
        {
            if (!_frameStarted) return;

            CommandBuffer commands = _frame.Commands;
            VkImageResource screen = _swapchain.Images[_imageIndex];

            // Never drawn this frame (can't happen through Renderer, but the image must still be handed back).
            if (!_screenDrawn)
            {
                _context.Transition(commands, screen, ImageLayout.ColorAttachmentOptimal, new Stage(PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None),
                    Stage.ColorOutput, discard: true);
                VkPipelines.BeginRendering(_context, commands, _swapchain.Width, _swapchain.Height, [(screen, new ClearColorValue(0f, 0f, 0f, 1f))]);
                _context.Vk.CmdEndRendering(commands);
            }

            Mark(commands, "");
            _context.Transition(commands, screen, ImageLayout.PresentSrcKhr, Stage.ColorOutput, Stage.Present);
            _context.Vk.EndCommandBuffer(commands);

            SemaphoreSubmitInfo wait = new()
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = _frame.ImageAvailable,
                StageMask = PipelineStageFlags2.ColorAttachmentOutputBit
            };
            SemaphoreSubmitInfo signal = new()
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = _swapchain.RenderFinished[_imageIndex],
                StageMask = PipelineStageFlags2.AllCommandsBit
            };
            CommandBufferSubmitInfo commandInfo = new() { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = commands };
            SubmitInfo2 submit = new()
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = 1,
                PWaitSemaphoreInfos = &wait,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
                SignalSemaphoreInfoCount = 1,
                PSignalSemaphoreInfos = &signal
            };

            Fence done = _frame.Done;
            _context.Vk.ResetFences(_context.Device, 1, &done);
            VkContext.Check(_context.Vk.QueueSubmit2(_context.Queue, 1, &submit, done), "submit the frame");

            ulong presentId = _context.PresentWait != null ? ++_lastPresentId : 0;
            if (!_swapchain.Present(_imageIndex, presentId)) _swapchainOutOfDate = true;

            _frameStarted = false;
            _frameNumber++;

            // Wait for the next frame's turn now, before handing back to the game loop, rather than at the start of the
            // next render. The loop reads input as soon as this returns, so the next frame is drawn from input read
            // just before it, instead of input that sat waiting through this wait: that waiting was felt as input lag.
            WaitForNextFrame();
        }

        /// <summary>
        /// Makes sure every mesh and texture this view uses is on the GPU and up to date. Copies can't happen while
        /// drawing, so they're all recorded here, before the view's first pass.
        /// </summary>
        private void Prepare(CommandBuffer commands, RenderFrame frame)
        {
            _uploadedThisView = false;

            GetTexture(commands, CloudNoise.Texture);

            IReadOnlyList<DrawCall> draws = frame.DrawCalls;
            for (int i = 0; i < draws.Count; i++)
            {
                GetMesh(commands, draws[i].Mesh);
                if (draws[i].Material.Texture is { } texture) GetTexture(commands, texture);
                if (draws[i].Material.NormalMap is { } normalMap) GetTexture(commands, normalMap);
            }

            // Things out of view can still cast shadows into it.
            for (int cascade = 0; cascade < ShadowCascades.Count; cascade++)
            {
                IReadOnlyList<DrawCall> casters = frame.ShadowCasters(cascade);
                for (int i = 0; i < casters.Count; i++) GetMesh(commands, casters[i].Mesh);
            }

            foreach (CanvasQuad quad in frame.CanvasQuads)
            {
                if (quad.Texture != null) GetTexture(commands, quad.Texture);
            }

            if (frame.Target != null) GetRenderTarget(frame.Target, commands);

            // New meshes' data must land before anything reads it as vertices.
            if (_uploadedThisView) _context.MemoryBarrier(commands, Stage.TransferWrite, Stage.VertexInput);
        }

        private VkMesh GetMesh(CommandBuffer commands, Mesh mesh)
        {
            if (_meshes.TryGetValue(mesh, out VkMesh? existing)) return existing;

            ulong vertexBytes = (ulong)(mesh.Vertices.Length * sizeof(MeshVertex));
            ulong indexOffset = (vertexBytes + 3) / 4 * 4;
            ulong size = indexOffset + (ulong)(mesh.Indices.Length * sizeof(uint));

            VkBufferResource staging = _context.CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            mesh.Vertices.AsSpan().CopyTo(new Span<MeshVertex>(staging.Mapped, mesh.Vertices.Length));
            mesh.Indices.AsSpan().CopyTo(new Span<uint>((byte*)staging.Mapped + indexOffset, mesh.Indices.Length));

            VkBufferResource buffer = _context.CreateBuffer(size,
                BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.DeviceLocalBit);
            BufferCopy copy = new(0, 0, size);
            _context.Vk.CmdCopyBuffer(commands, staging.Buffer, buffer.Buffer, 1, &copy);
            _frame.DeleteLater(() => staging.Destroy(_context));
            _uploadedThisView = true;

            var created = new VkMesh(buffer, indexOffset, (uint)mesh.Indices.Length);
            _meshes.Add(mesh, created);
            mesh.Disposed += OnMeshDisposed;
            return created;
        }

        /// <summary>The image to sample for <paramref name="texture"/>, uploading it (again, if it changed) when there are <paramref name="commands"/> to do it with.</summary>
        private VkImageResource GetTexture(CommandBuffer commands, Texture texture)
        {
            if (texture is RenderTexture renderTexture) return GetRenderTarget(renderTexture, commands).Color;

            if (!_textures.TryGetValue(texture, out VkTexture? existing))
            {
                uint mips = (uint)Math.Floor(Math.Log2(Math.Max(texture.Width, texture.Height))) + 1;
                VkImageResource image = _context.CreateImage(texture.Width, texture.Height, Format.R8G8B8A8Unorm,
                    ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips);
                Upload(commands, image, texture.Pixels, _frame.DeleteLater);

                existing = new VkTexture(image, texture.Version);
                _textures.Add(texture, existing);
                texture.Disposed += OnTextureDisposed;
            }
            else if (existing.Version != texture.Version)
            {
                // Changed since it was uploaded (a font atlas that gained new letters, say).
                Upload(commands, existing.Image, texture.Pixels, _frame.DeleteLater);
                existing.Version = texture.Version;
            }

            return existing.Image;
        }

        /// <summary>The image already uploaded for <paramref name="texture"/>, for use while drawing (see <see cref="Prepare"/>).</summary>
        private VkImageResource TextureImage(Texture? texture)
        {
            if (texture == null) return _white.Image;
            if (texture is RenderTexture renderTexture) return _renderTargets.TryGetValue(renderTexture, out VkRenderTarget? target) ? target.Color : _white.Image;
            return _textures.TryGetValue(texture, out VkTexture? existing) ? existing.Image : _white.Image;
        }

        private VkRenderTarget GetRenderTarget(RenderTexture texture, CommandBuffer commands = default)
        {
            if (_renderTargets.TryGetValue(texture, out VkRenderTarget? target) && target.Version == texture.Version) return target;
            if (commands.Handle == 0) throw new InvalidOperationException("Render targets are made in Prepare.");

            VkImageResource color = CreateRenderTargetImage(commands, texture);
            if (target == null)
            {
                target = new VkRenderTarget(color, new VkViewTargets(_context), texture.Version);
                _renderTargets.Add(texture, target);
                texture.Disposed += OnTextureDisposed;
            }
            else
            {
                // Resized: the old picture goes once the GPU is done with it.
                VkImageResource old = target.Color;
                _frame.DeleteLater(() => old.Destroy(_context));
                target.Color = color;
                target.Version = texture.Version;
            }

            return target;
        }

        // Black until a camera first draws into it, rather than whatever the GPU's memory held.
        private VkImageResource CreateRenderTargetImage(CommandBuffer commands, RenderTexture texture)
        {
            VkImageResource color = _context.CreateImage(texture.Width, texture.Height, VkViewTargets.OutputFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);

            _context.Transition(commands, color, ImageLayout.TransferDstOptimal, Stage.None, Stage.TransferWrite, discard: true);
            ClearColorValue black = new(0f, 0f, 0f, 1f);
            ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _context.Vk.CmdClearColorImage(commands, color.Image, ImageLayout.TransferDstOptimal, &black, 1, &range);
            _context.Transition(commands, color, ImageLayout.ShaderReadOnlyOptimal, Stage.TransferWrite, Stage.FragmentRead);
            return color;
        }

        /// <summary>Copies <paramref name="pixels"/> into mip level 0, then makes the smaller levels from it.</summary>
        private void Upload(CommandBuffer commands, VkImageResource image, byte[] pixels, Action<Action>? deleteLater)
        {
            VkBufferResource staging = _context.CreateBuffer((ulong)pixels.Length, BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            pixels.AsSpan().CopyTo(new Span<byte>(staging.Mapped, pixels.Length));

            // Anything still reading the old pixels finishes first; they're replaced entirely, so they needn't be kept.
            _context.Transition(commands, image, ImageLayout.TransferDstOptimal, Stage.FragmentRead, Stage.TransferWrite, discard: true);

            BufferImageCopy copy = new()
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)image.Width, (uint)image.Height, 1)
            };
            _context.Vk.CmdCopyBufferToImage(commands, staging.Buffer, image.Image, ImageLayout.TransferDstOptimal, 1, &copy);

            int width = image.Width, height = image.Height;
            for (uint level = 1; level < image.MipLevels; level++)
            {
                _context.TransitionMips(commands, image, level - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                    Stage.TransferWrite, Stage.TransferRead);

                int nextWidth = Math.Max(width / 2, 1), nextHeight = Math.Max(height / 2, 1);
                ImageBlit blit = new()
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1)
                };
                blit.SrcOffsets[1] = new Offset3D(width, height, 1);
                blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);
                _context.Vk.CmdBlitImage(commands, image.Image, ImageLayout.TransferSrcOptimal, image.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);

                width = nextWidth;
                height = nextHeight;
            }

            // Every level but the last was read from; the last was only written.
            if (image.MipLevels > 1)
            {
                _context.TransitionMips(commands, image, 0, image.MipLevels - 1, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                    Stage.TransferRead, Stage.FragmentRead);
            }

            _context.TransitionMips(commands, image, image.MipLevels - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                Stage.TransferWrite, Stage.FragmentRead);
            image.Layout = ImageLayout.ShaderReadOnlyOptimal;

            if (deleteLater != null) deleteLater(() => staging.Destroy(_context));
            else _frame.DeleteLater(() => staging.Destroy(_context));
        }

        /// <summary>The sun's view of everything that casts shadows, into each cascade in turn (see <see cref="ShadowCascades"/>).</summary>
        private void DrawShadows(CommandBuffer commands, RenderFrame frame)
        {
            // Made again at a new size if the resolution's been changed; the old ones go once the GPU's done with them.
            int size = frame.Cascades.Resolution;
            if (_shadowMaps.Size != size)
            {
                VkShadowMaps old = _shadowMaps;
                _frame.DeleteLater(() => old.Destroy(_context));
                _shadowMaps = new VkShadowMaps(_context, size);
            }

            // Whatever read the shadow maps last (an earlier view, or last frame) finishes before they're drawn again.
            _context.Transition(commands, _shadowMaps.Array, ImageLayout.DepthAttachmentOptimal, Stage.FragmentRead, Stage.DepthOutput, discard: true);

            for (int cascade = 0; cascade < ShadowCascades.Count; cascade++)
            {
                IReadOnlyList<DrawCall> draws = frame.ShadowCasters(cascade);
                VkPipelines.BeginRendering(_context, commands, size, size, [], _shadowMaps.Layers[cascade]);
                VkPipelines.SetViewport(_context, commands, size, size, flipped: true);
                _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, _shadow);

                Matrix4x4 lightSpace = frame.Cascades.Matrices[cascade];
                Mesh? currentMesh = null;
                for (int i = 0; i < draws.Count; i++)
                {
                    DrawCall draw = draws[i];
                    Matrix4x4 lightModel = draw.WorldMatrix * lightSpace;
                    _context.Vk.CmdPushConstants(commands, _shadowLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &lightModel);

                    VkMesh mesh = _meshes.TryGetValue(draw.Mesh, out VkMesh? found) ? found : throw new InvalidOperationException("Mesh wasn't prepared.");
                    if (draw.Mesh != currentMesh)
                    {
                        BindMesh(commands, mesh);
                        currentMesh = draw.Mesh;
                    }

                    _context.Vk.CmdDrawIndexed(commands, mesh.IndexCount, 1, 0, 0, 0);
                }

                _context.Vk.CmdEndRendering(commands);
            }

            _context.Transition(commands, _shadowMaps.Array, ImageLayout.ShaderReadOnlyOptimal, Stage.DepthOutput, Stage.FragmentRead);
        }

        private void BindMesh(CommandBuffer commands, VkMesh mesh)
        {
            VkBuffer buffer = mesh.Buffer.Buffer;
            ulong offset = 0;
            _context.Vk.CmdBindVertexBuffers(commands, 0, 1, &buffer, &offset);
            _context.Vk.CmdBindIndexBuffer(commands, buffer, mesh.IndexOffset, IndexType.Uint32);
        }

        private DescriptorSet WriteFrameData(in FrameUniforms data)
        {
            (VkBuffer buffer, ulong offset) = _frame.Uploads.Write(in data);
            DescriptorSet set = _frame.Descriptors.Allocate(_frameSetLayout);
            VkDescriptors.WriteBuffer(_context, set, 0, DescriptorType.UniformBuffer, buffer, offset, (ulong)sizeof(FrameUniforms));
            VkDescriptors.WriteImage(_context, set, 1, _shadowMaps.Array.View, _shadowSampler);
            VkDescriptors.WriteImage(_context, set, 2, TextureImage(CloudNoise.Texture).View, _repeatSampler);
            return set;
        }

        private DescriptorSet MaterialSet(Material material)
        {
            if (_materialSets.TryGetValue(material, out DescriptorSet set)) return set;

            MaterialUniforms data = MaterialUniforms.Create(material);
            (VkBuffer buffer, ulong offset) = _frame.Uploads.Write(in data);
            set = _frame.Descriptors.Allocate(_materialSetLayout);
            VkDescriptors.WriteBuffer(_context, set, 0, DescriptorType.UniformBuffer, buffer, offset, (ulong)sizeof(MaterialUniforms));
            VkDescriptors.WriteImage(_context, set, 1, TextureImage(material.Texture).View, _repeatSampler);
            VkDescriptors.WriteImage(_context, set, 2, TextureImage(material.NormalMap).View, _repeatSampler);
            _materialSets.Add(material, set);
            return set;
        }

        private void DrawScene(CommandBuffer commands, RenderFrame frame, VkViewTargets views, DescriptorSet frameSet, in FrameUniforms uniforms)
        {
            // Whatever read these last (the previous view's post passes) finishes first; what was in them doesn't matter.
            _context.Transition(commands, views.Color!, ImageLayout.ColorAttachmentOptimal, Stage.FragmentRead, Stage.ColorOutput, discard: true);
            _context.Transition(commands, views.Ambient!, ImageLayout.ColorAttachmentOptimal, Stage.FragmentRead, Stage.ColorOutput, discard: true);
            _context.Transition(commands, views.Glow!, ImageLayout.ColorAttachmentOptimal, Stage.FragmentRead, Stage.ColorOutput, discard: true);
            _context.Transition(commands, views.Depth!, ImageLayout.DepthAttachmentOptimal, Stage.FragmentRead, Stage.DepthOutput, discard: true);

            Vector4 clear = uniforms.ClearColor;
            VkPipelines.BeginRendering(_context, commands, frame.Width, frame.Height,
                [(views.Color!, new ClearColorValue(clear.X, clear.Y, clear.Z, 1f)), (views.Ambient!, new ClearColorValue(0f, 0f, 0f, 0f)), (views.Glow!, new ClearColorValue(0f, 0f, 0f, 0f))],
                views.Depth);
            VkPipelines.SetViewport(_context, commands, frame.Width, frame.Height, flipped: true);
            _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _sceneLayout, 0, 1, &frameSet, 0, null);

            // The draws come grouped by shader, then material, then mesh (see Renderer), so each is only set up when it changes.
            Pipeline currentPipeline = default;
            Material? currentMaterial = null;
            Mesh? currentMesh = null;
            VkMesh? mesh = null;
            IReadOnlyList<DrawCall> draws = frame.DrawCalls;
            for (int i = 0; i < draws.Count; i++)
            {
                DrawCall draw = draws[i];
                Material material = draw.Material;
                Pipeline pipeline = material.Shading == ShadingModel.Unlit ? _unlit : _lit;

                if (pipeline.Handle != currentPipeline.Handle)
                {
                    _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, pipeline);
                    currentPipeline = pipeline;
                }

                if (material != currentMaterial)
                {
                    DescriptorSet materialSet = MaterialSet(material);
                    _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _sceneLayout, 1, 1, &materialSet, 0, null);
                    currentMaterial = material;
                }

                if (draw.Mesh != currentMesh)
                {
                    mesh = _meshes.TryGetValue(draw.Mesh, out VkMesh? found) ? found : throw new InvalidOperationException("Mesh wasn't prepared.");
                    BindMesh(commands, mesh);
                    currentMesh = draw.Mesh;
                }

                Matrix4x4.Invert(draw.WorldMatrix, out Matrix4x4 inverseModel);
                var constants = new DrawConstants { Model = draw.WorldMatrix, NormalMatrix = Matrix4x4.Transpose(inverseModel) };
                _context.Vk.CmdPushConstants(commands, _sceneLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(DrawConstants), &constants);
                _context.Vk.CmdDrawIndexed(commands, mesh!.IndexCount, 1, 0, 0, 0);
            }

            // After the meshes, so it only fills what they left (see the sky pipeline).
            if (frame.Environment.DrawSky)
            {
                _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, _sky);
                _context.Vk.CmdDraw(commands, 3, 1, 0, 0);
            }

            DrawCpuParticles(commands, frame);

            if (_gpuParticlePool.DrawCount > 0)
            {
                DescriptorSet particleSet = _gpuParticlePool.DrawSet;
                _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, _gpuParticles);
                _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _gpuParticleLayout, 0, 1, &frameSet, 0, null);
                _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _gpuParticleLayout, 1, 1, &particleSet, 0, null);
                _context.Vk.CmdDraw(commands, (uint)_gpuParticlePool.DrawCount * 6, 1, 0, 0);
            }

            _context.Vk.CmdEndRendering(commands);

            _context.Transition(commands, views.Color!, ImageLayout.ShaderReadOnlyOptimal, Stage.ColorOutput, Stage.FragmentRead);
            _context.Transition(commands, views.Ambient!, ImageLayout.ShaderReadOnlyOptimal, Stage.ColorOutput, Stage.FragmentRead);
            _context.Transition(commands, views.Glow!, ImageLayout.ShaderReadOnlyOptimal, Stage.ColorOutput, Stage.FragmentRead);
            _context.Transition(commands, views.Depth!, ImageLayout.ShaderReadOnlyOptimal, Stage.DepthOutput, Stage.FragmentRead);
        }

        /// <summary>CPU-simulated particles: camera-facing quads built here, sent in one batch.</summary>
        private void DrawCpuParticles(CommandBuffer commands, RenderFrame frame)
        {
            IReadOnlyList<ParticleData> particles = frame.Particles;
            if (particles.Count == 0) return;

            int needed = particles.Count * 6 * _particleFloats;
            if (needed > _particleVertices.Length) Array.Resize(ref _particleVertices, Math.Max(needed, _particleVertices.Length * 2));

            // The camera's right and up, from the view matrix's columns.
            Matrix4x4 view = frame.ViewMatrix;
            var cameraRight = new Vector3(view.M11, view.M21, view.M31);
            var cameraUp = new Vector3(view.M12, view.M22, view.M32);

            int offset = 0;
            foreach (ParticleData particle in particles)
            {
                Vector3 right = cameraRight;
                Vector3 up = cameraUp;
                float halfWidth = particle.Size * 0.5f;
                float halfLength = halfWidth;

                float stretch = particle.Velocity.Length();
                if (stretch > 1e-4f)
                {
                    Vector3 along = particle.Velocity / stretch;
                    Vector3 side = Vector3.Cross(along, frame.CameraPosition - particle.Position);
                    if (side.LengthSquared() > 1e-8f)
                    {
                        up = along;
                        right = Vector3.Normalize(side);
                        halfLength += stretch * 0.5f;
                    }
                }

                Vector3 x = right * halfWidth;
                Vector3 y = up * halfLength;
                Vector3 p = particle.Position;

                AddParticleVertex(ref offset, p - x - y, -1f, -1f, particle);
                AddParticleVertex(ref offset, p + x - y, 1f, -1f, particle);
                AddParticleVertex(ref offset, p + x + y, 1f, 1f, particle);
                AddParticleVertex(ref offset, p - x - y, -1f, -1f, particle);
                AddParticleVertex(ref offset, p + x + y, 1f, 1f, particle);
                AddParticleVertex(ref offset, p - x + y, -1f, 1f, particle);
            }

            (VkBuffer buffer, ulong bufferOffset) = _frame.Uploads.Write<float>(_particleVertices.AsSpan(0, offset));
            _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, _particles);
            _context.Vk.CmdBindVertexBuffers(commands, 0, 1, &buffer, &bufferOffset);
            _context.Vk.CmdDraw(commands, (uint)(offset / _particleFloats), 1, 0, 0);
        }

        private void AddParticleVertex(ref int offset, Vector3 position, float cornerX, float cornerY, in ParticleData particle)
        {
            Span<float> v = _particleVertices.AsSpan(offset, _particleFloats);
            v[0] = position.X;
            v[1] = position.Y;
            v[2] = position.Z;
            v[3] = cornerX;
            v[4] = cornerY;
            v[5] = particle.Color.R;
            v[6] = particle.Color.G;
            v[7] = particle.Color.B;
            v[8] = particle.Color.A;
            v[9] = particle.Glow;
            v[10] = particle.Additive ? 1f : 0f;
            offset += _particleFloats;
        }

        /// <summary>Ambient occlusion from the depth, then blurred across and down (see <see cref="ShaderLibrary.OcclusionFragment"/>).</summary>
        private void Occlusion(CommandBuffer commands, VkViewTargets views, PostUniforms post)
        {
            RunPost(commands, _occlusion, views.Occlusion!, post, _white.Image, depth: views.Depth);
            post.TexelSize = new Vector2(1f / views.Occlusion!.Width, 1f / views.Occlusion.Height);
            post.Direction = Vector2.UnitX;
            RunPost(commands, _occlusionBlur, views.OcclusionBlur!, post, views.Occlusion!);
            post.Direction = Vector2.UnitY;
            RunPost(commands, _occlusionBlur, views.Occlusion!, post, views.OcclusionBlur!);
        }

        /// <summary>
        /// Blurs what's brighter than white: halved again and again (the first step keeps only the bright parts), then
        /// built back up, each size adding onto the next. False if there's no bloom this view.
        /// </summary>
        private bool Bloom(CommandBuffer commands, RenderFrame frame, VkViewTargets views, PostUniforms post)
        {
            if (frame.Bloom <= 0f || views.BloomLevels.Count == 0) return false;

            VkImageResource source = views.Color!;
            post.Threshold = PostUniforms.BloomThreshold;
            foreach (VkImageResource level in views.BloomLevels)
            {
                post.TexelSize = new Vector2(1f / source.Width, 1f / source.Height);
                RunPost(commands, _bloomDown, level, post, source, bloom: views.Glow);
                source = level;
                post.Threshold = 0f;
            }

            for (int i = views.BloomLevels.Count - 1; i > 0; i--)
            {
                VkImageResource smaller = views.BloomLevels[i];
                post.TexelSize = new Vector2(1f / smaller.Width, 1f / smaller.Height);

                // Added onto what the way down left there, so it's kept.
                RunPost(commands, _bloomUp, views.BloomLevels[i - 1], post, smaller, keep: true);
            }

            return true;
        }

        /// <summary>One post pass into <paramref name="target"/>, which is then ready to be read by the next.</summary>
        private void RunPost(CommandBuffer commands, Pipeline pipeline, VkImageResource target, in PostUniforms post, VkImageResource source,
            VkImageResource? bloom = null, VkImageResource? depth = null, VkImageResource? occlusion = null, VkImageResource? ambient = null,
            bool keep = false)
        {
            _context.Transition(commands, target, ImageLayout.ColorAttachmentOptimal, Stage.FragmentRead, Stage.ColorOutput, discard: !keep);
            VkPipelines.BeginRendering(_context, commands, target.Width, target.Height, [(target, (ClearColorValue?)null)]);
            PostDraw(commands, pipeline, target.Width, target.Height, post, source, bloom, depth, occlusion, ambient);
            _context.Vk.CmdEndRendering(commands);
            _context.Transition(commands, target, ImageLayout.ShaderReadOnlyOptimal, Stage.ColorOutput, Stage.FragmentRead);
        }

        /// <summary>Draws a post pass into whatever rendering has begun. Images it doesn't need are left as white.</summary>
        private void PostDraw(CommandBuffer commands, Pipeline pipeline, int width, int height, PostUniforms post, VkImageResource source,
            VkImageResource? bloom = null, VkImageResource? depth = null, VkImageResource? occlusion = null, VkImageResource? ambient = null)
        {
            DescriptorSet set = _frame.Descriptors.Allocate(_postSetLayout);
            VkDescriptors.WriteImage(_context, set, 0, source.View, _clampSampler);
            VkDescriptors.WriteImage(_context, set, 1, (bloom ?? _white.Image).View, _clampSampler);
            VkDescriptors.WriteImage(_context, set, 2, (depth ?? _white.Image).View, _clampSampler);
            VkDescriptors.WriteImage(_context, set, 3, (occlusion ?? _white.Image).View, _clampSampler);
            VkDescriptors.WriteImage(_context, set, 4, (ambient ?? _white.Image).View, _clampSampler);

            _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, pipeline);
            VkPipelines.SetViewport(_context, commands, width, height, flipped: false);
            _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _postLayout, 0, 1, &set, 0, null);
            _context.Vk.CmdPushConstants(commands, _postLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PostUniforms), &post);
            _context.Vk.CmdDraw(commands, 3, 1, 0, 0);
        }

        /// <summary>The 2D layer, in order, batched while quads share a texture.</summary>
        private void DrawCanvas(CommandBuffer commands, RenderFrame frame, int outputWidth, int outputHeight)
        {
            IReadOnlyList<CanvasQuad> quads = frame.CanvasQuads;
            if (quads.Count == 0) return;

            int needed = quads.Count * 6 * 8;
            if (needed > _canvasVertices.Length) Array.Resize(ref _canvasVertices, Math.Max(needed, _canvasVertices.Length * 2));

            _canvasBatches.Clear();
            int vertex = 0;
            foreach (CanvasQuad quad in quads)
            {
                Texture texture = quad.Texture ?? _whiteKey;
                if (_canvasBatches.Count == 0 || _canvasBatches[^1].Texture != texture) _canvasBatches.Add((texture, vertex, 0));

                // Two triangles per quad.
                AddCanvasVertex(ref vertex, quad.TopLeft);
                AddCanvasVertex(ref vertex, quad.TopRight);
                AddCanvasVertex(ref vertex, quad.BottomRight);
                AddCanvasVertex(ref vertex, quad.TopLeft);
                AddCanvasVertex(ref vertex, quad.BottomRight);
                AddCanvasVertex(ref vertex, quad.BottomLeft);

                (Texture batchTexture, int first, int count) = _canvasBatches[^1];
                _canvasBatches[^1] = (batchTexture, first, count + 6);
            }

            (VkBuffer buffer, ulong offset) = _frame.Uploads.Write<float>(_canvasVertices.AsSpan(0, vertex * 8));
            _context.Vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, _canvas);
            _context.Vk.CmdBindVertexBuffers(commands, 0, 1, &buffer, &offset);
            VkPipelines.SetViewport(_context, commands, outputWidth, outputHeight, flipped: true);

            var screen = new Vector2(frame.Width, frame.Height);
            _context.Vk.CmdPushConstants(commands, _canvasLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Vector2), &screen);

            foreach ((Texture texture, int first, int count) in _canvasBatches)
            {
                DescriptorSet set = TextureSet(texture);
                _context.Vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, _canvasLayout, 0, 1, &set, 0, null);
                _context.Vk.CmdDraw(commands, (uint)count, 1, (uint)first, 0);
            }
        }

        // Stands for "no texture" in the canvas's batches; drawn with the white image.
        private static readonly Texture _whiteKey = new(1, 1, [255, 255, 255, 255]);

        private DescriptorSet TextureSet(Texture texture)
        {
            if (_textureSets.TryGetValue(texture, out DescriptorSet set)) return set;

            set = _frame.Descriptors.Allocate(_textureSetLayout);
            VkImageResource image = texture == _whiteKey ? _white.Image : TextureImage(texture);
            VkDescriptors.WriteImage(_context, set, 0, image.View, _repeatSampler);
            _textureSets.Add(texture, set);
            return set;
        }

        private void AddCanvasVertex(ref int vertex, in CanvasVertex corner)
        {
            Span<float> v = _canvasVertices.AsSpan(vertex * 8, 8);
            v[0] = corner.Position.X;
            v[1] = corner.Position.Y;
            v[2] = corner.TexCoord.X;
            v[3] = corner.TexCoord.Y;
            v[4] = corner.Color.X;
            v[5] = corner.Color.Y;
            v[6] = corner.Color.Z;
            v[7] = corner.Color.W;
            vertex++;
        }

        private void OnMeshDisposed(Mesh mesh)
        {
            if (!_meshes.TryGetValue(mesh, out VkMesh? gpu)) return;

            _meshes.Remove(mesh);
            _frame.DeleteLater(() => gpu.Buffer.Destroy(_context));
        }

        private void OnTextureDisposed(Texture texture)
        {
            if (_textures.TryGetValue(texture, out VkTexture? gpu))
            {
                _textures.Remove(texture);
                _frame.DeleteLater(() => gpu.Image.Destroy(_context));
            }

            if (texture is RenderTexture renderTexture && _renderTargets.TryGetValue(renderTexture, out VkRenderTarget? target))
            {
                _renderTargets.Remove(renderTexture);
                target.Views.Release(_frame.DeleteLater);
                _frame.DeleteLater(() => target.Color.Destroy(_context));
            }
        }

        public void Dispose()
        {
            if (_context == null) return;

            _context.Vk.DeviceWaitIdle(_context.Device);

            foreach (VkFrame frame in _frames) frame?.Dispose();

            foreach (KeyValuePair<Mesh, VkMesh> entry in _meshes) entry.Value.Buffer.Destroy(_context);
            foreach (KeyValuePair<Texture, VkTexture> entry in _textures) entry.Value.Image.Destroy(_context);
            foreach (KeyValuePair<RenderTexture, VkRenderTarget> entry in _renderTargets)
            {
                entry.Value.Views.Release(delete => delete());
                entry.Value.Color.Destroy(_context);
            }
            _meshes.Clear();
            _textures.Clear();
            _renderTargets.Clear();

            _screenViews.Release(delete => delete());
            _shadowMaps.Destroy(_context);
            _white.Image.Destroy(_context);
            _gpuParticlePool.Dispose();

            Pipeline[] pipelines =
            [
                _lit, _unlit, _sky, _shadow, _particles, _gpuParticles, _canvas, _bloomDown, _bloomUp, _occlusion, _occlusionBlur,
                _compositeToScreen, _compositeToTexture, _antiAliasToScreen, _antiAliasToTexture
            ];
            foreach (Pipeline pipeline in pipelines) _context.Vk.DestroyPipeline(_context.Device, pipeline, null);
            foreach (ShaderModule shader in _shaders) _context.Vk.DestroyShaderModule(_context.Device, shader, null);
            foreach (PipelineLayout layout in new[] { _sceneLayout, _shadowLayout, _gpuParticleLayout, _canvasLayout, _postLayout })
                _context.Vk.DestroyPipelineLayout(_context.Device, layout, null);
            foreach (DescriptorSetLayout layout in new[] { _frameSetLayout, _materialSetLayout, _textureSetLayout, _postSetLayout })
                _context.Vk.DestroyDescriptorSetLayout(_context.Device, layout, null);
            foreach (Sampler sampler in new[] { _repeatSampler, _clampSampler, _shadowSampler })
                _context.Vk.DestroySampler(_context.Device, sampler, null);

            _swapchain.Dispose();
            _compiler.Dispose();
            _context.Dispose();
        }
    }
}
