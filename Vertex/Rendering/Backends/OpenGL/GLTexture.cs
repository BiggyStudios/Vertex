using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>The GPU copy of a <see cref="Texture"/>.</summary>
    internal class GLTexture : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _handle;

        /// <summary>The <see cref="Texture.Version"/> last uploaded.</summary>
        public int Version { get; private set; }

        public GLTexture(GL gl, Texture texture)
        {
            _gl = gl;
            _handle = gl.GenTexture();

            // Unit 0, which every texture is bound to for drawing anyway: another unit may hold something in use, such
            // as the shadow map, which binding this there would replace.
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _handle);

            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            // Sharper on surfaces seen at a slant, like a floor stretching away, where the driver has it.
            if (MaxAnisotropy > 1f) gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)TextureMaxAnisotropy, MaxAnisotropy);

            Upload(texture);
        }

        // GL_TEXTURE_MAX_ANISOTROPY and its limit, from EXT/ARB_texture_filter_anisotropic (core only from OpenGL 4.6).
        public const int TextureMaxAnisotropy = 0x84FE;
        public const int MaxTextureMaxAnisotropy = 0x84FF;

        /// <summary>How sharply textures are filtered at a slant; 1 for not at all. Set by the backend from what the driver can do.</summary>
        public static float MaxAnisotropy { get; set; } = 1f;

        /// <summary>An empty texture for a framebuffer to draw into: no mipmaps, and it doesn't repeat past its edges.</summary>
        public unsafe GLTexture(GL gl, int width, int height)
        {
            _gl = gl;
            _handle = gl.GenTexture();

            // Unit 0, which every texture is bound to for drawing anyway: another unit may hold something in use, such
            // as the shadow map, which binding this there would replace.
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, _handle);

            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }

        public uint Handle => _handle;

        /// <summary>Copies the texture's pixels to the GPU, replacing what was there.</summary>
        public unsafe void Upload(Texture texture)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _handle);

            fixed (byte* data = texture.Pixels)
            {
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)texture.Width, (uint)texture.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, data);
            }

            _gl.GenerateMipmap(TextureTarget.Texture2D);
            Version = texture.Version;
        }

        public void Bind(int unit)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, _handle);
        }

        public void Dispose() => _gl.DeleteTexture(_handle);
    }
}
