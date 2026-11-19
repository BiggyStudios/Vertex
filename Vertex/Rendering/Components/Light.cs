namespace Vertex.Rendering
{
    public enum LightType
    {
        /// <summary>Infinitely far away, like the sun. Shines along the transform's forward direction.</summary>
        Directional,

        /// <summary>Shines in all directions from the transform's position, fading out over <see cref="Light.Range"/>.</summary>
        Point,

        /// <summary>
        /// Shines in a cone from the transform's position along its forward direction, like a torch or a street lamp,
        /// fading out over <see cref="Light.Range"/>. See <see cref="Light.SpotAngle"/>.
        /// </summary>
        Spot
    }

    /// <summary>
    /// Lights the scene. Up to 4 directional lights are used at once, and up to 64 point and spot lights: those in view
    /// and nearest the camera, if there are more.
    /// </summary>
    public class Light : Component
    {
        public LightType Type { get; set; } = LightType.Directional;
        public Color Color { get; set; } = Color.White;
        public float Intensity { get; set; } = 1f;

        /// <summary>Distance at which a point or spot light fades to nothing.</summary>
        [ShowWhen(nameof(Type), LightType.Point, LightType.Spot)]
        public float Range { get; set; } = 10f;

        /// <summary>How wide a spot light's cone is, from one edge to the other, in degrees.</summary>
        [Range(1f, 179f), ShowWhen(nameof(Type), LightType.Spot)]
        public float SpotAngle { get; set; } = 50f;

        /// <summary>
        /// How much of a spot light's cone fades out towards its edge, from 0 (a hard edge) to 1 (fading all the way
        /// from the middle).
        /// </summary>
        [Range(0f, 1f), ShowWhen(nameof(Type), LightType.Spot)]
        public float SpotSoftness { get; set; } = 0.35f;

        /// <summary>
        /// How brightly this light shows in volumetric fog (see <see cref="Lighting.VolumetricFog"/>): 1 as bright as it
        /// lights surfaces, 0 not at all. Turn it down for a light behind a wall, which would otherwise light the fog on
        /// the other side.
        /// </summary>
        public float FogStrength { get; set; } = 1f;

        /// <summary>
        /// Whether objects block this light, casting shadows. Only directional lights cast shadows,
        /// and only the first one that has this on. See <see cref="Lighting.ShadowDistance"/>.
        /// </summary>
        [ShowWhen(nameof(Type), LightType.Directional)]
        public bool CastShadows { get; set; } = true;
    }
}
