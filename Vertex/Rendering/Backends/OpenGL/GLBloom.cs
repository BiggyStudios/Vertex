using System.Numerics;

using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// The scene is drawn into here rather than straight to the screen: its colour, plus a second image of only what
    /// glows. That glow is blurred wider and wider by halving it several times, then built back up, each size adding
    /// onto the next. The wide, soft result goes over the scene on its way to the screen.
    /// </summary>
    internal sealed unsafe class GLBloom : IDisposable
    {
        // Each one is half the size of the one before, so the last is 1/64 of the screen and the glow spreads wide.
        private const int _maxLevels = 6;

        private readonly GL _gl;
        private readonly GLShader _downsample;
        private readonly GLShader _upsample;
        private readonly GLShader _composite;
        private readonly uint _emptyVertexArray;

        private uint _sceneFramebuffer;
        private uint _sceneColor;
        private uint _sceneGlow;
        private uint _sceneDepth;

        private readonly List<(uint Framebuffer, uint Texture, int Width, int Height)> _levels = new();
        private int _width;
        private int _height;

        public GLBloom(GL gl, uint emptyVertexArray)
        {
            _gl = gl;
            _emptyVertexArray = emptyVertexArray;
            _downsample = new GLShader(gl, GLShaderSources.FullscreenVertex, GLShaderSources.BloomDownsampleFragment);
            _upsample = new GLShader(gl, GLShaderSources.FullscreenVertex, GLShaderSources.BloomUpsampleFragment);
            _composite = new GLShader(gl, GLShaderSources.FullscreenVertex, GLShaderSources.CompositeFragment);
        }

        /// <summary>Makes the images match the screen. Cheap when the size hasn't changed.</summary>
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;

            DeleteTargets();
            _width = width;
            _height = height;
            if (width <= 0 || height <= 0) return;

            _sceneColor = CreateTexture(width, height, InternalFormat.Rgba8, PixelType.UnsignedByte);
            _sceneGlow = CreateTexture(width, height, InternalFormat.Rgba16f, PixelType.Float);

            _sceneDepth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepth);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);

            _sceneFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _sceneColor, 0);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _sceneGlow, 0);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _sceneDepth);
            GLEnum* buffers = stackalloc GLEnum[2] { GLEnum.ColorAttachment0, GLEnum.ColorAttachment1 };
            _gl.DrawBuffers(2, buffers);
            Check("Scene");

            int levelWidth = width, levelHeight = height;
            for (int i = 0; i < _maxLevels; i++)
            {
                levelWidth = Math.Max(levelWidth / 2, 1);
                levelHeight = Math.Max(levelHeight / 2, 1);

                uint texture = CreateTexture(levelWidth, levelHeight, InternalFormat.Rgba16f, PixelType.Float);
                uint framebuffer = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
                Check("Bloom");
                _levels.Add((framebuffer, texture, levelWidth, levelHeight));

                if (levelWidth <= 2 || levelHeight <= 2) break;
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>Starts drawing the scene: into the scene images, cleared to <paramref name="clear"/> and no glow.</summary>
        public void BeginScene(Color clear)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFramebuffer);
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);

            float* color = stackalloc float[4] { clear.R, clear.G, clear.B, clear.A };
            float* none = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _gl.ClearBuffer(BufferKind.Color, 0, color);
            _gl.ClearBuffer(BufferKind.Color, 1, none);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
        }

        /// <summary>Blurs the glow and draws the scene with it to the screen. <paramref name="strength"/> 0 skips the glow.</summary>
        public void Finish(float strength)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.BindVertexArray(_emptyVertexArray);

            bool bloom = strength > 0f && _levels.Count > 0;
            if (bloom)
            {
                // Down: the glow to half size, then each level to the next.
                _downsample.Use();
                _downsample.SetInt("uSource", 0);
                uint source = _sceneGlow;
                int sourceWidth = _width, sourceHeight = _height;
                foreach ((uint framebuffer, uint texture, int width, int height) in _levels)
                {
                    DrawTo(framebuffer, width, height, _downsample, source, sourceWidth, sourceHeight);
                    source = texture;
                    sourceWidth = width;
                    sourceHeight = height;
                }

                // Back up: each level blurred onto the one above it, adding to what's there.
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                _upsample.Use();
                _upsample.SetInt("uSource", 0);
                for (int i = _levels.Count - 1; i > 0; i--)
                {
                    (_, uint texture, int width, int height) = _levels[i];
                    (uint target, _, int targetWidth, int targetHeight) = _levels[i - 1];
                    DrawTo(target, targetWidth, targetHeight, _upsample, texture, width, height);
                }
                _gl.Disable(EnableCap.Blend);
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.Viewport(0, 0, (uint)_width, (uint)_height);
            _composite.Use();
            _composite.SetInt("uScene", 0);
            _composite.SetInt("uBloom", 1);
            // Each level adds the glow once more, so share the strength between them.
            _composite.SetFloat("uBloomStrength", bloom ? strength * 3.5f / _levels.Count : 0f);
            BindTexture(0, _sceneColor);
            BindTexture(1, bloom ? _levels[0].Texture : _sceneGlow);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
        }

        private void DrawTo(uint framebuffer, int width, int height, GLShader shader, uint source, int sourceWidth, int sourceHeight)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            shader.SetVector2("uTexelSize", new Vector2(1f / sourceWidth, 1f / sourceHeight));
            BindTexture(0, source);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void BindTexture(int unit, uint texture)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, texture);
        }

        private uint CreateTexture(int width, int height, InternalFormat format, PixelType type)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)width, (uint)height, 0, PixelFormat.Rgba, type, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            return texture;
        }

        private void Check(string name)
        {
            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"{name} framebuffer is incomplete: {status}");
        }

        private void DeleteTargets()
        {
            if (_sceneFramebuffer != 0)
            {
                _gl.DeleteFramebuffer(_sceneFramebuffer);
                _gl.DeleteTexture(_sceneColor);
                _gl.DeleteTexture(_sceneGlow);
                _gl.DeleteRenderbuffer(_sceneDepth);
                _sceneFramebuffer = 0;
            }

            foreach ((uint framebuffer, uint texture, _, _) in _levels)
            {
                _gl.DeleteFramebuffer(framebuffer);
                _gl.DeleteTexture(texture);
            }
            _levels.Clear();
        }

        public void Dispose()
        {
            DeleteTargets();
            _downsample.Dispose();
            _upsample.Dispose();
            _composite.Dispose();
        }
    }
}
