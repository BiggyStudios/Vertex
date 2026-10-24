using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>A box collider. The default size matches <see cref="Rendering.Primitives.Cube"/>.</summary>
    public class BoxCollider : Collider
    {
        private Vector3 _size = Vector3.One;

        /// <summary>Full size of the box along each local axis, before the Transform's scale.</summary>
        public Vector3 Size
        {
            get => _size;
            set
            {
                if (value.X <= 0f || value.Y <= 0f || value.Z <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(value), "Box size must be greater than zero on every axis.");

                _size = value;
                Changed();
            }
        }
    }
}
