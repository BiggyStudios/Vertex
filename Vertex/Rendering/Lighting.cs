namespace Vertex.Rendering
{
    /// <summary>How a scene's sky looks.</summary>
    public enum SkyStyle
    {
        /// <summary>
        /// Worked out from where the sun is (the first directional light that casts shadows), like a real sky: deep blue
        /// overhead and paler at the horizon by day, orange round a low sun, dark once it's down. With clouds (see
        /// <see cref="Lighting.CloudCover"/>). The sun's light itself turns golden as it gets low and goes out once it's
        /// set, so moving the sun is all a day and night takes; overhead, it's exactly the light's own colour.
        /// </summary>
        Atmosphere,

        /// <summary>A plain gradient from <see cref="Lighting.HorizonColor"/> up to <see cref="Lighting.SkyColor"/>, for a stylised look.</summary>
        Gradient
    }

    /// <summary>
    /// A scene's sky, the light that comes from it, and how the picture is finished. The sky is drawn behind everything,
    /// lights every surface softly from all around (from above, and bounced up off the ground), and is reflected in
    /// smooth surfaces. Distant things fade into it, which gives a sense of depth.
    /// </summary>
    public class Lighting
    {
        /// <summary>How the sky looks: a real-looking one that follows the sun (the default), or a simple gradient.</summary>
        public SkyStyle Sky { get; set; } = SkyStyle.Atmosphere;

        /// <summary>The sky overhead, for <see cref="SkyStyle.Gradient"/>.</summary>
        public Color SkyColor { get; set; } = new(0.32f, 0.53f, 0.86f);

        /// <summary>The sky at the horizon, for <see cref="SkyStyle.Gradient"/>.</summary>
        public Color HorizonColor { get; set; } = new(0.74f, 0.83f, 0.93f);

        /// <summary>The ground's colour: seen below the horizon, and lighting surfaces from below as light bounces off it.</summary>
        public Color GroundColor { get; set; } = new(0.42f, 0.38f, 0.33f);

        /// <summary>How much of the sky is cloud, from 0 (clear) to 1 (overcast). They drift slowly with the wind.</summary>
        [Range(0f, 1f)]
        public float CloudCover { get; set; } = 0.4f;

        /// <summary>How strongly the sky and ground light surfaces (and are reflected in them), on top of the scene's Lights.</summary>
        public float AmbientIntensity { get; set; } = 1f;

        /// <summary>How quickly distant things fade into the sky. 0 is no fog; at 0.004, things 300 units away are mostly gone.</summary>
        public float FogDensity { get; set; } = 0.002f;

        /// <summary>Overall brightness, like a camera's exposure. Bright areas roll off smoothly rather than clipping to white.</summary>
        public float Exposure { get; set; } = 1f;

        /// <summary>
        /// How far from the camera the sun's shadows reach. They're sharpest close by and softer further out, fading away
        /// at this distance. Only directional lights with <see cref="Light.CastShadows"/> cast them (the first such light).
        /// </summary>
        public float ShadowDistance { get; set; } = 120f;

        /// <summary>
        /// How strongly bright things (the sun, glowing materials and particles, bright highlights) spill light onto the
        /// space around them, like a bright light seen through a lens. 0 turns it off.
        /// </summary>
        public float Bloom { get; set; } = 1f;

        /// <summary>
        /// How much corners, creases and the ground under things are darkened, where the soft light from all around
        /// can't reach so easily: from 0 (off) to 1. It's what makes things look like they sit on the ground.
        /// </summary>
        [Range(0f, 1f)]
        public float AmbientOcclusion { get; set; } = 1f;

        /// <summary>How far, in units, nearby things darken each other for <see cref="AmbientOcclusion"/>.</summary>
        public float AmbientOcclusionRadius { get; set; } = 0.8f;

        /// <summary>Smooths jagged edges (FXAA).</summary>
        public bool AntiAliasing { get; set; } = true;

        /// <summary>How much the corners of the picture darken, like a camera lens, from 0 (none) to 1.</summary>
        [Range(0f, 1f)]
        public float Vignette { get; set; } = 0.25f;

        /// <summary>
        /// A toon look, from 0 (off, smooth lighting) to 1: light from the scene's Lights falls off in a few soft-edged
        /// bands instead of smoothly, and highlights get a crisp edge. In between, it's a mix of the two, for a light touch.
        /// </summary>
        [Range(0f, 1f)]
        public float CelShading { get; set; }

        /// <summary>
        /// How dark the outlines round things are, from 0 (none) to 1: silhouettes and outer edges, found from the depth of
        /// what's drawn, fading out with distance so far-off things stay clean. Pairs with <see cref="CelShading"/>.
        /// </summary>
        [Range(0f, 1f)]
        public float Outlines { get; set; }

        /// <summary>Draw the sky. Off, the screen is cleared to the camera's ClearColor, and fog fades into that instead.</summary>
        public bool DrawSky { get; set; } = true;

        /// <summary>
        /// Fog that light shines through: lights glow in it, spot lights show their cones, and the sun casts shafts of
        /// light through gaps, with shadows falling through it. Worked out near the camera (see
        /// <see cref="VolumetricDistance"/>); further off, things fade into the sky as <see cref="FogDensity"/> has them.
        /// It covers particles, sprites and the sky as well as surfaces. Leave it off for scenes that don't need it: it
        /// takes a little time each frame.
        /// </summary>
        [Header("Volumetric Fog")]
        public bool VolumetricFog { get; set; }

        /// <summary>
        /// How thick the fog is. At 0.02, half the light from something 35 units away is lost on the way; at 0.1, half
        /// is gone in 7. See <see cref="FogVolume"/> for thicker or clearer places.
        /// </summary>
        public float VolumetricDensity { get; set; } = 0.02f;

        /// <summary>The fog's own colour: white takes on the colours of the lights in it, a tint colours all of them.</summary>
        public Color VolumetricColor { get; set; } = new(0.92f, 0.94f, 1f);

        /// <summary>The height up to which the fog is at its full thickness. Above it, it thins (see <see cref="VolumetricFalloff"/>).</summary>
        public float VolumetricHeight { get; set; }

        /// <summary>How quickly the fog thins above <see cref="VolumetricHeight"/>: at 0.1, a tenth as thick 23 units up. 0 is just as thick all the way up.</summary>
        public float VolumetricFalloff { get; set; } = 0.08f;

        /// <summary>How patchy the fog is, from 0 (even all through) to 1 (wisps and clear gaps).</summary>
        [Range(0f, 1f)]
        public float VolumetricNoise { get; set; } = 0.5f;

        /// <summary>Which way, and how fast in units a second, the patches drift.</summary>
        public System.Numerics.Vector3 VolumetricWind { get; set; } = new(0.6f, 0f, 0.25f);

        /// <summary>
        /// How much more light the fog sends on in the direction it was going than back the way it came, from 0 (the
        /// same every way) to just under 1. Higher makes bright halos round lights seen through the fog, and the sun's
        /// shafts strongest looking towards it.
        /// </summary>
        [Range(0f, 0.95f)]
        public float VolumetricForwardScattering { get; set; } = 0.6f;

        /// <summary>
        /// How strongly the light from the sky (see <see cref="AmbientIntensity"/>) lights the fog, as well as the scene's
        /// Lights. 0 leaves it dark but for them, for night scenes lit by lamps.
        /// </summary>
        public float VolumetricAmbient { get; set; } = 1f;

        /// <summary>How far from the camera the fog is worked out. Nearer is sharper, as it's worked out in the same number of steps.</summary>
        public float VolumetricDistance { get; set; } = 80f;
    }
}
