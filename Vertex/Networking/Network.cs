namespace Vertex.Networking
{
    /// <summary>
    /// The game's network connection. Start it with a transport (<see cref="SteamTransport"/>, <see cref="UdpTransport"/>),
    /// then send messages and listen to the events here. The events stay subscribed when the transport changes,
    /// and are raised on the main thread at the start of each frame.
    /// </summary>
    public static class Network
    {
        public static INetworkTransport? Transport { get; private set; }

        public static bool InSession => Transport?.InSession ?? false;

        public static PeerId LocalPeer => Transport?.LocalPeer ?? default;

        public static IReadOnlyCollection<PeerId> Peers => Transport?.Peers ?? [];

        public static event Action<PeerId>? PeerJoined;
        public static event Action<PeerId>? PeerLeft;
        public static event MessageHandler? MessageReceived;
        public static event Action? SessionStarted;
        public static event Action? SessionEnded;

        /// <summary>Switches to <paramref name="transport"/>, closing the current one first.</summary>
        public static void Start(INetworkTransport transport)
        {
            Stop();

            Transport = transport;
            transport.PeerJoined += OnPeerJoined;
            transport.PeerLeft += OnPeerLeft;
            transport.MessageReceived += OnMessageReceived;
            transport.SessionStarted += OnSessionStarted;
            transport.SessionEnded += OnSessionEnded;
        }

        public static void Stop()
        {
            if (Transport == null) return;

            INetworkTransport transport = Transport;
            Transport = null;

            transport.PeerJoined -= OnPeerJoined;
            transport.PeerLeft -= OnPeerLeft;
            transport.MessageReceived -= OnMessageReceived;
            transport.SessionStarted -= OnSessionStarted;
            transport.SessionEnded -= OnSessionEnded;
            bool wasInSession = transport.InSession;
            transport.Dispose();

            if (wasInSession) SessionEnded?.Invoke();
        }

        public static void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode) => Transport?.Send(peer, data, mode);

        /// <summary>Sends to everyone else in the session.</summary>
        public static void Broadcast(ReadOnlySpan<byte> data, SendMode mode)
        {
            if (Transport == null) return;

            foreach (PeerId peer in Transport.Peers)
            {
                Transport.Send(peer, data, mode);
            }
        }

        public static string GetName(PeerId peer) => Transport?.GetName(peer) ?? peer.ToString();

        internal static void Update() => Transport?.Update();

        private static void OnPeerJoined(PeerId peer) => PeerJoined?.Invoke(peer);
        private static void OnPeerLeft(PeerId peer) => PeerLeft?.Invoke(peer);
        private static void OnMessageReceived(PeerId from, ReadOnlySpan<byte> data) => MessageReceived?.Invoke(from, data);
        private static void OnSessionStarted() => SessionStarted?.Invoke();
        private static void OnSessionEnded() => SessionEnded?.Invoke();
    }
}
