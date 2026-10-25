using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// Moves a capsule through the world for players and NPCs: slides along walls, walks up steps, sticks to the
    /// ground going down slopes and pushes rigidbodies out of the way. It does no movement of its own: set
    /// <see cref="Velocity"/> (gravity, acceleration, jumping, ...) and call <see cref="Move"/> from FixedUpdate.
    /// The Transform's position is the bottom of the capsule. Don't add a Collider or Rigidbody to the same GameObject.
    /// It counts as a collider for triggers and for rigidbodies hitting it, which report it as the other collider.
    /// </summary>
    public class CharacterController : Collider
    {
        private float _radius = 0.4f;
        private float _height = 1.8f;
        private float _maxSlopeAngle = 45.57f;
        private float _mass = 80f;
        private float _pushStrength = 300f;

        public float Radius
        {
            get => _radius;
            set { _radius = value; Changed(); }
        }

        /// <summary>Full height of the capsule, including the rounded ends. At least twice the radius.</summary>
        public float Height
        {
            get => _height;
            set { _height = value; Changed(); }
        }

        /// <summary>The highest step that can be walked straight up while on the ground.</summary>
        public float StepHeight { get; set; } = 0.45f;

        /// <summary>Steeper slopes, in degrees, don't count as ground: the character slides down them.</summary>
        public float MaxSlopeAngle
        {
            get => _maxSlopeAngle;
            set { _maxSlopeAngle = value; Changed(); }
        }

        /// <summary>In kilograms. Used when the character pushes rigidbodies or stands on them.</summary>
        public float Mass
        {
            get => _mass;
            set { _mass = value; Changed(); }
        }

        /// <summary>The most force, in newtons, the character pushes rigidbodies with.</summary>
        public float PushStrength
        {
            get => _pushStrength;
            set { _pushStrength = value; Changed(); }
        }

        /// <summary>
        /// In units per second. <see cref="Move"/> moves by this, then removes any part of it that went into
        /// something it hit, so landing stops falling and running into a wall stops movement into the wall.
        /// </summary>
        public Vector3 Velocity { get; set; }

        /// <summary>Standing on something no steeper than <see cref="MaxSlopeAngle"/>. Updated by <see cref="Move"/>.</summary>
        public bool IsGrounded { get; internal set; }

        /// <summary>The surface direction of what's underneath, or straight up when there's nothing.</summary>
        public Vector3 GroundNormal { get; internal set; } = Vector3.UnitY;

        /// <summary>The collider being stood on, if <see cref="IsGrounded"/> and it's one of the scene's colliders.</summary>
        public Collider? GroundCollider { get; internal set; }

        /// <summary>
        /// How fast the ground underneath is moving, where it's being stood on: zero on still ground, and a moving
        /// platform's (or a kinematic body's) velocity on one. Characters that should ride along add it to their own.
        /// </summary>
        public Vector3 GroundVelocity { get; internal set; }

        /// <summary>Moves by <see cref="Velocity"/> over <paramref name="deltaTime"/>. Call it once per FixedUpdate.</summary>
        public void Move(float deltaTime) => GameObject.Scene.Physics.MoveCharacter(this, deltaTime);
    }
}
