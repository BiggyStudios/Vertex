using Vertex.Rendering;

namespace Vertex.Physics
{
    /// <summary>
    /// A collider made of a mesh's exact triangles, so it can be any shape, dents and curves included (curved ramps,
    /// arches, terrain). Only for scenery that never moves: a Rigidbody on the same object is ignored. Sliding along it is
    /// smooth across the seams between triangles. Without a mesh it does nothing.
    /// </summary>
    public class MeshCollider : Collider
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
