using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>One <see cref="GpuParticleSystem"/>'s settings, as the simulation and drawing need them.</summary>
    public readonly record struct GpuParticleSystemData(Color StartColor, Color EndColor, float EndSize, float Glow, float Stretch,
        float Gravity, float Drag, bool Additive, bool Visible);

    /// <summary>
    /// A burst of new particles for the GPU to make. The CPU never makes the particles themselves: each one's start is
    /// worked out from <see cref="Seed"/> and its number in the burst, the same way on the GPU and in the CPU fallback.
    /// </summary>
    public struct GpuParticleEmission
    {
        /// <summary>Places the <see cref="Area"/> box they start in.</summary>
        public Matrix4x4 World;

        /// <summary>Unit direction, in the world, they head around.</summary>
        public Vector3 Axis;

        /// <summary>Degrees either side of <see cref="Axis"/>.</summary>
        public float Spread;

        public Vector3 Area;
        public FloatRange Speed;
        public FloatRange Lifetime;
        public FloatRange Size;

        /// <summary>How many to make. Filled in by the renderer: at most the system's share of the pool.</summary>
        public int Count;

        public uint Seed;

        /// <summary>Filled in by the renderer: which system they belong to.</summary>
        public int System;

        /// <summary>Filled in by the renderer: the system's share of the pool, and where in it the first new one goes (wrapping round).</summary>
        public int RangeOffset;
        public int RangeSize;
        public int First;
    }

    /// <summary>
    /// What changed in the GPU particle pool since last frame, handed to <see cref="IRenderBackend.UpdateParticles"/> once
    /// a frame. The pool is one array of <see cref="PoolSize"/> particles; each system owns a range of it.
    /// </summary>
    public sealed class GpuParticleUpdate
    {
        internal readonly List<GpuParticleSystemData> SystemList = new();
        internal readonly List<GpuParticleEmission> EmissionList = new();
        internal readonly List<(int Offset, int Count)> ClearList = new();

        /// <summary>Seconds to move every particle on by.</summary>
        public float DeltaTime { get; internal set; }

        /// <summary>How many particles the pool must hold. It only ever grows; keep what's there when it does.</summary>
        public int PoolSize { get; internal set; }

        /// <summary>Every system's settings, by the number particles carry to say which they belong to.</summary>
        public IReadOnlyList<GpuParticleSystemData> Systems => SystemList;

        /// <summary>New particles to make, after moving the ones already there.</summary>
        public IReadOnlyList<GpuParticleEmission> Emissions => EmissionList;

        /// <summary>Ranges of the pool whose particles should vanish (a system cleared, or gone), before anything else.</summary>
        public IReadOnlyList<(int Offset, int Count)> Clears => ClearList;

        internal void Reset()
        {
            SystemList.Clear();
            EmissionList.Clear();
            ClearList.Clear();
        }
    }

    /// <summary>
    /// The random numbers particles start with: a PCG hash, the same here as in the GPU's shader, so the CPU fallback
    /// makes the same particles the GPU would.
    /// </summary>
    internal static class ParticleRandom
    {
        public static uint Hash(uint value)
        {
            unchecked
            {
                uint state = value * 747796405u + 2891336453u;
                uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
                return (word >> 22) ^ word;
            }
        }

        /// <summary>The state to draw particle <paramref name="index"/>'s numbers from.</summary>
        public static uint Start(uint seed, uint index) => Hash(seed ^ unchecked(index * 0x9E3779B9u));

        /// <summary>The next number from 0 up to 1.</summary>
        public static float Next(ref uint state)
        {
            state = Hash(state);
            return (state >> 8) / 16777216f;
        }
    }

    /// <summary>
    /// Shares the GPU particle pool out between the scene's <see cref="GpuParticleSystem"/>s and gathers what each frame
    /// changes into a <see cref="GpuParticleUpdate"/>.
    /// </summary>
    internal sealed class GpuParticleManager
    {
        private sealed class Registration
        {
            public int Slot;
            public int Offset;
            public int Size;
            public int Next;
            public int ClearCount;
            public bool Seen;
        }

        private readonly Dictionary<GpuParticleSystem, Registration> _systems = new();
        private readonly List<GpuParticleSystem> _gone = new();
        private readonly Stack<int> _freeSlots = new();
        private readonly List<(int Offset, int Size)> _freeRanges = new();
        private readonly List<GpuParticleSystemData> _slotData = new();
        private int _poolEnd;
        private float _lastTime = float.NaN;

        public GpuParticleUpdate Update { get; } = new();

        /// <summary>How many systems hold a share of the pool.</summary>
        public int SystemCount => _systems.Count;

        /// <summary>Gathers this frame's changes from <paramref name="scene"/>'s systems into <see cref="Update"/>.</summary>
        public GpuParticleUpdate Gather(Scene scene, float totalTime)
        {
            GpuParticleUpdate update = Update;
            update.Reset();
            update.DeltaTime = float.IsNaN(_lastTime) ? 0f : Math.Clamp(totalTime - _lastTime, 0f, 0.25f);
            _lastTime = totalTime;

            foreach (Registration registration in _systems.Values) registration.Seen = false;

            foreach (GpuParticleSystem system in scene.FindComponents<GpuParticleSystem>())
            {
                if (system.Released) continue;

                Registration registration = Register(system);
                registration.Seen = true;
                _slotData[registration.Slot] = system.Data(visible: true);

                if (registration.ClearCount != system.ClearCount)
                {
                    registration.ClearCount = system.ClearCount;
                    update.ClearList.Add((registration.Offset, registration.Size));
                }

                foreach (GpuParticleEmission pending in system.PendingEmissions)
                {
                    GpuParticleEmission emission = pending;
                    emission.Count = Math.Min(emission.Count, registration.Size);
                    emission.System = registration.Slot;
                    emission.RangeOffset = registration.Offset;
                    emission.RangeSize = registration.Size;
                    emission.First = registration.Next;
                    registration.Next = (registration.Next + emission.Count) % registration.Size;
                    update.EmissionList.Add(emission);
                }

                system.PendingEmissions.Clear();
            }

            // Systems not in the scene this frame (disabled, inactive, another scene) keep their particles but aren't
            // drawn; ones that are gone for good give their share back.
            foreach ((GpuParticleSystem system, Registration registration) in _systems)
            {
                if (system.Released) _gone.Add(system);
                else if (!registration.Seen) _slotData[registration.Slot] = _slotData[registration.Slot] with { Visible = false };
            }

            foreach (GpuParticleSystem system in _gone) Release(system, update);
            _gone.Clear();

            update.SystemList.AddRange(_slotData);
            update.PoolSize = _poolEnd;
            return update;
        }

        private Registration Register(GpuParticleSystem system)
        {
            int size = Math.Max(system.MaxParticles, 1);
            if (_systems.TryGetValue(system, out Registration? registration))
            {
                if (registration.Size == size) return registration;

                // Its MaxParticles changed: move it to a share of the new size.
                Release(system, Update);
            }

            int slot;
            if (_freeSlots.Count > 0)
            {
                slot = _freeSlots.Pop();
            }
            else
            {
                slot = _slotData.Count;
                _slotData.Add(default);
            }

            registration = new Registration { Slot = slot, Offset = Allocate(size), Size = size, ClearCount = system.ClearCount };
            _systems.Add(system, registration);
            return registration;
        }

        private void Release(GpuParticleSystem system, GpuParticleUpdate update)
        {
            Registration registration = _systems[system];
            _systems.Remove(system);

            // Its particles must go, or whatever gets the range next would show them.
            update.ClearList.Add((registration.Offset, registration.Size));
            Free(registration.Offset, registration.Size);
            _slotData[registration.Slot] = default;
            _freeSlots.Push(registration.Slot);
        }

        // First fit from the free ranges, else from the end of the pool.
        private int Allocate(int size)
        {
            for (int i = 0; i < _freeRanges.Count; i++)
            {
                (int offset, int free) = _freeRanges[i];
                if (free < size) continue;

                if (free == size) _freeRanges.RemoveAt(i);
                else _freeRanges[i] = (offset + size, free - size);
                return offset;
            }

            int start = _poolEnd;
            _poolEnd += size;
            return start;
        }

        // Back into the free ranges, joined to any it touches, so the pool doesn't break up into slivers.
        private void Free(int offset, int size)
        {
            int index = 0;
            while (index < _freeRanges.Count && _freeRanges[index].Offset < offset) index++;
            _freeRanges.Insert(index, (offset, size));

            if (index + 1 < _freeRanges.Count && _freeRanges[index].Offset + _freeRanges[index].Size == _freeRanges[index + 1].Offset)
            {
                _freeRanges[index] = (_freeRanges[index].Offset, _freeRanges[index].Size + _freeRanges[index + 1].Size);
                _freeRanges.RemoveAt(index + 1);
            }

            if (index > 0 && _freeRanges[index - 1].Offset + _freeRanges[index - 1].Size == _freeRanges[index].Offset)
            {
                _freeRanges[index - 1] = (_freeRanges[index - 1].Offset, _freeRanges[index - 1].Size + _freeRanges[index].Size);
                _freeRanges.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// The GPU particle simulation, run on the CPU for backends without compute shaders. It does exactly what the
    /// compute shaders do, step for step, then hands what's alive to the frame as ordinary particles.
    /// </summary>
    internal sealed class CpuParticlePool
    {
        private struct Particle
        {
            public Vector3 Position;
            public float Age;
            public Vector3 Velocity;
            public float Lifetime; // 0 for none
            public float Size;
            public int System;
        }

        private Particle[] _particles = [];
        private readonly List<GpuParticleSystemData> _systems = new();

        public int AliveCount { get; private set; }

        public void Apply(GpuParticleUpdate update)
        {
            if (update.PoolSize > _particles.Length) Array.Resize(ref _particles, update.PoolSize);

            _systems.Clear();
            _systems.AddRange(update.Systems);

            foreach ((int offset, int count) in update.Clears) Array.Clear(_particles, offset, count);

            float deltaTime = update.DeltaTime;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle particle = ref _particles[i];
                if (particle.Lifetime <= 0f) continue;

                particle.Age += deltaTime;
                if (particle.Age >= particle.Lifetime)
                {
                    particle.Lifetime = 0f;
                    continue;
                }

                GpuParticleSystemData system = _systems[particle.System];
                float slow = system.Drag > 0f ? MathF.Exp(-system.Drag * deltaTime) : 1f;
                particle.Velocity = (particle.Velocity + new Vector3(0f, -system.Gravity * deltaTime, 0f)) * slow;
                particle.Position += particle.Velocity * deltaTime;
            }

            foreach (GpuParticleEmission emission in update.Emissions)
            {
                for (int i = 0; i < emission.Count; i++)
                {
                    int slot = emission.RangeOffset + (emission.First + i) % emission.RangeSize;
                    _particles[slot] = Spawn(emission, (uint)i);
                }
            }
        }

        private static Particle Spawn(in GpuParticleEmission emission, uint index)
        {
            uint random = ParticleRandom.Start(emission.Seed, index);
            var local = new Vector3(ParticleRandom.Next(ref random) - 0.5f, ParticleRandom.Next(ref random) - 0.5f,
                ParticleRandom.Next(ref random) - 0.5f) * emission.Area;
            Vector3 direction = ParticleEmitter.SpreadDirection(emission.Axis, emission.Spread, ParticleRandom.Next(ref random),
                ParticleRandom.Next(ref random));

            return new Particle
            {
                Position = Vector3.Transform(local, emission.World),
                Velocity = direction * emission.Speed.Lerp(ParticleRandom.Next(ref random)),
                Lifetime = emission.Lifetime.Lerp(ParticleRandom.Next(ref random)),
                Size = emission.Size.Lerp(ParticleRandom.Next(ref random)),
                System = emission.System
            };
        }

        /// <summary>Adds the visible particles to <paramref name="particles"/>, the way the GPU would draw them.</summary>
        public void Collect(List<ParticleData> particles)
        {
            int alive = 0;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref Particle particle = ref _particles[i];
                if (particle.Lifetime <= 0f) continue;

                alive++;
                GpuParticleSystemData system = _systems[particle.System];
                if (!system.Visible) continue;

                float t = particle.Age / particle.Lifetime;
                float size = particle.Size * (1f + (system.EndSize - 1f) * t);
                Color color = Color.Lerp(system.StartColor, system.EndColor, t);
                if (size <= 0f || color.A <= 0.002f) continue;

                particles.Add(new ParticleData(particle.Position, particle.Velocity * system.Stretch, size, color, system.Glow, system.Additive));
            }

            AliveCount = alive;
        }
    }
}
