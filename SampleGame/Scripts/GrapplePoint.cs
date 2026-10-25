using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A glowing blue orb floating in the air that players can grapple onto (see <see cref="Grapple"/>). It isn't solid:
    /// you fly straight through it. It pulses gently, brightens while someone has it in their sights, and flares while
    /// they hang from it.
    /// </summary>
    public class GrapplePoint : Component
    {
        public static readonly Color DefaultColor = new(0.3f, 0.65f, 1f);

        /// <summary>Every grapple point in the scene.</summary>
        public static IReadOnlyList<GrapplePoint> All => _all;
        private static readonly List<GrapplePoint> _all = new();

        /// <summary>The size of the orb, which is also how close the crosshair has to be to aim at it.</summary>
        public float Radius { get; set; } = 1.2f;

        public Color Color { get; set; } = DefaultColor;

        /// <summary>Where the rope ties on: the middle of the orb.</summary>
        public Vector3 Position => Transform.WorldPosition;

        // How brightly the orb glows at rest, aimed at, and held.
        private const float _glow = 1.6f;
        private const float _targetedGlow = 4f;
        private const float _heldGlow = 7f;

        private Material? _material;
        private ParticleSystem _motes = null!;
        private float _glowNow = _glow;
        private float _phase;

        protected override void Start()
        {
            _all.Add(this);
            _phase = Random.Shared.NextSingle() * MathF.Tau;

            var orb = new GameObject("Orb");
            orb.Transform.Parent = Transform;
            orb.Transform.Scale = new Vector3(Radius * 2f);
            MeshRenderer renderer = orb.AddComponent<MeshRenderer>();
            renderer.Mesh = Primitives.Sphere;
            renderer.Material.Shading = ShadingModel.Unlit;
            renderer.Material.Color = Color;
            renderer.Material.Emission = Color;
            renderer.Material.EmissionStrength = _glow;
            renderer.CastShadows = false;
            _material = renderer.Material;

            // A slow drift of sparks round it, so it's easy to spot against the sky.
            var motes = new GameObject("Motes");
            motes.Transform.Parent = Transform;
            _motes = motes.AddComponent<ParticleSystem>();
            _motes.Rate = 10f;
            _motes.Area = new Vector3(Radius * 2.4f);
            _motes.Spread = 180f;
            _motes.Speed = new FloatRange(0.2f, 0.7f);
            _motes.Lifetime = new FloatRange(0.8f, 1.6f);
            _motes.Size = new FloatRange(0.05f, 0.1f);
            _motes.EndSize = 0.02f;
            _motes.StartColor = Color;
            _motes.EndColor = Color with { A = 0f };
            _motes.Glow = 2.5f;
        }

        protected override void OnDestroy() => _all.Remove(this);

        protected override void Update()
        {
            if (_material == null) return;

            bool held = Grapple.Held == this;
            float target = held ? _heldGlow : Grapple.Targeted == this ? _targetedGlow : _glow;
            _glowNow += (target - _glowNow) * (1f - MathF.Exp(-12f * Time.DeltaTime));
            _material.EmissionStrength = _glowNow * (1f + 0.15f * MathF.Sin(Time.TotalTime * 3f + _phase));
            _motes.Rate = held ? 40f : 10f;
        }
    }
}
