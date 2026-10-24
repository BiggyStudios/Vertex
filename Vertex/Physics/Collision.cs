using System.Numerics;

namespace Vertex.Physics
{
    internal enum ContactPhase
    {
        Enter,
        Stay,
        Exit
    }

    /// <summary>Details of a contact, passed to OnCollisionEnter, OnCollisionStay and OnCollisionExit.</summary>
    public class Collision
    {
        /// <summary>The collider that was hit.</summary>
        public Collider Collider { get; }

        /// <summary>The other object's rigidbody, or null if it's static.</summary>
        public Rigidbody? Rigidbody { get; }

        public GameObject GameObject => Collider.GameObject;

        /// <summary>Where the two touch, in world space. On exit, where they last touched.</summary>
        public Vector3 Point { get; }

        /// <summary>
        /// Points away from the other object, towards this one. When standing on the ground it points up,
        /// so <c>Normal.Y &gt; 0.7f</c> is a simple "am I on the ground" test.
        /// </summary>
        public Vector3 Normal { get; }

        internal Collision(Collider collider, Rigidbody? rigidbody, Vector3 point, Vector3 normal)
        {
            Collider = collider;
            Rigidbody = rigidbody;
            Point = point;
            Normal = normal;
        }
    }
}
