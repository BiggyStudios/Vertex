using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// The images a view is drawn through before it's finished, each with a framebuffer to draw into: the scene in
    /// linear light (half floats, so bright things stay bright) with the part of it that's soft light from all around and what glows,
    /// and depth; ambient occlusion and a second image to blur it through; bloom (the bright parts halved again and
    /// again); and the finished picture before anti-aliasing. One set for the screen and one for each render texture, as
    /// they're sized to match. Like the Vulkan backend's VkViewTargets.
    /// </summary>
    internal sealed unsafe class GLViewTargets : IDisposable
    {
        // Each one is half the size of the one before, so the last is 1/64 of the view and the glow spreads wide.
        private const int _maxLevels = 6;

        private readonly GL _gl;
        private readonly List<uint> _textures = new();
        private readonly List<uint> _framebuffers = new();

        public uint SceneFramebuffer { get; private set; }
        public uint Color { get; private set; }
        public uint Ambient { get; private set; }
        public uint Glow { get; private set; }
        public uint Depth { get; private set; }

        public uint OcclusionFramebuffer { get; private set; }
        public uint Occlusion { get; private set; }
        public uint OcclusionBlurFramebuffer { get; private set; }
        public uint OcclusionBlur { get; private set; }
        public int OcclusionWidth { get; private set; }
        public int OcclusionHeight { get; private set; }

        public uint FinishedFramebuffer { get; private set; }
        public uint Finished { get; private set; }

        public List<(uint Framebuffer, uint Texture, int Width, int Height)> BloomLevels { get; } = new();

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>
        /// The volumetric fog's images, made the first time the view has fog: its lit fog, twice (this frame's and last
        /// frame's, taking turns), and the fog added up from the camera. Each is a layer per slice, with a framebuffer
        /// for each pass's worth of slices.
        /// </summary>
        public uint[] FogLight { get; } = new uint[2];
        public uint FogTotal { get; private set; }
        public uint[][] FogLightFramebuffers { get; } = new uint[2][];
        public uint[] FogTotalFramebuffers { get; private set; } = [];
        public int FogWidth { get; private set; }
        public int FogHeight { get; private set; }
        public Shaders.VolumetricFog Fog { get; } = new();

        private readonly List<uint> _fogTextures = new();
        private readonly List<uint> _fogFramebuffers = new();

        public GLViewTargets(GL gl)
        {
            _gl = gl;
        }

        /// <summary>Makes the images match the view. Cheap when the size hasn't changed.</summary>
        public void Ensure(int width, int height)
        {
            if (width == Width && height == Height && SceneFramebuffer != 0) return;

            Delete();
            Width = width;
            Height = height;
            if (width <= 0 || height <= 0) return;

            Color = CreateTexture(width, height, InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
            // Light that's never negative and needs no alpha: packed floats, half the size of 16-bit ones.
            Ambient = CreateTexture(width, height, InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float);
            Glow = CreateTexture(width, height, InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float);

            // A texture rather than a renderbuffer, so ambient occlusion and the outlines can read it.
            Depth = CreateTexture(width, height, InternalFormat.DepthComponent24, PixelFormat.DepthComponent, PixelType.UnsignedInt, nearest: true);

            SceneFramebuffer = CreateFramebuffer("Scene", Color, depth: Depth, more: [Ambient, Glow]);

            // Ambient occlusion at half size (it's soft, and blurred anyway): how open each point is, and how far away.
            OcclusionWidth = Math.Max((width + 1) / 2, 1);
            OcclusionHeight = Math.Max((height + 1) / 2, 1);
            Occlusion = CreateTexture(OcclusionWidth, OcclusionHeight, InternalFormat.RG16f, PixelFormat.RG, PixelType.Float);
            OcclusionFramebuffer = CreateFramebuffer("Occlusion", Occlusion);
            OcclusionBlur = CreateTexture(OcclusionWidth, OcclusionHeight, InternalFormat.RG16f, PixelFormat.RG, PixelType.Float);
            OcclusionBlurFramebuffer = CreateFramebuffer("Occlusion blur", OcclusionBlur);

            Finished = CreateTexture(width, height, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
            FinishedFramebuffer = CreateFramebuffer("Finished", Finished);

            int levelWidth = width, levelHeight = height;
            for (int i = 0; i < _maxLevels; i++)
            {
                levelWidth = Math.Max(levelWidth / 2, 1);
                levelHeight = Math.Max(levelHeight / 2, 1);

                uint texture = CreateTexture(levelWidth, levelHeight, InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float);
                BloomLevels.Add((CreateFramebuffer("Bloom", texture), texture, levelWidth, levelHeight));

                if (levelWidth <= 2 || levelHeight <= 2) break;
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>Makes the fog's images <paramref name="width"/> by <paramref name="height"/> cells. True if they were just made.</summary>
        public bool EnsureFog(int width, int height)
        {
            if (width == FogWidth && height == FogHeight && FogTotal != 0) return false;

            DeleteFog();
            FogWidth = width;
            FogHeight = height;
            for (int i = 0; i < 2; i++)
            {
                FogLight[i] = CreateFogTexture(width, height);
                FogLightFramebuffers[i] = CreateFogFramebuffers(FogLight[i]);
            }

            FogTotal = CreateFogTexture(width, height);
            FogTotalFramebuffers = CreateFogFramebuffers(FogTotal);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return true;
        }

        private uint CreateFogTexture(int width, int height)
        {
            uint texture = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2DArray, texture);
            _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba16f, (uint)width, (uint)height, Shaders.ShaderLibrary.FogSlices, 0,
                PixelFormat.Rgba, PixelType.Float, null);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            _fogTextures.Add(texture);
            return texture;
        }

        // One framebuffer per pass, drawing into that pass's slices, one layer for each output.
        private uint[] CreateFogFramebuffers(uint texture)
        {
            const int perPass = Shaders.ShaderLibrary.FogSlicesPerPass;
            var framebuffers = new uint[Shaders.ShaderLibrary.FogSlices / perPass];
            GLEnum* buffers = stackalloc GLEnum[perPass];
            for (int pass = 0; pass < framebuffers.Length; pass++)
            {
                uint framebuffer = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                for (int i = 0; i < perPass; i++)
                {
                    _gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, texture, 0, pass * perPass + i);
                    buffers[i] = GLEnum.ColorAttachment0 + i;
                }
                _gl.DrawBuffers((uint)perPass, buffers);

                GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Fog framebuffer is incomplete: {status}");

                framebuffers[pass] = framebuffer;
                _fogFramebuffers.Add(framebuffer);
            }

            return framebuffers;
        }

        private void DeleteFog()
        {
            foreach (uint framebuffer in _fogFramebuffers) _gl.DeleteFramebuffer(framebuffer);
            foreach (uint texture in _fogTextures) _gl.DeleteTexture(texture);
            _fogFramebuffers.Clear();
            _fogTextures.Clear();
            FogTotal = 0;
            FogWidth = FogHeight = 0;
        }

        private uint CreateTexture(int width, int height, InternalFormat format, PixelFormat pixelFormat, PixelType type, bool nearest = false)
        {
            uint texture = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0, pixelFormat, type, null);
            int filter = (int)(nearest ? TextureMinFilter.Nearest : TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _textures.Add(texture);
            return texture;
        }

        /// <summary>A framebuffer drawing into <paramref name="color"/> (and <paramref name="more"/> colour images, and depth, if given).</summary>
        private uint CreateFramebuffer(string name, uint color, uint depth = 0, uint[]? more = null)
        {
            uint framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            if (more != null)
            {
                GLEnum* buffers = stackalloc GLEnum[more.Length + 1];
                buffers[0] = GLEnum.ColorAttachment0;
                for (int i = 0; i < more.Length; i++)
                {
                    _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1 + i, TextureTarget.Texture2D, more[i], 0);
                    buffers[i + 1] = GLEnum.ColorAttachment1 + i;
                }
                _gl.DrawBuffers((uint)more.Length + 1, buffers);
            }
            if (depth != 0) _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"{name} framebuffer is incomplete: {status}");

            _framebuffers.Add(framebuffer);
            return framebuffer;
        }

        private void Delete()
        {
            foreach (uint framebuffer in _framebuffers) _gl.DeleteFramebuffer(framebuffer);
            foreach (uint texture in _textures) _gl.DeleteTexture(texture);
            _framebuffers.Clear();
            _textures.Clear();
            BloomLevels.Clear();
            SceneFramebuffer = 0;
            Width = Height = 0;
        }

        public void Dispose()
        {
            Delete();
            DeleteFog();
        }
    }
}
