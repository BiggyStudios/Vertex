using System.Numerics;

namespace Vertex.Rendering
{
    public enum ShadingModel
    {
        /// <summary>Affected by lights, the sky and shadows.</summary>
        Lit,

        /// <summary>Flat colour/texture, ignores lights.</summary>
        Unlit
    }

    /// <summary>
    /// Describes how a surface looks. Backend-independent: each renderer decides how to draw it.
    /// <para>
    /// Lit surfaces are lit physically: <see cref="Roughness"/> is the main thing to set, from mirror-smooth to chalky,
    /// and <see cref="Metallic"/> for metals. Every surface reflects the sky, more at grazing angles, as real ones do.
    /// </para>
    /// </summary>
    public class Material
    {
        /// <summary>
        /// The .material file it was loaded from (see <see cref="Assets.LoadMaterial"/>), if it is one: then it's shared by
        /// everything that uses it, and saved scenes refer to the file instead of holding a copy.
        /// </summary>
        [HideInInspector, Serialization.DontSave]
        public string? Source { get; set; }

        public ShadingModel Shading { get; set; } = ShadingModel.Lit;
        public Color Color { get; set; } = Color.White;

        /// <summary>Optional texture, multiplied with <see cref="Color"/>.</summary>
        public Texture? Texture { get; set; }

        /// <summary>
        /// Optional normal map: a texture of tiny bumps and grooves (its colours are directions, the usual blue-ish
        /// tangent-space kind, green pointing up), which light catches as if the surface really had them. See
        /// <see cref="Texture.CreateNormalMap"/> to make one from a height map. Uses the same texture coordinates as
        /// <see cref="Texture"/>.
        /// </summary>
        public Texture? NormalMap { get; set; }

        /// <summary>How deep the <see cref="NormalMap"/>'s bumps look: 1 as made, 0 flat, more to exaggerate them.</summary>
        [Range(0f, 2f)]
        public float NormalStrength { get; set; } = 1f;

        /// <summary>How many times the textures repeat across the surface's texture coordinates. (1, 1) as the mesh has them.</summary>
        public Vector2 TextureScale { get; set; } = Vector2.One;

        /// <summary>Shifts the textures across the surface, in texture repeats. Change it over time to make them flow, like water.</summary>
        public Vector2 TextureOffset { get; set; }

        /// <summary>
        /// How rough the surface is, from 0 (polished: sharp highlights and clear reflections of the sky) to 1 (chalky:
        /// broad, faint highlights and no reflection to speak of).
        /// </summary>
        [Range(0f, 1f)]
        public float Roughness { get; set; } = 0.6f;

        /// <summary>
        /// 1 for metal, 0 for anything else. Metals reflect everything, tinted by their <see cref="Color"/>, and aren't
        /// lit any other way. In between is for mixes, like dusty or rusty metal.
        /// </summary>
        [Range(0f, 1f)]
        public float Metallic { get; set; }

        /// <summary>
        /// How much a non-metal reflects, from 0 (not at all) to 1. 0.5, the default, is right for nearly everything
        /// (about 4% looking straight at it, rising towards all of it at grazing angles).
        /// </summary>
        [Range(0f, 1f)]
        public float Specular { get; set; } = 0.5f;

        /// <summary>
        /// Light the surface gives off itself, whatever lights it: black for none. Bright enough, it spills onto the space
        /// around it as bloom (see <see cref="Lighting.Bloom"/>).
        /// </summary>
        public Color Emission { get; set; } = Color.Black;

        /// <summary>How bright <see cref="Emission"/> is. Above 1 it's brighter than any colour can show, and glows more.</summary>
        public float EmissionStrength { get; set; } = 1f;

        /// <summary>
        /// A surface shader that works out the look at each point itself, from a .glsl file or a shader graph: flowing
        /// lava, a force field, a scrolling screen. It's given the settings above to start from, and changes what it
        /// likes. Null for the standard look, from the settings alone.
        /// </summary>
        [ShaderFor(ShaderPurpose.Surface)]
        public Shader? Shader { get; set; }

        /// <summary>
        /// The <see cref="Shader"/>'s settings changed from its own starting values, by name. Those the shader no longer
        /// has are kept, in case it's being edited and they come back.
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

        /// <summary>A shader setting's value: as set, or the shader's starting value. Zero if the shader has no such setting.</summary>
        public Vector4 Get(string name) => ShaderValue.Get(Values, Shader, name);

        /// <summary>Puts a shader setting back to the shader's starting value.</summary>
        public void Reset(string name) => Values.RemoveAll(value => value.Name == name);

        /// <summary>Whether a shader setting has been changed from the shader's starting value.</summary>
        public bool IsSet(string name) => Values.Exists(value => value.Name == name);

        public Material() {}

        public Material(Color color)
        {
            Color = color;
        }

        /// <summary>A copy with the same settings, of its own (not tied to a file): to change one object's look without the rest.</summary>
        public Material Copy()
        {
            var copy = (Material)MemberwiseClone();
            copy.Source = null;
            copy.Values = Values.Select(v => new ShaderValue { Name = v.Name, Value = v.Value, Texture = v.Texture }).ToList();
            return copy;
        }
    }
}
