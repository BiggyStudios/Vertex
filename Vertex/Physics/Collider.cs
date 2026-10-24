using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// Gives a GameObject a collision shape. On its own a collider is static: other objects hit it but it never moves.
    /// Add a <see cref="Rigidbody"/> to the same GameObject to make it move. Sizes are scaled by the Transform, like meshes.
    /// Only the first enabled collider on a GameObject is used.
    /// </summary>
    public abstract class Collider : Component
    {
        private Vector3 _center;
        private float _friction = 0.5f;
        private float _bounciness;
        private bool _isTrigger;

        /// <summary>Offset of the shape from the GameObject's origin, in local space.</summary>
        public Vector3 Center
        {
            get => _center;
            set { _center = value; Changed(); }
        }

        /// <summary>0 = ice, 1 = rubber. Combined with the other collider's friction on contact.</summary>
        public float Friction
        {
            get => _friction;
            set { _friction = value; Changed(); }
        }

        /// <summary>0 = no bounce, 1 = bounces back at full speed. The bouncier of the two colliders wins.</summary>
        public float Bounciness
        {
            get => _bounciness;
            set { _bounciness = value; Changed(); }
        }

        /// <summary>
        /// A trigger doesn't block anything; it only reports when colliders enter and leave it,
        /// through <see cref="Component.OnTriggerEnter"/> and <see cref="Component.OnTriggerExit"/>.
        /// </summary>
        public bool IsTrigger
        {
            get => _isTrigger;
            set { _isTrigger = value; Changed(); }
        }

        /// <summary>Bumped whenever a setting changes, so the physics world knows to rebuild the body.</summary>
        internal int Version { get; private set; }

        private protected void Changed() => Version++;
    }
}
