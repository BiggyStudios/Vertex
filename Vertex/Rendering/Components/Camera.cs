using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Renders the scene from its GameObject's point of view, looking along <see cref="Transform.Forward"/>.
    /// </summary>
    public class Camera : Component
    {
        private static Camera? _main;

        /// <summary>The camera used for rendering. Defaults to the first camera in the active scene.</summary>
        public static Camera? Main
        {
            get
            {
                if (_main != null && !_main.GameObject.IsDestroyed) return _main;
                return Scene.Active.FindComponent<Camera>();
            }
            set => _main = value;
        }

        /// <summary>Vertical field of view in degrees.</summary>
        public float FieldOfView { get; set; } = 60f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 1000f;
        public Color ClearColor { get; set; } = Color.CornflowerBlue;

        public Matrix4x4 ViewMatrix
        {
            get
            {
                Vector3 position = Transform.WorldPosition;
                return Matrix4x4.CreateLookAt(position, position + Transform.Forward, Transform.Up);
            }
        }

        public Matrix4x4 GetProjectionMatrix(float aspectRatio)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(MathHelper.ToRadians(FieldOfView), aspectRatio, NearPlane, FarPlane);
        }
    }
}
