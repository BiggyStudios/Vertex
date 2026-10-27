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
        /// <summary>Their body's colour, and their name's in chat and the scoreboard. The host hands these out.</summary>
        public Color Color
        {
            get => _body.Material.Color;
            set => _body.Material.Color = value;
        }

        public bool Alive { get; private set; }
        public int Health { get; private set; } = Player.MaxHealth;

        /// <summary>
        /// How long a message takes to get to them and back, in milliseconds, smoothed; null until it's measured. Only the
        /// host measures it: everyone's ping on the scoreboard is to the host.
        /// </summary>
        public float? Ping { get; private set; }

        /// <summary>A new round trip, in milliseconds. Each one only moves <see cref="Ping"/> a little, so one slow message doesn't make it jump.</summary>
        internal void AddPing(float milliseconds) => Ping = Ping is float before ? before + (milliseconds - before) * 0.3f : milliseconds;

        /// <summary>Their score, as they last reported it.</summary>
        public int Kills => _states.Count > 0 ? _states[^1].Kills : 0;
        public int Deaths => _states.Count > 0 ? _states[^1].Deaths : 0;

        private readonly List<PlayerState> _states = new();
        private float? _clockOffset;
        private float _diedAfter = float.NegativeInfinity;

        private readonly CapsuleCollider _collider;
        private readonly GameObject _model;
        private readonly MeshRenderer _body;
        private readonly GameObject _gunPivot;
        private readonly Dictionary<WeaponId, (GameObject Model, GameObject Muzzle)> _guns = new();
        private readonly GameObject _flash;
        private readonly GameObject _healthFill;
        private readonly Rope _rope;
        private Vector3? _grapple;
        private WeaponId _weapon = WeaponId.Deagle;
        private float _gunKick;
        private float _flashTime;
        private float _fallSpeed;

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

            _body = AddShape(_model, "Body", Primitives.Sphere, new Vector3(0f, 0.72f, 0f), new Vector3(_radius * 2f, HeadHeight, _radius * 2f), color)
                .GetComponent<MeshRenderer>()!;
            AddShape(_model, "Head", Primitives.Sphere, new Vector3(0f, 1.62f, 0f), new Vector3(0.3f), new Color(0.9f, 0.75f, 0.6f));

            // Turns up and down with where they're looking.
            _gunPivot = new GameObject("Gun Pivot");
            _gunPivot.Transform.Parent = _model.Transform;
            _gunPivot.Transform.Position = new Vector3(0.22f, 1.35f, 0f);

            // One model per weapon, each with a muzzle at its tip; whichever they're holding is shown.
            GameObject rifle = AddGun(WeaponId.Rifle, "Rifle", new Vector3(0f, 0f, -0.42f));
            AddShape(rifle, "Receiver", Primitives.Cube, new Vector3(0f, 0f, -0.2f), new Vector3(0.07f, 0.09f, 0.4f), new Color(0.2f, 0.2f, 0.22f));

            GameObject sniper = AddGun(WeaponId.Sniper, "Sniper Rifle", new Vector3(0f, 0.01f, -0.82f));
            AddShape(sniper, "Stock", Primitives.Cube, new Vector3(0f, -0.01f, -0.15f), new Vector3(0.07f, 0.1f, 0.5f), new Color(0.3f, 0.35f, 0.22f));
            AddShape(sniper, "Barrel", Primitives.Cube, new Vector3(0f, 0.01f, -0.6f), new Vector3(0.03f, 0.03f, 0.4f), new Color(0.2f, 0.2f, 0.22f));
            AddShape(sniper, "Scope", Primitives.Cube, new Vector3(0f, 0.08f, -0.2f), new Vector3(0.05f, 0.05f, 0.28f), new Color(0.08f, 0.08f, 0.09f));

            GameObject shotgun = AddGun(WeaponId.Shotgun, "Shotgun", new Vector3(0f, 0.01f, -0.6f));
            AddShape(shotgun, "Body", Primitives.Cube, new Vector3(0f, -0.01f, -0.05f), new Vector3(0.07f, 0.1f, 0.4f), new Color(0.45f, 0.27f, 0.14f));
            AddShape(shotgun, "Barrel", Primitives.Cube, new Vector3(0f, 0.01f, -0.4f), new Vector3(0.05f, 0.05f, 0.4f), new Color(0.18f, 0.18f, 0.2f));
            AddShape(shotgun, "Pump", Primitives.Cube, new Vector3(0f, -0.03f, -0.35f), new Vector3(0.06f, 0.05f, 0.14f), new Color(0.36f, 0.21f, 0.11f));

            GameObject launcher = AddGun(WeaponId.RocketLauncher, "Rocket Launcher", new Vector3(0f, 0.03f, -0.62f));
            AddShape(launcher, "Tube", Primitives.Cube, new Vector3(0f, 0.03f, -0.2f), new Vector3(0.13f, 0.13f, 0.85f), new Color(0.36f, 0.4f, 0.26f));
            AddShape(launcher, "Grip", Primitives.Cube, new Vector3(0f, -0.06f, -0.05f), new Vector3(0.04f, 0.1f, 0.05f), new Color(0.1f, 0.1f, 0.11f));

            GameObject deagle = AddGun(WeaponId.Deagle, "Deagle", new Vector3(0f, 0.025f, -0.22f));
            AddShape(deagle, "Barrel", Primitives.Cube, new Vector3(0f, 0.027f, -0.16f), new Vector3(0.05f, 0.055f, 0.12f), new Color(0.64f, 0.65f, 0.68f));
            AddShape(deagle, "Slide", Primitives.Cube, new Vector3(0f, 0.025f, -0.04f), new Vector3(0.045f, 0.048f, 0.14f), new Color(0.64f, 0.65f, 0.68f));
            AddShape(deagle, "Frame", Primitives.Cube, new Vector3(0f, -0.008f, -0.09f), new Vector3(0.04f, 0.022f, 0.2f), new Color(0.09f, 0.09f, 0.1f));
            AddShape(deagle, "Grip", Primitives.Cube, new Vector3(0f, -0.06f, 0.01f), new Vector3(0.045f, 0.11f, 0.06f), new Color(0.09f, 0.09f, 0.1f));

            GameObject knife = AddGun(WeaponId.Knife, "Knife", new Vector3(0f, 0f, -0.3f));
            AddShape(knife, "Handle", Primitives.Cube, new Vector3(0f, 0f, 0f), new Vector3(0.035f, 0.04f, 0.12f), new Color(0.1f, 0.1f, 0.11f));
            AddShape(knife, "Blade", Primitives.Cube, new Vector3(0f, 0.005f, -0.15f), new Vector3(0.01f, 0.04f, 0.18f), new Color(0.62f, 0.64f, 0.68f));

            // Moved to the tip of whichever gun they're holding.
            _flash = AddShape(_guns[WeaponId.Deagle].Muzzle, "Muzzle Flash", Primitives.Sphere, Vector3.Zero, new Vector3(0.1f), new Color(1f, 0.9f, 0.5f), unlit: true);
            MeshRenderer flashRenderer = _flash.GetComponent<MeshRenderer>()!;
            flashRenderer.Material.Emission = new Color(1f, 0.75f, 0.35f);
            flashRenderer.Material.EmissionStrength = 2.5f;
            _flash.Active = false;

            AddShape(_model, "Health Bar Background", Primitives.Cube, new Vector3(0f, 2.05f, 0f), new Vector3(0.62f, 0.07f, 0.02f), new Color(0.05f, 0.05f, 0.05f), unlit: true);
            _healthFill = AddShape(_model, "Health Bar", Primitives.Cube, new Vector3(0f, 2.05f, 0f), new Vector3(0.6f, 0.05f, 0.03f), Color.White, unlit: true);

            _rope = new Rope("Grapple Rope", GrapplePoint.DefaultColor, Transform);
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

        /// <summary>
        /// Plays the gunshot of <paramref name="weapon"/> from where they are, and draws their muzzle flash and the shot's
        /// tracer. A knife swing has no pellets: just its whoosh, and the knife jerking forward. It switches them to that
        /// weapon too, in case the shot arrived before the update saying they changed.
        /// </summary>
        public void ShowShot(IReadOnlyList<Vector3> ends, Weapon weapon)
        {
            ShowWeapon(weapon.Id);
            Vector3 muzzle = _guns[_weapon].Muzzle.Transform.WorldPosition;
            foreach (Vector3 end in ends)
            {
                Effects.Tracer(muzzle, end);

                // The shot only says where each pellet ended, so look along it again here for what it hit there.
                Vector3 path = end - muzzle;
                float length = path.Length();
                if (length > 0.01f && Scene.Physics.Raycast(muzzle, path, length + 0.1f, out RaycastHit hit, ignore: _collider) && hit.Distance > length - 0.25f)
                    Effects.Impact(hit);
            }

            // Full volume within a few metres, then quieter with distance; it comes from their direction as you turn.
            // The sniper rifle is louder, so it carries further.
            Sound.PlayAt(weapon.Sound, muzzle, volume: 1f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f, fullVolumeDistance: weapon.SoundVolume * 5f);

            _gunKick = 1f;
            if (!weapon.Melee) _flashTime = 0.05f;
        }

        /// <summary>
        /// Dust at their feet when they land: they were falling, and now they've stopped (or jumped straight off again)
        /// with the ground right under them. The ground check keeps a surfer leaving a ramp from counting.
        /// </summary>
        private void ShowLanding(in PlayerState shown)
        {
            float verticalSpeed = shown.Velocity.Y;
            if (!Alive || verticalSpeed < -0.5f)
            {
                _fallSpeed = Alive ? MathF.Max(_fallSpeed, -verticalSpeed) : 0f;
                return;
            }

            if (_fallSpeed > 0f && Scene.Physics.Raycast(shown.Position + new Vector3(0f, 0.2f, 0f), -Vector3.UnitY, 0.5f, out _, ignore: _collider))
                Effects.Landing(shown.Position, _fallSpeed);
            _fallSpeed = 0f;
        }

        /// <summary>Their grapple rope, from their hand to the point it's caught on, with a zip when it catches.</summary>
        private void ShowGrapple(in PlayerState shown)
        {
            Vector3? grapple = Alive ? shown.Grapple : null;
            if (grapple is Vector3 anchor)
            {
                if (_grapple != anchor) Sound.PlayAt(Sounds.GrappleShot, _gunPivot.Transform.WorldPosition, volume: 0.6f);
                _rope.Show(_gunPivot.Transform.WorldPosition, anchor);
            }
            else
            {
                _rope.Hide();
            }

            _grapple = grapple;
        }

        /// <summary>Whether shots hit them and they block the way: while they're alive.</summary>
        protected virtual bool Solid => Alive;

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
            _collider.Enabled = Solid;

            ShowLanding(shown);
            ShowGrapple(shown);

            _gunKick *= MathF.Exp(-14f * Time.DeltaTime);
            ShowWeapon(newest.Weapon);

            // Guns kick back and up; the knife jabs forward.
            bool melee = Weapon.Get(_weapon).Melee;
            _gunPivot.Transform.EulerAngles = new Vector3(shown.Pitch + (melee ? 0f : _gunKick * 8f), 0f, 0f);
            GameObject gun = _guns[_weapon].Model;
            gun.Transform.Position = new Vector3(0f, 0f, melee ? -_gunKick * 0.25f : _gunKick * 0.06f);

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

        /// <summary>One weapon's model, held on the gun pivot, with a muzzle at <paramref name="muzzle"/>. Only the Deagle starts shown.</summary>
        private GameObject AddGun(WeaponId id, string name, Vector3 muzzle)
        {
            var gun = new GameObject(name);
            gun.Transform.Parent = _gunPivot.Transform;
            gun.Active = id == WeaponId.Deagle;
            _guns[id] = (gun, AddShape(gun, "Muzzle", null, muzzle, Vector3.One, default));
            return gun;
        }

        private void ShowWeapon(WeaponId weapon)
        {
            // An unknown weapon, from a newer version of the game, shows as the rifle.
            if (!_guns.ContainsKey(weapon)) weapon = WeaponId.Rifle;
            if (weapon == _weapon) return;

            _weapon = weapon;
            foreach ((WeaponId id, (GameObject model, _)) in _guns) model.Active = id == weapon;
            _flash.Transform.Parent = _guns[weapon].Muzzle.Transform;
        }

        /// <summary>A coloured shape, or with no mesh, an empty object to mark a position.</summary>
        private static GameObject AddShape(GameObject parent, string name, Mesh? mesh, Vector3 position, Vector3 scale, Color color, bool unlit = false)
        {
            var shape = new GameObject(name);
            shape.Transform.Parent = parent.Transform;
            shape.Transform.Position = position;
            shape.Transform.Scale = scale;

            if (mesh == null) return shape;

            MeshRenderer renderer = shape.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Color = color;
            if (unlit) renderer.Material.Shading = ShadingModel.Unlit;
            return shape;
        }
    }
}
