using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;

namespace SampleGame
{
    /// <summary>
    /// Fly camera: hold right mouse to look around, WASD to move, E/Q for up/down, Shift to go faster.
    /// </summary>
    public class CameraController : Component
    {
        public float MoveSpeed { get; set; } = 5f;
        public float LookSensitivity { get; set; } = 0.15f;

        private float _yaw;
        private float _pitch;

        protected override void Start()
        {
            // Pick up whatever direction the camera was facing when the game started.
            Vector3 forward = Transform.Forward;
            _yaw = MathHelper.ToDegrees(MathF.Atan2(-forward.X, -forward.Z));
            _pitch = MathHelper.ToDegrees(MathF.Asin(forward.Y));
        }

        protected override void Update()
        {
            bool looking = InputSystem.GetMouseButton(MouseButton.Right);
            InputSystem.CursorLocked = looking;

            if (looking)
            {
                Vector2 delta = InputSystem.MouseDelta;
                _yaw -= delta.X * LookSensitivity;
                _pitch = Math.Clamp(_pitch - delta.Y * LookSensitivity, -89f, 89f);
                Transform.EulerAngles = new Vector3(_pitch, _yaw, 0f);
            }

            Vector3 move = Vector3.Zero;
            if (InputSystem.GetKey(Key.W)) move += Transform.Forward;
            if (InputSystem.GetKey(Key.S)) move -= Transform.Forward;
            if (InputSystem.GetKey(Key.D)) move += Transform.Right;
            if (InputSystem.GetKey(Key.A)) move -= Transform.Right;
            if (InputSystem.GetKey(Key.E)) move += Vector3.UnitY;
            if (InputSystem.GetKey(Key.Q)) move -= Vector3.UnitY;

            if (move != Vector3.Zero)
            {
                float speed = InputSystem.GetKey(Key.ShiftLeft) ? MoveSpeed * 3f : MoveSpeed;
                Transform.Position += Vector3.Normalize(move) * speed * Time.DeltaTime;
            }
        }
    }
}
