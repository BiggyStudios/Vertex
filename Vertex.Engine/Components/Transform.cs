using OpenTK.Mathematics;

using Vertex.Engine.Core;

namespace Vertex.Engine.Components
{
    public class Transform : Component
    {
        private Vector3 _position = Vector3.Zero;
        private Vector3 _rotation = Vector3.Zero;
        private Vector3 _scale = Vector3.One;

        private Matrix4 _modelMatrix = Matrix4.Identity;
        private bool _isDirty = true;

        private Transform _parent;
        private readonly List<Transform> _children = new();

        public Vector3 Position
        {
            get => _position;
            set
            {
                _position = value;
                SetDirty();
            }
        }

        public Vector3 Rotation
        {
            get => _rotation;
            set
            {
                _rotation = value;
                SetDirty();
            }
        }

        public Vector3 Scale
        {
            get => _scale;
            set
            {
                _scale = value;
                SetDirty();
            }
        }

        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Quaternion.FromEulerAngles(_rotation));
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, Quaternion.FromEulerAngles(_rotation));
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, Quaternion.FromEulerAngles(_rotation));

        public void SetParent(Transform parent)
        {
            _parent?._children.Remove(this);
            _parent = parent;
            _parent?._children.Add(this);
            SetDirty();
        }

        public void Translate(Vector3 translation)
        {
            Position += translation;
        }

        public void Rotate(Vector3 eulerAngles)
        {
            Rotation += eulerAngles;
        }

        public void RotateAround(Vector3 point, Vector3 axis, float angle)
        {
            Vector3 direction = Position - point;
            Quaternion rotation = Quaternion.FromAxisAngle(axis, angle);
            direction = Vector3.Transform(direction, rotation);
            Position = point + direction;
            Rotation += axis * angle;
        }

        public void LookAt(Vector3 target, Vector3 up = default)
        {
            if (up == default)
                up = Vector3.UnitY;

            Vector3 direction = (target - Position).Normalized();
            Rotation = GetEulerAngles(Matrix4.LookAt(Position, target, up));
        }

        public Matrix4 LocalMatrix
        {
            get
            {
                if (_isDirty)
                    RecalculateMatrix();
                return _modelMatrix;
            }
        }

        public Matrix4 WorldMatrix
        {
            get
            {
                Matrix4 localMatrix = LocalMatrix;
                return _parent != null ? localMatrix * _parent.WorldMatrix : localMatrix;
            }
        }

        private void RecalculateMatrix()
        {
            _modelMatrix = Matrix4.CreateScale(_scale) *
                            Matrix4.CreateRotationX(_rotation.X) *
                            Matrix4.CreateRotationY(_rotation.Y) *
                            Matrix4.CreateRotationZ(_rotation.Z) *
                            Matrix4.CreateTranslation(_position);
            _isDirty = false;
        }

        private void SetDirty()
        {
            _isDirty = true;

            foreach (var child in _children)
                child.SetDirty();
        }

        private static Vector3 GetEulerAngles(Matrix4 matrix)
        {
            float x, y, z;

            y = MathF.Asin(MathHelper.Clamp(matrix.M13, -1, 1));

            if (MathF.Abs(matrix.M13) < 0.99999f)
            {
                x = MathF.Atan2(-matrix.M23, matrix.M33);
                z = MathF.Atan2(-matrix.M12, matrix.M11);
            }

            else
            {
                x = 0;
                z = MathF.Atan2(matrix.M21, matrix.M22);
            }

            return new Vector3(x, y, z);
        }

        public Vector3 TransformPoint(Vector3 point)
        {
            Vector4 temp = new Vector4(point, 1.0f);
            temp = temp * WorldMatrix;
            return new Vector3(temp.X, temp.Y, temp.Z);
        }

        public Vector3 TransformDirection(Vector3 direction)
        {
            return Vector3.TransformNormal(direction, WorldMatrix);
        }

        public Vector3 InverseTransformPoint(Vector3 worldPoint)
        {
            Vector4 temp = new Vector4(worldPoint, 1.0f);
            temp = Matrix4.Invert(WorldMatrix) * temp;
            return new Vector3(temp.X, temp.Y, temp.Z);
        }

        public Vector3 InverseTransformDirection(Vector3 worldDirection)
        {
            return Vector3.TransformNormal(worldDirection, Matrix4.Invert(WorldMatrix));
        }
    }
}