using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Vertex.Engine.Rendering
{
    /// <summary>
    /// Represents a material that defines how a mesh should be rendered using a shader and textures.
    /// </summary>
    public class Material
    {
        private readonly Shader _shader;
        private readonly Dictionary<string, Texture> _textures = new();

        /// <summary>
        /// Initializes a new instance  of the Material class with specified shader.
        /// </summary>
        /// <param name="shader">The shader to use for rendering.</param>
        public Material(Shader shader)
        {
            _shader = shader;
        }

        /// <summary>
        /// Activates this material's shader for rendering.
        /// </summary>
        public void Use()
        {
            _shader.Use();
        }

        /// <summary>
        /// Sets a texture uniform in the shader.
        /// </summary>
        /// <param name="name">The name of the texture uniform.</param>
        /// <param name="texture">The texture to set.</param>
        /// <param name="unit">The texture unit to use (default: 0).</param>
        public void SetTexture(string name, Texture texture, int unit = 0)
        {
            _textures[name] = texture;
            texture.Use(TextureUnit.Texture0 + unit);
            _shader.SetInt(name, unit);
        }

        /// <summary>
        /// Sets a Matrix4 uniform in the shader.
        /// </summary>
        /// <param name="name">The name of the uniform.</param>
        /// <param name="matrix">The matrix value to set.</param>
        public void SetMatrix4(string name, Matrix4 matrix)
        {
            _shader.SetMatrix4(name, matrix);
        }

        /// <summary>
        /// Sets a Vector3 uniform in the shader.
        /// </summary>
        /// <param name="name">The name of the uniform.</param>
        /// <param name="vector">The vector value to set.</param>
        public void SetVector3(string name, Vector3 vector)
        {
            _shader.SetVector3(name, vector);
        }

        /// <summary>
        /// Sets a float uniform in the shader.
        /// </summary>
        /// <param name="name">The name of the uniform.</param>
        /// <param name="value">The float value to set.</param>
        public void SetFloat(string name, float value)
        {
            _shader.SetFloat(name, value);
        }

        /// <summary>
        /// Sets an integer uniform in the shader.
        /// </summary>
        /// <param name="name">The name of the uniform.</param>
        /// <param name="value">The integer value to set.</param>
        public void SetInt(string name, int value)
        {
            _shader.SetInt(name, value);
        }
    }
}