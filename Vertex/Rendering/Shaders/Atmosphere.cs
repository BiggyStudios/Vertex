using System.Numerics;

namespace Vertex.Rendering.Shaders
{
    /// <summary>
    /// The same sky as the shaders' (see <see cref="ShaderLibrary.Frame"/>), worked out on the CPU for what the shaders
    /// would otherwise have to add up over the whole sky for every pixel: how much light comes from above (the sky's
    /// average, weighted towards overhead, as a surface facing up sees it) and the colour at the horizon.
    /// </summary>
    internal static class Atmosphere
    {
        /// <summary>
        /// How bright the sky is next to its sun: a clear sky overhead comes out at about a quarter of what a white
        /// surface facing the sun reflects, as it looks in photos.
        /// </summary>
        public const float Brightness = 10f;

        private static readonly Vector3 _rayleigh = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f) * 8000f;
        private const float _mie = 3.996e-6f * 1200f;
        private const float _mieExtinction = 4.44e-6f * 1200f;
        private static readonly Vector3 _ozone = new Vector3(0.650e-6f, 1.881e-6f, 0.085e-6f) * 15000f;
        private static readonly Vector3 _extinction = _rayleigh + new Vector3(_mieExtinction) + _ozone;

        private static float AirMass(float cosZenith)
        {
            float zenith = MathF.Min(MathHelper.ToDegrees(MathF.Acos(Math.Clamp(cosZenith, 0f, 1f))), 90f);
            return 1f / (MathF.Max(cosZenith, 0f) + 0.50572f * MathF.Pow(96.07995f - zenith, -1.6364f));
        }

        private static Vector3 SunTransmittance(float sunHeight, float viewHeight)
        {
            float path = AirMass(sunHeight) * float.Lerp(1f, 0.35f, Math.Clamp(viewHeight, 0f, 1f));
            float set = SmoothStep(-0.1f, 0.03f, sunHeight);
            return Exp(-_extinction * path) * set;
        }

        /// <summary>
        /// What the sun's light is multiplied by on its way down through the air from a height of
        /// <paramref name="sunHeight"/> (the sine of its elevation): nothing when it's overhead, golden near the horizon,
        /// nothing at all once it's set.
        /// </summary>
        public static Vector3 SunlightTint(float sunHeight) => SunTransmittance(sunHeight, 0f) / SunTransmittance(1f, 0f);

        /// <summary>The sky's light coming from <paramref name="direction"/> (above the horizon), for a sun towards <paramref name="sun"/>.</summary>
        public static Vector3 Radiance(Vector3 direction, Vector3 sun, Vector3 sunColor)
        {
            float cosTheta = Vector3.Dot(direction, sun);
            float viewHeight = MathF.Max(direction.Y, 0f);
            Vector3 extinction = _extinction;

            float rayleighPhase = 0.0596831f * (1f + cosTheta * cosTheta);
            const float g = 0.76f;
            float miePhase = 0.1193662f * (1f - g * g) * (1f + cosTheta * cosTheta) / ((2f + g * g) * MathF.Pow(1f + g * g - 2f * g * cosTheta, 1.5f));

            Vector3 scattering = _rayleigh * rayleighPhase + new Vector3(_mie * miePhase);
            Vector3 inscattered = scattering / extinction * (Vector3.One - Exp(-extinction * AirMass(viewHeight)));
            return inscattered * SunTransmittance(sun.Y, viewHeight) * sunColor * Brightness;
        }

        /// <summary>
        /// The light the sky gives a surface facing straight up (the average over the sky, weighted as such a surface sees
        /// it), and the sky's colour round the horizon.
        /// </summary>
        public static (Vector3 FromAbove, Vector3 Horizon) Ambient(Vector3 sun, Vector3 sunColor)
        {
            const int rings = 6, around = 8;
            Vector3 fromAbove = Vector3.Zero;
            float weights = 0f;
            for (int ring = 0; ring < rings; ring++)
            {
                // Up from the horizon in equal steps, each ring counted by how much of the sky it covers and how
                // squarely an upward-facing surface faces it.
                float elevation = (ring + 0.5f) / rings * MathF.PI * 0.5f;
                float weight = MathF.Sin(elevation) * MathF.Cos(elevation);
                for (int i = 0; i < around; i++)
                {
                    float angle = (i + 0.5f) / around * MathF.Tau;
                    var direction = new Vector3(MathF.Cos(elevation) * MathF.Cos(angle), MathF.Sin(elevation), MathF.Cos(elevation) * MathF.Sin(angle));
                    fromAbove += Radiance(direction, sun, sunColor) * weight;
                    weights += weight;
                }
            }

            Vector3 horizon = Vector3.Zero;
            for (int i = 0; i < around; i++)
            {
                float angle = (i + 0.5f) / around * MathF.Tau;
                horizon += Radiance(Vector3.Normalize(new Vector3(MathF.Cos(angle), 0.03f, MathF.Sin(angle))), sun, sunColor);
            }

            return (fromAbove / weights, horizon / around);
        }

        private static Vector3 Exp(Vector3 v) => new(MathF.Exp(v.X), MathF.Exp(v.Y), MathF.Exp(v.Z));

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
