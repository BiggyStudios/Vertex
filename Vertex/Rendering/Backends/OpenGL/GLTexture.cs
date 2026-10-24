using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>The GPU copy of a <see cref="Texture"/>.</summary>
    internal class GLTexture : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _handle;

        public unsafe GLTexture(GL gl, Texture texture)
        {
            _gl = gl;
            _handle = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _handle);

            fixed (byte* data = texture.Pixels)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)texture.Width, (uint)texture.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, data);
            }

            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        public void Bind(int unit)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + unit);
            _gl.BindTexture(TextureTarget.Texture2D, _handle);
        }

        public void Dispose() => _gl.DeleteTexture(_handle);
    }
}
