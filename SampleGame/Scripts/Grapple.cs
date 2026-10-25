using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Audio;
using Vertex.Input;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The player's grapple. Put the crosshair on a <see cref="GrapplePoint"/> in range and an animated circle locks onto
    /// it; hold Shift to fire a rope at it, and let go to release.
    /// <para>
    /// The rope never pushes or slows you while it's slack: it only stops you going further from the point than you
    /// were when you caught it, keeping your speed but bending your path round it, like a pendulum. Used at the right
    /// moment that swings you round corners and over gaps faster than strafing could. Hang on too long and the swing
    /// carries you past the point and the rope hauls you back the way you came: that's the cost of mistiming it. While
    /// you hang on, a ring round the point fills as you swing past it, turning orange when it's time to let go.
    /// </para>
    /// </summary>
    public class Grapple : Component
    {
        /// <summary>How far away a point can be caught, in metres.</summary>
        public const float MaxRange = 50f;

        // How far off the crosshair, in degrees, a point can be and still be aimed at, on top of the orb's own size.
        private const float _aimAngle = 5f;

        // Where the rope ties on to the player: about chest height.
        private static readonly Vector3 _tie = new(0f, 1.2f, 0f);

        private static readonly Color _color = GrapplePoint.DefaultColor;
        private static readonly Color _tautColor = new(1f, 0.55f, 0.2f);

        /// <summary>The point the local player has in their sights, if any, for it to light up.</summary>
        public static GrapplePoint? Targeted { get; private set; }

        /// <summary>The point the local player is hanging from, if any.</summary>
        public static GrapplePoint? Held { get; private set; }

        /// <summary>The point the rope is caught on, or null.</summary>
        public GrapplePoint? Anchor { get; private set; }

        /// <summary>How long the rope is: as far as the player was from the point when it caught.</summary>
        public float RopeLength { get; private set; }

        public bool IsHooked => Anchor != null;

        /// <summary>Raised when the rope catches a point.</summary>
        public event Action<GrapplePoint>? Hooked;

        private Player _player = null!;
        private Rope _rope = null!;
        private bool _fromInput;
        private bool _shiftUsed;
        private GrapplePoint? _reticleOn;
        private float _reticleTime;

        internal void Attach(Player player) => _player = player;

        /// <summary>Where the rope ties on to the player now.</summary>
        public Vector3 TiePosition => _player.Transform.WorldPosition + _tie;

        protected override void Start() => _rope = new Rope("Grapple Rope", _color);

        protected override void OnDestroy()
        {
            Release();
            Targeted = null;
            _rope.Destroy();
        }

        /// <summary>Fires the rope at <paramref name="point"/>, which catches straight away if it's in range.</summary>
        public void Hook(GrapplePoint point)
        {
            float distance = Vector3.Distance(TiePosition, point.Position);
            if (distance > MaxRange + 2f) return;

            Anchor = point;
            Held = point;
            RopeLength = distance;
            Sound.Play(Sounds.GrappleShot, volume: 0.6f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);
            Hooked?.Invoke(point);
        }

        /// <summary>Lets go of the rope, if holding one.</summary>
        public void Release()
        {
            if (Anchor == null) return;

            if (Held == Anchor) Held = null;
            Anchor = null;
            _fromInput = false;
            _rope?.Hide();
        }

        /// <summary>
        /// Applies the rope to the velocity the player is about to move with, over <paramref name="dt"/>. Slack, it does
        /// nothing. If the move would take them past the rope's length, it turns the velocity just enough to keep them on
        /// it, without changing their speed: what was carrying them away from the point carries them round it instead.
        /// </summary>
        public void Pull(ref Vector3 velocity, float dt)
        {
            if (Anchor == null || dt <= 0f) return;

            Vector3 anchor = Anchor.Position;
            Vector3 next = TiePosition + velocity * dt - anchor;
            float distance = next.Length();
            if (distance <= RopeLength || distance < 0.001f) return;

            // Split the velocity into along the rope and across it.
            Vector3 outward = next / distance;
            float along = Vector3.Dot(velocity, outward);
            Vector3 across = velocity - outward * along;

            // Along the rope: just enough to end the tick on it. Done every tick, that traces the swing.
            float alongNow = along - (distance - RopeLength) / dt;

            // Across it: the speed the rope took away along it goes here, so the rope only ever turns them. Except the pull of
            // gravity this tick, which the rope just holds up, as a real one would: passing that on would swing them higher
            // every time. Straight away from the point, with nothing across, there's no way to turn it, and the rope just
            // stops them.
            float held = MathF.Min(MathF.Max(along, 0f), MathF.Max(-outward.Y, 0f) * _player.Gravity * dt);
            float acrossSpeed = across.Length();
            float gained = (along - held) * (along + held) - alongNow * alongNow;
            if (acrossSpeed > 0.001f && gained > 0f) across *= MathF.Sqrt(acrossSpeed * acrossSpeed + gained) / acrossSpeed;

            velocity = across + outward * alongNow;
        }

        protected override void Update()
        {
            if (_player.IsDead || _player.Noclip)
            {
                Release();
                Targeted = null;
                return;
            }

            bool shift = !GameInput.Blocked && (InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight));
            if (!shift) _shiftUsed = false;

            // Let go when Shift is. One catch per press: holding it after letting go doesn't grab the next point.
            if (_fromInput && !shift) Release();

            Targeted = IsHooked || GameInput.LookBlocked ? null : FindTarget();
            if (!IsHooked && shift && !_shiftUsed && Targeted != null)
            {
                Hook(Targeted);
                Targeted = null;
                _fromInput = true;
                _shiftUsed = true;
            }

            if (Anchor != null) _rope.Show(HandPosition(), Anchor.Position);
            else _rope.Hide();

            DrawReticle();
        }

        /// <summary>The point nearest the middle of the crosshair, within range and in plain sight, if any.</summary>
        private GrapplePoint? FindTarget()
        {
            Transform eye = _player.Camera.Transform;
            Vector3 origin = eye.WorldPosition;
            Vector3 forward = eye.Forward;

            GrapplePoint? best = null;
            float bestAngle = float.MaxValue;
            foreach (GrapplePoint point in GrapplePoint.All)
            {
                Vector3 offset = point.Position - origin;
                float distance = offset.Length();
                if (distance > MaxRange || distance < 1f) continue;

                Vector3 direction = offset / distance;
                float angle = MathHelper.ToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(direction, forward), -1f, 1f)));
                float allowed = _aimAngle + MathHelper.ToDegrees(MathF.Atan(point.Radius / distance));
                if (angle > allowed || angle >= bestAngle) continue;

                if (GameObject.Scene.Physics.Raycast(origin, direction, distance - point.Radius, out _, ignore: _player.Controller)) continue;

                best = point;
                bestAngle = angle;
            }

            return best;
        }

        /// <summary>Where the rope comes from on screen: low and to the right, as if from a launcher on the wrist.</summary>
        private Vector3 HandPosition()
        {
            Transform eye = _player.Camera.Transform;
            return eye.WorldPosition + eye.Right * 0.22f - eye.Up * 0.25f + eye.Forward * 0.35f;
        }

        /// <summary>
        /// The circle on the point: while aimed at, four arcs closing in and turning round it with "SHIFT" underneath;
        /// while held, a steady ring with an arc round it that fills as they swing on past the point, turning orange around the
        /// point where letting go flings them furthest.
        /// </summary>
        private void DrawReticle()
        {
            GrapplePoint? point = Anchor ?? Targeted;
            if (point != _reticleOn)
            {
                _reticleOn = point;
                _reticleTime = 0f;
            }

            if (point == null || GameInput.LookBlocked) return;
            if (_player.Camera.WorldToScreen(point.Position, Canvas.Size) is not Vector2 centre) return;

            _reticleTime += Time.DeltaTime;
            float time = Time.TotalTime;

            if (Anchor == null)
            {
                float settle = MathF.Exp(-_reticleTime * 12f);
                float radius = 20f + 2.5f * MathF.Sin(time * 7f) + 30f * settle;
                Color color = _color with { A = 1f - settle };
                for (int i = 0; i < 4; i++) Canvas.DrawArc(centre, radius, time * 160f + i * 90f, 52f, 2.5f, color);
                Canvas.DrawArc(centre, 6f, 0f, 360f, 1.5f, color with { A = color.A * 0.7f });

                const string label = "SHIFT";
                float width = Canvas.MeasureText(Fonts.Bold, label, 12f).X;
                Canvas.DrawText(Fonts.Bold, label, centre + new Vector2(-width * 0.5f, radius + 6f), color, 12f, shadow: true);
                return;
            }

            // How far past the point they've swung, the way they're going, as a share of the rope.
            Vector3 heading = _player.Controller.Velocity with { Y = 0f };
            Vector3 offset = (TiePosition - Anchor.Position) with { Y = 0f };
            float past = heading.LengthSquared() > 0.01f ? Math.Clamp(Vector3.Dot(offset, Vector3.Normalize(heading)) / MathF.Max(RopeLength, 0.01f), 0f, 1f) : 0f;
            float warn = Math.Clamp((past - 0.4f) / 0.2f, 0f, 1f);
            Canvas.DrawArc(centre, 13f, 0f, 360f, 2.5f, _color);
            Canvas.DrawArc(centre, 21f, -90f, 360f, 1.5f, _color with { A = 0.25f });
            Canvas.DrawArc(centre, 21f, -90f, 360f * past, 3f, Color.Lerp(_color, _tautColor, warn));
        }
    }
}
