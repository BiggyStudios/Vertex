using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Particles simulated on the graphics card: emitting, moving and drawing thousands costs the CPU next to nothing,
    /// because it only ever sends where and how many to emit. Every GPU particle system in the scene shares one pool,
    /// moved by one compute pass and drawn in one draw call.
    /// <para>
    /// Unlike <see cref="ParticleSystem"/>, alpha-blended ones aren't sorted back to front, so they suit sparks, embers,
    /// muzzle flashes and other additive effects best, and thin dust or haze where the order hardly shows. Past
    /// <see cref="ParticleEmitter.MaxParticles"/>, new particles replace the oldest. On graphics APIs without compute
    /// shaders (OpenGL 3.3, WebGL) the engine runs the same simulation on the CPU instead, so effects look the same
    /// everywhere. See <see cref="ParticleEmitter"/> for the settings.
    /// </para>
    /// </summary>
    public class GpuParticleSystem : ParticleEmitter
    {
        private readonly List<GpuParticleEmission> _pending = new();
        private float _lastEmitTime = float.NegativeInfinity;
        private float _clearedTime = float.NegativeInfinity;

        /// <summary>True from the frame something is emitted until the last particle could have faded out.</summary>
        public bool IsAlive => Time.TotalTime < _lastEmitTime + Lifetime.Max;

        /// <summary>Set when it's destroyed, so the renderer frees its share of the pool.</summary>
        internal bool Released { get; private set; }

        /// <summary>Bumped by <see cref="Clear"/>, so the renderer knows to kill what's in its share of the pool.</summary>
        internal int ClearCount { get; private set; }

        public override void Clear()
        {
            _pending.Clear();
            ClearCount++;
            _clearedTime = Time.TotalTime;
            _lastEmitTime = float.NegativeInfinity;
        }

        private protected override void EmitFrom(int count, Matrix4x4 world, Vector3 worldDirection)
        {
            if (count <= 0) return;

            _pending.Add(new GpuParticleEmission
            {
                World = world,
                Axis = Axis(worldDirection),
                Spread = Spread,
                Area = Area,
                Speed = Speed,
                Lifetime = new FloatRange(MathF.Max(Lifetime.Min, 0.01f), MathF.Max(Lifetime.Max, 0.01f)),
                Size = Size,
                Count = count,
                Seed = (uint)Random.Shared.Next() ^ ((uint)Random.Shared.Next() << 16)
            });

            _lastEmitTime = Time.TotalTime;
        }

        protected internal override void Update()
        {
            int emit = RateEmission(Time.DeltaTime);
            if (emit > 0) Emit(emit);

            if (DestroyWhenFinished && !IsAlive && _pending.Count == 0 && (!Emitting || Rate <= 0f) && _lastEmitTime > _clearedTime)
                GameObject.Destroy();
        }

        protected internal override void OnDestroy()
        {
            Released = true;
            _pending.Clear();
        }

        /// <summary>The emissions since the renderer last took them, which it then sends to be simulated.</summary>
        internal List<GpuParticleEmission> PendingEmissions => _pending;

        /// <summary>This system's settings as the simulation and drawing need them.</summary>
        internal GpuParticleSystemData Data(bool visible) => new(StartColor, EndColor, EndSize, Glow, Stretch, Gravity, Drag,
            Blend == ParticleBlend.Additive, visible);
    }
}
