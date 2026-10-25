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
        public float Random() => Min + (Max - Min) * System.Random.Shared.NextSingle();

        /// <summary>Always the same value.</summary>
        public static implicit operator FloatRange(float value) => new(value, value);
    }

    /// <summary>
    /// Sprays soft, glowing dots: sparks, dust, embers. Each particle starts in the emitter's area, heads off within
    /// <see cref="Spread"/> degrees of <see cref="Direction"/>, then falls, slows and fades over its lifetime. They
    /// live in the world, so moving the emitter doesn't drag the ones already out. Emit a steady stream with
    /// <see cref="Rate"/>, or a burst at once with <see cref="Emit(int)"/>. Particles always face the camera.
    /// </summary>
    public class ParticleSystem : Component
    {
        private struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Age;
            public float Lifetime;
            public float Size;
        }

        private Particle[] _particles = new Particle[16];
        private int _count;
        private float _toEmit;

        /// <summary>Particles per second while <see cref="Emitting"/>. 0 for bursts only.</summary>
        public float Rate { get; set; }

        /// <summary>Whether <see cref="Rate"/> is emitting. Bursts work either way.</summary>
        public bool Emitting { get; set; } = true;

        /// <summary>The most alive at once; new ones aren't made past this.</summary>
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

        /// <summary>How many are alive right now.</summary>
        public int Count => _count;

        /// <summary>Emits <paramref name="count"/> particles at once, from the emitter's area and in its direction.</summary>
        public void Emit(int count)
        {
            Matrix4x4 world = Transform.WorldMatrix;
            Emit(count, world, Vector3.TransformNormal(Direction, world));
        }

        /// <summary>
        /// Emits <paramref name="count"/> particles at once from somewhere else: an <see cref="Area"/>-sized box (lined up
        /// with the world) around <paramref name="position"/>, heading around <paramref name="direction"/>. One emitter can
        /// then make the same effect anywhere, like sparks wherever bullets hit.
        /// </summary>
        public void Emit(int count, Vector3 position, Vector3 direction) => Emit(count, Matrix4x4.CreateTranslation(position), direction);

        private void Emit(int count, Matrix4x4 world, Vector3 worldDirection)
        {
            Vector3 axis = worldDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(worldDirection) : Vector3.UnitY;

            for (int i = 0; i < count && _count < MaxParticles; i++)
            {
                if (_count == _particles.Length) Array.Resize(ref _particles, Math.Min(_particles.Length * 2, Math.Max(MaxParticles, 16)));

                Vector3 local = new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f) * Area;
                _particles[_count++] = new Particle
                {
                    Position = Vector3.Transform(local, world),
                    Velocity = RandomDirection(axis, Spread) * Speed.Random(),
                    Lifetime = MathF.Max(Lifetime.Random(), 0.01f),
                    Size = Size.Random()
                };
            }
        }

        /// <summary>Removes every particle at once.</summary>
        public void Clear() => _count = 0;

        protected internal override void Update()
        {
            float deltaTime = Time.DeltaTime;

            if (Emitting && Rate > 0f)
            {
                _toEmit += Rate * deltaTime;
                int whole = (int)_toEmit;
                _toEmit -= whole;
                if (whole > 0) Emit(whole);
            }

            float slow = Drag > 0f ? MathF.Exp(-Drag * deltaTime) : 1f;
            var fall = new Vector3(0f, -Gravity * deltaTime, 0f);

            for (int i = 0; i < _count; i++)
            {
                ref Particle particle = ref _particles[i];
                particle.Age += deltaTime;
                if (particle.Age >= particle.Lifetime)
                {
                    // Order doesn't matter, so the last one fills the gap.
                    _particles[i--] = _particles[--_count];
                    continue;
                }

                particle.Velocity = (particle.Velocity + fall) * slow;
                particle.Position += particle.Velocity * deltaTime;
            }

            if (DestroyWhenFinished && _count == 0 && (!Emitting || Rate <= 0f)) GameObject.Destroy();
        }

        /// <summary>Adds what's alive to the frame, for <see cref="Renderer"/>.</summary>
        internal void Collect(List<ParticleData> particles)
        {
            bool additive = Blend == ParticleBlend.Additive;

            for (int i = 0; i < _count; i++)
            {
                ref Particle particle = ref _particles[i];
                float t = particle.Age / particle.Lifetime;
                float size = particle.Size * (1f + (EndSize - 1f) * t);
                Color color = Color.Lerp(StartColor, EndColor, t);
                if (size <= 0f || color.A <= 0.002f) continue;

                particles.Add(new ParticleData(particle.Position, particle.Velocity * Stretch, size, color, Glow, additive));
            }
        }

        /// <summary>A random direction within <paramref name="spread"/> degrees of <paramref name="axis"/>, evenly over that part of a sphere.</summary>
        private static Vector3 RandomDirection(Vector3 axis, float spread)
        {
            float maxAngle = MathHelper.ToRadians(Math.Clamp(spread, 0f, 180f));
            float cosAngle = 1f - Random.Shared.NextSingle() * (1f - MathF.Cos(maxAngle));
            float sinAngle = MathF.Sqrt(MathF.Max(1f - cosAngle * cosAngle, 0f));
            float around = Random.Shared.NextSingle() * MathF.Tau;

            Vector3 side = MathF.Abs(axis.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY)) : Vector3.UnitX;
            Vector3 up = Vector3.Cross(side, axis);
            return axis * cosAngle + (side * MathF.Cos(around) + up * MathF.Sin(around)) * sinAngle;
        }
    }
}
