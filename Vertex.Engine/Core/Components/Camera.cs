using OpenTK.Mathematics;

namespace Vertex.Engine.Core.Components
{
    public class Camera : Component
    {
        private float _fov = MathHelper.PiOver2;
        private float _aspectRatio = 1.0f;
        private Vector3 _front = -Vector3.UnitZ;
        private Vector3 _up = Vector3.UnitY;

        public float FieldOfView
        {
            get => _fov;
            set => _fov = value;
        }

        public float AspectRatio
        {
            get => _aspectRatio;
            set => _aspectRatio = value;
        }

        public Vector3 Front => _front;
        public Vector3 Up => _up;

        public Matrix4 ViewMatrix => Matrix4.LookAt(Transform.Position, Transform.Position + _front, _up);
        public Matrix4 ProjectionMatrix => Matrix4.CreatePerspectiveFieldOfView(_fov, _aspectRatio, 0.1f, 100.0f);
    }
}