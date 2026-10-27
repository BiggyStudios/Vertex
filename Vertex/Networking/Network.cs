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

        /// <summary>The room's name, as its host set it. Empty when not in a room.</summary>
        public static string RoomName => Transport is { InSession: true } transport ? transport.RoomName : "";

        /// <summary>Whether the room has a password, so only those who know it are in. False when not in a room.</summary>
        public static bool RoomIsPrivate => Transport is { InSession: true, RoomIsPrivate: true };

        /// <summary>Who's in charge of the session's shared settings. See <see cref="INetworkTransport.HostPeer"/>.</summary>
        public static PeerId HostPeer => Transport?.HostPeer ?? default;

        /// <summary>True when this machine is the host, or isn't in a session (playing alone, you're in charge).</summary>
        public static bool IsHost => !InSession || LocalPeer == HostPeer;

        public static event Action<PeerId>? PeerJoined;
        public static event Action<PeerId>? PeerLeft;
        public static event MessageHandler? MessageReceived;
        public static event Action? SessionStarted;
        public static event Action? SessionEnded;

        /// <summary>Raised instead of <see cref="SessionStarted"/> when joining a room didn't work.</summary>
        public static event Action<JoinError>? JoinFailed;

        /// <summary>
        /// Raised when the host kicks this machine out of the room, just before <see cref="SessionEnded"/>, with how long
        /// until it can come back: null if it can't.
        /// </summary>
        public static event Action<TimeSpan?>? Kicked;

        /// <summary>
        /// This copy of the game's version. Rooms only let in players on the same one, so copies with different features
        /// never play together; bump it whenever a change would matter to someone else in the room.
        /// </summary>
        public static string GameVersion { get; set; } = "";

        /// <summary>How long someone kicked for the first time is kept out of the room. Kicked again, they're out for good.</summary>
        public static TimeSpan FirstKickBan { get; set; } = TimeSpan.FromSeconds(20);

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
            transport.JoinFailed += OnJoinFailed;
            transport.Kicked += OnKicked;
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
            transport.JoinFailed -= OnJoinFailed;
            transport.Kicked -= OnKicked;
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

        /// <summary>
        /// The host only: puts <paramref name="peer"/> out of the room, and keeps them out for <see cref="FirstKickBan"/>,
        /// or for good if they've been kicked before.
        /// </summary>
        public static void Kick(PeerId peer)
        {
            if (InSession && IsHost) Transport!.Kick(peer, FirstKickBan);
        }

        public static string GetName(PeerId peer) => Transport?.GetName(peer) ?? peer.ToString();

        internal static void Update() => Transport?.Update();

        private static void OnPeerJoined(PeerId peer) => PeerJoined?.Invoke(peer);
        private static void OnPeerLeft(PeerId peer) => PeerLeft?.Invoke(peer);
        private static void OnMessageReceived(PeerId from, ReadOnlySpan<byte> data) => MessageReceived?.Invoke(from, data);
        private static void OnSessionStarted() => SessionStarted?.Invoke();
        private static void OnSessionEnded() => SessionEnded?.Invoke();
        private static void OnJoinFailed(JoinError error) => JoinFailed?.Invoke(error);
        private static void OnKicked(TimeSpan? banFor) => Kicked?.Invoke(banFor);
    }
}
