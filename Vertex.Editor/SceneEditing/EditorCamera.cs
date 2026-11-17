using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>
    /// The scene view's camera, which belongs to the editor rather than the scene. It looks at a pivot point from some
    /// distance, and every move eases there instead of jumping, so zooming, turning and framing feel smooth:
    /// <list type="bullet">
    /// <item>right mouse held: look around, with WASD to fly (Q and E down and up, Shift faster, the wheel sets the speed);</item>
    /// <item>Alt and left mouse: orbit round the pivot; middle mouse: pan; the wheel: zoom towards the pivot;</item>
    /// <item>F: fly to frame what's selected.</item>
    /// </list>
    /// </summary>
    public sealed class EditorCamera
    {
        private struct View
        {
            public Vector3 Pivot;
            public float Yaw;
            public float Pitch;
            public float Distance;
        }

        private View _target;
        private View _current;

        // While looking around, the camera's own position stays put and the pivot swings round it instead.
        private Vector3? _anchor;
        private Vector3 _flyVelocity;

        public Camera Camera { get; }

        /// <summary>How fast WASD flies, in metres a second. The wheel changes it while looking around.</summary>
        public float FlySpeed { get; set; } = 8f;

        /// <summary>How quickly moves ease to where they're going: higher is snappier.</summary>
        public float Smoothing { get; set; } = 16f;

        /// <summary>The point the camera turns and zooms around: where it's headed, not where it's got to.</summary>
        public Vector3 Pivot => _target.Pivot;
        public float Distance => _current.Distance;
        public Vector3 Position => Camera.Transform.Position;
        public Vector3 Forward => Direction(_current.Yaw, _current.Pitch);

        /// <param name="scene">A scene of the editor's own to keep the camera in, so the game's scene never sees it.</param>
        public EditorCamera(Scene scene)
        {
            var gameObject = new GameObject("Editor Camera", scene);
            Camera = gameObject.AddComponent<Camera>();
            Camera.NearPlane = 0.05f;
            Camera.FarPlane = 5000f;

            _target = new View { Pivot = new Vector3(0f, 1f, 0f), Yaw = -35f, Pitch = -22f, Distance = 14f };
            _current = _target;
            Apply();
        }

        /// <summary>The way a camera with this yaw and pitch (in degrees) looks.</summary>
        public static Vector3 Direction(float yaw, float pitch)
        {
            float y = yaw * MathF.PI / 180f, p = pitch * MathF.PI / 180f;
            return new Vector3(-MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p));
        }

        private static Vector3 PositionOf(in View view) => view.Pivot - Direction(view.Yaw, view.Pitch) * view.Distance;

        /// <summary>Turns the camera where it stands, by degrees: looking around.</summary>
        public void Look(float yaw, float pitch)
        {
            _anchor ??= Camera.Transform.Position;
            _target.Yaw += yaw;
            _target.Pitch = Math.Clamp(_target.Pitch + pitch, -89f, 89f);
        }

        /// <summary>Stops looking around: the pivot stays where it's swung to.</summary>
        public void EndLook()
        {
            _anchor = null;
            _flyVelocity = Vector3.Zero;
        }

        /// <summary>Swings the camera round the pivot, by degrees.</summary>
        public void Orbit(float yaw, float pitch)
        {
            _target.Yaw += yaw;
            _target.Pitch = Math.Clamp(_target.Pitch + pitch, -89f, 89f);
        }

        /// <summary>Slides the camera and pivot sideways and up by a drag of <paramref name="drag"/> over a view <paramref name="viewHeight"/> tall, so what's under the mouse stays under it.</summary>
        public void Pan(Vector2 drag, float viewHeight)
        {
            if (viewHeight <= 0f) return;

            float metresPerUnit = 2f * _current.Distance * MathF.Tan(Camera.FieldOfView * MathF.PI / 360f) / viewHeight;
            Vector3 forward = Direction(_target.Yaw, _target.Pitch);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            Vector3 up = Vector3.Cross(right, forward);
            Vector3 move = (-right * drag.X + up * drag.Y) * metresPerUnit;
            _target.Pivot += move;

            // Panning is direct: lagging behind the mouse would feel loose.
            _current.Pivot += move * 0.6f;
        }

        /// <summary>
        /// Zooms by mouse wheel notches (positive is in): each takes a share of the way to the pivot, so zooming feels
        /// the same near and far. Zooming right in pushes through the pivot instead of stopping at it.
        /// </summary>
        public void Zoom(float notches)
        {
            float distance = _target.Distance * MathF.Pow(0.82f, notches);
            if (distance < 0.25f)
            {
                // Out of room: carry on forwards, taking the pivot along.
                _target.Pivot += Direction(_target.Yaw, _target.Pitch) * (0.25f - distance + 0.1f * MathF.Max(notches, 0f));
                distance = 0.25f;
            }

            _target.Distance = MathF.Min(distance, 4000f);
        }

        /// <summary>Flies in <paramref name="direction"/> (camera-relative: X right, Y up, Z back) at <see cref="FlySpeed"/>, easing in and out.</summary>
        public void Fly(Vector3 direction, float deltaTime, bool fast)
        {
            _anchor ??= Camera.Transform.Position;

            Vector3 forward = Direction(_target.Yaw, _target.Pitch);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            Vector3 world = right * direction.X + Vector3.UnitY * direction.Y - forward * direction.Z;
            if (world.LengthSquared() > 1f) world = Vector3.Normalize(world);

            Vector3 wanted = world * FlySpeed * (fast ? 3f : 1f);
            float t = 1f - MathF.Exp(-10f * deltaTime);
            _flyVelocity += (wanted - _flyVelocity) * t;
            _anchor += _flyVelocity * deltaTime;
        }

        /// <summary>Flies to look at a sphere round <paramref name="centre"/> from the same angle, near enough that it fills the view.</summary>
        public void Frame(Vector3 centre, float radius)
        {
            EndLook();
            _target.Pivot = centre;
            float fit = MathF.Max(radius, 0.25f) / MathF.Sin(Camera.FieldOfView * MathF.PI / 360f);
            _target.Distance = Math.Clamp(fit * 1.1f, 0.5f, 4000f);
        }

        /// <summary>Swings round the pivot to look along <paramref name="direction"/>: for the axis buttons (look down, look along X).</summary>
        public void LookAlong(Vector3 direction)
        {
            EndLook();
            direction = Vector3.Normalize(direction);
            float pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) * 180f / MathF.PI;
            float yaw = MathF.Abs(direction.Y) > 0.999f ? _target.Yaw : MathF.Atan2(-direction.X, -direction.Z) * 180f / MathF.PI;

            // The short way round.
            while (yaw - _target.Yaw > 180f) yaw -= 360f;
            while (yaw - _target.Yaw < -180f) yaw += 360f;

            _target.Yaw = yaw;
            _target.Pitch = Math.Clamp(pitch, -89.9f, 89.9f);
        }

        /// <summary>Eases towards where the camera is headed, and puts the camera there.</summary>
        public void Update(float deltaTime)
        {
            if (_anchor is Vector3 anchor)
            {
                // Looking or flying: the camera's position is fixed (or flown), the pivot swings to stay in front.
                _target.Pivot = anchor + Direction(_target.Yaw, _target.Pitch) * _target.Distance;
            }

            float t = 1f - MathF.Exp(-Smoothing * deltaTime);
            float turn = 1f - MathF.Exp(-Smoothing * 1.6f * deltaTime);
            _current.Yaw += (_target.Yaw - _current.Yaw) * turn;
            _current.Pitch += (_target.Pitch - _current.Pitch) * turn;

            // Distance eases in proportion, so zooming far out doesn't race.
            _current.Distance = MathF.Exp(MathF.Log(_current.Distance) + (MathF.Log(_target.Distance) - MathF.Log(_current.Distance)) * t);

            if (_anchor is Vector3 fixedAt)
            {
                _current.Pivot = fixedAt + Direction(_current.Yaw, _current.Pitch) * _current.Distance;
            }
            else
            {
                _current.Pivot += (_target.Pivot - _current.Pivot) * t;
            }

            Apply();
        }

        /// <summary>Jumps straight to where the camera is headed: for tests, and opening a scene.</summary>
        public void Settle()
        {
            if (_anchor is Vector3 anchor) _target.Pivot = anchor + Direction(_target.Yaw, _target.Pitch) * _target.Distance;
            _current = _target;
            Apply();
        }

        private void Apply()
        {
            Camera.Transform.Position = PositionOf(_current);
            Camera.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(_current.Yaw * MathF.PI / 180f, _current.Pitch * MathF.PI / 180f, 0f);

            // Close in, see small things clearly; far out, see far.
            Camera.NearPlane = Math.Clamp(_current.Distance * 0.005f, 0.02f, 1f);
            Camera.FarPlane = MathF.Max(5000f, _current.Distance * 20f);
        }

        /// <summary>The ray from the camera through a point in a view: <paramref name="point"/> from 0 to 1 across and down it.</summary>
        public (Vector3 Origin, Vector3 Direction) Ray(Vector2 point, float aspect)
        {
            Matrix4x4 viewProjection = Camera.ViewMatrix * Camera.GetProjectionMatrix(aspect);
            Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);

            var ndc = new Vector2(point.X * 2f - 1f, 1f - point.Y * 2f);
            Vector4 near = Vector4.Transform(new Vector4(ndc, 0f, 1f), inverse);
            Vector4 far = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
            Vector3 from = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 to = new Vector3(far.X, far.Y, far.Z) / far.W;
            return (Camera.Transform.Position, Vector3.Normalize(to - from));
        }
    }
}
