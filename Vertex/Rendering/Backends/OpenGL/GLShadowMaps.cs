using Silk.NET.OpenGL;

using Vertex.Rendering.Shaders;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// The sun's shadow maps: one depth texture array, a layer per cascade (see <see cref="ShadowCascades"/>), each
    /// with a framebuffer to draw it. The scene shaders read the whole array, comparing depths as they do, with linear
    /// filtering so each lookup already blends four texels' tests.
    /// </summary>
    internal sealed unsafe class GLShadowMaps : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _texture;
        private readonly uint[] _framebuffers = new uint[ShadowCascades.Count];

        public GLShadowMaps(GL gl)
        {
            _gl = gl;

            _texture = gl.GenTexture();
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2DArray, _texture);
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.DepthComponent24, ShadowCascades.Size, ShadowCascades.Size, ShadowCascades.Count, 0,
                PixelFormat.DepthComponent, PixelType.UnsignedInt, null);

            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);

            // Outside the map counts as lit.
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToBorder);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToBorder);
            float* border = stackalloc float[4] { 1f, 1f, 1f, 1f };
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBorderColor, border);
            gl.BindTexture(TextureTarget.Texture2DArray, 0);

            for (int i = 0; i < _framebuffers.Length; i++)
            {
                _framebuffers[i] = gl.GenFramebuffer();
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[i]);
                gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, _texture, 0, i);
                gl.DrawBuffer(DrawBufferMode.None);
                gl.ReadBuffer(ReadBufferMode.None);

                GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Shadow map framebuffer is incomplete: {status}");
            }

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        /// <summary>The framebuffer that draws into <paramref name="cascade"/>'s layer.</summary>
        public uint Framebuffer(int cascade) => _framebuffers[cascade];

        public void Bind(int unit)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2DArray, _texture);
        }

        public void Dispose()
        {
            foreach (uint framebuffer in _framebuffers) _gl.DeleteFramebuffer(framebuffer);
            _gl.DeleteTexture(_texture);
        }
    }
}
