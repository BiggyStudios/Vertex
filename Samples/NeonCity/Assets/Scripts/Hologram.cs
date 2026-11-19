using System.Numerics;

using Vertex;

namespace NeonCity
{
    /// <summary>Turns slowly and floats up and down: a hologram over a doorway.</summary>
    public class Hologram : Component
    {
        /// <summary>Degrees a second it turns.</summary>
        public float Spin { get; set; } = 35f;

        /// <summary>How far up and down it floats.</summary>
        public float Bob { get; set; } = 0.12f;

        private Vector3 _home;

        protected override void Start()
        {
            _home = Transform.Position;
        }

        protected override void Update()
        {
            Transform.Position = _home + new Vector3(0f, MathF.Sin(Time.TotalTime * 1.3f) * Bob, 0f);
            Transform.EulerAngles = new Vector3(0f, Time.TotalTime * Spin % 360f, 0f);
        }
    }
}
