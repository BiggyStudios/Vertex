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
    }
}
