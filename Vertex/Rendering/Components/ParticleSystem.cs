using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Particles simulated on the CPU. Alpha-blended ones are sorted back to front with every other CPU particle, so
    /// dust and smoke layer properly, and <see cref="Count"/> says how many are alive. For large numbers, or effects
    /// that don't need sorting (sparks, embers, anything additive), <see cref="GpuParticleSystem"/> is far cheaper.
    /// See <see cref="ParticleEmitter"/> for the settings.
    /// </summary>
    public class ParticleSystem : ParticleEmitter
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

        /// <summary>How many are alive right now.</summary>
        public int Count => _count;

        /// <summary>New particles aren't made past <see cref="ParticleEmitter.MaxParticles"/>.</summary>
        private protected override void EmitFrom(int count, Matrix4x4 world, Vector3 worldDirection)
        {
            Vector3 axis = Axis(worldDirection);

            for (int i = 0; i < count && _count < MaxParticles; i++)
            {
                if (_count == _particles.Length) Array.Resize(ref _particles, Math.Min(_particles.Length * 2, Math.Max(MaxParticles, 16)));

                Vector3 local = new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f) * Area;
                _particles[_count++] = new Particle
                {
                    Position = Vector3.Transform(local, world),
                    Velocity = SpreadDirection(axis, Spread, Random.Shared.NextSingle(), Random.Shared.NextSingle()) * Speed.Random(),
                    Lifetime = MathF.Max(Lifetime.Random(), 0.01f),
                    Size = Size.Random()
                };
            }
        }

        public override void Clear() => _count = 0;

        protected internal override void Update()
        {
            float deltaTime = Time.DeltaTime;

            int emit = RateEmission(deltaTime);
            if (emit > 0) Emit(emit);

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
    }
}
