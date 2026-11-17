using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Renders the scene from its GameObject's point of view, looking along <see cref="Transform.Forward"/>.
    /// </summary>
    public class Camera : Component
    {
        private static Camera? _main;

        /// <summary>
        /// The camera that draws to the screen. Defaults to the first camera in the active scene without a
        /// <see cref="Target"/>.
        /// </summary>
        public static Camera? Main
        {
            get
            {
                if (_main != null && !_main.GameObject.IsDestroyed && _main.GameObject.Scene == Scene.Active) return _main;

                foreach (Camera camera in Scene.Active.FindComponents<Camera>())
                {
                    if (camera.Target == null) return camera;
                }

                return null;
            }
            set => _main = value;
        }

        /// <summary>
        /// Where this camera draws: null for the screen (if it's <see cref="Main"/>), or a texture. Cameras with a target
        /// draw every frame they're enabled, before the screen, so it can show what they drew that same frame.
        /// </summary>
        public RenderTexture? Target { get; set; }

        /// <summary>Vertical field of view in degrees.</summary>
        [Range(1f, 179f)]
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
