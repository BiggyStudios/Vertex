namespace Vertex.Rendering
{
    /// <summary>
    /// A box of thicker (or thinner) volumetric fog, the size of the object's scale: mist hanging in an alley, steam over
    /// a kitchen, or, with a negative <see cref="Density"/>, clear air inside a building. Only shows with
    /// <see cref="Lighting.VolumetricFog"/> on. Up to 8 are used at once, those nearest the camera.
    /// </summary>
    public class FogVolume : Component
    {
        /// <summary>
        /// How much thicker the fog is inside, on top of the scene's (see <see cref="Lighting.VolumetricDensity"/>).
        /// Negative thins it: as negative as the scene's density clears it completely.
        /// </summary>
        public float Density { get; set; } = 0.1f;

        /// <summary>How gradually it fades in from its sides, from 0 (a hard edge) to 1 (fading all the way to the middle).</summary>
        [Range(0f, 1f)]
        public float Softness { get; set; } = 0.6f;
    }
}
