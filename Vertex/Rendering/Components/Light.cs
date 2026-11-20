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

    /// <summary>How a light's light is worked out: as the game runs, or baked beforehand (the scene's Lighting › Bake).</summary>
    public enum LightMode
    {
        /// <summary>Worked out as the game runs, every frame: it can move and change. Its light doesn't bounce.</summary>
        Realtime,

        /// <summary>
        /// Lights things directly as the game runs, like <see cref="Realtime"/>, and its light bouncing off things is
        /// baked. Move or change it and the bounce stays as it was baked. Good for the sun.
        /// </summary>
        Mixed,

        /// <summary>
        /// All baked: static things get its light with soft shadows and its bounce, and moving ones get it from the light
        /// probes baked with it, so it costs nothing as the game runs, however many there are. It can't move or change
        /// once baked, and it has no highlights. It still shows in volumetric fog, and with <see cref="Light.CastShadows"/>
        /// on, things that move cast shadows from it. For lamps and lights that stay put.
        /// </summary>
        Baked
    }

    /// <summary>
    /// Lights the scene. Up to 4 directional lights are used at once, and up to 256 point and spot lights: those in view
    /// and nearest the camera, if there are more. Baked lights (see <see cref="Mode"/>) cost nothing as the game runs.
    /// </summary>
    public class Light : Component
    {
        public LightType Type { get; set; } = LightType.Directional;
        public Color Color { get; set; } = Color.White;
        public float Intensity { get; set; } = 1f;

        /// <summary>Whether its light is worked out as the game runs, or baked beforehand, or both: see <see cref="LightMode"/>.</summary>
        [Tooltip("Realtime: worked out as the game runs. Mixed: that, and its bounce baked. Baked: all baked, costing nothing as the game runs")]
        public LightMode Mode { get; set; } = LightMode.Realtime;

        /// <summary>
        /// How big the light itself is, for its baked shadows: they're sharp from a small light and softer from a bigger
        /// one. For a point or spot light, its radius in units (a bulb's is about 0.05); for the sun, how wide it looks
        /// in degrees (the real sun's is 0.5).
        /// </summary>
        [ShowWhen(nameof(Mode), LightMode.Mixed, LightMode.Baked)]
        public float Size { get; set; } = 0.2f;

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
        /// Whether objects block this light, casting shadows. For the sun, the first directional light that has this
        /// on (see <see cref="Lighting.ShadowDistance"/>). Point and spot lights' shadows are kept from frame to frame and
        /// only drawn again when the light or something near it moves, so lamps in a still room cost little; the nearest
        /// 16 in view that have this on get them. A baked light's shadows from static things are baked whatever this is;
        /// with it on, things that move cast shadows from it too (from up to 4 baked lights that reach the same place).
        /// </summary>
        [Tooltip("Objects block its light. Point and spot lights' shadows are only drawn again when something near them moves. A baked light's static shadows are baked: with this on, moving things cast shadows from it too")]
        public bool CastShadows { get; set; } = true;

        /// <summary>
        /// Which channel of the scene's shadow mask (see <see cref="BakedLighting.ShadowMask"/>) has how much of this
        /// light reached each static surface when it was baked, or -1 if none. Set by baking.
        /// </summary>
        [HideInInspector]
        public int ShadowMaskChannel { get; set; } = -1;
    }
}
