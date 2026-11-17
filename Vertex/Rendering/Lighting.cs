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

        /// <summary>
        /// How strongly glowing things (materials' emission, glowing particles) spill light onto the space around them,
        /// like a bright light seen through a lens. 0 turns it off.
        /// </summary>
        public float Bloom { get; set; } = 1f;

        /// <summary>
        /// A toon look, from 0 (off, smooth lighting) to 1: light from the scene's Lights falls off in a few soft-edged
        /// bands instead of smoothly, and highlights get a crisp edge. In between, it's a mix of the two, for a light touch.
        /// </summary>
        public float CelShading { get; set; }

        /// <summary>
        /// How dark the outlines round things are, from 0 (none) to 1: silhouettes and outer edges, found from the depth of
        /// what's drawn, fading out with distance so far-off things stay clean. Pairs with <see cref="CelShading"/>.
        /// </summary>
        public float Outlines { get; set; }

        /// <summary>Draw the sky. Off, the screen is cleared to the camera's ClearColor, and fog fades into that instead.</summary>
        public bool DrawSky { get; set; } = true;
    }
}
