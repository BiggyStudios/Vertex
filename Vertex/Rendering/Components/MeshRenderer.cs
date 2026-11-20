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

        /// <summary>
        /// For things that never move (walls, floors, buildings, furniture): their light can be baked (the scene's
        /// Lighting › Bake), so they get soft shadows from baked lights, light bounced off what's round them and the sky's
        /// light kept out of corners and rooms, for almost nothing while the game runs. Their light bounces onto others
        /// too. Moving it once it's baked leaves its light where it was: bake again.
        /// </summary>
        [Tooltip("Never moves: its light is baked (Lighting > Bake), with soft shadows and light bounced round the scene, for almost nothing while the game runs")]
        public bool Static { get; set; }

        /// <summary>Which of the scene's baked meshes this is (see <see cref="BakedLighting"/>), or -1 if it hasn't been baked. Set by baking.</summary>
        [HideInInspector]
        public int LightmapIndex { get; set; } = -1;

        /// <summary>The mesh as it's drawn this frame: <see cref="Mesh"/>, or for a skinned mesh, it bent to its bones.</summary>
        internal virtual Mesh? DrawnMesh => Mesh;
    }
}
