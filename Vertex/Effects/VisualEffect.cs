using System.Numerics;

using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.Effects
{
    /// <summary>
    /// Plays a VFX graph (a .vfx file, made in the editor) on its object: particles designed with nodes rather than
    /// settings, so anything can change with anything (colour by age, size by speed, a swirl that grows over time). Each
    /// object playing the same graph has its own particles, and its own values for the graph's properties
    /// (<see cref="SetProperty"/>). Simulated on the CPU and drawn with the other particles.
    /// </summary>
    public sealed class VisualEffect : Component, IParticleEffect
    {
        /// <summary>The effect to play: a .vfx file.</summary>
        [Tooltip("The effect to play: a .vfx file, made in the editor")]
        [GraphKind(VfxNodes.Kind)]
        public Graph? Effect { get; set; }

        /// <summary>Starts playing when the game starts. Off, it waits for <see cref="Play"/>.</summary>
        public bool PlayOnStart { get; set; } = true;

        private sealed class SystemState
        {
            public VfxParticle[] Particles = new VfxParticle[16];
            public int Count;
            public float ToEmit;
            public float Time;
            public bool Started;
            public bool Done;
            public Matrix4x4 LastWorld;
            public bool HasLastWorld;
        }

        private readonly VfxFrame _frame = new();
        private readonly Dictionary<string, Vector4> _properties = new();
        private readonly List<SystemState> _states = new();
        private VfxProgram? _program;
        private Graph? _compiledGraph;
        private int _compiledVersion = -1;
        private bool _begun;
        private bool _playing;
        private float _playTime;

        /// <summary>How many particles are alive, across all the graph's particle systems.</summary>
        public int Count => _states.Sum(s => s.Count);

        int? IParticleEffect.Alive => Count;

        public float PlayTime => _playTime;

        /// <summary>Whether it's making particles (and will go on to): playing, and not past the end of a duration that doesn't loop.</summary>
        public bool Playing => _playing && (_program == null || _states.Count == 0 || _states.Any(s => !s.Done));

        public bool Finished => !Playing && Count == 0;

        bool IParticleEffect.PlaysByItself => PlayOnStart;

        /// <summary>Starts playing from the beginning (with its bursts), keeping particles already out.</summary>
        public void Play()
        {
            _begun = true;
            _playing = true;
            _playTime = 0f;
            foreach (SystemState state in _states)
            {
                state.Started = false;
                state.Done = false;
                state.ToEmit = 0f;
            }
        }

        /// <summary>Stops making particles. Those already out live out their lives.</summary>
        public void Stop() => _playing = false;

        /// <summary>Takes every particle away and plays from the beginning.</summary>
        public void Restart()
        {
            Clear();
            Play();
        }

        /// <summary>Takes every particle away at once.</summary>
        public void Clear()
        {
            foreach (SystemState state in _states) state.Count = 0;
        }

        public void Rewind()
        {
            Clear();
            _begun = false;
            _playing = false;
            _playTime = 0f;
            foreach (SystemState state in _states)
            {
                state.Started = false;
                state.Done = false;
                state.ToEmit = 0f;
                state.HasLastWorld = false;
            }
        }

        /// <summary>
        /// Sets one of the graph's properties (its variables) for this object: a number, true/false, vector or colour.
        /// Get Property nodes read it from then on.
        /// </summary>
        public void SetProperty(string name, object value)
        {
            PortType type = Effect?.Variable(name)?.Type ?? value switch
            {
                bool => PortType.Boolean,
                Vector3 => PortType.Vector,
                Color => PortType.Color,
                _ => PortType.Number
            };
            _properties[name] = VfxProgram.ToValue(type, value);
        }

        protected internal override void Start()
        {
            if (!_begun && PlayOnStart) Play();
        }

        protected internal override void Update() => Simulate(Time.DeltaTime);

        public void Simulate(float deltaTime)
        {
            if (!Ready()) return;
            if (!_begun)
            {
                _begun = true;
                _playing = PlayOnStart;
            }

            if (_playing) _playTime += deltaTime;
            _frame.Time = _playTime;
            _frame.DeltaTime = deltaTime;
            _frame.World = Transform.WorldMatrix;
            _frame.Properties.Clear();
            foreach ((string name, Vector4 value) in _properties) _frame.Properties[name] = value;

            for (int i = 0; i < _program!.Systems.Count; i++) Step(_program.Systems[i], _states[i], deltaTime);
        }

        /// <summary>Works the graph out again if it's new or has changed. False if there's nothing (or nothing right) to play.</summary>
        private bool Ready()
        {
            if (Effect == null || Effect.Kind != VfxNodes.Kind)
            {
                _program = null;
                return false;
            }

            if (_compiledGraph == Effect && _compiledVersion == Effect.Version) return _program != null;
            _compiledGraph = Effect;
            _compiledVersion = Effect.Version;

            try
            {
                _program = VfxProgram.Compile(Effect);
            }
            catch (VfxException exception)
            {
                _program = null;
                Effect.Failed(exception.Node.Id, exception.Message);
                Log.Error($"VFX graph {Effect.Name} on {GameObject.Name}: {exception.Message}");
                return false;
            }

            // Particles already out stay, as long as the systems line up.
            while (_states.Count < _program.Systems.Count) _states.Add(new SystemState());
            if (_states.Count > _program.Systems.Count) _states.RemoveRange(_program.Systems.Count, _states.Count - _program.Systems.Count);
            return true;
        }

        private void Step(VfxSystem system, SystemState state, float deltaTime)
        {
            Matrix4x4 world = _frame.World;
            if (system.LocalSpace && state.HasLastWorld && world != state.LastWorld && Matrix4x4.Invert(state.LastWorld, out Matrix4x4 undo))
            {
                // Carried along by however the object's moved since last frame.
                Matrix4x4 moved = undo * world;
                for (int i = 0; i < state.Count; i++)
                {
                    state.Particles[i].Position = Vector3.Transform(state.Particles[i].Position, moved);
                    state.Particles[i].Velocity = Vector3.TransformNormal(state.Particles[i].Velocity, moved);
                }
            }

            state.LastWorld = world;
            state.HasLastWorld = true;

            if (_playing && !state.Done) Spawn(system, state, deltaTime);

            VfxFrame frame = _frame;
            float scale = 1f;
            for (int i = 0; i < state.Count; i++)
            {
                ref VfxParticle particle = ref state.Particles[i];
                particle.Age += deltaTime;
                bool killed = particle.Age >= particle.Lifetime;

                if (!killed && system.HasUpdate)
                {
                    Vector4 force = system.Force!(ref particle, frame);
                    float drag = system.Drag!(ref particle, frame).X;
                    float turbulence = system.Turbulence!(ref particle, frame).X;
                    if (turbulence != 0f)
                    {
                        scale = 1f / MathF.Max(system.TurbulenceScale!(ref particle, frame).X, 0.01f);
                        force += new Vector4(VfxNodes.Swirl(particle.Position * scale, particle.Random * 100f + _playTime * 0.4f) * turbulence, 0f);
                    }

                    particle.Velocity += new Vector3(force.X, force.Y, force.Z) * deltaTime;
                    if (drag > 0f) particle.Velocity *= MathF.Exp(-drag * deltaTime);
                    particle.Rotation += system.Spin!(ref particle, frame).X * (MathF.PI / 180f) * deltaTime;
                    killed = system.Kill!(ref particle, frame).X != 0f;
                }

                if (killed)
                {
                    // Order doesn't matter, so the last one fills the gap.
                    state.Particles[i--] = state.Particles[--state.Count];
                    continue;
                }

                particle.Position += particle.Velocity * deltaTime;
            }
        }

        private void Spawn(VfxSystem system, SystemState state, float deltaTime)
        {
            var none = new VfxParticle();
            if (!state.Started)
            {
                state.Started = true;
                state.Time = 0f;
                Emit(system, state, (int)system.Burst(ref none, _frame).X);
            }

            state.Time += deltaTime;
            if (system.Duration > 0f && state.Time >= system.Duration)
            {
                if (!system.Loop)
                {
                    state.Time = system.Duration;
                    state.Done = true;
                    return;
                }

                state.Time %= system.Duration;
                Emit(system, state, (int)system.Burst(ref none, _frame).X);
            }

            state.ToEmit += MathF.Max(system.Rate(ref none, _frame).X, 0f) * deltaTime;
            int whole = (int)state.ToEmit;
            state.ToEmit -= whole;
            Emit(system, state, whole);
        }

        private void Emit(VfxSystem system, SystemState state, int count)
        {
            Matrix4x4 world = _frame.World;
            for (int n = 0; n < count && state.Count < system.MaxParticles; n++)
            {
                if (state.Count == state.Particles.Length) Array.Resize(ref state.Particles, Math.Min(state.Particles.Length * 2, Math.Max(system.MaxParticles, 16)));

                var particle = new VfxParticle
                {
                    Lifetime = 1f,
                    Size = 0.1f,
                    Color = Vector4.One,
                    Velocity = Vector3.UnitY,
                    Random = Random.Shared.NextSingle()
                };

                // What it starts as, each worked out for it in turn: the lifetime first, as others may read it.
                if (system.Lifetime != null)
                {
                    particle.Lifetime = system.Lifetime(ref particle, _frame).X;
                    Vector4 position = system.Position!(ref particle, _frame);
                    particle.Position = new Vector3(position.X, position.Y, position.Z);
                    Vector4 velocity = system.Velocity!(ref particle, _frame);
                    particle.Velocity = new Vector3(velocity.X, velocity.Y, velocity.Z);
                    particle.Size = system.Size!(ref particle, _frame).X;
                    particle.Color = system.Color!(ref particle, _frame);
                    particle.Rotation = system.Rotation!(ref particle, _frame).X * (MathF.PI / 180f);
                }

                particle.Lifetime = MathF.Max(particle.Lifetime, 0.01f);
                particle.Position = Vector3.Transform(particle.Position, world);
                particle.Velocity = Vector3.TransformNormal(particle.Velocity, world);
                state.Particles[state.Count++] = particle;
            }
        }

        /// <summary>Adds what's alive to the frame, for <see cref="Renderer"/>.</summary>
        internal void Collect(List<ParticleData> particles)
        {
            if (_program == null) return;

            for (int s = 0; s < _program.Systems.Count && s < _states.Count; s++)
            {
                VfxSystem system = _program.Systems[s];
                SystemState state = _states[s];
                if (!system.HasOutput) continue;

                bool additive = system.Blend == ParticleBlend.Additive;
                for (int i = 0; i < state.Count; i++)
                {
                    ref VfxParticle particle = ref state.Particles[i];
                    Vector4 color = particle.Color * system.Tint!(ref particle, _frame);
                    float size = particle.Size * system.SizeScale!(ref particle, _frame).X;
                    if (size <= 0f || color.W <= 0.002f) continue;

                    float glow = system.Glow!(ref particle, _frame).X;
                    float stretch = system.Stretch!(ref particle, _frame).X;
                    particles.Add(new ParticleData(particle.Position, particle.Velocity * stretch, size, new Color(color.X, color.Y, color.Z, Math.Clamp(color.W, 0f, 1f)),
                        glow, additive, particle.Rotation, system.Look));
                }
            }
        }
    }
}
