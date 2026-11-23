using System.Numerics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Renders the scene from its GameObject's point of view, looking along <see cref="Transform.Forward"/>.
    /// </summary>
    public class Camera : Component
    {
        private static Camera? _main, _found;

        /// <summary>
        /// The camera that draws to the screen. Defaults to the first camera in the active scene without a
        /// <see cref="Target"/>.
        /// </summary>
        public static Camera? Main
        {
            get
            {
                if (_main != null && !_main.GameObject.IsDestroyed && _main.GameObject.Scene == Scene.Active) return _main;

                // The one found last time, while it's still there and still draws to the screen: looking through the scene
                // for it costs, and many things ask every frame.
                if (_found != null && !_found.GameObject.IsDestroyed && _found.GameObject.Scene == Scene.Active && _found.Target == null && _found.Enabled && _found.GameObject.ActiveInHierarchy) return _found;

                foreach (Camera camera in Scene.Active.FindComponents<Camera>())
                {
                    if (camera.Target != null) continue;
                    _found = camera;
                    return camera;
                }

                _found = null;
                return null;
            }
            set => _main = value;
        }

        /// <summary>
        /// Where this camera draws: null for the screen (if it's <see cref="Main"/>), or a texture. Cameras with a target
        /// draw every frame they're enabled, before the screen, so it can show what they drew that same frame.
        /// </summary>
        public RenderTexture? Target { get; set; }

        /// <summary>
        /// Perspective (further away looks smaller, as eyes see) or orthographic (the same size at any distance: for 2D
        /// games, and maps and plans seen straight on). An orthographic camera has no distance fog: how far back it stands
        /// doesn't change what it shows.
        /// </summary>
        [Tooltip("Perspective: further away looks smaller. Orthographic: the same size at any distance, for 2D games")]
        public CameraProjection Projection { get; set; } = CameraProjection.Perspective;

        /// <summary>Vertical field of view in degrees, for a perspective camera.</summary>
        [Range(1f, 179f)]
        public float FieldOfView { get; set; } = 60f;

        /// <summary>For an orthographic camera: half the height of what it sees, in world units. Smaller zooms in.</summary>
        [Tooltip("Orthographic: half the height of what it sees, in world units. Smaller zooms in")]
        public float OrthographicSize { get; set; } = 5f;

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
            if (Projection == CameraProjection.Orthographic)
            {
                float height = MathF.Max(OrthographicSize, 0.001f) * 2f;
                return Matrix4x4.CreateOrthographic(height * aspectRatio, height, NearPlane, FarPlane);
            }

            return Matrix4x4.CreatePerspectiveFieldOfView(MathHelper.ToRadians(FieldOfView), aspectRatio, NearPlane, FarPlane);
        }

        /// <summary>
        /// Where the line through a point of a screen of <paramref name="screenSize"/> (from its top-left, in the same units
        /// as <paramref name="screenPoint"/>) starts and which way it goes: what the mouse is pointing at, to raycast along.
        /// </summary>
        public (Vector3 Origin, Vector3 Direction) ScreenToRay(Vector2 screenPoint, Vector2 screenSize)
        {
            var device = new Vector2(screenPoint.X / MathF.Max(screenSize.X, 1f) * 2f - 1f, 1f - screenPoint.Y / MathF.Max(screenSize.Y, 1f) * 2f);
            Matrix4x4.Invert(ViewMatrix * GetProjectionMatrix(screenSize.X / MathF.Max(screenSize.Y, 1f)), out Matrix4x4 inverse);
            Vector4 near = Vector4.Transform(new Vector4(device, 0f, 1f), inverse);
            Vector4 far = Vector4.Transform(new Vector4(device, 1f, 1f), inverse);
            Vector3 from = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 to = new Vector3(far.X, far.Y, far.Z) / far.W;
            return (from, Vector3.Normalize(to - from));
        }

        /// <summary>
        /// The point in the world under a point of the screen, on the flat plane facing the camera at <paramref name="depth"/>
        /// in front of it: where a 2D game's mouse is, in its world.
        /// </summary>
        public Vector3 ScreenToWorld(Vector2 screenPoint, Vector2 screenSize, float depth)
        {
            (Vector3 origin, Vector3 direction) = ScreenToRay(screenPoint, screenSize);
            Vector3 forward = Transform.Forward;
            float along = Vector3.Dot(direction, forward);
            float distance = MathF.Abs(along) > 1e-6f ? (depth - Vector3.Dot(origin - Transform.WorldPosition, forward)) / along : 0f;
            return origin + direction * distance;
        }

        /// <summary>
        /// Where <paramref name="point"/> appears on a screen of <paramref name="screenSize"/>, measured from the top-left
        /// corner in the same units, such as <see cref="UI.Canvas.Size"/>. Null if it's behind the camera.
        /// </summary>
        public Vector2? WorldToScreen(Vector3 point, Vector2 screenSize)
        {
            if (screenSize.X <= 0f || screenSize.Y <= 0f) return null;

            // Behind the camera, or nearer than it sees, isn't on the screen.
            if (-Vector3.Transform(point, ViewMatrix).Z < NearPlane) return null;
            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), ViewMatrix * GetProjectionMatrix(screenSize.X / screenSize.Y));

            var device = new Vector2(clip.X, clip.Y) / clip.W;
            return new Vector2((device.X + 1f) * 0.5f * screenSize.X, (1f - device.Y) * 0.5f * screenSize.Y);
        }
    }

    /// <summary>How a <see cref="Camera"/> sees: with perspective, or orthographic (flat, for 2D).</summary>
    public enum CameraProjection
    {
        /// <summary>Further away looks smaller, as eyes see.</summary>
        Perspective,

        /// <summary>The same size at any distance: for 2D games, and maps and plans seen straight on.</summary>
        Orthographic
    }
}
