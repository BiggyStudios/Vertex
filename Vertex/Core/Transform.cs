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

        private Vector3 _position;
        private Quaternion _rotation = Quaternion.Identity;
        private Vector3 _scale = Vector3.One;

        public Vector3 Position
        {
            get => _position;
            set
            {
                if (_position == value) return;
                _position = value;
                Moved();
            }
        }

        public Quaternion Rotation
        {
            get => _rotation;
            set
            {
                if (_rotation == value) return;
                _rotation = value;
                Moved();
            }
        }

        public Vector3 Scale
        {
            get => _scale;
            set
            {
                if (_scale == value) return;
                _scale = value;
                Moved();
            }
        }

        // What it all comes to, worked out when first asked for after a change and kept until the next: a big scene has
        // thousands of things that never move, and their places are asked for every frame (drawing, physics, sound).
        private Matrix4x4 _localMatrix, _worldMatrix;
        private Quaternion _worldRotation;
        private Vector3 _worldScale;
        private bool _localDirty = true, _worldDirty = true;
        private int _version;

        /// <summary>
        /// Goes up when this or anything above it moves (turns, scales or changes parent), so something that kept where it was
        /// can tell cheaply whether it's still right. Reading it works out the world matrix.
        /// </summary>
        internal int Version
        {
            get
            {
                if (_worldDirty) UpdateWorld();
                return _version;
            }
        }

        private void Moved()
        {
            _localDirty = true;
            WorldMoved();
        }

        // Everything under a transform that hasn't been worked out since is already marked, so this stops there.
        private void WorldMoved()
        {
            if (_worldDirty) return;
            _worldDirty = true;
            _version++;
            for (int i = 0; i < _children.Count; i++) _children[i].WorldMoved();
        }

        private void UpdateWorld()
        {
            if (_localDirty)
            {
                _localMatrix = Matrix4x4.CreateScale(_scale) * Matrix4x4.CreateFromQuaternion(_rotation) * Matrix4x4.CreateTranslation(_position);
                _localDirty = false;
            }

            if (_parent == null)
            {
                _worldMatrix = _localMatrix;
                _worldRotation = _rotation;
                _worldScale = _scale;
            }
            else
            {
                _worldMatrix = _localMatrix * _parent.WorldMatrix;
                _worldRotation = Quaternion.Concatenate(_rotation, _parent.WorldRotation);
                _worldScale = _scale * _parent.WorldScale;
            }

            _worldDirty = false;
        }

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

                if (value != null && value.GameObject.Scene != GameObject.Scene)
                    throw new InvalidOperationException("A transform can only be parented to one in the same scene.");

                for (Transform? t = value; t != null; t = t._parent)
                {
                    if (t == this) throw new InvalidOperationException("A transform cannot be parented to itself or one of its children.");
                }

                _parent?._children.Remove(this);
                _parent = value;
                _parent?._children.Add(this);
                GameObject.HierarchyVersion++;

                // Under something else now: wherever it is in the world has to be worked out again.
                _worldDirty = false;
                WorldMoved();
            }
        }

        /// <summary>
        /// Where this comes among its parent's children, or among the scene's top-level objects if it has no parent: 0 is
        /// first. Setting it moves it there (as dragging in an editor's outliner does). It only changes the order objects
        /// are listed and updated in.
        /// </summary>
        public int SiblingIndex
        {
            get => _parent != null ? _parent._children.IndexOf(this) : GameObject.Scene.RootIndex(GameObject);
            set
            {
                if (_parent == null)
                {
                    GameObject.Scene.SetRootIndex(GameObject, value);
                    return;
                }

                _parent._children.Remove(this);
                _parent._children.Insert(Math.Clamp(value, 0, _parent._children.Count), this);
            }
        }

        /// <summary>
        /// The rotation as euler angles in degrees (X = pitch, Y = yaw, Z = roll), applied roll first, then pitch, then yaw.
        /// Reading gives pitch from -90 to 90 and yaw and roll from -180 to 180, which may differ from the angles set but
        /// turn the same way.
        /// </summary>
        public Vector3 EulerAngles
        {
            get => ToEuler(Rotation);
            set => Rotation = FromEuler(value);
        }

        public Matrix4x4 LocalMatrix
        {
            get
            {
                if (_localDirty) UpdateWorld();
                return _localMatrix;
            }
        }

        public Matrix4x4 WorldMatrix
        {
            get
            {
                if (_worldDirty) UpdateWorld();
                return _worldMatrix;
            }
        }

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
            get
            {
                if (_worldDirty) UpdateWorld();
                return _worldRotation;
            }
            set => Rotation = _parent == null ? value : Quaternion.Concatenate(value, Quaternion.Inverse(_parent.WorldRotation));
        }

        /// <summary>
        /// Scale relative to the world. Approximate when a parent is both rotated and non-uniformly scaled,
        /// since that combination can skew the object, which a scale alone can't describe.
        /// </summary>
        public Vector3 WorldScale
        {
            get
            {
                if (_worldDirty) UpdateWorld();
                return _worldScale;
            }
        }

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

        /// <summary>The euler angles in degrees that <see cref="EulerAngles"/> would turn into <paramref name="rotation"/>.</summary>
        public static Vector3 ToEuler(Quaternion rotation)
        {
            // Where the rotation takes each axis: the columns of its matrix. For yaw * pitch * roll, the Z axis's height
            // is -sin(pitch), and yaw and roll can be read off the rest.
            Vector3 x = Vector3.Transform(Vector3.UnitX, rotation);
            Vector3 y = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 z = Vector3.Transform(Vector3.UnitZ, rotation);

            float sinPitch = Math.Clamp(-z.Y, -1f, 1f);
            float pitch = MathF.Asin(sinPitch);
            float yaw, roll;

            if (MathF.Abs(sinPitch) < 0.9999f)
            {
                yaw = MathF.Atan2(z.X, z.Z);
                roll = MathF.Atan2(x.Y, y.Y);
            }
            else
            {
                // Looking straight up or down, yaw and roll turn about the same axis: put it all in yaw.
                yaw = MathF.Atan2(-x.Z, x.X);
                roll = 0f;
            }

            return new Vector3(pitch, yaw, roll) * (180f / MathF.PI);
        }

        internal static Quaternion FromEuler(Vector3 degrees)
        {
            return Quaternion.CreateFromYawPitchRoll(
                MathHelper.ToRadians(degrees.Y), MathHelper.ToRadians(degrees.X), MathHelper.ToRadians(degrees.Z));
        }
    }
}
