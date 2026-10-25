namespace Vertex.Rendering
{
    /// <summary>
    /// A scene's sky and the light that comes from it. The sky is drawn behind everything, fading from
    /// <see cref="HorizonColor"/> up to <see cref="SkyColor"/>. It also lights every surface softly from all around:
    /// the sky's colour from above and <see cref="GroundColor"/> bounced up from below. Distant things fade into the
    /// horizon colour, which gives a sense of depth.
    /// </summary>
    public class Lighting
    {
        public Color SkyColor { get; set; } = new(0.32f, 0.53f, 0.86f);
        public Color HorizonColor { get; set; } = new(0.74f, 0.83f, 0.93f);
        public Color GroundColor { get; set; } = new(0.42f, 0.38f, 0.33f);

        /// <summary>How strongly the sky and ground light surfaces, on top of the scene's Lights.</summary>
        public float AmbientIntensity { get; set; } = 0.6f;

        /// <summary>How quickly distant things fade into the horizon. 0 is no fog; at 0.004, things 300 units away are mostly gone.</summary>
        public float FogDensity { get; set; } = 0.004f;

        /// <summary>Overall brightness, like a camera's exposure. Bright areas roll off smoothly rather than clipping to white.</summary>
        public float Exposure { get; set; } = 1f;

        /// <summary>
        /// How far around the camera the sun's shadows reach. Larger covers more but makes shadow edges blurrier.
        /// Only directional lights with <see cref="Light.CastShadows"/> cast them (the first such light).
        /// </summary>
        public float ShadowDistance { get; set; } = 40f;

        /// <summary>Draw the sky. Off, the screen is cleared to the camera's ClearColor, and fog fades into that instead.</summary>
        public bool DrawSky { get; set; } = true;
    }
}
