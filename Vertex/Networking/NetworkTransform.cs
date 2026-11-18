using System.Numerics;

namespace Vertex.Networking
{
    /// <summary>
    /// Shows an object's moves on everyone's machine: its owner sends where it is several times a second, and everyone
    /// else's copy glides there. Needs a <see cref="NetworkObject"/> (one's added if it hasn't).
    /// </summary>
    public sealed class NetworkTransform : Component
    {
        /// <summary>How many times a second the owner sends where it is. More is smoother and costs more.</summary>
        [Range(1f, 60f)]
        public float SendRate { get; set; } = 20f;

        /// <summary>How quickly other players' copies catch up with where it's been sent to be: higher is snappier.</summary>
        public float Smoothing { get; set; } = 18f;

        private NetworkObject? _networked;
        private float _sinceSent;
        private Vector3 _position;
        private Quaternion _rotation = Quaternion.Identity;
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
                float t = 1f - MathF.Exp(-Smoothing * Time.DeltaTime);
                Transform.WorldPosition = Vector3.Lerp(Transform.WorldPosition, _position, t);
                Transform.WorldRotation = Quaternion.Slerp(Transform.WorldRotation, _rotation, t);
            }
        }

        /// <summary>Where the owner says it is now. The first time, it goes straight there.</summary>
        internal void Receive(Vector3 position, Quaternion rotation)
        {
            _position = position;
            _rotation = rotation;
            if (_received) return;

            _received = true;
            Transform.WorldPosition = position;
            Transform.WorldRotation = rotation;
        }
    }
}
