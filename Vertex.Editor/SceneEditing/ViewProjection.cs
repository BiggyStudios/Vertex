using System.Numerics;

using Vertex.Editor.Gui;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>
    /// Between the 3D scene and the scene view's rectangle on screen: where a point in the world shows, and which way the
    /// mouse points into the world. Built fresh each frame from the camera and where the view is.
    /// </summary>
    public readonly struct ViewProjection
    {
        private readonly Matrix4x4 _viewProjection;

        public Rect Viewport { get; }
        public EditorCamera Camera { get; }

        public ViewProjection(EditorCamera camera, Rect viewport)
        {
            Camera = camera;
            Viewport = viewport;
            float aspect = viewport.Height > 0f ? viewport.Width / viewport.Height : 1f;
            _viewProjection = camera.Camera.ViewMatrix * camera.Camera.GetProjectionMatrix(aspect);
        }

        public Vector3 CameraPosition => Camera.Position;
        public Vector3 CameraForward => Camera.Forward;

        /// <summary>Where <paramref name="world"/> shows on screen, in UI units, or null if it's behind the camera.</summary>
        public Vector2? ToScreen(Vector3 world)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), _viewProjection);
            if (clip.W <= 0.001f) return null;

            var device = new Vector2(clip.X, clip.Y) / clip.W;
            return new Vector2(Viewport.X + (device.X + 1f) * 0.5f * Viewport.Width, Viewport.Y + (1f - device.Y) * 0.5f * Viewport.Height);
        }

        /// <summary>The ray from the camera through <paramref name="screen"/> (in UI units).</summary>
        public (Vector3 Origin, Vector3 Direction) Ray(Vector2 screen)
        {
            var point = new Vector2((screen.X - Viewport.X) / Viewport.Width, (screen.Y - Viewport.Y) / Viewport.Height);
            return Camera.Ray(point, Viewport.Width / Viewport.Height);
        }

        /// <summary>How many metres one UI unit covers at <paramref name="world"/>: for handles that stay the same size on screen however far away.</summary>
        public float MetresPerUnit(Vector3 world)
        {
            // Without perspective, the same everywhere.
            if (Camera.Camera.Projection == Rendering.CameraProjection.Orthographic) return 2f * Camera.Camera.OrthographicSize / MathF.Max(Viewport.Height, 1f);

            float depth = MathF.Max(Vector3.Dot(world - Camera.Position, Camera.Forward), 0.01f);
            return 2f * depth * MathF.Tan(Camera.Camera.FieldOfView * MathF.PI / 360f) / MathF.Max(Viewport.Height, 1f);
        }

        /// <summary>A line between two points in the world, drawn over the view; left out if either end is behind the camera.</summary>
        public void Line(Vector3 from, Vector3 to, Color color, float thickness = 1.5f)
        {
            if (ToScreen(from) is Vector2 a && ToScreen(to) is Vector2 b) Ui.Line(a, b, color, thickness);
        }
    }
}
