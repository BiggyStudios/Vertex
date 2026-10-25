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
    /// How a player died: who killed them (null for nobody), whether by headshot, whether they fell off the map, the
    /// weapon, whether it was a sniper shot without the scope, and how far away the killer was, in metres.
    /// </summary>
    public readonly record struct Death(PeerId? Killer, bool Headshot, bool Fell = false, WeaponId Weapon = WeaponId.Rifle,
        bool NoScope = false, float Distance = 0f);

    /// <summary>
    /// First-person player with Quake / Source style movement, ported from Source's gamemovement.cpp:
    /// ground friction and acceleration, air control that allows air strafing, and bunny hopping.
    /// Mouse to look, WASD to move, Space or mouse wheel to jump, left mouse to shoot, hold right mouse to scope (sniper rifle),
    /// V to toggle noclip.
    /// </summary>
    public class Player : GameObject
    {
        // Source measures in inches ("hammer units"); this engine uses metres.
        private const float _units = 0.0254f;

        // Moving up faster than this means you're airborne even while touching the ground (just jumped, launched by a ramp).
        private const float _nonJumpVelocity = 140f * _units;

        private const float _maxVelocity = 3500f * _units;
        private const float _eyeHeight = 64f * _units;

        // Flying from a bounce pad, air control only takes away this share of the speed it normally would.
        private const float _padFlightSpeedLoss = 0.25f;

        // Defaults match Counter-Strike: Source, except air control, which is set like a bhop server's. Each comment names
        // the Source cvar the value comes from.
        public float MaxSpeed { get; set; } = 250f * _units;       // sv_maxspeed (run speed with a knife)
        public float Acceleration { get; set; } = 10f;            // sv_accelerate
        public float AirAcceleration { get; set; } = 100f;        // sv_airaccelerate (as on bhop and KZ servers; CS's default is 10-12)
        public float Friction { get; set; } = 4f;                 // sv_friction
        public float StopSpeed { get; set; } = 75f * _units;       // sv_stopspeed
        public float Gravity { get; set; } = DefaultGravity;       // sv_gravity

        /// <summary>sv_gravity's default, which bounce pads are aimed with.</summary>
        public const float DefaultGravity = 1000f * _units;
        public float JumpHeight { get; set; } = 57f * _units;

        /// <summary>Air control in surf areas, used instead of AirAcceleration while <see cref="InSurfZone"/>.</summary>
        public float SurfAirAcceleration { get; set; } = 150f;     // sv_surf_airaccelerate (surf servers' sv_airaccelerate)

        /// <summary>The area the player is in, set by teleport pads: where they respawn, and whether it's for surfing.</summary>
        public Zone Zone { get; set; } = Zone.Sandbox;

        public bool InSurfZone => Zone.Surf;

        /// <summary>Holding jump jumps again the moment you land (sv_autobunnyhopping). Otherwise each jump needs a fresh press.</summary>
        public bool AutoBunnyhop { get; set; } = true;

        /// <summary>
        /// In the air, speed along the direction you're pressing is capped at this, but acceleration still uses your full
        /// wish speed. Turning while strafing keeps adding a little speed sideways: that's air strafing. Each tick of
        /// strafing can add at most the square of this to your speed squared, so raising it makes speed build faster.
        /// Source uses 30 units/s (CS:GO's sv_air_max_wishspeed); this is a little higher, so speed is easier to build.
        /// </summary>
        public float AirSpeedCap { get; set; } = 40f * _units;  // sv_air_max_wishspeed

        /// <summary>Degrees of turn per pixel of mouse movement. Scoped in, it's less, by as much as the view is zoomed.</summary>
        public float LookSensitivity { get; set; } = 0.1f;

        /// <summary>
        /// The camera's vertical field of view in degrees, when not zoomed. Source's default 90 degrees is horizontal at
        /// 4:3, which is about 74 degrees vertical.
        /// </summary>
        public float FieldOfView { get; set; } = 73.74f;

        /// <summary>How fast the player can run right now: sv_maxspeed, or less if the gun they hold is heavy.</summary>
        public float RunSpeed => MathF.Min(MaxSpeed, Gun.MoveSpeed);

        /// <summary>Their class, chosen from the class menu, which decides their weapon.</summary>
        public PlayerClass Class { get; private set; } = PlayerClass.Rifleman;

        public float NoclipSpeed { get; set; } = 10f;

        public const int MaxHealth = 100;
        private const float _respawnDelay = 3f;

        /// <summary>Falling below this height off the edge of the map counts as dying, like Source's trigger_hurt pits.</summary>
        public float FallDeathHeight { get; set; } = -30f;

        public int Health { get; private set; } = MaxHealth;
        public bool IsDead { get; private set; }

        public CharacterController Controller => _controller;
        public Camera Camera { get; }

        /// <summary>Which way the player faces, in degrees: yaw around the vertical axis, pitch up and down.</summary>
        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public Gun Gun { get; }
        public Hud Hud { get; }

        /// <summary>Seconds until respawning, while dead.</summary>
        public float RespawnIn => IsDead ? MathF.Max(_respawnTimer, 0f) : 0f;

        /// <summary>Raised when health reaches zero.</summary>
        public event Action<Death>? Died;

        private readonly CharacterController _controller;
        private readonly GameObject _eye;

        private float _yaw;
        private float _pitch;
        private bool _onGround;
        private bool _padFlight;
        private bool _jumpHeld;
        private bool _scrollJumpQueued;
        private bool _noclip;
        private float _respawnTimer;

        public static Vector3 SpawnPosition { get; } = new(0f, 0f, 6f);

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
            Camera = _eye.AddComponent<Camera>();
            Camera.FieldOfView = FieldOfView;

            // Only visible in noclip; from inside it the back faces are culled.
            var body = new GameObject("Body");
            body.Transform.Parent = Transform;
            body.Transform.Position = new Vector3(0f, _controller.Height * 0.5f, 0f);
            body.Transform.Scale = new Vector3(_controller.Radius * 2f, _controller.Height, _controller.Radius * 2f);
            MeshRenderer bodyRenderer = body.AddComponent<MeshRenderer>();
            bodyRenderer.Mesh = Primitives.Sphere;
            bodyRenderer.Material.Color = Color.Orange;
            // The camera is inside it, so its shadow would fall on the gun and darken everything in view.
            bodyRenderer.CastShadows = false;

            Gun = AddComponent<Gun>();
            Gun.Attach(this, _eye);
            Hud = AddComponent<Hud>();
            Hud.Attach(this);
        }

        /// <summary>
        /// Loses health, dying at zero. <paramref name="attacker"/> is who to credit for the kill, and the rest say how,
        /// for the kill feed if it's the killing blow: <paramref name="distance"/> is how far away the attacker was, in
        /// metres.
        /// </summary>
        public void TakeDamage(int amount, PeerId? attacker = null, bool headshot = false, WeaponId weapon = WeaponId.Rifle,
            bool noScope = false, float distance = 0f)
        {
            if (IsDead || amount <= 0) return;

            Health = Math.Max(Health - amount, 0);
            Hud.ShowDamage(amount);
            if (Health == 0) Die(new Death(attacker, headshot, Weapon: weapon, NoScope: noScope, Distance: distance));
        }

        /// <summary>Changes class, switching to its weapon straight away.</summary>
        public void SetClass(PlayerClass playerClass)
        {
            if (playerClass == Class) return;

            Class = playerClass;
            Gun.Equip(playerClass.Weapon);
        }

        /// <summary>
        /// Throws the player through the air at <paramref name="velocity"/>, from a bounce pad. Unlike a jump, it replaces
        /// their velocity rather than adding to it. Until they land, strafing costs less speed than usual (see
        /// <see cref="_padFlightSpeedLoss"/>), so they can steer onto where the pad is aimed without stalling.
        /// </summary>
        public void Launch(Vector3 velocity)
        {
            if (_noclip) return;

            _controller.Velocity = velocity;
            _onGround = false;
            _padFlight = true;
        }

        /// <summary>
        /// Dies straight away, credited to nobody: the console's kill command, or a kill trigger
        /// (<paramref name="fell"/>, shown as "fell to their death").
        /// </summary>
        public void Kill(bool fell = false)
        {
            if (!IsDead) Die(new Death(Killer: null, Headshot: false, fell));
        }

        private void Die(Death death)
        {
            IsDead = true;
            _respawnTimer = _respawnDelay;
            Died?.Invoke(death);
        }

        private void Respawn()
        {
            IsDead = false;
            Health = MaxHealth;
            _eye.Transform.Position = new Vector3(0f, _eyeHeight, 0f);
            SpawnPoint spawn = Zone.RandomSpawn();
            Teleport(spawn.Position, spawn.Yaw);
        }

        protected override void Start()
        {
            InputSystem.CursorLocked = true;
        }

        /// <summary>
        /// Adds <paramref name="velocityChange"/> to the player's velocity, like the shotgun's knockback. Pushed upwards
        /// hard enough, they leave the ground, as with a jump.
        /// </summary>
        public void Push(Vector3 velocityChange)
        {
            if (_noclip || IsDead) return;

            Vector3 velocity = _controller.Velocity + velocityChange;
            if (velocity.Y > _nonJumpVelocity) _onGround = false;
            _controller.Velocity = velocity;
        }

        /// <summary>
        /// Moves the player instantly, facing <paramref name="yaw"/> degrees (0 faces -Z). Normally this stops all
        /// movement. With <paramref name="keepVelocity"/>, they keep their speed, turned along with them: whatever was
        /// forward before is forward after, so running onto a pad at full tilt carries on in the new direction.
        /// </summary>
        public void Teleport(Vector3 position, float yaw, bool keepVelocity = false)
        {
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathHelper.ToRadians(yaw - _yaw));
            Vector3 velocity = keepVelocity ? Vector3.Transform(_controller.Velocity, turn) : Vector3.Zero;

            Transform.Position = position;
            _yaw = yaw;
            _pitch = 0f;
            Transform.EulerAngles = new Vector3(0f, _yaw, 0f);
            _eye.Transform.EulerAngles = Vector3.Zero;

            _controller.Velocity = velocity;
            _onGround = false;
            _padFlight = false;
        }

        protected override void Update()
        {
            // Look in Update, so turning is as smooth as the frame rate. Movement happens in FixedUpdate.
            // While a menu or the console is open, the mouse is pointing at that instead.
            // Zoomed in, the view turns slower to match, so the mouse moves what's in the scope as far as it would unzoomed.
            Vector2 mouse = GameInput.LookBlocked ? Vector2.Zero : InputSystem.MouseDelta;
            float sensitivity = LookSensitivity / Gun.Zoom;
            _yaw -= mouse.X * sensitivity;
            _pitch = Math.Clamp(_pitch - mouse.Y * sensitivity, -89f, 89f);

            float halfAngle = MathF.Atan(MathF.Tan(MathHelper.ToRadians(FieldOfView) * 0.5f) / Gun.Zoom);
            Camera.FieldOfView = MathHelper.ToDegrees(halfAngle) * 2f;
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

            if (InputSystem.GetKeyDown(Key.V) && !GameInput.Blocked)
            {
                _noclip = !_noclip;
                // Disabled, the controller leaves the physics world, so nothing bumps into where the player was.
                _controller.Enabled = !_noclip;
                _padFlight = false;
                _controller.Velocity = Vector3.Zero;
            }

            if (_noclip && !GameInput.Blocked) NoclipMove(Time.DeltaTime);
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
            // Typing in chat also stops the keys moving you.
            bool controllable = !IsDead && !GameInput.Blocked;
            if (!controllable) (wishDir, wishSpeed) = (Vector3.Zero, 0f);

            bool jumpDown = InputSystem.GetKey(Key.Space) && controllable;
            bool jump = (jumpDown && (!_jumpHeld || AutoBunnyhop)) || (_scrollJumpQueued && controllable);
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
                Vector3 before = velocity;
                AirAccelerate(ref velocity, wishDir, wishSpeed, dt);
                if (_padFlight) SoftenSpeedLoss(ref velocity, before);
            }

            _controller.Velocity = Vector3.Clamp(velocity, new Vector3(-_maxVelocity), new Vector3(_maxVelocity));
            _controller.Move(dt);
            velocity = _controller.Velocity;

            _onGround = _controller.IsGrounded && velocity.Y <= _nonJumpVelocity;
            if (_onGround) _padFlight = false;

            if (_onGround) velocity.Y = 0f;
            else velocity.Y -= Gravity * 0.5f * dt;

            _controller.Velocity = velocity;

            // Noclip skips all of this, so flying below the map is fine.
            if (!IsDead && Transform.WorldPosition.Y < FallDeathHeight) Die(new Death(Killer: null, Headshot: false, Fell: true));
        }

        /// <summary>The flat direction the movement keys point, relative to where the player faces.</summary>
        private (Vector3 Direction, float Speed) GetWishDirection()
        {
            float forwardMove = (InputSystem.GetKey(Key.W) ? 1f : 0f) - (InputSystem.GetKey(Key.S) ? 1f : 0f);
            float sideMove = (InputSystem.GetKey(Key.D) ? 1f : 0f) - (InputSystem.GetKey(Key.A) ? 1f : 0f);

            Vector3 forward = Transform.Forward with { Y = 0f };
            Vector3 right = Transform.Right with { Y = 0f };
            Vector3 wish = Vector3.Normalize(forward) * forwardMove + Vector3.Normalize(right) * sideMove;

            // Source's key speeds (cl_forwardspeed 450) are always clamped down to the run speed, even diagonally.
            return wish == Vector3.Zero ? (Vector3.Zero, 0f) : (Vector3.Normalize(wish), RunSpeed);
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

        /// <summary>
        /// Gives back most of the speed across the ground that air control just took away. Turning while strafing keeps
        /// the new direction at nearly the old speed, so it steers without bleeding speed. Air control that pushes you
        /// backwards (holding back) only takes effect a share at a time, so it slows you to a stop smoothly instead of
        /// flipping you round.
        /// </summary>
        private static void SoftenSpeedLoss(ref Vector3 velocity, Vector3 before)
        {
            Vector3 across = velocity with { Y = 0f };
            Vector3 was = before with { Y = 0f };
            float speed = across.Length();
            float speedBefore = was.Length();
            if (speed >= speedBefore) return;

            if (Vector3.Dot(across, was) <= 0f || speed < 0.01f)
            {
                across = was + (across - was) * _padFlightSpeedLoss;
            }
            else
            {
                float softened = speedBefore - (speedBefore - speed) * _padFlightSpeedLoss;
                across *= softened / speed;
            }

            velocity = across with { Y = velocity.Y };
        }

        private void AirAccelerate(ref Vector3 velocity, Vector3 wishDir, float wishSpeed, float dt)
        {
            float addSpeed = MathF.Min(wishSpeed, AirSpeedCap) - Vector3.Dot(velocity, wishDir);
            if (addSpeed <= 0f) return;

            // Uses the full wish speed, not the capped one: see AirSpeedCap.
            float acceleration = InSurfZone ? SurfAirAcceleration : AirAcceleration;
            velocity += wishDir * MathF.Min(acceleration * wishSpeed * dt, addSpeed);
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
