using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// The closest collider a ray hit, from <see cref="PhysicsWorld.Raycast"/>. <see cref="Normal"/> is the way the
    /// surface faces where it was hit.
    /// </summary>
    public readonly record struct RaycastHit(Collider Collider, Vector3 Point, float Distance, Vector3 Normal)
    {
        public GameObject GameObject => Collider.GameObject;
    }
}
