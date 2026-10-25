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
            gl.BindTexture(TextureTarget.Texture2D, _handle);

            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            Upload(texture);
        }

        /// <summary>Copies the texture's pixels to the GPU, replacing what was there.</summary>
        public unsafe void Upload(Texture texture)
        {
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
