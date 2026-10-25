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

        /// <summary>
        /// Where <paramref name="point"/> appears on a screen of <paramref name="screenSize"/>, measured from the top-left
        /// corner in the same units, such as <see cref="UI.Canvas.Size"/>. Null if it's behind the camera.
        /// </summary>
        public Vector2? WorldToScreen(Vector3 point, Vector2 screenSize)
        {
            if (screenSize.X <= 0f || screenSize.Y <= 0f) return null;

            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), ViewMatrix * GetProjectionMatrix(screenSize.X / screenSize.Y));
            if (clip.W <= NearPlane) return null;

            var device = new Vector2(clip.X, clip.Y) / clip.W;
            return new Vector2((device.X + 1f) * 0.5f * screenSize.X, (1f - device.Y) * 0.5f * screenSize.Y);
        }
    }
}
