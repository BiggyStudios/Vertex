using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Vertex.Engine.Rendering
{
    public class Material
    {
        private readonly Shader _shader;
        private readonly Dictionary<string, Texture> _textures = new();

        public Material(Shader shader)
        {
            _shader = shader;
        }

        public void Use()
        {
            _shader.Use();
        }

        public void SetTexture(string name, Texture texture, int unit = 0)
        {
            _textures[name] = texture;
            texture.Use(TextureUnit.Texture0 + unit);
            _shader.SetInt(name, unit);
        }

        public void SetMatrix4(string name, Matrix4 matrix)
        {
            _shader.SetMatrix4(name, matrix);
        }

        public void SetVector3(string name, Vector3 vector)
        {
            _shader.SetVector3(name, vector);
        }

        public void SetFloat(string name, float value)
        {
            _shader.SetFloat(name, value);
        }

        public void SetInt(string name, int value)
        {
            _shader.SetInt(name, value);
        }
    }
}