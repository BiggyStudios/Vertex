using System.Numerics;

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

        // Where its mesh's box is in the world, kept until it moves or the mesh changes: worked out for every renderer
        // every frame otherwise, for culling and shadows.
        private Mesh? _boundsMesh;
        private int _boundsVersion = -1;
        private Vector3 _boundsOf, _boundsSize, _worldCentre, _worldExtents;

        /// <summary>The box round <paramref name="mesh"/> (what's drawn for it) in the world, and its world matrix.</summary>
        internal void WorldBounds(Mesh mesh, out Matrix4x4 world, out Vector3 centre, out Vector3 extents)
        {
            world = Transform.WorldMatrix;
            int version = Transform.Version;
            if (version != _boundsVersion || mesh != _boundsMesh || mesh.BoundsCentre != _boundsOf || mesh.BoundsExtents != _boundsSize)
            {
                Culling.WorldBounds(mesh, world, out _worldCentre, out _worldExtents);
                _boundsVersion = version;
                _boundsMesh = mesh;
                _boundsOf = mesh.BoundsCentre;
                _boundsSize = mesh.BoundsExtents;
            }

            centre = _worldCentre;
            extents = _worldExtents;
        }
    }
}
