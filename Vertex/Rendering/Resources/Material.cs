namespace Vertex.Rendering
{
    public enum ShadingModel
    {
        /// <summary>Affected by lights (diffuse + specular).</summary>
        Lit,

        /// <summary>Flat colour/texture, ignores lights.</summary>
        Unlit
    }

    /// <summary>
    /// Describes how a surface looks. Backend-independent: each renderer decides how to draw it.
    /// </summary>
    public class Material
    {
        public ShadingModel Shading { get; set; } = ShadingModel.Lit;
        public Color Color { get; set; } = Color.White;

        /// <summary>Optional texture, multiplied with <see cref="Color"/>.</summary>
        public Texture? Texture { get; set; }

        /// <summary>Strength of specular highlights (0 = matte).</summary>
        public float Specular { get; set; } = 0.3f;

        /// <summary>Higher values give smaller, sharper highlights.</summary>
        public float Shininess { get; set; } = 32f;

        /// <summary>
        /// Light the surface gives off itself, whatever lights it: black for none. It shines out onto the space around
        /// it as bloom (see <see cref="Lighting.Bloom"/>), more the brighter it is.
        /// </summary>
        public Color Emission { get; set; } = Color.Black;

        /// <summary>How bright <see cref="Emission"/> is. Above 1 it's brighter than any colour can show, and glows more.</summary>
        public float EmissionStrength { get; set; } = 1f;

        public Material() {}

        public Material(Color color)
        {
            Color = color;
        }
    }
}
