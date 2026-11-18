using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Particles simulated on the CPU. Alpha-blended ones are sorted back to front with every other CPU particle, so
    /// dust and smoke layer properly, and <see cref="Count"/> says how many are alive. For large numbers, or effects
    /// that don't need sorting (sparks, embers, anything additive), <see cref="GpuParticleSystem"/> is far cheaper.
    /// See <see cref="ParticleEmitter"/> for the settings they share; these ones are its own: where particles start
    /// (<see cref="Shape"/>), how they turn, the shape they're drawn as, swirling, and playing in the emitter's space.
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
            public float Rotation;
            public float Spin;
            public float Seed;
        }

        private Particle[] _particles = new Particle[16];
        private int _count;
        private bool _prewarmed;

        /// <summary>How many are alive right now.</summary>
        public int Count => _count;

        public override int? Alive => _count;

        public override bool Finished => !Playing && _count == 0;

        /// <summary>Where particles start: in a box (<see cref="ParticleEmitter.Area"/>), a ball or a flat circle.</summary>
        [Header("Shape")]
        public EmitterShape Shape { get; set; } = EmitterShape.Box;

        /// <summary>How big the ball or circle they start in is, in metres.</summary>
        public float Radius { get; set; } = 0.5f;

        /// <summary>Start on the shape's edge only (a ball's surface, a circle's rim, a box's sides), not anywhere in it.</summary>
        public bool FromEdge { get; set; }

        /// <summary>
        /// Head away from the shape's centre (an explosion, a ring spreading out) instead of along
        /// <see cref="ParticleEmitter.Direction"/>.
        /// </summary>
        public bool Outward { get; set; }

        /// <summary>The shape each particle is drawn as.</summary>
        [Header("Particles")]
        public ParticleLook Look { get; set; } = ParticleLook.Soft;

        /// <summary>Degrees each one starts turned by. Turning shows on shapes that aren't round, and on stretched ones not at all.</summary>
        public FloatRange Rotation { get; set; } = 0f;

        /// <summary>Degrees a second each one keeps turning by: negative turns the other way.</summary>
        public FloatRange Spin { get; set; } = 0f;

        /// <summary>The fraction of its life over which each one fades in, from 0 (it starts fully there) to 1. Softens smoke.</summary>
        [Range(0f, 1f)]
        public float FadeIn { get; set; }

        /// <summary>How strongly particles swirl about, in metres per second squared: smoke curling, embers drifting. 0 is still air.</summary>
        [Header("Movement")]
        public float Turbulence { get; set; }

        /// <summary>How big the swirls are, in metres: small for fine flutter, large for slow billows.</summary>
        public float TurbulenceScale { get; set; } = 1f;

        /// <summary>
        /// Particles move with the object, rather than staying where they were let go in the world: a flame on a moving
        /// torch's tip, a shield's sparkle.
        /// </summary>
        public bool LocalSpace { get; set; }

        /// <summary>Starts as if it had been going for a while already, so a steady stream is already full on the first frame.</summary>
        public bool Prewarm { get; set; }

        // Where the emitter was last frame, for moving local particles with it.
        private Matrix4x4 _lastWorld;
        private bool _hasLastWorld;

        /// <summary>New particles aren't made past <see cref="ParticleEmitter.MaxParticles"/>.</summary>
        private protected override void EmitFrom(int count, Matrix4x4 world, Vector3 worldDirection)
        {
            Vector3 axis = Axis(worldDirection);
            Vector3 centre = world.Translation;

            for (int i = 0; i < count && _count < MaxParticles; i++)
            {
                if (_count == _particles.Length) Array.Resize(ref _particles, Math.Min(_particles.Length * 2, Math.Max(MaxParticles, 16)));

                Vector3 position = Vector3.Transform(StartPoint(), world);
                Vector3 heading = Outward && Vector3.DistanceSquared(position, centre) > 1e-8f
                    ? Vector3.Normalize(position - centre)
                    : SpreadDirection(axis, Spread, Random.Shared.NextSingle(), Random.Shared.NextSingle());

                _particles[_count++] = new Particle
                {
                    Position = position,
                    Velocity = heading * Speed.Random(),
                    Lifetime = MathF.Max(Lifetime.Random(), 0.01f),
                    Size = Size.Random(),
                    Rotation = Rotation.Random() * (MathF.PI / 180f),
                    Spin = Spin.Random() * (MathF.PI / 180f),
                    Seed = Random.Shared.NextSingle() * 100f
                };
            }
        }

        /// <summary>Where a new particle starts, in the emitter's own space.</summary>
        private Vector3 StartPoint()
        {
            switch (Shape)
            {
                case EmitterShape.Sphere:
                {
                    Vector3 direction = SpreadDirection(Vector3.UnitY, 180f, Random.Shared.NextSingle(), Random.Shared.NextSingle());
                    float distance = FromEdge ? Radius : Radius * MathF.Cbrt(Random.Shared.NextSingle());
                    return direction * distance;
                }

                case EmitterShape.Circle:
                {
                    // On the plane across Direction.
                    Vector3 axis = Axis(Direction);
                    Vector3 side = MathF.Abs(axis.Y) < 0.99f ? Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY)) : Vector3.UnitX;
                    Vector3 up = Vector3.Cross(side, axis);
                    float angle = Random.Shared.NextSingle() * MathF.Tau;
                    float distance = FromEdge ? Radius : Radius * MathF.Sqrt(Random.Shared.NextSingle());
                    return (side * MathF.Cos(angle) + up * MathF.Sin(angle)) * distance;
                }

                default:
                {
                    var point = new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f);
                    if (FromEdge)
                    {
                        // Pushed out to the nearest face.
                        int face = Random.Shared.Next(3);
                        float side = Random.Shared.Next(2) == 0 ? -0.5f : 0.5f;
                        if (face == 0) point.X = side;
                        else if (face == 1) point.Y = side;
                        else point.Z = side;
                    }

                    return point * Area;
                }
            }
        }

        public override void Clear() => _count = 0;

        protected internal override void Update()
        {
            Simulate(Time.DeltaTime);
            if (DestroyWhenFinished && _count == 0 && !Playing) GameObject.Destroy();
        }

        public override void Simulate(float deltaTime)
        {
            MoveWithEmitter();

            if (Prewarm && !_prewarmed && Emitting)
            {
                // A lifetime's worth, in steps, before the first frame.
                _prewarmed = true;
                float warm = MathF.Max(Lifetime.Max, 0.01f);
                const float step = 1f / 30f;
                for (float t = 0f; t < warm; t += step) Step(step);
            }

            _prewarmed = true;
            Step(deltaTime);
        }

        private void Step(float deltaTime)
        {
            Tick(deltaTime);

            float slow = Drag > 0f ? MathF.Exp(-Drag * deltaTime) : 1f;
            var fall = new Vector3(0f, -Gravity * deltaTime, 0f);
            float swirl = Turbulence * deltaTime;
            float scale = 1f / MathF.Max(TurbulenceScale, 0.01f);
            float clock = PlayTime;

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

                if (swirl != 0f) particle.Velocity += Swirl(particle.Position * scale, particle.Seed + clock * 0.4f) * swirl;
                particle.Velocity = (particle.Velocity + fall) * slow;
                particle.Position += particle.Velocity * deltaTime;
                particle.Rotation += particle.Spin * deltaTime;
            }
        }

        /// <summary>Particles in the emitter's space are carried along by however it's moved since last frame.</summary>
        private void MoveWithEmitter()
        {
            Matrix4x4 world = Transform.WorldMatrix;
            if (LocalSpace && _hasLastWorld && world != _lastWorld && Matrix4x4.Invert(_lastWorld, out Matrix4x4 undo))
            {
                Matrix4x4 moved = undo * world;
                for (int i = 0; i < _count; i++)
                {
                    ref Particle particle = ref _particles[i];
                    particle.Position = Vector3.Transform(particle.Position, moved);
                    particle.Velocity = Vector3.TransformNormal(particle.Velocity, moved);
                }
            }

            _lastWorld = world;
            _hasLastWorld = true;
        }

        /// <summary>
        /// A smoothly changing push, different from place to place: sums of waves at odd angles, cheap and without
        /// visible repeats at the scales particles move.
        /// </summary>
        private static Vector3 Swirl(Vector3 p, float t) => new(
            MathF.Sin(p.Y * 1.7f + t) + MathF.Sin(p.Z * 2.3f - t * 0.7f) * 0.5f,
            MathF.Sin(p.Z * 1.9f + t * 1.3f) + MathF.Sin(p.X * 2.1f + t * 0.5f) * 0.5f,
            MathF.Sin(p.X * 1.5f - t * 0.9f) + MathF.Sin(p.Y * 2.7f + t * 1.1f) * 0.5f);

        /// <summary>Adds what's alive to the frame, for <see cref="Renderer"/>.</summary>
        internal void Collect(List<ParticleData> particles)
        {
            bool additive = Blend == ParticleBlend.Additive;
            float fadeIn = Math.Clamp(FadeIn, 0f, 1f);

            for (int i = 0; i < _count; i++)
            {
                ref Particle particle = ref _particles[i];
                float t = particle.Age / particle.Lifetime;
                float size = particle.Size * (1f + (EndSize - 1f) * t);
                Color color = Color.Lerp(StartColor, EndColor, t);
                if (fadeIn > 0f && t < fadeIn) color.A *= t / fadeIn;
                if (size <= 0f || color.A <= 0.002f) continue;

                particles.Add(new ParticleData(particle.Position, particle.Velocity * Stretch, size, color, Glow, additive, particle.Rotation, Look));
            }
        }
    }
}
