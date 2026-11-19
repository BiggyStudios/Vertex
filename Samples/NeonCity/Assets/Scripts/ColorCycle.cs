using Vertex;
using Vertex.Rendering;

namespace NeonCity
{
    /// <summary>
    /// Fades the object's light, and the colour and glow of its mesh's material, round three colours in turn: a big
    /// advert that keeps changing, and the street it lights.
    /// </summary>
    public class ColorCycle : Component
    {
        public Color First { get; set; } = new(1f, 0.15f, 0.6f);
        public Color Second { get; set; } = new(0.1f, 0.75f, 1f);
        public Color Third { get; set; } = new(0.6f, 0.2f, 1f);

        /// <summary>How long each colour lasts, fading to the next for the last third of it.</summary>
        public float Seconds { get; set; } = 4f;

        /// <summary>Whether it colours the material's own colour too, not only its glow: for pictures, which it tints.</summary>
        public bool TintColor { get; set; }

        private Light? _light;
        private Material? _material;

        protected override void Start()
        {
            _light = GetComponent<Light>();
            _material = GetComponent<MeshRenderer>()?.Material;
        }

        protected override void Update()
        {
            Color[] colors = [First, Second, Third];
            float t = Time.TotalTime / MathF.Max(Seconds, 0.1f);
            int index = (int)MathF.Floor(t) % colors.Length;
            float fade = Math.Clamp((t - MathF.Floor(t) - 0.66f) / 0.34f, 0f, 1f);
            Color color = Color.Lerp(colors[index], colors[(index + 1) % colors.Length], fade * fade * (3f - 2f * fade));

            if (_light != null) _light.Color = color;
            if (_material == null) return;

            _material.Emission = color;
            if (TintColor) _material.Color = Color.Lerp(Color.White, color, 0.5f);
        }
    }
}
