using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>The closest collider a ray hit, from <see cref="PhysicsWorld.Raycast"/>.</summary>
    public readonly record struct RaycastHit(Collider Collider, Vector3 Point, float Distance)
    {
        public GameObject GameObject => Collider.GameObject;
    }
}
