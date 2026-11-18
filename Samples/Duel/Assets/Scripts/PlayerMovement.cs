using System.Numerics;

using Vertex;
using Vertex.Input;
using Vertex.Physics;

namespace Duel
{
    /// <summary>
    /// Walks the player with WASD, jumps with Space and looks around with the mouse. Every machine has every player, but
    /// only the one who owns this player moves it: everyone else sees it move through its Network Transform.
    /// </summary>
    public class PlayerMovement : Component
    {
        [Header("Moving")]
        public float Speed { get; set; } = 6f;
        public float JumpSpeed { get; set; } = 5f;
        public float Gravity { get; set; } = 20f;

        [Header("Looking")]
        public float MouseSensitivity { get; set; } = 0.15f;

        /// <summary>The camera's object, under the player: it tips up and down while the whole player turns left and right.</summary>
        public GameObject? Head { get; set; }

        private CharacterController? _character;
        private float _pitch;

        protected override void Start()
        {
            _character = GetComponent<CharacterController>();
        }

        protected override void Update()
        {
            if (!IsMine || _character == null) return;

            // Click in the game to take the mouse, Escape to let it go.
            if (InputSystem.GetMouseButtonDown(MouseButton.Left)) InputSystem.CursorLocked = true;
            if (InputSystem.GetKeyDown(Key.Escape)) InputSystem.CursorLocked = false;

            if (InputSystem.CursorLocked) Look();
            Walk(_character);
        }

        private void Look()
        {
            Vector2 mouse = InputSystem.MouseDelta * MouseSensitivity;

            // Turning left and right turns the whole player; looking up and down only tips the head.
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
            move *= Speed;

            // On the ground it jumps or stays down; in the air it falls faster and faster.
            float rise = character.Velocity.Y;
            if (character.IsGrounded) rise = InputSystem.GetKey(Key.Space) ? JumpSpeed : -1f;
            else rise -= Gravity * Time.DeltaTime;

            character.Velocity = new Vector3(move.X, rise, move.Z);
            character.Move(Time.DeltaTime);
        }
    }
}
