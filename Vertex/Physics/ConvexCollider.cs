using Vertex.Rendering;

namespace Vertex.Physics
{
    /// <summary>
    /// A collider shrink-wrapped around a mesh's vertices, for shapes a box or sphere can't match (ramps, wedges, rocks).
    /// Any dents or holes in the mesh are filled in: the collider is always convex. Without a mesh it does nothing.
    /// </summary>
    public class ConvexCollider : Collider
    {
        private Mesh? _mesh;

        /// <summary>Usually the same mesh the GameObject's MeshRenderer draws.</summary>
        public Mesh? Mesh
        {
            get => _mesh;
            set { _mesh = value; Changed(); }
        }
    }
}
