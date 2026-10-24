namespace Vertex.Physics
{
    /// <summary>
    /// A capsule (a cylinder with rounded ends) standing upright along Y, centred on the GameObject plus
    /// <see cref="Collider.Center"/>. The usual shape for characters.
    /// </summary>
    public class CapsuleCollider : Collider
    {
        private float _radius = 0.5f;
        private float _height = 2f;

        public float Radius
        {
            get => _radius;
            set
            {
                if (value <= 0f) throw new ArgumentOutOfRangeException(nameof(value), "Radius must be greater than zero.");

                _radius = value;
                Changed();
            }
        }

        /// <summary>Full height, including the rounded ends. Shorter than twice the radius makes a sphere.</summary>
        public float Height
        {
            get => _height;
            set
            {
                if (value <= 0f) throw new ArgumentOutOfRangeException(nameof(value), "Height must be greater than zero.");

                _height = value;
                Changed();
            }
        }
    }
}
