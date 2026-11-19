using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Changes the finished picture of the camera on the same object with a <see cref="Rendering.Shader"/> written in
    /// GLSL: a look of the game's own, such as dithering, film grain or a colour grade. Effects run after everything
    /// else (tone mapping and anti-aliasing included), in the order they're on the object, each on what the one before made.
    /// </summary>
    public class PostEffect : Component
    {
        /// <summary>The effect: a .glsl file defining <c>vec4 effect(vec2 uv)</c>. See <see cref="Rendering.Shader"/>.</summary>
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

        public void Set(string name, Vector4 value)
        {
            foreach (ShaderValue saved in Values)
            {
                if (saved.Name != name) continue;
                saved.Value = value;
                return;
            }

            Values.Add(new ShaderValue { Name = name, Value = value });
        }

        /// <summary>Puts a setting back to the shader's starting value.</summary>
        public void Reset(string name) => Values.RemoveAll(value => value.Name == name);

        /// <summary>A setting's value: as set, or the shader's starting value. Zero if the shader has no such setting.</summary>
        public Vector4 Get(string name)
        {
            foreach (ShaderValue saved in Values)
            {
                if (saved.Name == name) return saved.Value;
            }

            return Shader?.Property(name)?.Default ?? Vector4.Zero;
        }

        /// <summary>Whether a setting has been changed from the shader's starting value.</summary>
        public bool IsSet(string name) => Values.Exists(value => value.Name == name);

        /// <summary>The values to hand the shader, in its settings' order.</summary>
        internal Vector4[] Pack()
        {
            var packed = new Vector4[Rendering.Shader.MaxProperties];
            if (Shader == null) return packed;

            foreach (ShaderProperty property in Shader.Properties) packed[property.Slot] = Get(property.Name);
            return packed;
        }
    }

    /// <summary>One of a <see cref="PostEffect"/>'s settings, kept by name: a float in X, a bool as 0 or 1.</summary>
    public sealed class ShaderValue
    {
        public string Name { get; set; } = "";
        public Vector4 Value { get; set; }
    }
}
