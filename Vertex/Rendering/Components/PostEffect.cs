using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Changes the finished picture of the camera on the same object with a <see cref="Rendering.Shader"/> written in
    /// GLSL or made in a shader graph: a look of the game's own, such as dithering, film grain or a colour grade. Effects
    /// run after everything else (tone mapping and anti-aliasing included), in the order they're on the object, each on
    /// what the one before made.
    /// </summary>
    public class PostEffect : Component
    {
        /// <summary>The effect: a .glsl file defining <c>vec4 effect(vec2 uv)</c>, or a post effect shader graph. See <see cref="Rendering.Shader"/>.</summary>
        public Shader? Shader { get; set; }

        /// <summary>
        /// The settings changed from the shader's own starting values, by name. Those the shader no longer has are kept,
        /// in case it's being edited and they come back.
        /// </summary>
        [HideInInspector]
        public List<ShaderValue> Values { get; set; } = new();

        /// <summary>Sets one of the shader's settings (a float, an int or a bool given as 0 or 1).</summary>
        public void Set(string name, float value) => Set(name, new Vector4(value, 0f, 0f, 0f));

        public void Set(string name, bool value) => Set(name, value ? 1f : 0f);
        public void Set(string name, Vector2 value) => Set(name, new Vector4(value, 0f, 0f));
        public void Set(string name, Vector3 value) => Set(name, new Vector4(value, 0f));
        public void Set(string name, Color value) => Set(name, new Vector4(value.R, value.G, value.B, value.A));
        public void Set(string name, Vector4 value) => ShaderValue.Set(Values, name, value);

        /// <summary>Sets one of the shader's texture settings; null puts it back to the shader's own.</summary>
        public void SetTexture(string name, Texture? texture) => ShaderValue.SetTexture(Values, name, texture);

        /// <summary>A texture setting: as set, or the file the shader starts it as. Null for a plain one.</summary>
        public Texture? GetTexture(string name) => ShaderValue.GetTexture(Values, Shader, name);

        /// <summary>Puts a setting back to the shader's starting value.</summary>
        public void Reset(string name) => Values.RemoveAll(value => value.Name == name);

        /// <summary>A setting's value: as set, or the shader's starting value. Zero if the shader has no such setting.</summary>
        public Vector4 Get(string name) => ShaderValue.Get(Values, Shader, name);

        /// <summary>Whether a setting has been changed from the shader's starting value.</summary>
        public bool IsSet(string name) => Values.Exists(value => value.Name == name);
    }
}
