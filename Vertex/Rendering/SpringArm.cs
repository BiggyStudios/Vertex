using System.Numerics;

using Vertex.Input;
using Vertex.Serialization;
using Vertex.Physics;

namespace Vertex.Rendering
{
    /// <summary>
    /// Holds its object out behind something on an arm, for a third-person camera: put it on the camera, make the camera
    /// a child of the character (or point <see cref="Follow"/> at it), and the camera sits <see cref="Length"/> behind,
    /// turning about a point above it with the mouse. When something's in the way the arm pulls in at once, so walls never
    /// block the view, then springs back out once the way is clear. It can lag behind as it follows, and sway like a
    /// camera held in the hand.
    /// <para>
    /// Scripts can read <see cref="FlatForward"/> and <see cref="FlatRight"/> to move a character the way the camera looks,
    /// and set <see cref="Yaw"/> and <see cref="Pitch"/> to aim it. Call <see cref="Snap"/> after moving what it follows a
    /// long way at once, so it doesn't drift across.
    /// </para>
    /// </summary>
    public sealed class SpringArm : Component
    {
        [Header("Arm")]
        [Tooltip("What it follows. Empty for the object it's a child of")]
        public Transform? Follow { get; set; }

        [Tooltip("Where the arm turns about, from what it follows: up to about the shoulders for a character")]
        public Vector3 PivotOffset { get; set; } = new(0f, 1.6f, 0f);

        [Tooltip("How far behind the pivot it sits, in metres")]
        [Range(0f, 30f)]
        public float Length { get; set; } = 4f;

        [Tooltip("Moves it across and up at the end of the arm, as it looks: (0.6, 0.2, 0) looks over the right shoulder")]
        public Vector3 Offset { get; set; }

        /// <summary>How far up it looks, in degrees: negative looks down. The mouse changes it as the game runs.</summary>
        [Tooltip("How far up it looks to start with, in degrees: a little down (negative) looks over the character")]
        [Range(-89f, 89f)]
        public float Pitch { get; set; } = -15f;

        [Header("Walls")]
        [Tooltip("Pulls the arm in when something's between the pivot and the end, so the view isn't blocked")]
        public bool Collide { get; set; } = true;

        [Tooltip("How thick the arm is when it checks for walls, in metres: wide enough that walls never cut into the view")]
        [Range(0f, 1f)]
        public float Thickness { get; set; } = 0.25f;

        [Tooltip("Seconds it takes to spring back out once the way is clear (it pulls in at once)")]
        [Range(0f, 2f)]
        public float ReturnTime { get; set; } = 0.3f;

        [Header("Following")]
        [Tooltip("Seconds it takes to catch up as what it follows moves. 0 keeps up exactly; more feels looser")]
        [Range(0f, 1f)]
        public float FollowLag { get; set; } = 0.08f;

        [Tooltip("Seconds it takes to turn after what it follows, when it doesn't turn with the mouse")]
        [Range(0f, 2f)]
        public float TurnLag { get; set; } = 0.25f;

        [Header("Mouse")]
        [Tooltip("Turns about the pivot as the mouse moves. Off, it stays behind what it follows as that turns")]
        public bool TurnWithMouse { get; set; } = true;

        [Tooltip("Hides and locks the mouse pointer when the game starts, so the mouse turns it as far as you like")]
        public bool LockCursor { get; set; } = true;

        [Tooltip("Degrees it turns for each pixel the mouse moves")]
        [Range(0.01f, 1f)]
        public float Sensitivity { get; set; } = 0.15f;

        [Tooltip("Moving the mouse up looks down")]
        public bool InvertY { get; set; }

        [Tooltip("How far down it can look, in degrees (negative is down)")]
        [Range(-89f, 89f)]
        public float MinPitch { get; set; } = -60f;

        [Tooltip("How far up it can look, in degrees")]
        [Range(-89f, 89f)]
        public float MaxPitch { get; set; } = 60f;

        [Tooltip("The mouse wheel moves it in and out, between Min Length and Max Length")]
        public bool ZoomWithScroll { get; set; }

        [Range(0f, 30f)]
        public float MinLength { get; set; } = 1.5f;

        [Range(0f, 30f)]
        public float MaxLength { get; set; } = 8f;

        [Header("Sway")]
        [Tooltip("A slow drift in where it looks, like a camera held in the hand: how far, in degrees. 0 for none")]
        [Range(0f, 10f)]
        public float Sway { get; set; }

        [Tooltip("How quickly it sways: about how many times a second it drifts back and forth")]
        [Range(0.02f, 3f)]
        public float SwaySpeed { get; set; } = 0.3f;

        /// <summary>Which way it faces round, in degrees: 0 looks along -Z (forward), and more turns it to the left.</summary>
        [DontSave]
        [HideInInspector]
        public float Yaw { get; set; }

        /// <summary>How long the arm is now: shorter than <see cref="Length"/> while something's in the way.</summary>
        public float CurrentLength => _reach;

        /// <summary>Which way it looks, along the ground: to move a character the way the camera faces.</summary>
        public Vector3 FlatForward => new(-MathF.Sin(Yaw * _degrees), 0f, -MathF.Cos(Yaw * _degrees));

        /// <summary>Its right, along the ground.</summary>
        public Vector3 FlatRight => new(MathF.Cos(Yaw * _degrees), 0f, -MathF.Sin(Yaw * _degrees));

        private const float _degrees = MathF.PI / 180f;

        private Vector3 _pivot;
        private float _reach;
        private float _swayTime;
        private bool _placed;

        /// <summary>What it follows: <see cref="Follow"/>, or the object it's a child of.</summary>
        private Transform? Followed => Follow ?? Transform.Parent;

        protected internal override void Start()
        {
            // Behind what it follows, facing the way it faces.
            if (Followed is { } followed) Yaw = YawOf(followed.Forward);
            else Yaw = YawOf(Transform.Forward);
            if (LockCursor && TurnWithMouse) InputSystem.CursorLocked = true;
        }

        /// <summary>Catches up at once: no lag or springing back this frame, for after what it follows jumps somewhere.</summary>
        public void Snap() => _placed = false;

        /// <summary>
        /// Where the arm turns about, and where its end is with nothing in the way and nothing lagging: what the editor shows
        /// before the game runs it.
        /// </summary>
        internal (Vector3 Pivot, Vector3 End) Resting()
        {
            Transform? followed = Followed;
            Vector3 pivot = followed != null ? PivotOf(followed) : Transform.WorldPosition;
            float yaw = Started ? Yaw : YawOf((followed ?? Transform).Forward);
            float pitch = Math.Clamp(Pitch, MathF.Min(MinPitch, MaxPitch), MathF.Max(MinPitch, MaxPitch));
            Quaternion look = Quaternion.CreateFromYawPitchRoll(yaw * _degrees, pitch * _degrees, 0f);
            return (pivot, pivot + Vector3.Transform(Offset + new Vector3(0f, 0f, Length), look));
        }

        private Vector3 PivotOf(Transform followed) => followed.WorldPosition + Vector3.Transform(PivotOffset, followed.WorldRotation);

        protected internal override void LateUpdate()
        {
            float deltaTime = Time.DeltaTime;
            Transform? followed = Followed;

            if (TurnWithMouse)
            {
                Vector2 mouse = InputSystem.MouseDelta;
                Yaw -= mouse.X * Sensitivity;
                Pitch -= mouse.Y * Sensitivity * (InvertY ? -1f : 1f);
            }
            else if (followed != null)
            {
                float behind = YawOf(followed.Forward);
                Yaw = _placed ? Yaw + Damp(0f, Wrap(behind - Yaw), TurnLag, deltaTime) : behind;
            }

            Yaw = Wrap(Yaw);
            Pitch = Math.Clamp(Pitch, MathF.Min(MinPitch, MaxPitch), MathF.Max(MinPitch, MaxPitch));

            if (ZoomWithScroll && InputSystem.ScrollDelta != 0f)
            {
                float low = MathF.Min(MinLength, MaxLength), high = MathF.Max(MinLength, MaxLength);
                Length = Math.Clamp(Length * MathF.Pow(0.9f, InputSystem.ScrollDelta), low, high);
            }

            // Where the arm turns about now, and where it's got to following that.
            Vector3 pivot = followed != null ? PivotOf(followed) : _pivot;
            if (followed == null && !_placed) pivot = Transform.WorldPosition;
            _pivot = _placed ? Damp(_pivot, pivot, FollowLag, deltaTime) : pivot;

            Quaternion look = Quaternion.CreateFromYawPitchRoll(Yaw * _degrees, Pitch * _degrees, 0f);
            Vector3 wanted = _pivot + Vector3.Transform(Offset + new Vector3(0f, 0f, Length), look);

            // As far as it can reach from where the pivot really is, so lagging behind never takes it through a wall.
            Vector3 toEnd = wanted - pivot;
            float distance = toEnd.Length();
            float reach = distance;
            if (Collide && distance > 1e-4f && GameObject.Scene.Physics.SphereCast(pivot, Thickness, toEnd, distance, out RaycastHit hit, IgnoredCollider(followed)))
            {
                reach = hit.Distance;
            }

            _reach = !_placed || reach < _reach ? reach : Damp(_reach, reach, ReturnTime, deltaTime);
            _placed = true;

            Transform.WorldPosition = distance > 1e-4f ? pivot + toEnd * (_reach / distance) : wanted;
            Transform.WorldRotation = Swayed(look, deltaTime);
        }

        /// <summary>The look turned by a little of the handheld drift: sums of slow waves that never quite repeat.</summary>
        private Quaternion Swayed(Quaternion look, float deltaTime)
        {
            if (Sway <= 0f) return look;
            _swayTime += deltaTime * SwaySpeed * MathF.Tau;
            float t = _swayTime;
            float yaw = (MathF.Sin(t) * 0.6f + MathF.Sin(t * 0.537f + 1.3f) * 0.4f) * Sway;
            float pitch = (MathF.Sin(t * 0.81f + 2.1f) * 0.6f + MathF.Sin(t * 0.433f + 0.4f) * 0.4f) * Sway * 0.7f;
            float roll = MathF.Sin(t * 0.61f + 4.2f) * Sway * 0.3f;
            return Quaternion.Concatenate(Quaternion.CreateFromYawPitchRoll(yaw * _degrees, pitch * _degrees, roll * _degrees), look);
        }

        /// <summary>What it follows mustn't count as a wall: its own collider, or the first one above it (the character's).</summary>
        private static Collider? IgnoredCollider(Transform? followed)
        {
            for (Transform? at = followed; at != null; at = at.Parent)
            {
                if (at.GameObject.GetComponent<Collider>() is { } collider) return collider;
            }

            return null;
        }

        private static float YawOf(Vector3 forward) =>
            forward.X * forward.X + forward.Z * forward.Z < 1e-8f ? 0f : MathF.Atan2(-forward.X, -forward.Z) / _degrees;

        /// <summary>An angle in degrees, from -180 to 180.</summary>
        private static float Wrap(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }

        private static float Damp(float from, float to, float time, float deltaTime) =>
            time <= 0f ? to : from + (to - from) * (1f - MathF.Exp(-deltaTime / time));

        private static Vector3 Damp(Vector3 from, Vector3 to, float time, float deltaTime) =>
            time <= 0f ? to : Vector3.Lerp(from, to, 1f - MathF.Exp(-deltaTime / time));
    }
}
