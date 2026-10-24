using System.Numerics;

namespace Vertex
{
    /// <summary>
    /// Position, rotation and scale of a GameObject, relative to its parent (or the world if it has none).
    /// Right-handed coordinates: +X right, +Y up, -Z forward.
    /// </summary>
    public class Transform
    {
        private readonly List<Transform> _children = new();
        private Transform? _parent;

        public GameObject GameObject { get; }

        public Vector3 Position { get; set; } = Vector3.Zero;
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;

        public IReadOnlyList<Transform> Children => _children;

        internal Transform(GameObject gameObject)
        {
            GameObject = gameObject;
        }

        public Transform? Parent
        {
            get => _parent;
            set
            {
                if (_parent == value) return;

                for (Transform? t = value; t != null; t = t._parent)
                {
                    if (t == this) throw new InvalidOperationException("A transform cannot be parented to itself or one of its children.");
                }

                _parent?._children.Remove(this);
                _parent = value;
                _parent?._children.Add(this);
            }
        }

        /// <summary>Sets the rotation from euler angles in degrees (X = pitch, Y = yaw, Z = roll).</summary>
        public Vector3 EulerAngles
        {
            set => Rotation = FromEuler(value);
        }

        public Matrix4x4 LocalMatrix =>
            Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);

        public Matrix4x4 WorldMatrix => _parent == null ? LocalMatrix : LocalMatrix * _parent.WorldMatrix;

        public Vector3 WorldPosition
        {
            get => _parent == null ? Position : WorldMatrix.Translation;
            set
            {
                if (_parent == null)
                {
                    Position = value;
                    return;
                }

                Matrix4x4.Invert(_parent.WorldMatrix, out Matrix4x4 inverseParent);
                Position = Vector3.Transform(value, inverseParent);
            }
        }

        public Quaternion WorldRotation
        {
            get => _parent == null ? Rotation : Quaternion.Concatenate(Rotation, _parent.WorldRotation);
            set => Rotation = _parent == null ? value : Quaternion.Concatenate(value, Quaternion.Inverse(_parent.WorldRotation));
        }

        /// <summary>
        /// Scale relative to the world. Approximate when a parent is both rotated and non-uniformly scaled,
        /// since that combination can skew the object, which a scale alone can't describe.
        /// </summary>
        public Vector3 WorldScale => _parent == null ? Scale : Scale * _parent.WorldScale;

        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, WorldRotation);
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, WorldRotation);
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, WorldRotation);

        public void Translate(Vector3 offset) => Position += offset;

        /// <summary>Rotates by the given euler angles in degrees, around the object's local axes.</summary>
        public void Rotate(Vector3 eulerDegrees)
        {
            Rotation = Quaternion.Normalize(Quaternion.Concatenate(FromEuler(eulerDegrees), Rotation));
        }

        /// <summary>Rotates so that <see cref="Forward"/> points at the target (world space).</summary>
        public void LookAt(Vector3 target, Vector3? worldUp = null)
        {
            Vector3 position = WorldPosition;
            if (Vector3.DistanceSquared(position, target) < 1e-8f) return;

            Matrix4x4 view = Matrix4x4.CreateLookAt(position, target, worldUp ?? Vector3.UnitY);
            Matrix4x4.Invert(view, out Matrix4x4 world);
            Quaternion worldRotation = Quaternion.CreateFromRotationMatrix(world);

            Rotation = _parent == null
                ? worldRotation
                : Quaternion.Concatenate(worldRotation, Quaternion.Inverse(_parent.WorldRotation));
        }

        private static Quaternion FromEuler(Vector3 degrees)
        {
            return Quaternion.CreateFromYawPitchRoll(
                MathHelper.ToRadians(degrees.Y), MathHelper.ToRadians(degrees.X), MathHelper.ToRadians(degrees.Z));
        }
    }
}
