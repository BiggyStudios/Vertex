using System.Numerics;

using Vertex;
using Vertex.Audio;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A booster on a surf ramp, marked by glowing arrows: surfing through it adds <see cref="Boost"/> to your speed along
    /// the ramp, whichever way along it you're going. Needs a trigger collider covering that stretch of the ramp, with the
    /// GameObject's forward (-Z) along the ramp.
    /// </summary>
    public class SurfBooster : Component
    {
        // So brushing the edge of the trigger, in and out and back in, doesn't boost twice.
        private const float _cooldown = 0.4f;

        private const float _glow = 1.6f;
        private const float _flareGlow = 5f;

        /// <summary>Metres per second added each time through: 500 units a second, a strong push.</summary>
        public float Boost { get; set; } = 500f * 0.0254f;

        /// <summary>It never boosts you past this speed across the ground, in metres per second.</summary>
        public float MaxSpeed { get; set; } = 3000f * 0.0254f;

        /// <summary>The arrows' material, which flares when someone goes through. Optional.</summary>
        public Material? Arrows { get; set; }

        private float _sinceBoost = float.PositiveInfinity;

        protected override void Start()
        {
            if (Arrows != null) Arrows.EmissionStrength = _glow;
        }

        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is not Player { IsDead: false } player || _sinceBoost < _cooldown) return;

            // Along the ramp, the way they're already going along it; straight across it counts as the way the arrows point.
            Vector3 along = Vector3.Normalize(Transform.Forward with { Y = 0f });
            Vector3 velocity = player.Controller.Velocity;
            if (Vector3.Dot(velocity, along) < -0.5f) along = -along;

            Vector3 across = velocity with { Y = 0f };
            float speed = across.Length();
            float boost = MathF.Min(Boost, MaxSpeed - speed);
            if (boost <= 0f) return;

            player.Push(along * boost);
            Sound.PlayAt(Sounds.Bounce, player.Transform.WorldPosition, volume: 0.35f, pitch: 1.5f + Random.Shared.NextSingle() * 0.1f);
            _sinceBoost = 0f;
        }

        protected override void Update()
        {
            _sinceBoost += Time.DeltaTime;
            if (Arrows == null) return;

            float flare = MathF.Max(1f - _sinceBoost * 2.5f, 0f);
            Arrows.EmissionStrength = _glow + (_flareGlow - _glow) * flare * flare;
        }
    }
}
