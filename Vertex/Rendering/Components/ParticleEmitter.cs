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

    /// <summary>The shape each particle's drawn as.</summary>
    public enum ParticleLook
    {
        /// <summary>A soft round dot, brightest in the middle: smoke, dust, glows.</summary>
        Soft,

        /// <summary>A round dot with a crisp edge: bubbles, confetti, stylised effects.</summary>
        Disc,

        Square,

        /// <summary>A circle's outline: shockwaves, ripples.</summary>
        Ring,

        /// <summary>A bright point with four rays: twinkles, magic, glints.</summary>
        Spark
    }

    /// <summary>Where a <see cref="ParticleSystem"/>'s particles start.</summary>
    public enum EmitterShape
    {
        /// <summary>Anywhere in a box the size of <see cref="ParticleEmitter.Area"/> (a point, if that's zero).</summary>
        Box,

        /// <summary>Anywhere in a ball of <see cref="ParticleSystem.Radius"/>.</summary>
        Sphere,

        /// <summary>Anywhere on a flat disc of <see cref="ParticleSystem.Radius"/>, facing <see cref="ParticleEmitter.Direction"/>.</summary>
        Circle
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
    public abstract class ParticleEmitter : Component, IParticleEffect
    {
        private float _toEmit;

        /// <summary>Particles per second while <see cref="Emitting"/>. 0 for bursts only.</summary>
        [Header("Emission")]
        public float Rate { get; set; }

        /// <summary>Whether <see cref="Rate"/> is emitting. Bursts work either way.</summary>
        public bool Emitting { get; set; } = true;

        /// <summary>Particles emitted at once each time it starts playing (and each time it loops): an explosion's burst.</summary>
        public int Burst { get; set; }

        /// <summary>
        /// Seconds it plays for each time it starts: <see cref="Rate"/> emits for this long, then it stops, or starts again
        /// with <see cref="Loop"/>. 0 plays for as long as it's on.
        /// </summary>
        public float Duration { get; set; }

        /// <summary>Starts again (with its <see cref="Burst"/>) each time <see cref="Duration"/> is up.</summary>
        public bool Loop { get; set; } = true;

        /// <summary>The most alive at once.</summary>
        public int MaxParticles { get; set; } = 500;

        /// <summary>Destroys the GameObject once it's no longer emitting and its last particle is gone. For one-off effects.</summary>
        public bool DestroyWhenFinished { get; set; }

        /// <summary>Which way particles head, in the emitter's own space. They spread around it by <see cref="Spread"/>.</summary>
        [Header("Shape")]
        public Vector3 Direction { get; set; } = Vector3.UnitY;

        /// <summary>
        /// Degrees either side of <see cref="Direction"/> particles can head: 0 is a straight line, 90 a half sphere,
        /// 180 every way.
        /// </summary>
        public float Spread { get; set; } = 30f;

        /// <summary>The box, in the emitter's own space, that particles start anywhere in. Zero starts them all at its centre.</summary>
        public Vector3 Area { get; set; }

        /// <summary>Seconds each particle lives.</summary>
        [Header("Particles")]
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

        /// <summary>
        /// Stretches each one along the way it's moving, by how far it travels in this many seconds, so fast sparks
        /// streak. 0 keeps them round.
        /// </summary>
        public float Stretch { get; set; }

        /// <summary>Downwards pull in metres per second squared: 9.8 falls like a real thing, negative rises like smoke.</summary>
        [Header("Movement")]
        public float Gravity { get; set; }

        /// <summary>How quickly they slow down: the fraction of their speed lost each second, more or less. 0 never slows.</summary>
        public float Drag { get; set; }

        private float _playTime;
        private bool _started;

        /// <summary>Seconds since it started playing (since it last looped, with a <see cref="Duration"/>).</summary>
        public float PlayTime => _playTime;

        /// <summary>
        /// Whether it'll make more particles by itself: it's <see cref="Emitting"/> with a <see cref="Rate"/>, or will
        /// burst again, and its <see cref="Duration"/> isn't up.
        /// </summary>
        public bool Playing => Emitting && (Rate > 0f || Burst > 0 && Duration > 0f && Loop || !_started && Burst > 0) &&
                               (Duration <= 0f || Loop || _playTime < Duration);

        /// <summary>How many particles are alive, if it can tell.</summary>
        public virtual int? Alive => null;

        /// <summary>Whether it won't make any more particles by itself, and those it made are gone.</summary>
        public virtual bool Finished => !Playing;

        bool IParticleEffect.PlaysByItself => Emitting;

        /// <summary>Starts playing from the beginning (with its <see cref="Burst"/>), keeping particles already out.</summary>
        public void Play()
        {
            Emitting = true;
            _started = false;
            _playTime = 0f;
            _toEmit = 0f;
        }

        /// <summary>Stops making particles. Those already out live out their lives.</summary>
        public void Stop() => Emitting = false;

        /// <summary>Takes its particles away and goes back to the start, without changing whether it's <see cref="Emitting"/>: for previewing it.</summary>
        public void Rewind()
        {
            Clear();
            _started = false;
            _playTime = 0f;
            _toEmit = 0f;
        }

        /// <summary>Takes away every particle and plays from the beginning.</summary>
        public void Restart()
        {
            Clear();
            Play();
        }

        /// <summary>
        /// Moves the effect on by <paramref name="deltaTime"/> seconds, as each frame of the game does: for showing it
        /// outside the game, as the editor does while it's selected. Never destroys anything.
        /// </summary>
        public abstract void Simulate(float deltaTime);

        /// <summary>
        /// Plays the effect on by <paramref name="deltaTime"/>: the <see cref="Burst"/> when it starts or loops, and what
        /// <see cref="Rate"/> wants emitted.
        /// </summary>
        private protected void Tick(float deltaTime)
        {
            if (!Emitting) return;

            if (!_started)
            {
                _started = true;
                _playTime = 0f;
                if (Burst > 0) Emit(Burst);
            }

            _playTime += deltaTime;
            if (Duration > 0f && _playTime >= Duration)
            {
                if (!Loop)
                {
                    _playTime = Duration;
                    return;
                }

                _playTime %= Duration;
                if (Burst > 0) Emit(Burst);
            }

            int emit = RateEmission(deltaTime);
            if (emit > 0) Emit(emit);
        }

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
