using System.Numerics;

using Vertex;
using Vertex.Audio;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A rocket from the rocket launcher. It flies straight at the weapon's speed and explodes on whatever it hits,
    /// hurting and throwing everyone nearby. Its top is solid and stays level however it's pointing, so players can stand
    /// on it and ride it, as long as it's right under their feet; they're carried along (see Player's moving ground), so
    /// they can jump and land back on it, or jump off it, and it flies on. Only getting in front of it sets it off.
    /// <para>
    /// Every player's game flies its own copy of each rocket. The one who fired it decides when it hits the world or
    /// another player; anyone else only decides when it hits them, which is how landing in front of one you jumped off sets
    /// it off. Whoever decides raises <see cref="Exploded"/> so the others can be told, and each game hurts and throws
    /// only its own player.
    /// </para>
    /// </summary>
    public class Rocket : Component
    {
        private const float _halfLength = 0.8f;
        // How long it flies before going off by itself: long enough to ride it a good long way.
        private const float _lifetime = 20f;

        // Before this, the solid top is left off, so a rocket doesn't knock into the player who just fired it.
        private const float _solidAfter = 0.15f;

        // How far from its middle someone standing on top can go, across and along it, before KeepOnTop stops them.
        private const float _standWidth = 0.1f;
        private const float _standLength = 0.65f;

        // Your own rocket hurts you this much less than it would anyone else, so rocket jumping is worth the health.
        private const float _selfDamage = 0.4f;

        // How hard the blast throws someone right next to it, in metres per second, less further out.
        private const float _blastPush = 14f;

        private static readonly Dictionary<(ulong Owner, ushort Id), Rocket> _flying = new();
        private static ushort _nextId;

        /// <summary>
        /// A rocket blew up here, decided by this game (its owner hit something, or it hit this game's player), with
        /// where. Not raised for an explosion this game was told about with <see cref="Explode"/>.
        /// </summary>
        public static event Action<Rocket, Vector3>? Exploded;

        /// <summary>Raised for every explosion, decided here or not, with where.</summary>
        public static event Action<Rocket, Vector3>? Detonated;

        /// <summary>Who fired it.</summary>
        public PeerId Owner { get; private set; }

        /// <summary>The owner's number for it, so every game knows which rocket a message is about.</summary>
        public ushort Id { get; private set; }

        /// <summary>Fired by this game's player.</summary>
        public bool IsMine { get; private set; }

        public Weapon Weapon { get; private set; } = Weapon.RocketLauncher;
        public Vector3 Velocity { get; private set; }

        /// <summary>Once it's exploded, the player it flew straight into, if any: a direct hit kills outright.</summary>
        public PeerId? DirectHit { get; private set; }

        private Player _localPlayer = null!;
        private BoxCollider _top = null!;
        private GameObject _visual = null!;
        private ParticleSystem[] _trail = [];
        private Vector3 _position;
        private Vector3 _previousPosition;
        private float _age;
        private bool _exploded;

        /// <summary>
        /// Fires a rocket from <paramref name="origin"/> along <paramref name="direction"/>. <paramref name="localPlayer"/>
        /// is this game's player, who it can hurt. A rocket fired here gets the next number; one fired by someone else
        /// takes the number they gave it.
        /// </summary>
        public static Rocket Spawn(Player localPlayer, PeerId owner, bool mine, Vector3 origin, Vector3 direction, ushort? id = null)
        {
            direction = Vector3.Normalize(direction);
            Weapon weapon = Weapon.RocketLauncher;

            var root = new GameObject("Rocket");
            root.Transform.Position = origin;
            // The solid top turns with the rocket's heading but never tilts, so it's always something to stand on.
            Vector3 flat = direction with { Y = 0f };
            float yaw = flat.LengthSquared() > 1e-6f ? MathF.Atan2(-flat.X, -flat.Z) : 0f;
            root.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

            Rocket rocket = root.AddComponent<Rocket>();
            rocket._localPlayer = localPlayer;
            rocket.Owner = owner;
            rocket.IsMine = mine;
            rocket.Id = id ?? _nextId++;
            rocket.Weapon = weapon;
            rocket.Velocity = direction * weapon.ProjectileSpeed * 0.0254f;
            rocket._position = rocket._previousPosition = origin;

            // About the size of the rocket you see, so you only ride it with it right under your feet.
            rocket._top = root.AddComponent<BoxCollider>();
            rocket._top.Size = new Vector3(0.36f, 0.24f, _halfLength * 2f);
            rocket._top.Enabled = false;
            root.AddComponent<Rigidbody>().IsKinematic = true;

            rocket.BuildModel(MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)));

            _flying[(owner.Value, rocket.Id)] = rocket;
            return rocket;
        }

        /// <summary>The rocket <paramref name="owner"/> numbered <paramref name="id"/>, if it's still flying.</summary>
        public static Rocket? Find(PeerId owner, ushort id) => _flying.GetValueOrDefault((owner.Value, id));

        protected override void FixedUpdate()
        {
            if (_exploded) return;

            float dt = Time.FixedDeltaTime;
            _age += dt;
            _top.Enabled = _age >= _solidAfter;

            // Someone else's rocket that was never reported exploding (a lost message) just goes away.
            if (_age >= _lifetime + (IsMine ? 0f : 2f))
            {
                if (IsMine) Explode(_position, decidedHere: true);
                else Remove();
                return;
            }

            Vector3 step = Velocity * dt;
            if (FindHit(step, out Vector3 point, out GameObject? target))
            {
                PeerId? direct = target switch
                {
                    RemotePlayer remote => remote.Peer,
                    // Anyone it flies straight into, its owner too: fire one point-blank into your own feet and that's it.
                    Player => Network.LocalPeer,
                    _ => null
                };
                Explode(point, decidedHere: true, direct);
                return;
            }

            _previousPosition = _position;
            _position += step;
            Transform.Position = _position;
        }

        protected override void Update()
        {
            if (_exploded) return;

            // Drawn between its last two steps, like physics objects, so it moves smoothly alongside a player riding it.
            Vector3 drawn = Vector3.Lerp(_previousPosition, _position, Time.FixedAlpha);
            _visual.Transform.Position = Vector3.Transform(drawn - _position, Quaternion.Inverse(Transform.Rotation));
        }

        /// <summary>
        /// Looks ahead along this step for something to hit: from the middle and from four points round its edge, so
        /// it can't slip past a corner. The owner's rocket hits anything; anyone else's only hits this game's player.
        /// Nobody standing on it counts.
        /// </summary>
        private bool FindHit(Vector3 step, out Vector3 point, out GameObject? target)
        {
            point = default;
            target = null;
            float distance = step.Length() + _halfLength * 0.6f;
            Vector3 direction = Vector3.Normalize(step);
            Vector3 side = Vector3.Normalize(MathF.Abs(direction.Y) < 0.99f ? Vector3.Cross(direction, Vector3.UnitY) : Vector3.UnitX);
            Vector3 up = Vector3.Cross(side, direction);

            float nearest = float.MaxValue;
            foreach (Vector3 offset in (ReadOnlySpan<Vector3>)[Vector3.Zero, side * 0.12f, -side * 0.12f, up * 0.12f, -up * 0.12f])
            {
                if (!GameObject.Scene.Physics.Raycast(_position + offset, direction, distance, out RaycastHit hit, ignore: _top)) continue;
                if (hit.Distance >= nearest || !Hits(hit.GameObject)) continue;

                nearest = hit.Distance;
                point = hit.Point - direction * 0.1f;
                target = hit.GameObject;
            }

            return nearest < float.MaxValue;
        }

        private bool Hits(GameObject target)
        {
            // Your own rocket hits anything but you: surfing faster than it, you can catch it up, so in flight it never
            // hits you (point-blank shots into your own feet are caught when firing, in Gun). Anyone else's only hits you.
            bool localPlayer = target == _localPlayer;
            if (IsMine == localPlayer) return false;

            Vector3? feet = target switch
            {
                Player player => player.Transform.WorldPosition,
                RemotePlayer remote => remote.Transform.WorldPosition,
                _ => null
            };

            return feet is not Vector3 position || !IsRiding(position);
        }

        /// <summary>
        /// Someone standing on top, running about at <paramref name="relative"/> to it: the same, but stopping at its edges
        /// rather than running off it, which at running speed takes a tenth of a second on something this small. Only
        /// jumping takes them off. <paramref name="feet"/> is where they stand before this physics step, which the rocket
        /// hasn't taken yet either.
        /// </summary>
        public Vector3 KeepOnTop(Vector3 feet, Vector3 relative, float dt)
        {
            Quaternion toLocal = Quaternion.Inverse(Transform.Rotation);
            Vector3 at = Vector3.Transform(feet - _position, toLocal);
            Vector3 move = Vector3.Transform(relative, toLocal);

            move.X = StopAtEdge(at.X, move.X, _standWidth);
            move.Z = StopAtEdge(at.Z, move.Z, _standLength);
            return Vector3.Transform(move, Transform.Rotation);

            // Heading past the edge, only as far as it; already past it (landed on the very end), no further.
            float StopAtEdge(float position, float speed, float edge)
            {
                float next = position + speed * dt;
                if (next > edge && speed > 0f) return MathF.Max((edge - position) / dt, 0f);
                if (next < -edge && speed < 0f) return MathF.Min((-edge - position) / dt, 0f);
                return speed;
            }
        }

        /// <summary>Whether someone with their feet at <paramref name="feet"/> is standing on top.</summary>
        public bool IsRiding(Vector3 feet)
        {
            Vector3 local = Vector3.Transform(feet - _position, Quaternion.Inverse(Transform.Rotation));
            return MathF.Abs(local.X) < 0.5f && MathF.Abs(local.Z) < _halfLength + 0.4f && local.Y > -0.1f && local.Y < 0.9f;
        }

        /// <summary>
        /// Blows it up at <paramref name="position"/>: the blast, and hurting and throwing this game's player if they're
        /// close. <paramref name="decidedHere"/> is false when another game decided it and this one was told.
        /// <paramref name="directHit"/> is the player it flew straight into, if any, who's killed outright.
        /// </summary>
        public void Explode(Vector3 position, bool decidedHere, PeerId? directHit = null)
        {
            if (_exploded) return;
            _exploded = true;
            DirectHit = directHit;

            Effects.Explosion(position);
            Sound.PlayAt(Sounds.Explosion, position, volume: 1f, pitch: 0.92f + Random.Shared.NextSingle() * 0.16f, fullVolumeDistance: 8f);
            HurtLocalPlayer(position);
            PushProps(position);

            if (decidedHere) Exploded?.Invoke(this, position);
            Detonated?.Invoke(this, position);
            Remove();
        }

        /// <summary>
        /// How much a blast at <paramref name="position"/> hurts someone with their feet at <paramref name="feet"/>, and
        /// how hard it throws them: the full amount right next to it, falling to nothing at the blast's edge.
        /// </summary>
        public float Strength(Vector3 position, Vector3 feet)
        {
            float distance = Vector3.Distance(position, feet + new Vector3(0f, 0.9f, 0f));
            return Math.Clamp(1f - distance / Weapon.BlastRadius, 0f, 1f);
        }

        private void HurtLocalPlayer(Vector3 position)
        {
            Player player = _localPlayer;
            if (player.IsDead) return;

            Vector3 centre = player.Transform.WorldPosition + new Vector3(0f, 0.9f, 0f);
            float strength = Strength(position, player.Transform.WorldPosition);
            if (strength <= 0f && DirectHit != Network.LocalPeer) return;

            // Away from the blast, and always a little upwards, so a rocket at your feet launches you.
            Vector3 away = centre - position;
            away = away.LengthSquared() > 1e-4f ? Vector3.Normalize(away) : Vector3.UnitY;
            player.Push(Vector3.Normalize(away + new Vector3(0f, 0.5f, 0f)) * (_blastPush * strength));

            // A direct hit kills outright; otherwise it's less the further from the blast.
            bool direct = DirectHit == Network.LocalPeer;
            float damage = direct ? Player.MaxHealth : Weapon.BodyDamage * strength * (IsMine ? _selfDamage : 1f);
            player.TakeDamage((int)MathF.Ceiling(damage), IsMine ? null : (PeerId?)Owner, weapon: WeaponId.RocketLauncher,
                distance: IsMine ? 0f : Vector3.Distance(position, centre));
        }

        private void PushProps(Vector3 position)
        {
            foreach (Rigidbody body in GameObject.Scene.FindComponents<Rigidbody>())
            {
                if (body.IsKinematic) continue;

                Vector3 offset = body.Transform.WorldPosition - position;
                float strength = Math.Clamp(1f - offset.Length() / Weapon.BlastRadius, 0f, 1f);
                if (strength > 0f) body.AddImpulse(Vector3.Normalize(offset + new Vector3(0f, 0.3f, 0f)) * Weapon.ImpactForce * strength);
            }
        }

        /// <summary>Takes it out of the world, leaving its smoke trail to drift and fade.</summary>
        private void Remove()
        {
            _exploded = true;
            _flying.Remove((Owner.Value, Id));

            foreach (ParticleSystem trail in _trail)
            {
                trail.Emitting = false;
                trail.DestroyWhenFinished = true;
                trail.Transform.Parent = null;
            }

            GameObject.Destroy();
        }

        protected override void OnDestroy()
        {
            if (_flying.TryGetValue((Owner.Value, Id), out Rocket? rocket) && rocket == this) _flying.Remove((Owner.Value, Id));
        }

        /// <summary>
        /// The rocket you see, tilted to point where it's going (the solid top doesn't tilt): an olive body with a red
        /// nose and fins, a glowing motor at the back, and a trail of flame and smoke.
        /// </summary>
        private void BuildModel(float pitch)
        {
            _visual = new GameObject("Rocket Model");
            _visual.Transform.Parent = Transform;

            var pivot = new GameObject("Rocket Pivot");
            pivot.Transform.Parent = _visual.Transform;
            pivot.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);

            var olive = new Color(0.36f, 0.4f, 0.26f);
            var red = new Color(0.75f, 0.15f, 0.1f);
            AddPart(pivot, Primitives.Cube, Vector3.Zero, new Vector3(0.2f, 0.2f, 0.9f), olive);
            AddPart(pivot, Primitives.Sphere, new Vector3(0f, 0f, -0.48f), new Vector3(0.2f, 0.2f, 0.32f), red);
            AddPart(pivot, Primitives.Cube, new Vector3(0f, 0f, 0.38f), new Vector3(0.46f, 0.03f, 0.18f), red);
            AddPart(pivot, Primitives.Cube, new Vector3(0f, 0f, 0.38f), new Vector3(0.03f, 0.46f, 0.18f), red);

            var flame = new Color(1f, 0.6f, 0.2f);
            MeshRenderer motor = AddPart(pivot, Primitives.Sphere, new Vector3(0f, 0f, 0.5f), new Vector3(0.17f, 0.17f, 0.24f), flame);
            motor.Material.Shading = ShadingModel.Unlit;
            motor.Material.Emission = flame;
            motor.Material.EmissionStrength = 3f;
            motor.CastShadows = false;

            var tail = new GameObject("Rocket Tail");
            tail.Transform.Parent = pivot.Transform;
            tail.Transform.Position = new Vector3(0f, 0f, 0.6f);

            ParticleSystem fire = tail.AddComponent<ParticleSystem>();
            fire.Rate = 90f;
            fire.Direction = Vector3.UnitZ;
            fire.Spread = 15f;
            fire.Speed = new FloatRange(1f, 3f);
            fire.Lifetime = new FloatRange(0.06f, 0.12f);
            fire.Size = new FloatRange(0.2f, 0.3f);
            fire.EndSize = 0.4f;
            fire.StartColor = new Color(1f, 0.8f, 0.4f);
            fire.EndColor = new Color(1f, 0.3f, 0.05f, 0f);
            fire.Glow = 3f;

            var smokeHolder = new GameObject("Rocket Smoke");
            smokeHolder.Transform.Parent = tail.Transform;
            ParticleSystem smoke = smokeHolder.AddComponent<ParticleSystem>();
            smoke.Blend = ParticleBlend.Alpha;
            smoke.Rate = 70f;
            smoke.Spread = 180f;
            smoke.Speed = new FloatRange(0.2f, 0.6f);
            smoke.Lifetime = new FloatRange(0.8f, 1.3f);
            smoke.Size = new FloatRange(0.2f, 0.3f);
            smoke.EndSize = 6f;
            smoke.StartColor = new Color(0.8f, 0.78f, 0.75f, 0.5f);
            smoke.EndColor = new Color(0.85f, 0.85f, 0.85f, 0f);
            smoke.Gravity = -0.4f;
            smoke.Drag = 1.5f;

            _trail = [fire, smoke];
        }

        private static MeshRenderer AddPart(GameObject parent, Mesh mesh, Vector3 position, Vector3 scale, Color color)
        {
            var part = new GameObject("Rocket Part");
            part.Transform.Parent = parent.Transform;
            part.Transform.Position = position;
            part.Transform.Scale = scale;

            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material.Color = color;
            renderer.Material.Specular = 0.3f;
            return renderer;
        }
    }
}
