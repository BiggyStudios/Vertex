using OpenTK.Mathematics;

namespace Vertex.Engine.Core.Components
{
    /// <summary>
    /// Component that handles positioning, rotation, and scaling of GameObjects.
    /// </summary>
    public class Transform : Component
    {
        private Vector3 _position = Vector3.Zero;
        private Vector3 _rotation = Vector3.Zero;
        private Vector3 _scale = Vector3.One;
        private Transform? _parent;
        private readonly List<Transform> _children = new();

        /// <summary>
        /// Gets or sets the local position relative to the parent transform.
        /// </summary>
        public Vector3 Position
        {
            get => _position;
            set
            {
                _position = value;
                UpdateMatrices();
            }
        }

        /// <summary>
        /// Gets or sets the local rotation in euler angles.
        /// </summary>
        public Vector3 Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
                UpdateMatrices();
            }
        }

        /// <summary>
        /// Gets or sets the local scale.
        /// </summary>
        public Vector3 Scale
        {
            get => _scale;
            set
            {
                _scale = value;
                UpdateMatrices();
            }
        }


        /// <summary>
        /// Gets or sets the parent transform.
        /// </summary>
        public Transform? Parent
        {
            get => _parent;
            set
            {
                if (_parent == value) return;
                _parent?._children.Remove(this);
                _parent = value;
                _parent?._children.Add(this);
                UpdateMatrices();
            }
        }

        /// <summary>
        /// Gets the list of child transforms.
        /// </summary>
        public IReadOnlyList<Transform> Children => _children;

        private Matrix4 _localMatrix = Matrix4.Identity;
        private Matrix4 _worldMatrix = Matrix4.Identity;

        /// <summary>
        /// Gets the local transformation matrix.
        /// </summary>
        public Matrix4 LocalMatrix => _localMatrix;
        /// <summary>
        /// Gets the world transformation matrix.
        /// </summary>
        public Matrix4 WorldMatrix => _worldMatrix;

        /// <summary>
        /// Gets the forward direction vector.
        /// </summary>
        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Quaternion.FromEulerAngles(_rotation));
        /// <summary>
        /// Gets the right direction vector.
        /// </summary>
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, Quaternion.FromEulerAngles(_rotation));
        /// <summary>
        /// Gets the up direction vector.
        /// </summary>
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, Quaternion.FromEulerAngles(_rotation));

        private void UpdateMatrices()
        {
            _localMatrix = Matrix4.CreateScale(_scale) *
                            Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(_rotation)) *
                            Matrix4.CreateTranslation(_position);

            _worldMatrix = _parent != null ? _localMatrix * _parent._worldMatrix : _localMatrix;

            foreach (var child in _children)
            {
                child.UpdateMatrices();
            }
        }

        /// <summary>
        /// Translates the transform by the specified vector.
        /// </summary>
        /// <param name="translation">The translation vector to apply.</param>
        public void Translate(Vector3 translation)
        {
            Position += translation;
        }

        /// <summary>
        /// Rotates the transform by the specified euler angles.
        /// </summary>
        /// <param name="eulerAngles">The rotation angles in degrees around each axis.</param>
        public void Rotate(Vector3 eulerAngles)
        {
            Rotation += eulerAngles;
        }

        /// <summary>
        /// Rotates the transform to look at a target position.
        /// </summary>
        /// <param name="target">The position to look at.</param>
        /// <param name="up">The up vector to use for orientation.</param>
        public void LookAt(Vector3 target, Vector3 up)
        {
            var matrix = Matrix4.LookAt(_position, target, up);
            Rotation = matrix.ExtractRotation().ToEulerAngles();
        }
    }
}