using System.Numerics;

using Vertex;
using Vertex.Audio;
using Vertex.Physics;

namespace SampleGame
{
    /// <summary>
    /// Throws the player into the air when they touch it, like a jump pad. Needs a trigger collider. It always sends
    /// them <see cref="Height"/> above the pad. With a <see cref="ForwardSpeed"/>, it also throws them that fast in
    /// whichever direction they're facing, replacing their own speed across the ground: the same throw every time, so
    /// it's up to them to face the right way and air strafe onto where they want to land. Without one, it bounces them
    /// straight up and they keep their speed across the ground, so they can run or surf over it.
    /// </summary>
    public class BouncePad : Component
    {
        /// <summary>How high the throw peaks above the pad, in metres.</summary>
        public float Height { get; set; } = 8f;

        /// <summary>How fast it throws the player the way they're facing, in metres per second. 0 bounces straight up.</summary>
        public float ForwardSpeed { get; set; }

        /// <summary>The glowing top, which squashes and springs back when used. Optional.</summary>
        public GameObject? Top { get; set; }

        private float _bounce;
        private Vector3 _topScale;

        protected override void Start()
        {
            if (Top != null) _topScale = Top.Transform.Scale;
        }

        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is not Player { IsDead: false } player) return;

            Vector3 velocity = player.Controller.Velocity;
            if (ForwardSpeed > 0f)
            {
                Vector3 facing = Vector3.Normalize(player.Transform.Forward with { Y = 0f });
                velocity = facing * ForwardSpeed;
            }

            // Measured from the pad, not the player: falling back onto it sets it off a little way up, and should
            // still send you to the same height rather than higher every bounce.
            float rise = MathF.Max(Transform.WorldPosition.Y + Height - player.Transform.WorldPosition.Y, 0.1f);
            velocity.Y = MathF.Sqrt(2f * player.Gravity * rise);

            player.Launch(velocity);
            Sound.PlayAt(Sounds.Bounce, Transform.WorldPosition, volume: 0.8f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);
            _bounce = 1f;
        }

        /// <summary>
        /// The forward speed that carries a throw peaking <paramref name="height"/> above the pad
        /// <paramref name="distance"/> across the ground, to land <paramref name="landingRise"/> above the pad, with no
        /// air strafing, under <paramref name="gravity"/>. It rises to the peak and falls to the landing height, crossing
        /// the ground at a steady speed in the time that takes.
        /// </summary>
        public static float SpeedToReach(float distance, float landingRise, float height, float gravity)
        {
            float flightTime = MathF.Sqrt(2f * height / gravity) + MathF.Sqrt(2f * (height - landingRise) / gravity);
            return distance / flightTime;
        }

        protected override void Update()
        {
            if (Top == null) return;

            // Squash flat, then spring back with a little wobble.
            _bounce = MathF.Max(_bounce - Time.DeltaTime * 2.5f, 0f);
            float wobble = _bounce * _bounce * MathF.Sin(_bounce * 18f);
            Top.Transform.Scale = _topScale * new Vector3(1f + wobble * 0.15f, 1f + wobble * 1.5f, 1f + wobble * 0.15f);
        }
    }
}
