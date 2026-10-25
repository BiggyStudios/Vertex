namespace Vertex.Rendering
{
    /// <summary>
    /// Draws a mesh at its GameObject's transform.
    /// </summary>
    public class MeshRenderer : Component
    {
        public Mesh? Mesh { get; set; }

        /// <summary>Each renderer gets its own material, so changing it only affects this object.</summary>
        public Material Material { get; set; } = new Material();

        /// <summary>
        /// Whether this blocks sunlight. Turn off for things that shouldn't, like a first-person gun or the
        /// player's own body. Unlit materials never cast shadows.
        /// </summary>
        public bool CastShadows { get; set; } = true;
    }
}
