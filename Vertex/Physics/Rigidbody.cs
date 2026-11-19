using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// Makes a GameObject's <see cref="Collider"/> move under physics: gravity, forces and collisions move its Transform.
    /// Needs a collider on the same GameObject. Use it on root objects, or children of objects that don't move.
    /// </summary>
    public class Rigidbody : Component
    {
        private float _mass = 1f;
        private bool _useGravity = true;
        private bool _isKinematic;
        private float _drag = 0.05f;
        private float _angularDrag = 0.05f;
        private bool _continuousCollision;

        // Velocities are read back from the simulation after every step. Writes are sent before the next one.
        internal Vector3 CurrentVelocity;
        internal Vector3 CurrentAngularVelocity;
        internal bool VelocityChanged;
        internal bool AngularVelocityChanged;

        // Forces and impulses wait here until the next physics step.
        internal Vector3 PendingForce;
        internal Vector3 PendingImpulse;
        internal Vector3 PendingTorque;

        /// <summary>In kilograms. Heavier objects push lighter ones around.</summary>
        public float Mass
        {
            get => _mass;
            set
            {
                if (value <= 0f) throw new ArgumentOutOfRangeException(nameof(value), "Mass must be greater than zero.");

                _mass = value;
                Changed();
            }
        }

        public bool UseGravity
        {
            get => _useGravity;
            set { _useGravity = value; Changed(); }
        }

        /// <summary>
        /// A kinematic body ignores forces and gravity and follows its Transform instead, so you move it from code
        /// (a moving platform, a door, a player you move yourself). It still pushes other bodies out of the way.
        /// </summary>
        public bool IsKinematic
        {
            get => _isKinematic;
            set { _isKinematic = value; Changed(); }
        }

        /// <summary>How quickly movement slows down on its own, like air resistance. 0 = never.</summary>
        public float Drag
        {
            get => _drag;
            set { _drag = value; Changed(); }
        }

        /// <summary>How quickly spinning slows down on its own. 0 = never.</summary>
        public float AngularDrag
        {
            get => _angularDrag;
            set { _angularDrag = value; Changed(); }
        }

        /// <summary>
        /// Sweeps the body along its path each step, so fast objects (bullets, thrown balls) can't pass through thin walls.
        /// Costs more, so only turn it on for objects that need it.
        /// </summary>
        public bool ContinuousCollision
        {
            get => _continuousCollision;
            set { _continuousCollision = value; Changed(); }
        }

        /// <summary>
        /// Physics runs at a fixed rate, which can be slower than the screen refreshes. Interpolating draws the body
        /// smoothly between its last two physics positions instead of jumping from one to the next.
        /// It draws up to one physics step behind, which is rarely noticeable.
        /// </summary>
        public bool Interpolate { get; set; } = true;

        /// <summary>
        /// World axes it can't move along: a door that only swings, a puck kept on the table (Y), a 2D game's objects kept
        /// in the picture's plane (Z). Collisions and forces along them are ignored.
        /// </summary>
        [Tooltip("World axes it can't move along: Y keeps a puck on its table, Z keeps a 2D game's objects in the picture")]
        public Axes LockPosition
        {
            get => _lockPosition;
            set { _lockPosition = value; Changed(); }
        }

        /// <summary>World axes it can't turn about: lock X and Z to keep it upright, Y as well to stop it turning at all.</summary>
        [Tooltip("World axes it can't turn about: X and Z keep it upright")]
        public Axes LockRotation
        {
            get => _lockRotation;
            set { _lockRotation = value; Changed(); }
        }

        private Axes _lockPosition;
        private Axes _lockRotation;

        /// <summary>Axes it's always locked along, whatever <see cref="LockPosition"/> says: Z, for a 2D one.</summary>
        internal virtual Axes AlwaysLockedPosition => Axes.None;

        /// <summary>Axes it's always locked about, whatever <see cref="LockRotation"/> says: X and Y, for a 2D one.</summary>
        internal virtual Axes AlwaysLockedRotation => Axes.None;

        /// <summary>In units per second, world space.</summary>
        [Serialization.DontSave]
        public Vector3 Velocity
        {
            get => CurrentVelocity;
            set
            {
                CurrentVelocity = value;
                VelocityChanged = true;
            }
        }

        /// <summary>In radians per second around each world axis.</summary>
        [Serialization.DontSave]
        public Vector3 AngularVelocity
        {
            get => CurrentAngularVelocity;
            set
            {
                CurrentAngularVelocity = value;
                AngularVelocityChanged = true;
            }
        }

        /// <summary>A push in newtons over the next physics step. Call it from FixedUpdate every step for a steady force.</summary>
        public void AddForce(Vector3 force) => PendingForce += force;

        /// <summary>An instant kick (mass × change in velocity), e.g. a jump or an explosion.</summary>
        public void AddImpulse(Vector3 impulse) => PendingImpulse += impulse;

        /// <summary>A twisting force in newton-metres over the next physics step.</summary>
        public void AddTorque(Vector3 torque) => PendingTorque += torque;

        /// <summary>Bumped whenever a setting changes, so the physics world knows to rebuild the body.</summary>
        internal int Version { get; private set; }

        private protected void Changed() => Version++;
    }

    /// <summary>Some of the X, Y and Z axes: which ones a <see cref="Rigidbody"/> is locked along, say.</summary>
    [Flags]
    public enum Axes
    {
        None = 0,
        X = 1,
        Y = 2,
        Z = 4,
        All = X | Y | Z
    }
}
