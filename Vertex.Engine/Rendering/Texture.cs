using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace Vertex.Engine.Rendering
{
    /// <summary>
    /// Represents an OpenGL texture that can be loaded from and image file and used in shaders.
    /// </summary>
    public class Texture
    {
        /// <summary>
        /// Gets the OpenGL handle for this texture.
        /// </summary>
        public readonly int Handle;

        /// <summary>
        /// Creates a new texture from the specified image file path.
        /// </summary>
        /// <param name="path">Path to the image file.</param>
        /// <returns>A new texture instance.</returns>
        public static Texture LoadFromFile(string path)
        {
            int handle = GL.GenTexture();

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, handle);

            StbImage.stbi_set_flip_vertically_on_load(1);

            using (Stream stream = File.OpenRead(path))
            {
                ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
            }

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            return new Texture(handle);
        }

        /// <summary>
        /// Creates a new texture with the specified OpenGL handle.
        /// </summary>
        /// <param name="gHandle">OpenGL texture handle.</param>
        public Texture(int gHandle)
        {
            Handle = gHandle;
        }

        /// <summary>
        /// Activates this texture on the specified texture unit.
        /// </summary>
        /// <param name="unit">Texture unit to activate on.</param>
        public void Use(TextureUnit unit)
        {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, Handle);
        }
    }
}