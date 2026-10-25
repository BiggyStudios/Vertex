namespace Vertex.Rendering
{
    public enum LightType
    {
        /// <summary>Infinitely far away, like the sun. Shines along the transform's forward direction.</summary>
        Directional,

        /// <summary>Shines in all directions from the transform's position, fading out over <see cref="Light.Range"/>.</summary>
        Point
    }

    /// <summary>
    /// Lights the scene. Up to 4 directional and 8 point lights are used at once.
    /// </summary>
    public class Light : Component
    {
        public LightType Type { get; set; } = LightType.Directional;
        public Color Color { get; set; } = Color.White;
        public float Intensity { get; set; } = 1f;

        /// <summary>Distance at which a point light fades to nothing.</summary>
        public float Range { get; set; } = 10f;

        /// <summary>
        /// Whether objects block this light, casting shadows. Only directional lights cast shadows,
        /// and only the first one that has this on. See <see cref="Lighting.ShadowDistance"/>.
        /// </summary>
        public bool CastShadows { get; set; } = true;
    }
}
