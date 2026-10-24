namespace Vertex.Networking
{
    /// <summary>A player in a network session. On Steam it's their SteamID.</summary>
    public readonly record struct PeerId(ulong Value)
    {
        public override string ToString() => Value.ToString();
    }

    public enum SendMode
    {
        /// <summary>Fast, but may be dropped or arrive out of order. For things sent every tick, like positions.</summary>
        Unreliable,

        /// <summary>Always arrives, in the order sent. For one-off events, like a hit or a death.</summary>
        Reliable
    }

    public delegate void MessageHandler(PeerId from, ReadOnlySpan<byte> data);

    /// <summary>
    /// The contract every networking backend (Steam, local UDP, ...) implements: a session of peers that
    /// can send each other messages. Game code goes through <see cref="Network"/> rather than a transport directly.
    /// </summary>
    public interface INetworkTransport : IDisposable
    {
        /// <summary>This machine's player.</summary>
        PeerId LocalPeer { get; }

        /// <summary>True while in a session, even if nobody else has joined yet.</summary>
        bool InSession { get; }

        /// <summary>Everyone else in the session.</summary>
        IReadOnlyCollection<PeerId> Peers { get; }

        /// <summary>A display name for a peer, such as their Steam name.</summary>
        string GetName(PeerId peer);

        void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode);

        /// <summary>Receives waiting messages and session changes, raising the events below. Called once per frame.</summary>
        void Update();

        event Action<PeerId>? PeerJoined;
        event Action<PeerId>? PeerLeft;
        event MessageHandler? MessageReceived;

        /// <summary>Raised when this machine joins a session.</summary>
        event Action? SessionStarted;

        /// <summary>Raised when this machine leaves a session or loses its connection to it.</summary>
        event Action? SessionEnded;
    }
}
