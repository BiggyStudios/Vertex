using Vertex;
using Vertex.Rendering;

namespace NeonCity
{
    /// <summary>How a <see cref="Flicker"/> flickers.</summary>
    public enum FlickerStyle
    {
        /// <summary>On, but now and then it stutters, like a neon tube on its way out.</summary>
        Buzz,

        /// <summary>On and off in a steady beat, like a warning light on a roof.</summary>
        Blink
    }

    /// <summary>
    /// Flickers the object's light and its glow (the emission of its mesh's material) together, as a failing neon sign
    /// or a blinking warning light does.
    /// </summary>
    public class Flicker : Component
    {
        public FlickerStyle Style { get; set; } = FlickerStyle.Buzz;

        /// <summary>Buzz: how often it stutters, about every this many seconds. Blink: seconds from one blink to the next.</summary>
        public float Seconds { get; set; } = 3f;

        /// <summary>How dark it goes when it's off, from 0 (out) to 1 (no darker).</summary>
        [Range(0f, 1f)]
        public float Low { get; set; } = 0.08f;

        private Light? _light;
        private Material? _material;
        private float _intensity;
        private float _emission;
        private float _stutter;
        private float _next;

        protected override void Start()
        {
            _light = GetComponent<Light>();
            _material = GetComponent<MeshRenderer>()?.Material;
            _intensity = _light?.Intensity ?? 0f;
            _emission = _material?.EmissionStrength ?? 0f;
            _next = Random.Shared.NextSingle() * Seconds;
        }

        protected override void Update()
        {
            float brightness = Style == FlickerStyle.Blink ? Blink() : Buzz();
            if (_light != null) _light.Intensity = _intensity * brightness;
            if (_material != null) _material.EmissionStrength = _emission * brightness;
        }

        private float Blink()
        {
            float t = Time.TotalTime % MathF.Max(Seconds, 0.1f) / MathF.Max(Seconds, 0.1f);
            return t < 0.18f ? 1f : Low;
        }

        // Mostly on; every so often a burst of quick drops for a moment.
        private float Buzz()
        {
            _next -= Time.DeltaTime;
            if (_next <= 0f)
            {
                _stutter = 0.1f + Random.Shared.NextSingle() * 0.5f;
                _next = Seconds * (0.5f + Random.Shared.NextSingle());
            }

            if (_stutter <= 0f) return 1f;

            _stutter -= Time.DeltaTime;
            return Random.Shared.NextSingle() < 0.55f ? Low + Random.Shared.NextSingle() * 0.3f : 1f;
        }
    }
}
