using System.Numerics;

namespace Vertex.Physics
{
    /// <summary>
    /// A <see cref="Rigidbody"/> for a 2D game: it only moves across the X-Y plane (the picture a 2D camera, looking
    /// along -Z, sees) and only turns about Z, so it never tips out of the picture. Lock rotation about Z too to keep
    /// it upright, as a platformer's player should be. Give it a 2D collider: a <see cref="BoxCollider2D"/>,
    /// <see cref="CircleCollider2D"/> or <see cref="CapsuleCollider2D"/>. Gravity pulls down Y, as in 3D.
    /// </summary>
    public sealed class Rigidbody2D : Rigidbody
    {
        internal override Axes AlwaysLockedPosition => Axes.Z;
        internal override Axes AlwaysLockedRotation => Axes.X | Axes.Y;

        /// <summary>Its velocity across the picture, in units a second.</summary>
        [Serialization.DontSave, HideInInspector]
        public Vector2 Velocity2D
        {
            get => new(Velocity.X, Velocity.Y);
            set => Velocity = new Vector3(value, 0f);
        }

        /// <summary>How fast it's turning, in radians a second: positive is anticlockwise, seen by the camera.</summary>
        [Serialization.DontSave, HideInInspector]
        public float AngularVelocity2D
        {
            get => AngularVelocity.Z;
            set => AngularVelocity = new Vector3(0f, 0f, value);
        }

        /// <summary>A push in newtons over the next physics step, across the picture.</summary>
        public void AddForce(Vector2 force) => AddForce(new Vector3(force, 0f));

        /// <summary>An instant kick across the picture (mass × change in velocity): a jump, a knockback.</summary>
        public void AddImpulse(Vector2 impulse) => AddImpulse(new Vector3(impulse, 0f));
    }

    /// <summary>
    /// A rectangle in the X-Y plane, for 2D games. It goes <see cref="Depth"/> deep along Z, so things a little nearer
    /// or further still bump into it.
    /// </summary>
    public sealed class BoxCollider2D : Collider
    {
        private Vector2 _size = Vector2.One;
        private float _depth = 1f;

        /// <summary>Width and height, before the object's scale.</summary>
        public Vector2 Size
        {
            get => _size;
            set { _size = value; Changed(); }
        }

        [Tooltip("How deep it goes along Z, so things a little nearer or further still meet it")]
        public float Depth
        {
            get => _depth;
            set { _depth = value; Changed(); }
        }
    }

    /// <summary>A circle in the X-Y plane, for 2D games: a ball, a coin. It goes <see cref="Depth"/> deep along Z.</summary>
    public sealed class CircleCollider2D : Collider
    {
        private float _radius = 0.5f;
        private float _depth = 1f;

        /// <summary>Before the object's scale (the larger of X and Y).</summary>
        public float Radius
        {
            get => _radius;
            set { _radius = value; Changed(); }
        }

        [Tooltip("How deep it goes along Z, so things a little nearer or further still meet it")]
        public float Depth
        {
            get => _depth;
            set { _depth = value; Changed(); }
        }
    }

    /// <summary>
    /// An upright pill in the X-Y plane, for 2D games: rounded top and bottom, so a character slides over steps and
    /// corners rather than catching on them.
    /// </summary>
    public sealed class CapsuleCollider2D : Collider
    {
        private float _radius = 0.5f;
        private float _height = 2f;

        /// <summary>How round its ends are, and half as wide as it is. Before the object's scale (X).</summary>
        public float Radius
        {
            get => _radius;
            set { _radius = value; Changed(); }
        }

        /// <summary>How tall it is, end to end, before the object's scale (Y).</summary>
        public float Height
        {
            get => _height;
            set { _height = value; Changed(); }
        }
    }
}
