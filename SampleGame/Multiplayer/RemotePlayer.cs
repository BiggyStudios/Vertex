using System.Numerics;

using Vertex;
using Vertex.Audio;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// Another player, as seen on this machine. Their updates arrive several times a second and slightly unevenly,
    /// so they're drawn a little in the past (<see cref="_interpolationDelay"/>), blending smoothly between the two
    /// updates either side of that moment, like Source's cl_interp. They have a capsule collider so shots can hit them.
    /// </summary>
    public class RemotePlayer : GameObject
    {
        /// <summary>Hits this high above the feet or higher are headshots.</summary>
        public const float HeadHeight = 1.45f;

        private const float _units = 0.0254f;
        private const float _height = 72f * _units;
        private const float _radius = 16f * _units;
        private const float _interpolationDelay = 0.1f;
        private const float _teleportDistance = 5f;

        public PeerId Peer { get; }
        public bool Alive { get; private set; }
        public int Health { get; private set; } = Player.MaxHealth;

        private readonly List<PlayerState> _states = new();
        private float? _clockOffset;
        private float _diedAfter = float.NegativeInfinity;

        private readonly CapsuleCollider _collider;
        private readonly GameObject _model;
        private readonly GameObject _gunPivot;
        private readonly GameObject _gunModel;
        private readonly GameObject _muzzle;
        private readonly GameObject _flash;
        private readonly GameObject _healthFill;
        private float _gunKick;
        private float _flashTime;

        public RemotePlayer(PeerId peer, string name, Color color) : base(name)
        {
            Peer = peer;

            // Hidden until their first update says where they are.
            Active = false;

            // Moved by this code, so kinematic: it blocks the local player and gets hit by shots, but nothing pushes it.
            _collider = AddComponent<CapsuleCollider>();
            _collider.Radius = _radius;
            _collider.Height = _height;
            _collider.Center = new Vector3(0f, _height * 0.5f, 0f);
            AddComponent<Rigidbody>().IsKinematic = true;

            _model = new GameObject("Model");
            _model.Transform.Parent = Transform;

            AddShape(_model, "Body", Primitives.Sphere, new Vector3(0f, 0.72f, 0f), new Vector3(_radius * 2f, HeadHeight, _radius * 2f), color);
            AddShape(_model, "Head", Primitives.Sphere, new Vector3(0f, 1.62f, 0f), new Vector3(0.3f), new Color(0.9f, 0.75f, 0.6f));

            // Turns up and down with where they're looking.
            _gunPivot = new GameObject("Gun Pivot");
            _gunPivot.Transform.Parent = _model.Transform;
            _gunPivot.Transform.Position = new Vector3(0.22f, 1.35f, 0f);

            _gunModel = new GameObject("Gun");
            _gunModel.Transform.Parent = _gunPivot.Transform;
            AddShape(_gunModel, "Receiver", Primitives.Cube, new Vector3(0f, 0f, -0.2f), new Vector3(0.07f, 0.09f, 0.4f), new Color(0.2f, 0.2f, 0.22f));

            _muzzle = new GameObject("Muzzle");
            _muzzle.Transform.Parent = _gunModel.Transform;
            _muzzle.Transform.Position = new Vector3(0f, 0f, -0.42f);

            _flash = AddShape(_muzzle, "Muzzle Flash", Primitives.Sphere, Vector3.Zero, new Vector3(0.1f), new Color(1f, 0.9f, 0.5f), unlit: true);
            _flash.Active = false;

            AddShape(_model, "Health Bar Background", Primitives.Cube, new Vector3(0f, 2.05f, 0f), new Vector3(0.62f, 0.07f, 0.02f), new Color(0.05f, 0.05f, 0.05f), unlit: true);
            _healthFill = AddShape(_model, "Health Bar", Primitives.Cube, new Vector3(0f, 2.05f, 0f), new Vector3(0.6f, 0.05f, 0.03f), Color.White, unlit: true);
        }

        internal void AddState(in PlayerState state)
        {
            // Estimate how far the sender's clock is behind ours. Take the smallest gap seen, which is the
            // update that arrived quickest, and let it creep up slowly in case the clocks drift apart.
            float offset = Time.TotalTime - state.Time;
            _clockOffset = _clockOffset is not float current || offset < current ? offset : current + (offset - current) * 0.01f;

            // Unreliable updates can arrive out of order; keep the list sorted by the sender's time.
            int index = _states.Count;
            while (index > 0 && _states[index - 1].Time > state.Time) index--;
            if (index > 0 && _states[index - 1].Time == state.Time) return;
            _states.Insert(index, state);

            // Two seconds of history is plenty.
            while (_states.Count > 2 && _states[0].Time < state.Time - 2f) _states.RemoveAt(0);

            Active = true;
        }

        /// <summary>Plays their gunshot from where they are, and draws their muzzle flash and the shot's tracer.</summary>
        public void ShowShot(Vector3 end)
        {
            Vector3 muzzle = _muzzle.Transform.WorldPosition;
            Effects.Tracer(muzzle, end);

            // Full volume within 4 units, then quieter with distance; it comes from their direction as you turn.
            Sound.PlayAt(Sounds.Gunshot, muzzle, volume: 1f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f, fullVolumeDistance: 4f);

            _gunKick = 1f;
            _flashTime = 0.05f;
        }

        /// <summary>Called when they announce their death, so they go down straight away rather than at their next update.</summary>
        public void MarkDead()
        {
            Alive = false;
            Health = 0;

            // Updates sent before they died can still arrive after this; they mustn't bring them back.
            if (_states.Count > 0) _diedAfter = _states[^1].Time;
        }

        protected override void Update()
        {
            if (_states.Count == 0 || _clockOffset is not float clockOffset) return;

            float renderTime = Time.TotalTime - clockOffset - _interpolationDelay;
            PlayerState shown = Sample(renderTime);

            Transform.Position = shown.Position;
            Transform.EulerAngles = new Vector3(0f, shown.Yaw, 0f);

            // Health and life come from the newest update, so damage shows up straight away.
            PlayerState newest = _states[^1];
            if (newest.Time > _diedAfter)
            {
                Alive = newest.Alive;
                Health = newest.Health;
            }

            _model.Active = Alive;
            _collider.Enabled = Alive;

            _gunKick *= MathF.Exp(-14f * Time.DeltaTime);
            _gunPivot.Transform.EulerAngles = new Vector3(shown.Pitch + _gunKick * 8f, 0f, 0f);
            _gunModel.Transform.Position = new Vector3(0f, 0f, _gunKick * 0.06f);

            _flashTime -= Time.DeltaTime;
            _flash.Active = _flashTime > 0f;

            float fraction = Math.Clamp(Health / (float)Player.MaxHealth, 0f, 1f);
            _healthFill.Transform.Scale = new Vector3(MathF.Max(0.6f * fraction, 0.001f), 0.05f, 0.03f);
            _healthFill.Transform.Position = new Vector3(-0.3f + 0.3f * fraction, 2.05f, 0f);
            _healthFill.GetComponent<MeshRenderer>()!.Material.Color = new Color(1f - fraction, 0.25f + 0.65f * fraction, 0.2f);
        }

        /// <summary>Where they were at <paramref name="time"/> on their clock, blending between the updates either side.</summary>
        private PlayerState Sample(float time)
        {
            if (time <= _states[0].Time) return _states[0];

            for (int i = 1; i < _states.Count; i++)
            {
                PlayerState before = _states[i - 1];
                PlayerState after = _states[i];
                if (time > after.Time) continue;

                // A big jump is a teleport or respawn: don't slide across the map.
                if (Vector3.Distance(before.Position, after.Position) > _teleportDistance) return after;

                float t = (time - before.Time) / (after.Time - before.Time);
                return after with
                {
                    Position = Vector3.Lerp(before.Position, after.Position, t),
                    Yaw = before.Yaw + DeltaAngle(before.Yaw, after.Yaw) * t,
                    Pitch = before.Pitch + (after.Pitch - before.Pitch) * t
                };
            }

            // Past the newest update (it's late, or lost): carry on moving for a moment, then wait.
            PlayerState last = _states[^1];
            float ahead = MathF.Min(time - last.Time, 0.1f);
            return last with { Position = last.Position + last.Velocity * ahead };
        }

        /// <summary>The shortest turn from one angle to another, in degrees: from 350 to 10 is +20, not -340.</summary>
        private static float DeltaAngle(float from, float to) => ((to - from) % 360f + 540f) % 360f - 180f;

        private static GameObject AddShape(GameObject parent, string name, Mesh mesh, Vector3 position, Vector3 scale, Color color, bool unlit = false)
        {
            var shape = new GameObject(name);
            shape.Transform.Parent = parent.Transform;
            shape.Transform.Position = position;
            shape.Transform.Scale = scale;

            MeshRenderer renderer = shape.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Color = color;
            if (unlit) renderer.Material.Shading = ShadingModel.Unlit;
            return shape;
        }
    }
}
