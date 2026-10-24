namespace Vertex.Physics
{
    /// <summary>
    /// A sphere collider. The default radius matches <see cref="Rendering.Primitives.Sphere"/>.
    /// Spheres stay round, so a non-uniform scale uses its largest axis.
    /// </summary>
    public class SphereCollider : Collider
    {
        private float _radius = 0.5f;

        /// <summary>Radius before the Transform's scale.</summary>
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
    }
}
