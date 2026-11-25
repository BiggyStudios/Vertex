using System.Numerics;

namespace Vertex.Networking
{
    /// <summary>
    /// Shows an object's moves on everyone's machine: its owner sends where it is several times a second, and everyone
    /// else's copy glides there. Between updates a copy keeps going the way the last ones showed it going, so something
    /// moving fast doesn't lurch forward and stop with each update. Needs a <see cref="NetworkObject"/> (one's added if
    /// it hasn't).
    /// </summary>
    public sealed class NetworkTransform : Component
    {
        /// <summary>How many times a second the owner sends where it is. More is smoother and costs more.</summary>
        [Range(1f, 60f)]
        public float SendRate { get; set; } = 20f;

        /// <summary>How quickly other players' copies catch up with where it's been sent to be: higher is snappier.</summary>
        public float Smoothing { get; set; } = 18f;

        /// <summary>
        /// Moving faster than this (metres a second) between two updates counts as a jump, not a move: the copy goes
        /// straight there instead of racing across.
        /// </summary>
        public float TeleportSpeed { get; set; } = 50f;

        private NetworkObject? _networked;
        private float _sinceSent;
        private Vector3 _position, _velocity;
        private Quaternion _rotation = Quaternion.Identity;
        private float _sinceReceived;
        private bool _received;

        protected internal override void Start()
        {
            _networked = GameObject.GetComponent<NetworkObject>() ?? GameObject.AddComponent<NetworkObject>();
        }

        protected internal override void Update()
        {
            if (_networked == null || !Network.InSession) return;

            if (_networked.IsMine)
            {
                _sinceSent += Time.DeltaTime;
                if (_sinceSent < 1f / MathF.Max(SendRate, 1f)) return;
                _sinceSent = 0f;
                NetworkScene.SendTransform(_networked, Transform.WorldPosition, Transform.WorldRotation);
            }
            else if (_received)
            {
                Follow(Time.DeltaTime);
            }
        }

        /// <summary>
        /// Glides the copy a step towards where it should be by now: where it was last sent to be, carried on at the speed
        /// it was going, but no further than an update and a half's worth (it may have stopped).
        /// </summary>
        internal void Follow(float deltaTime)
        {
            _sinceReceived += deltaTime;
            float guess = MathF.Min(_sinceReceived, 1.5f / MathF.Max(SendRate, 1f));
            float t = 1f - MathF.Exp(-Smoothing * deltaTime);
            Transform.WorldPosition = Vector3.Lerp(Transform.WorldPosition, _position + _velocity * guess, t);
            Transform.WorldRotation = Quaternion.Slerp(Transform.WorldRotation, _rotation, t);
        }

        /// <summary>Where the owner says it is now. The first time, or after a jump, it goes straight there.</summary>
        internal void Receive(Vector3 position, Quaternion rotation)
        {
            if (_received)
            {
                float gap = Math.Clamp(_sinceReceived, 1f / 120f, 0.5f);
                _velocity = (position - _position) / gap;
                if (_velocity.Length() > TeleportSpeed)
                {
                    _velocity = Vector3.Zero;
                    Transform.WorldPosition = position;
                }
            }

            _position = position;
            _rotation = rotation;
            _sinceReceived = 0f;
            if (_received) return;

            _received = true;
            Transform.WorldPosition = position;
            Transform.WorldRotation = rotation;
        }
    }
}
