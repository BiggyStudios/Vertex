using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// Joins this object's <see cref="Rigidbody"/> to another (<see cref="ConnectedBody"/>), or to a fixed point in the
    /// world if there's none: a door to its frame, a wheel to a car, a lamp to the ceiling. The kinds of joint differ in
    /// how the two may still move: <see cref="FixedJoint"/> not at all, <see cref="HingeJoint"/> turning about an axis,
    /// <see cref="SliderJoint"/> along one, <see cref="BallJoint"/> turning any way, <see cref="DistanceJoint"/> within a
    /// reach of each other, <see cref="SpringJoint"/> pulled back to a length.
    /// <para>
    /// A joint holds the two as they are when it's made (when the scene starts, or the joint is added or switched on),
    /// so place them where they should be joined first. Changing a setting while the game runs makes it again from
    /// where they are then; the motor and spring settings change in place.
    /// </para>
    /// </summary>
    public abstract class Joint : Component
    {
        private Rigidbody? _connectedBody;
        private Vector3 _anchor;
        private float _breakForce;
        private bool _collideConnected;

        /// <summary>The body it's joined to. None joins it to the world, where the joint is when it's made.</summary>
        [Tooltip("The body it's joined to. None joins it to a fixed point in the world")]
        public Rigidbody? ConnectedBody
        {
            get => _connectedBody;
            set { _connectedBody = value; Changed(); }
        }

        /// <summary>Where the joint is, in this object's own space: the hinge's pin, the end of the rope.</summary>
        [Tooltip("Where the joint is, from this object's middle in its own space: a hinge's pin, a rope's end")]
        public Vector3 Anchor
        {
            get => _anchor;
            set { _anchor = value; Changed(); }
        }

        /// <summary>Pulled harder than this, in newtons, it breaks (see <see cref="IsBroken"/>). 0 never breaks.</summary>
        [Tooltip("Pulled harder than this, in newtons, it breaks and lets go. 0 never breaks")]
        public float BreakForce
        {
            get => _breakForce;
            set { _breakForce = value; Changed(); }
        }

        /// <summary>Whether the two joined bodies still bump into each other. Off, they pass through each other.</summary>
        [Tooltip("Whether the two joined bodies still bump into each other")]
        public bool CollideConnected
        {
            get => _collideConnected;
            set { _collideConnected = value; Changed(); }
        }

        /// <summary>Whether it's broken (pulled harder than <see cref="BreakForce"/>): it holds nothing until <see cref="Repair"/>.</summary>
        [Serialization.DontSave, HideInInspector]
        public bool IsBroken { get; internal set; }

        /// <summary>Raised when it breaks. Its object's components hear <see cref="Component.OnJointBreak"/> too.</summary>
        public event Action<Joint>? Broken;

        /// <summary>
        /// How hard the joint pulled on the bodies in the last physics step, in newtons: what <see cref="BreakForce"/> is
        /// measured against.
        /// </summary>
        [Serialization.DontSave, HideInInspector]
        public float CurrentForce { get; internal set; }

        /// <summary>Makes a broken joint hold again, from where the two are now.</summary>
        public void Repair()
        {
            IsBroken = false;
            Changed();
        }

        internal void Break()
        {
            IsBroken = true;
            Broken?.Invoke(this);
            GameObject.SendJointBreak(this);
        }

        /// <summary>Bumped whenever a setting changes, so the physics world knows to make the joint again.</summary>
        internal int Version { get; private set; }

        private protected void Changed() => Version++;

        /// <summary>A direction kept unit length, or <paramref name="fallback"/> if it has no length.</summary>
        internal static Vector3 Direction(Vector3 value, Vector3 fallback) => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;
    }

    /// <summary>
    /// Holds the two bodies together as if they were one: neither moves or turns apart from the other. With a
    /// <see cref="Joint.BreakForce"/>, a wall that a hit can knock a piece off.
    /// </summary>
    public sealed class FixedJoint : Joint;

    /// <summary>What drives a <see cref="HingeJoint"/> or <see cref="SliderJoint"/> round or along on its own.</summary>
    public enum JointMotor
    {
        /// <summary>Nothing: it moves only as it's pushed.</summary>
        Off,

        /// <summary>Turns or slides at a speed: a wheel, a conveyor.</summary>
        Speed,

        /// <summary>Turns or slides to a place and holds it there, springing back if pushed: a door closer, a lift.</summary>
        Target
    }

    /// <summary>
    /// Lets the two bodies turn about one axis only, like a door on its hinges or a wheel on its axle. Its angle can be
    /// kept between limits, and a motor can turn it.
    /// </summary>
    public sealed class HingeJoint : Joint
    {
        private Vector3 _axis = Vector3.UnitY;
        private bool _useLimits;
        private float _minAngle = -90f;
        private float _maxAngle = 90f;
        private float _friction;

        /// <summary>The axis it turns about, in this object's own space: up (0, 1, 0) for a door, sideways for a wheel.</summary>
        [Tooltip("The axis it turns about, in this object's own space: up for a door, sideways for a wheel")]
        public Vector3 Axis
        {
            get => _axis;
            set { _axis = value; Changed(); }
        }

        [Header("Limits")]
        [Tooltip("Keeps its angle between Min Angle and Max Angle")]
        public bool UseLimits
        {
            get => _useLimits;
            set { _useLimits = value; Changed(); }
        }

        /// <summary>In degrees, from where it started: -180 to 0.</summary>
        [Tooltip("How far it can turn one way, in degrees from where it started")]
        [Range(-180f, 0f)]
        public float MinAngle
        {
            get => _minAngle;
            set { _minAngle = value; Changed(); }
        }

        /// <summary>In degrees, from where it started: 0 to 180.</summary>
        [Tooltip("How far it can turn the other way, in degrees from where it started")]
        [Range(0f, 180f)]
        public float MaxAngle
        {
            get => _maxAngle;
            set { _maxAngle = value; Changed(); }
        }

        /// <summary>How stiff it is to turn, as a turning force (newton-metres) it has to overcome. 0 turns freely.</summary>
        [Tooltip("How stiff it is to turn: the twisting force it takes to turn it at all, in newton-metres")]
        public float Friction
        {
            get => _friction;
            set { _friction = value; Changed(); }
        }

        [Header("Motor")]
        [Tooltip("Turns it on its own: at a speed, or to a target angle and holds it there")]
        public JointMotor Motor { get; set; }

        /// <summary>For a <see cref="JointMotor.Speed"/> motor: degrees a second.</summary>
        [Tooltip("For a Speed motor: how fast it turns, in degrees a second")]
        public float MotorSpeed { get; set; } = 90f;

        /// <summary>For a <see cref="JointMotor.Target"/> motor: the angle it turns to, in degrees from where it started.</summary>
        [Tooltip("For a Target motor: the angle it turns to and holds, in degrees from where it started")]
        public float TargetAngle { get; set; }

        /// <summary>The most turning force the motor has, in newton-metres.</summary>
        [Tooltip("The most twisting force the motor has, in newton-metres")]
        public float MotorStrength { get; set; } = 100f;

        /// <summary>How far it has turned from where it started, in degrees.</summary>
        [Serialization.DontSave, HideInInspector]
        public float Angle { get; internal set; }

        internal Vector3 WorldAxis => Direction(Vector3.TransformNormal(Direction(_axis, Vector3.UnitY), Transform.WorldMatrix), Vector3.UnitY);
    }

    /// <summary>
    /// Lets the two bodies slide apart along one axis only, without turning: a drawer, a piston, a lift on its rails.
    /// How far can be kept between limits, and a motor can move it.
    /// </summary>
    public sealed class SliderJoint : Joint
    {
        private Vector3 _axis = Vector3.UnitX;
        private bool _useLimits;
        private float _minDistance = -1f;
        private float _maxDistance = 1f;
        private float _friction;

        /// <summary>The axis it slides along, in this object's own space.</summary>
        [Tooltip("The axis it slides along, in this object's own space")]
        public Vector3 Axis
        {
            get => _axis;
            set { _axis = value; Changed(); }
        }

        [Header("Limits")]
        [Tooltip("Keeps it between Min Distance and Max Distance from where it started")]
        public bool UseLimits
        {
            get => _useLimits;
            set { _useLimits = value; Changed(); }
        }

        /// <summary>How far it can slide back along the axis from where it started, in metres: 0 or less.</summary>
        [Tooltip("How far it can slide back along the axis, in metres (0 or less)")]
        public float MinDistance
        {
            get => _minDistance;
            set { _minDistance = value; Changed(); }
        }

        /// <summary>How far it can slide on along the axis from where it started, in metres: 0 or more.</summary>
        [Tooltip("How far it can slide on along the axis, in metres (0 or more)")]
        public float MaxDistance
        {
            get => _maxDistance;
            set { _maxDistance = value; Changed(); }
        }

        /// <summary>How stiff it is to slide, as a force in newtons it has to overcome. 0 slides freely.</summary>
        [Tooltip("How stiff it is to slide: the force it takes to move it at all, in newtons")]
        public float Friction
        {
            get => _friction;
            set { _friction = value; Changed(); }
        }

        [Header("Motor")]
        [Tooltip("Moves it on its own: at a speed, or to a target place and holds it there")]
        public JointMotor Motor { get; set; }

        /// <summary>For a <see cref="JointMotor.Speed"/> motor: metres a second.</summary>
        [Tooltip("For a Speed motor: how fast it slides, in metres a second")]
        public float MotorSpeed { get; set; } = 1f;

        /// <summary>For a <see cref="JointMotor.Target"/> motor: where it slides to, in metres along the axis from where it started.</summary>
        [Tooltip("For a Target motor: where it slides to and holds, in metres from where it started")]
        public float TargetPosition { get; set; }

        /// <summary>The most force the motor has, in newtons.</summary>
        [Tooltip("The most force the motor has, in newtons")]
        public float MotorStrength { get; set; } = 1000f;

        /// <summary>How far it has slid from where it started, in metres along the axis.</summary>
        [Serialization.DontSave, HideInInspector]
        public float Position { get; internal set; }

        internal Vector3 WorldAxis => Direction(Vector3.TransformNormal(Direction(_axis, Vector3.UnitX), Transform.WorldMatrix), Vector3.UnitX);
    }

    /// <summary>
    /// Holds the two bodies together at a point but lets them turn any way about it, like a shoulder or a chain's link.
    /// How far it can swing and twist can be limited.
    /// </summary>
    public sealed class BallJoint : Joint
    {
        private Vector3 _axis = -Vector3.UnitY;
        private float _swingLimit = 180f;
        private float _twistLimit = 180f;

        /// <summary>The direction it hangs or points, in this object's own space: what <see cref="SwingLimit"/> is measured from.</summary>
        [Tooltip("The way it points, in this object's own space: swinging is measured from it, and twisting is about it")]
        public Vector3 Axis
        {
            get => _axis;
            set { _axis = value; Changed(); }
        }

        /// <summary>How far it can swing away from <see cref="Axis"/>, in degrees: 180 swings freely.</summary>
        [Tooltip("How far it can swing away from its axis, in degrees: 180 is freely")]
        [Range(0f, 180f)]
        public float SwingLimit
        {
            get => _swingLimit;
            set { _swingLimit = value; Changed(); }
        }

        /// <summary>How far it can twist about <see cref="Axis"/> either way, in degrees: 180 twists freely.</summary>
        [Tooltip("How far it can twist about its axis either way, in degrees: 180 is freely")]
        [Range(0f, 180f)]
        public float TwistLimit
        {
            get => _twistLimit;
            set { _twistLimit = value; Changed(); }
        }

        internal Vector3 WorldAxis => Direction(Vector3.TransformNormal(Direction(_axis, -Vector3.UnitY), Transform.WorldMatrix), -Vector3.UnitY);
    }

    /// <summary>
    /// Keeps a point on this body within a reach of a point on the other (or in the world), like a rope: it can go nearer,
    /// but no further than <see cref="MaxDistance"/>. With a <see cref="MinDistance"/> as well, a rod.
    /// </summary>
    public sealed class DistanceJoint : Joint
    {
        private Vector3 _connectedAnchor;
        private float _minDistance;
        private float _maxDistance;

        /// <summary>The other end: a point in the connected body's own space, or in the world if there's no connected body.</summary>
        [Tooltip("The other end: a point in the connected body's own space, or in the world if there's none")]
        public Vector3 ConnectedAnchor
        {
            get => _connectedAnchor;
            set { _connectedAnchor = value; Changed(); }
        }

        /// <summary>The nearest the two ends can come, in metres: 0 lets the rope go slack.</summary>
        [Tooltip("The nearest the two ends can come, in metres: 0 lets it go slack like a rope")]
        public float MinDistance
        {
            get => _minDistance;
            set { _minDistance = value; Changed(); }
        }

        /// <summary>The furthest the two ends can go apart, in metres: 0 keeps them no further apart than they start.</summary>
        [Tooltip("The furthest apart the two ends can go, in metres: 0 is as far as they start")]
        public float MaxDistance
        {
            get => _maxDistance;
            set { _maxDistance = value; Changed(); }
        }
    }

    /// <summary>
    /// Pulls a point on this body back to a length from a point on the other (or in the world), like a spring or a
    /// bungee: stretched or squashed, it springs back, and bounces until <see cref="Damping"/> settles it.
    /// </summary>
    public sealed class SpringJoint : Joint
    {
        private Vector3 _connectedAnchor;
        private bool _autoLength = true;
        private float _length = 1f;
        private float _stiffness = 2f;
        private float _damping = 0.2f;

        /// <summary>The other end: a point in the connected body's own space, or in the world if there's no connected body.</summary>
        [Tooltip("The other end: a point in the connected body's own space, or in the world if there's none")]
        public Vector3 ConnectedAnchor
        {
            get => _connectedAnchor;
            set { _connectedAnchor = value; Changed(); }
        }

        /// <summary>Takes the length the ends are apart when it's made as its <see cref="Length"/>.</summary>
        [Tooltip("Takes how far apart the ends start as the length it springs back to")]
        public bool AutoLength
        {
            get => _autoLength;
            set { _autoLength = value; Changed(); }
        }

        /// <summary>The length it springs back to, in metres, unless <see cref="AutoLength"/>.</summary>
        [Tooltip("The length it springs back to, in metres (unless Auto Length)")]
        public float Length
        {
            get => _length;
            set { _length = value; Changed(); }
        }

        /// <summary>How stiff: how many times a second it would bounce, undamped. 0.5 is a soft bungee, 5 a stiff spring.</summary>
        [Tooltip("How stiff: about how many times a second it bounces. 0.5 is a soft bungee, 5 a stiff spring")]
        [Range(0.1f, 20f)]
        public float Stiffness
        {
            get => _stiffness;
            set { _stiffness = value; Changed(); }
        }

        /// <summary>How quickly bouncing dies away: 0 bounces on and on, 1 settles without bouncing.</summary>
        [Tooltip("How quickly bouncing dies away: 0 bounces on and on, 1 settles without bouncing")]
        [Range(0f, 1f)]
        public float Damping
        {
            get => _damping;
            set { _damping = value; Changed(); }
        }
    }
}
