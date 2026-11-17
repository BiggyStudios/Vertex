using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>How particles mix with what's behind them.</summary>
    public enum ParticleBlend
    {
        /// <summary>Adds light, like sparks, embers and magic. Order doesn't matter, and they never darken anything.</summary>
        Additive,

        /// <summary>Covers what's behind, like dust and smoke.</summary>
        Alpha
    }

    /// <summary>A value picked at random between <see cref="Min"/> and <see cref="Max"/> each time it's used.</summary>
    public readonly record struct FloatRange(float Min, float Max)
    {
        public float Random() => Lerp(System.Random.Shared.NextSingle());

        /// <summary>The value <paramref name="t"/> of the way from <see cref="Min"/> to <see cref="Max"/>.</summary>
        public float Lerp(float t) => Min + (Max - Min) * t;

        /// <summary>Always the same value.</summary>
        public static implicit operator FloatRange(float value) => new(value, value);
    }

    /// <summary>
    /// What every particle effect is made of, whether it's simulated on the CPU (<see cref="ParticleSystem"/>) or the
    /// GPU (<see cref="GpuParticleSystem"/>). Each particle starts in the emitter's area, heads off within
    /// <see cref="Spread"/> degrees of <see cref="Direction"/>, then falls, slows and fades over its lifetime. They live
    /// in the world, so moving the emitter doesn't drag the ones already out. Emit a steady stream with
    /// <see cref="Rate"/>, or a burst at once with <see cref="Emit(int)"/>. Particles always face the camera.
    /// </summary>
    public abstract class ParticleEmitter : Component
    {
        private float _toEmit;

        /// <summary>Particles per second while <see cref="Emitting"/>. 0 for bursts only.</summary>
        public float Rate { get; set; }

        /// <summary>Whether <see cref="Rate"/> is emitting. Bursts work either way.</summary>
        public bool Emitting { get; set; } = true;

        /// <summary>The most alive at once.</summary>
        public int MaxParticles { get; set; } = 500;

        /// <summary>Seconds each particle lives.</summary>
        public FloatRange Lifetime { get; set; } = new(0.5f, 1f);

        /// <summary>Metres per second each one starts at.</summary>
        public FloatRange Speed { get; set; } = new(1f, 2f);

        /// <summary>Width in metres at the start.</summary>
        public FloatRange Size { get; set; } = 0.1f;

        /// <summary>How big it ends compared to how it starts: 0 shrinks away, 2 grows to twice the size.</summary>
        public float EndSize { get; set; } = 1f;

        /// <summary>The colour each starts with and fades to over its lifetime, alpha included. Fade out with a 0 alpha end.</summary>
        public Color StartColor { get; set; } = Color.White;
        public Color EndColor { get; set; } = new(1f, 1f, 1f, 0f);

        /// <summary>
        /// How much they glow on top of their colour, like a material's emission: 0 doesn't bloom at all, and 3 or more
        /// is bright and hot. See <see cref="Lighting.Bloom"/>.
        /// </summary>
        public float Glow { get; set; }

        public ParticleBlend Blend { get; set; } = ParticleBlend.Additive;

        /// <summary>Which way particles head, in the emitter's own space. They spread around it by <see cref="Spread"/>.</summary>
        public Vector3 Direction { get; set; } = Vector3.UnitY;

        /// <summary>
        /// Degrees either side of <see cref="Direction"/> particles can head: 0 is a straight line, 90 a half sphere,
        /// 180 every way.
        /// </summary>
        public float Spread { get; set; } = 30f;

        /// <summary>The box, in the emitter's own space, that particles start anywhere in. Zero starts them all at its centre.</summary>
        public Vector3 Area { get; set; }

        /// <summary>Downwards pull in metres per second squared: 9.8 falls like a real thing, negative rises like smoke.</summary>
        public float Gravity { get; set; }

        /// <summary>How quickly they slow down: the fraction of their speed lost each second, more or less. 0 never slows.</summary>
        public float Drag { get; set; }

        /// <summary>
        /// Stretches each one along the way it's moving, by how far it travels in this many seconds, so fast sparks
        /// streak. 0 keeps them round.
        /// </summary>
        public float Stretch { get; set; }

        /// <summary>Destroys the GameObject once it's no longer emitting and its last particle is gone. For one-off effects.</summary>
        public bool DestroyWhenFinished { get; set; }

        /// <summary>Emits <paramref name="count"/> particles at once, from the emitter's area and in its direction.</summary>
        public void Emit(int count)
        {
            Matrix4x4 world = Transform.WorldMatrix;
            EmitFrom(count, world, Vector3.TransformNormal(Direction, world));
        }

        /// <summary>
        /// Emits <paramref name="count"/> particles at once from somewhere else: an <see cref="Area"/>-sized box (lined up
        /// with the world) around <paramref name="position"/>, heading around <paramref name="direction"/>. One emitter can
        /// then make the same effect anywhere, like sparks wherever bullets hit.
        /// </summary>
        public void Emit(int count, Vector3 position, Vector3 direction) => EmitFrom(count, Matrix4x4.CreateTranslation(position), direction);

        /// <summary>Removes every particle at once.</summary>
        public abstract void Clear();

        /// <summary>
        /// Emits <paramref name="count"/> particles from an <see cref="Area"/> box placed by <paramref name="world"/>, heading
        /// around <paramref name="worldDirection"/> (any length; zero means up).
        /// </summary>
        private protected abstract void EmitFrom(int count, Matrix4x4 world, Vector3 worldDirection);

        /// <summary>How many particles <see cref="Rate"/> wants emitted over <paramref name="deltaTime"/> seconds.</summary>
        private protected int RateEmission(float deltaTime)
        {
            if (!Emitting || Rate <= 0f) return 0;

            _toEmit += Rate * deltaTime;
            int whole = (int)_toEmit;
            _toEmit -= whole;
            return whole;
        }

        /// <summary>The unit direction to spread around, from any direction; straight up if it's zero.</summary>
        private protected static Vector3 Axis(Vector3 direction) =>
            direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.UnitY;

        /// <summary>
        /// A direction within <paramref name="spread"/> degrees of <paramref name="axis"/>, evenly over that part of a
        /// sphere, from two random numbers 0 to 1. The GPU particle shader works it out exactly the same way.
        /// </summary>
        internal static Vector3 SpreadDirection(Vector3 axis, float spread, float random1, float random2)
        {
            float maxAngle = MathHelper.ToRadians(Math.Clamp(spread, 0f, 180f));
            float cosAngle = 1f - random1 * (1f - MathF.Cos(maxAngle));
            float sinAngle = MathF.Sqrt(MathF.Max(1f - cosAngle * cosAngle, 0f));
            float around = random2 * MathF.Tau;

            Vector3 side = MathF.Abs(axis.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY)) : Vector3.UnitX;
            Vector3 up = Vector3.Cross(side, axis);
            return axis * cosAngle + (side * MathF.Cos(around) + up * MathF.Sin(around)) * sinAngle;
        }
    }
}
