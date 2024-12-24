using OpenTK.Mathematics;

namespace Vertex.Engine.Core.Components
{
    /// <summary>
    /// A component that handles camera perspective and view calculations.
    /// </summary>
    public class Camera : Component
    {
        private float _fov = MathHelper.PiOver2;
        private float _aspectRatio = 1.0f;
        private Vector3 _front = -Vector3.UnitZ;
        private Vector3 _up = Vector3.UnitY;
        private Vector3 _right = Vector3.UnitX;

        /// <summary>
        /// Gets or sets the field of view.
        /// </summary>
        public float FieldOfView
        {
            get => _fov;
            set => _fov = value;
        }

        /// <summary>
        /// Gets or sets the aspect ratio of the viewport (width/height).
        /// </summary>
        public float AspectRatio
        {
            get => _aspectRatio;
            set => _aspectRatio = value;
        }

        /// <summary>
        /// Gets the normalized front vector of the camera.
        /// </summary>
        public Vector3 Front => _front;
        /// <summary>
        /// Gets the normalized up vector of the camera.
        /// </summary>
        public Vector3 Up => _up;
        /// <summary>
        /// Gets the normalized right vector of the camera.
        /// </summary>
        public Vector3 Right => _right;

        /// <summary>
        /// Updates the camera's orientation vectors.
        /// </summary>
        /// <param name="front">The new front direction vector</param>
        public void UpdateVectors(Vector3 front)
        {
            _front = Vector3.Normalize(front);
            _right = Vector3.Normalize(Vector3.Cross(_front, Vector3.UnitY));
            _up = Vector3.Normalize(Vector3.Cross(_right, _front));
        }

        /// <summary>
        /// Gets the view matrix for the camera's current position and orientation.
        /// </summary>
        public Matrix4 ViewMatrix => Matrix4.LookAt(Transform.Position, Transform.Position + _front, _up);

        /// <summary>
        /// Gets the projection matrix based on the camera's field of view and aspect ratio.
        /// </summary>
        public Matrix4 ProjectionMatrix => Matrix4.CreatePerspectiveFieldOfView(_fov, _aspectRatio, 0.1f, 100.0f);
    }
}