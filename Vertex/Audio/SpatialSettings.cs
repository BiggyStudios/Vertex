using System.Numerics;

namespace Vertex.Audio
{
    /// <summary>How a sound in the world gets quieter further from the listener.</summary>
    public enum Rolloff
    {
        /// <summary>As in open air: it halves in loudness each time the distance doubles, and is quietest at Max Distance.</summary>
        Natural,

        /// <summary>Evenly quieter with distance, silent at Max Distance: for a sound that should stop at a set range.</summary>
        Linear,

        /// <summary>The same loudness at any distance, though still heard from its direction.</summary>
        None
    }

    /// <summary>
    /// How a sound placed in the world is heard: how it fades with distance, whether it's louder in front of where it
    /// faces, and how much its pitch shifts as it races past (the Doppler effect). An <see cref="AudioSource"/> has these
    /// as settings; <see cref="Sound.PlayAt(AudioClip, Vector3, SpatialSettings, float, float)"/> takes them for a one-off sound.
    /// </summary>
    public readonly record struct SpatialSettings
    {
        public SpatialSettings() {}

        /// <summary>Within this distance, in metres, it plays at full volume.</summary>
        public float FullVolumeDistance { get; init; } = 3f;

        /// <summary>Where it stops getting quieter (<see cref="Rolloff.Natural"/>) or falls silent (<see cref="Rolloff.Linear"/>), in metres.</summary>
        public float MaxDistance { get; init; } = 100f;

        public Rolloff Rolloff { get; init; } = Rolloff.Natural;

        /// <summary>How much its pitch rises as it comes closer fast and falls as it goes away: 0 none, 1 as in real life.</summary>
        public float Doppler { get; init; }

        /// <summary>
        /// The angle, in degrees, of the cone in front of it (along its object's forward) where it's at full volume: 360
        /// is all round. Outside <see cref="OuterConeAngle"/> it's at <see cref="OuterConeVolume"/>, and between the two
        /// it fades from one to the other: a loudspeaker, a voice facing away.
        /// </summary>
        public float ConeAngle { get; init; } = 360f;

        /// <summary>The angle, in degrees, beyond which it's at <see cref="OuterConeVolume"/>.</summary>
        public float OuterConeAngle { get; init; } = 360f;

        /// <summary>How loud it is behind, outside <see cref="OuterConeAngle"/>: 0 silent, 1 as loud as in front.</summary>
        public float OuterConeVolume { get; init; }

        /// <summary>Settings for a sound that's at full volume within <paramref name="fullVolumeDistance"/> and fades naturally beyond it.</summary>
        public static SpatialSettings Within(float fullVolumeDistance) => new() { FullVolumeDistance = fullVolumeDistance };

        /// <summary>
        /// How loud it is <paramref name="distance"/> metres from the listener, from 0 to 1, by its rolloff: what the audio
        /// device works out, for game code that wants to know too (whether an enemy could hear it, say).
        /// </summary>
        public float VolumeAt(float distance)
        {
            float near = MathF.Max(FullVolumeDistance, 0.01f);
            float far = MathF.Max(MaxDistance, near);
            float d = Math.Clamp(distance, near, far);
            return Rolloff switch
            {
                Rolloff.None => 1f,
                Rolloff.Linear => far > near ? 1f - (d - near) / (far - near) : 1f,
                _ => near / d
            };
        }

        /// <summary>The same settings, with the cone kept to sensible angles (inner within outer, both 0 to 360).</summary>
        internal SpatialSettings Clamped()
        {
            float inner = Math.Clamp(ConeAngle, 0f, 360f);
            return this with
            {
                FullVolumeDistance = MathF.Max(FullVolumeDistance, 0.01f),
                MaxDistance = MathF.Max(MaxDistance, MathF.Max(FullVolumeDistance, 0.01f)),
                Doppler = MathF.Max(Doppler, 0f),
                ConeAngle = inner,
                OuterConeAngle = Math.Clamp(OuterConeAngle, inner, 360f),
                OuterConeVolume = Math.Clamp(OuterConeVolume, 0f, 1f)
            };
        }
    }

    /// <summary>
    /// Where a voice is heard from: a place in the world with its spatial settings, how it's moving and which way it
    /// faces; or everywhere the same (no position), panned left or right.
    /// </summary>
    internal readonly record struct SoundPlacement(Vector3? Position, SpatialSettings Spatial, Vector3 Velocity, Vector3 Forward, float Pan)
    {
        /// <summary>The same in both ears, at the middle.</summary>
        public static SoundPlacement Everywhere => new(null, new SpatialSettings(), Vector3.Zero, Vector3.Zero, 0f);

        public static SoundPlacement At(Vector3 position, SpatialSettings spatial) => new(position, spatial, Vector3.Zero, Vector3.Zero, 0f);
    }
}
