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
            return copy;
        }
    }
}
