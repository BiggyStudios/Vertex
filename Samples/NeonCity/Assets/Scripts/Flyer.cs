using System.Numerics;

using Vertex;

namespace NeonCity
{
    /// <summary>
    /// A flying car: glides along its forward direction, bobbing gently, and once it's gone <see cref="Distance"/> it starts
    /// again from where it began, out of sight in the fog.
    /// </summary>
    public class Flyer : Component
    {
        public float Speed { get; set; } = 18f;
        public float Distance { get; set; } = 300f;

        /// <summary>How far along it starts, so cars on the same path are spread out.</summary>
        public float Offset { get; set; }

        private Vector3 _from;
        private Vector3 _forward;

        protected override void Start()
        {
            _from = Transform.Position;
            _forward = Transform.Forward;
        }

        protected override void Update()
        {
            float along = (Offset + Time.TotalTime * Speed) % MathF.Max(Distance, 1f);
            float bob = MathF.Sin(Time.TotalTime * 0.9f + Offset) * 0.4f;
            Transform.Position = _from + _forward * along + new Vector3(0f, bob, 0f);
        }
    }
}
