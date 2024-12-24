using OpenTK.Mathematics;

namespace Vertex.Engine.Core.Components
{
    public class Transform : Component
    {
        private Vector3 _position = Vector3.Zero;
        private Vector3 _rotation = Vector3.Zero;
        private Vector3 _scale = Vector3.One;
        private Transform? _parent;
        private readonly List<Transform> _children = new();

        public Vector3 Position
        {
            get => _position;
            set
            {
                _position = value;
                UpdateMatrices();
            }
        }

        public Vector3 Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
                UpdateMatrices();
            }
        }

        public Vector3 Scale
        {
            get => _scale;
            set
            {
                _scale = value;
                UpdateMatrices();
            }
        }

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

        public IReadOnlyList<Transform> Children => _children;

        private Matrix4 _localMatrix = Matrix4.Identity;
        private Matrix4 _worldMatrix = Matrix4.Identity;

        public Matrix4 LocalMatrix => _localMatrix;
        public Matrix4 WorldMatrix => _worldMatrix;

        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Quaternion.FromEulerAngles(_rotation));
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, Quaternion.FromEulerAngles(_rotation));
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

        public void Translate(Vector3 translation)
        {
            Position += translation;
        }

        public void Rotate(Vector3 eulerAngles)
        {
            Rotation += eulerAngles;
        }

        public void LookAt(Vector3 target, Vector3 up)
        {
            var matrix = Matrix4.LookAt(_position, target, up);
            Rotation = matrix.ExtractRotation().ToEulerAngles();
        }
    }
}