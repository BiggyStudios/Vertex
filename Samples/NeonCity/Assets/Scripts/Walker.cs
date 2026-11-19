using System.Numerics;

using Vertex;
using Vertex.Input;
using Vertex.Physics;

namespace NeonCity
{
    /// <summary>
    /// Walks with WASD (Shift to hurry), jumps with Space and looks around with the mouse. Click in the window to take
    /// the mouse, Escape to let it go.
    /// </summary>
    public class Walker : Component
    {
        [Header("Moving")]
        public float Speed { get; set; } = 3.5f;
        public float RunSpeed { get; set; } = 7f;
        public float JumpSpeed { get; set; } = 4.5f;
        public float Gravity { get; set; } = 18f;

        [Header("Looking")]
        public float MouseSensitivity { get; set; } = 0.12f;

        /// <summary>The camera's object, under this one: it tips up and down while the whole walker turns left and right.</summary>
        public GameObject? Head { get; set; }

        private CharacterController? _character;
        private float _pitch;

        protected override void Start()
        {
            _character = GetComponent<CharacterController>();
            if (Head != null) _pitch = Head.Transform.EulerAngles.X;
        }

        protected override void Update()
        {
            if (_character == null) return;

            if (InputSystem.GetMouseButtonDown(MouseButton.Left)) InputSystem.CursorLocked = true;
            if (InputSystem.GetKeyDown(Key.Escape)) InputSystem.CursorLocked = false;

            if (InputSystem.CursorLocked) Look();
            Walk(_character);
        }

        private void Look()
        {
            Vector2 mouse = InputSystem.MouseDelta * MouseSensitivity;
            Transform.EulerAngles += new Vector3(0f, -mouse.X, 0f);
            _pitch = Math.Clamp(_pitch - mouse.Y, -85f, 85f);
            if (Head != null) Head.Transform.EulerAngles = new Vector3(_pitch, 0f, 0f);
        }

        private void Walk(CharacterController character)
        {
            float forward = (InputSystem.GetKey(Key.W) ? 1f : 0f) - (InputSystem.GetKey(Key.S) ? 1f : 0f);
            float right = (InputSystem.GetKey(Key.D) ? 1f : 0f) - (InputSystem.GetKey(Key.A) ? 1f : 0f);
            Vector3 move = Transform.Forward * forward + Transform.Right * right;
            move.Y = 0f;
            if (move.LengthSquared() > 1f) move = Vector3.Normalize(move);
            move *= InputSystem.GetKey(Key.ShiftLeft) ? RunSpeed : Speed;

            float rise = character.Velocity.Y;
            if (character.IsGrounded) rise = InputSystem.GetKey(Key.Space) ? JumpSpeed : -1f;
            else rise -= Gravity * Time.DeltaTime;

            character.Velocity = new Vector3(move.X, rise, move.Z);
            character.Move(Time.DeltaTime);
        }
    }
}
