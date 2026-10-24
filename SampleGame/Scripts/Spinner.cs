using System.Numerics;

using Vertex;

namespace SampleGame
{
    /// <summary>A small reusable component: spins its GameObject.</summary>
    public class Spinner : Component
    {
        /// <summary>Degrees per second around each axis.</summary>
        public Vector3 Speed { get; set; } = new Vector3(0f, 45f, 0f);

        protected override void Update()
        {
            Transform.Rotate(Speed * Time.DeltaTime);
        }
    }
}
