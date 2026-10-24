using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// First-person player with Quake / Source style movement, ported from Source's gamemovement.cpp:
    /// ground friction and acceleration, air control that allows air strafing, and bunny hopping.
    /// Mouse to look, WASD to move, Space or mouse wheel to jump, left mouse to shoot, V to toggle noclip.
    /// </summary>
    public class Player : GameObject
    {
        // Source measures in inches ("hammer units"); this engine uses metres.
        private const float _units = 0.0254f;

        // In the air, speed along the direction you're pressing is capped at 30 units/s, but acceleration still uses
        // your full wish speed. Turning while strafing keeps adding a little speed sideways: that's air strafing.
        private const float _airSpeedCap = 30f * _units;

        // Moving up faster than this means you're airborne even while touching the ground (just jumped, launched by a ramp).
        private const float _nonJumpVelocity = 140f * _units;

        private const float _maxVelocity = 3500f * _units;
        private const float _eyeHeight = 64f * _units;

        // Defaults match Counter-Strike: Source. Each comment names the Source cvar the value comes from.
        public float MaxSpeed { get; set; } = 250f * _units;       // sv_maxspeed (run speed with a knife)
        public float Acceleration { get; set; } = 10f;            // sv_accelerate
        public float AirAcceleration { get; set; } = 12f;         // sv_airaccelerate
        public float Friction { get; set; } = 4f;                 // sv_friction
        public float StopSpeed { get; set; } = 75f * _units;       // sv_stopspeed
        public float Gravity { get; set; } = 1000f * _units;        // sv_gravity
        public float JumpHeight { get; set; } = 57f * _units;

        /// <summary>Holding jump jumps again the moment you land (sv_autobunnyhopping). Otherwise each jump needs a fresh press.</summary>
        public bool AutoBunnyhop { get; set; }

        /// <summary>Degrees of turn per pixel of mouse movement.</summary>
        public float LookSensitivity { get; set; } = 0.1f;

        public float NoclipSpeed { get; set; } = 10f;

        public const int MaxHealth = 100;
        private const float _respawnDelay = 3f;

        /// <summary>Falling below this height off the edge of the map counts as dying, like Source's trigger_hurt pits.</summary>
        public float FallDeathHeight { get; set; } = -30f;

        public int Health { get; private set; } = MaxHealth;
        public bool IsDead { get; private set; }

        public CharacterController Controller => _controller;

        /// <summary>Which way the player faces, in degrees: yaw around the vertical axis, pitch up and down.</summary>
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public Gun Gun { get; }
        public Hud Hud { get; }

        /// <summary>Raised when health reaches zero, with who did it (null if nobody, or offline).</summary>
        public event Action<PeerId?>? Died;

        private readonly CharacterController _controller;
        private readonly GameObject _eye;

        private float _yaw;
        private float _pitch;
        private bool _onGround;
        private bool _jumpHeld;
        private bool _scrollJumpQueued;
        private bool _noclip;
        private float _respawnTimer;

        public static Vector3 SpawnPosition { get; } = new(0f, 0f, 6f);

        // Spread around the sandbox, clear of the crates and the house.
        private static readonly Vector3[] _respawnPoints =
        [
            SpawnPosition, new(-10f, 0f, 8f), new(10f, 0f, -8f), new(-11f, 0f, -11f), new(11f, 0f, 11f), new(0f, 0f, -12f)
        ];

        public Player() : base("Player")
        {
            Transform.Position = SpawnPosition;

            // Source's player is 32 x 32 x 72 units and steps up 18 units.
            _controller = AddComponent<CharacterController>();
            _controller.Radius = 16f * _units;
            _controller.Height = 72f * _units;
            _controller.StepHeight = 18f * _units;

            _eye = new GameObject("Eye");
            _eye.Transform.Parent = Transform;
            _eye.Transform.Position = new Vector3(0f, _eyeHeight, 0f);
            // Source's default 90 degree field of view is horizontal at 4:3, which is about 74 degrees vertical.
            _eye.AddComponent<Camera>().FieldOfView = 73.74f;

            // Only visible in noclip; from inside it the back faces are culled.
            var body = new GameObject("Body");
            body.Transform.Parent = Transform;
            body.Transform.Position = new Vector3(0f, _controller.Height * 0.5f, 0f);
            body.Transform.Scale = new Vector3(_controller.Radius * 2f, _controller.Height, _controller.Radius * 2f);
            MeshRenderer bodyRenderer = body.AddComponent<MeshRenderer>();
            bodyRenderer.Mesh = Primitives.Sphere;
            bodyRenderer.Material.Color = Color.Orange;

            var lamp = new GameObject("Player Lamp");
            lamp.Transform.Parent = Transform;
            lamp.Transform.Position = new Vector3(0f, 2.2f, 0f);

            Light light = lamp.AddComponent<Light>();
            light.Type = LightType.Point;
            light.Color = new Color(1f, 0.6f, 0.2f);
            light.Intensity = 1.5f;
            light.Range = 6f;

            Gun = AddComponent<Gun>();
            Gun.Attach(this, _eye);
            Hud = AddComponent<Hud>();
            Hud.Attach(this, _eye);
        }

        /// <summary>Loses health, dying at zero. <paramref name="attacker"/> is who to credit for the kill.</summary>
        public void TakeDamage(int amount, PeerId? attacker = null)
        {
            if (IsDead || amount <= 0) return;

            Health = Math.Max(Health - amount, 0);
            if (Health == 0) Die(attacker);
        }

        private void Die(PeerId? killer)
        {
            IsDead = true;
            _respawnTimer = _respawnDelay;
            Died?.Invoke(killer);
        }

        private void Respawn()
        {
            IsDead = false;
            Health = MaxHealth;
            _eye.Transform.Position = new Vector3(0f, _eyeHeight, 0f);
            Teleport(_respawnPoints[Random.Shared.Next(_respawnPoints.Length)], yaw: Random.Shared.NextSingle() * 360f);
        }

        protected override void Start()
        {
            InputSystem.CursorLocked = true;
        }

        /// <summary>Moves the player instantly, facing <paramref name="yaw"/> degrees (0 faces -Z), and stops all movement.</summary>
        public void Teleport(Vector3 position, float yaw)
        {
            Transform.Position = position;
            _yaw = yaw;
            _pitch = 0f;
            Transform.EulerAngles = new Vector3(0f, _yaw, 0f);
            _eye.Transform.EulerAngles = Vector3.Zero;

            _controller.Velocity = Vector3.Zero;
            _onGround = false;
        }

        protected override void Update()
        {
            // Look in Update, so turning is as smooth as the frame rate. Movement happens in FixedUpdate.
            Vector2 mouse = InputSystem.MouseDelta;
            _yaw -= mouse.X * LookSensitivity;
            _pitch = Math.Clamp(_pitch - mouse.Y * LookSensitivity, -89f, 89f);
            Transform.EulerAngles = new Vector3(0f, _yaw, 0f);
            _eye.Transform.EulerAngles = new Vector3(_pitch, 0f, IsDead ? 40f : 0f);

            if (IsDead)
            {
                // Death camera: slump to the floor, head tilted, until respawning.
                Vector3 eye = _eye.Transform.Position;
                _eye.Transform.Position = eye with { Y = eye.Y + (0.3f - eye.Y) * (1f - MathF.Exp(-6f * Time.DeltaTime)) };

                _respawnTimer -= Time.DeltaTime;
                if (_respawnTimer <= 0f) Respawn();
                return;
            }

            // The wheel only reports for one frame, so remember it until the next physics step.
            if (InputSystem.ScrollDelta != 0f) _scrollJumpQueued = true;

            if (InputSystem.GetKeyDown(Key.V))
            {
                _noclip = !_noclip;
                // Disabled, the controller leaves the physics world, so nothing bumps into where the player was.
                _controller.Enabled = !_noclip;
                _controller.Velocity = Vector3.Zero;
            }

            if (_noclip) NoclipMove(Time.DeltaTime);
        }

        /// <summary>One tick of Source's FullWalkMove.</summary>
        protected override void FixedUpdate()
        {
            if (_noclip) return;

            float dt = Time.FixedDeltaTime;
            Vector3 velocity = _controller.Velocity;
            (Vector3 wishDir, float wishSpeed) = GetWishDirection();

            // Half the gravity before moving and half after, which follows the true arc of a jump exactly.
            velocity.Y -= Gravity * 0.5f * dt;

            // The dead still fall and slide to a stop, but can't steer or jump.
            if (IsDead) (wishDir, wishSpeed) = (Vector3.Zero, 0f);

            bool jumpDown = InputSystem.GetKey(Key.Space) && !IsDead;
            bool jump = (jumpDown && (!_jumpHeld || AutoBunnyhop)) || (_scrollJumpQueued && !IsDead);
            _jumpHeld = jumpDown;
            _scrollJumpQueued = false;

            if (_onGround && jump)
            {
                // Added on top of the half-step of gravity above, like Source, so the jump peaks at exactly JumpHeight.
                velocity.Y += MathF.Sqrt(2f * Gravity * JumpHeight);
                // Leaving the ground before friction runs is what makes bunny hopping keep its speed.
                _onGround = false;
            }

            if (_onGround)
            {
                velocity.Y = 0f;
                ApplyFriction(ref velocity, dt);
                Accelerate(ref velocity, wishDir, wishSpeed, Acceleration, dt);
                velocity.Y = 0f;
            }
            else
            {
                AirAccelerate(ref velocity, wishDir, wishSpeed, dt);
            }

            _controller.Velocity = Vector3.Clamp(velocity, new Vector3(-_maxVelocity), new Vector3(_maxVelocity));
            _controller.Move(dt);
            velocity = _controller.Velocity;

            _onGround = _controller.IsGrounded && velocity.Y <= _nonJumpVelocity;

            if (_onGround) velocity.Y = 0f;
            else velocity.Y -= Gravity * 0.5f * dt;

            _controller.Velocity = velocity;

            // Noclip skips all of this, so flying below the map is fine.
            if (!IsDead && Transform.WorldPosition.Y < FallDeathHeight) Die(null);
        }

        /// <summary>The flat direction the movement keys point, relative to where the player faces.</summary>
        private (Vector3 Direction, float Speed) GetWishDirection()
        {
            float forwardMove = (InputSystem.GetKey(Key.W) ? 1f : 0f) - (InputSystem.GetKey(Key.S) ? 1f : 0f);
            float sideMove = (InputSystem.GetKey(Key.D) ? 1f : 0f) - (InputSystem.GetKey(Key.A) ? 1f : 0f);

            Vector3 forward = Transform.Forward with { Y = 0f };
            Vector3 right = Transform.Right with { Y = 0f };
            Vector3 wish = Vector3.Normalize(forward) * forwardMove + Vector3.Normalize(right) * sideMove;

            // Source's key speeds (cl_forwardspeed 450) are always clamped down to sv_maxspeed, even diagonally.
            return wish == Vector3.Zero ? (Vector3.Zero, 0f) : (Vector3.Normalize(wish), MaxSpeed);
        }

        private void ApplyFriction(ref Vector3 velocity, float dt)
        {
            float speed = velocity.Length();
            if (speed < 0.1f * _units) return;

            // Below the stop speed, friction acts as if you were going at the stop speed, so you come to a halt quickly.
            float control = MathF.Max(speed, StopSpeed);
            float newSpeed = MathF.Max(speed - control * Friction * dt, 0f);
            velocity *= newSpeed / speed;
        }

        private static void Accelerate(ref Vector3 velocity, Vector3 wishDir, float wishSpeed, float acceleration, float dt)
        {
            // Only the speed already going in the wish direction counts, so you can always accelerate sideways.
            float addSpeed = wishSpeed - Vector3.Dot(velocity, wishDir);
            if (addSpeed <= 0f) return;

            velocity += wishDir * MathF.Min(acceleration * wishSpeed * dt, addSpeed);
        }

        private void AirAccelerate(ref Vector3 velocity, Vector3 wishDir, float wishSpeed, float dt)
        {
            float addSpeed = MathF.Min(wishSpeed, _airSpeedCap) - Vector3.Dot(velocity, wishDir);
            if (addSpeed <= 0f) return;

            // Uses the full wish speed, not the capped one: see _airSpeedCap.
            velocity += wishDir * MathF.Min(AirAcceleration * wishSpeed * dt, addSpeed);
        }

        /// <summary>Flies where the camera looks, through everything. Shift to go faster, E/Q for up/down.</summary>
        private void NoclipMove(float dt)
        {
            Vector3 move = Vector3.Zero;
            if (InputSystem.GetKey(Key.W)) move += _eye.Transform.Forward;
            if (InputSystem.GetKey(Key.S)) move -= _eye.Transform.Forward;
            if (InputSystem.GetKey(Key.D)) move += Transform.Right;
            if (InputSystem.GetKey(Key.A)) move -= Transform.Right;
            if (InputSystem.GetKey(Key.E)) move += Vector3.UnitY;
            if (InputSystem.GetKey(Key.Q)) move -= Vector3.UnitY;
            if (move == Vector3.Zero) return;

            float speed = InputSystem.GetKey(Key.ShiftLeft) ? NoclipSpeed * 3f : NoclipSpeed;
            Transform.Position += Vector3.Normalize(move) * speed * dt;
        }
    }
}
