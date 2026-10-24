using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Vertex.Networking
{
    /// <summary>
    /// A simple UDP transport for testing on one PC or a home network: one game hosts, the others join its address,
    /// and the host passes messages between them. Reliable messages aren't resent if lost, which is rare on one PC
    /// or a LAN. Joining over the internet needs port forwarding; use <see cref="SteamTransport"/> for that instead.
    /// </summary>
    public sealed class UdpTransport : INetworkTransport
    {
        public const int DefaultPort = 27015;

        private static readonly PeerId _hostId = new(1);
        private const double _pingInterval = 1.0;
        private const double _timeout = 5.0;
        private const double _joinRetryInterval = 1.0;
        private const double _joinGiveUpAfter = 10.0;

        private enum Packet : byte
        {
            Join,       // client -> host
            Welcome,    // host -> client: your id, then everyone already in the session
            PeerJoined, // host -> clients
            PeerLeft,   // host -> clients
            Data,       // client -> host: a message for the host. host -> client: sender id, then a message.
            Relay,      // client -> host: recipient id, then a message to pass on
            Ping,
            Leave
        }

        private readonly UdpClient _socket;
        private readonly bool _isHost;
        private readonly IPEndPoint? _hostEndPoint;
        private readonly HashSet<PeerId> _peers = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // Host only: where each client is, and when they were last heard from. Clients only track the host.
        private readonly Dictionary<PeerId, IPEndPoint> _endPoints = new();
        private readonly Dictionary<IPEndPoint, PeerId> _peersByEndPoint = new();
        private readonly Dictionary<PeerId, double> _lastHeard = new();
        private ulong _nextPeerId = 2;

        private double _lastPingSent;
        private double _lastJoinSent;
        private bool _startedPending;
        private bool _disposed;
        private readonly List<PeerId> _timedOut = new();

        public PeerId LocalPeer { get; private set; }
        public bool InSession { get; private set; }
        public IReadOnlyCollection<PeerId> Peers => _peers;

        public event Action<PeerId>? PeerJoined;
        public event Action<PeerId>? PeerLeft;
        public event MessageHandler? MessageReceived;
        public event Action? SessionStarted;
        public event Action? SessionEnded;

        private double Now => _clock.Elapsed.TotalSeconds;

        private UdpTransport(UdpClient socket, bool isHost, IPEndPoint? hostEndPoint)
        {
            _socket = socket;
            _isHost = isHost;
            _hostEndPoint = hostEndPoint;

            // On Windows, a message to a closed port makes the next receive throw. Turn that off.
            if (OperatingSystem.IsWindows()) _socket.Client.IOControl(unchecked((int)0x9800000C), [0], null);
        }

        /// <summary>Starts a session that others can join on <paramref name="port"/>.</summary>
        public static UdpTransport Host(int port = DefaultPort)
        {
            var transport = new UdpTransport(new UdpClient(port), isHost: true, hostEndPoint: null)
            {
                LocalPeer = _hostId,
                InSession = true,
                _startedPending = true
            };

            Console.WriteLine($"[Vertex] Hosting on UDP port {port}.");
            return transport;
        }

        /// <summary>Joins the session hosted at <paramref name="address"/>. <see cref="SessionStarted"/> is raised once the host answers.</summary>
        public static UdpTransport Join(string address, int port = DefaultPort)
        {
            // The host listens on IPv4, so prefer that when a name like "localhost" also has an IPv6 address.
            IPAddress ip = IPAddress.TryParse(address, out IPAddress? parsed) ? parsed
                : Dns.GetHostAddresses(address).OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).First();
            var transport = new UdpTransport(new UdpClient(0, ip.AddressFamily), isHost: false, new IPEndPoint(ip, port));

            transport.SendPacket(transport._hostEndPoint!, Packet.Join, ReadOnlySpan<byte>.Empty);
            transport._lastJoinSent = transport.Now;

            Console.WriteLine($"[Vertex] Joining {ip}:{port}...");
            return transport;
        }

        public string GetName(PeerId peer) => $"Player {peer.Value}";

        public void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode)
        {
            if (_disposed || !InSession) return;

            if (_isHost)
            {
                if (_endPoints.TryGetValue(peer, out IPEndPoint? endPoint)) SendPacket(endPoint, Packet.Data, data, LocalPeer.Value);
            }
            else if (peer == _hostId)
            {
                SendPacket(_hostEndPoint!, Packet.Data, data);
            }
            else
            {
                SendPacket(_hostEndPoint!, Packet.Relay, data, peer.Value);
            }
        }

        public void Update()
        {
            if (_disposed) return;

            if (_startedPending)
            {
                _startedPending = false;
                SessionStarted?.Invoke();
            }

            while (_socket.Available > 0)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] packet;

                try
                {
                    packet = _socket.Receive(ref from);
                }
                catch (SocketException)
                {
                    continue;
                }

                if (packet.Length == 0) continue;

                if (_isHost) HandleOnHost((Packet)packet[0], packet.AsSpan(1), from);
                else if (from.Equals(_hostEndPoint)) HandleOnClient((Packet)packet[0], packet.AsSpan(1));
            }

            if (_isHost) UpdateHost();
            else UpdateClient();
        }

        private void HandleOnHost(Packet type, ReadOnlySpan<byte> body, IPEndPoint from)
        {
            if (type == Packet.Join)
            {
                if (!_peersByEndPoint.TryGetValue(from, out PeerId joined))
                {
                    joined = new PeerId(_nextPeerId++);
                    _endPoints[joined] = from;
                    _peersByEndPoint[from] = joined;

                    foreach (PeerId other in _peers) SendPacket(_endPoints[other], Packet.PeerJoined, ReadOnlySpan<byte>.Empty, joined.Value);
                    _peers.Add(joined);
                    PeerJoined?.Invoke(joined);
                }

                // Sent again if the client asks again, in case the first welcome was lost.
                _lastHeard[joined] = Now;
                SendWelcome(joined);
                return;
            }

            if (!_peersByEndPoint.TryGetValue(from, out PeerId sender)) return;
            _lastHeard[sender] = Now;

            switch (type)
            {
                case Packet.Data:
                    MessageReceived?.Invoke(sender, body);
                    break;

                case Packet.Relay:
                    var to = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                    if (_endPoints.TryGetValue(to, out IPEndPoint? target)) SendPacket(target, Packet.Data, body[8..], sender.Value);
                    break;

                case Packet.Leave:
                    RemoveClient(sender);
                    break;
            }
        }

        private void HandleOnClient(Packet type, ReadOnlySpan<byte> body)
        {
            _lastHeard[_hostId] = Now;

            switch (type)
            {
                case Packet.Welcome:
                    if (InSession) return;

                    LocalPeer = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                    InSession = true;
                    Console.WriteLine($"[Vertex] Joined as {GetName(LocalPeer)}.");
                    SessionStarted?.Invoke();

                    AddPeer(_hostId);
                    for (int offset = 8; offset + 8 <= body.Length; offset += 8)
                    {
                        AddPeer(new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body[offset..])));
                    }
                    break;

                case Packet.PeerJoined:
                    AddPeer(new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body)));
                    break;

                case Packet.PeerLeft:
                    var left = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                    if (_peers.Remove(left)) PeerLeft?.Invoke(left);
                    break;

                case Packet.Data:
                    var from = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                    if (_peers.Contains(from)) MessageReceived?.Invoke(from, body[8..]);
                    break;

                case Packet.Leave:
                    Console.WriteLine("[Vertex] The host closed the session.");
                    EndSession();
                    break;
            }
        }

        private void UpdateHost()
        {
            if (Now - _lastPingSent >= _pingInterval)
            {
                _lastPingSent = Now;
                foreach (IPEndPoint endPoint in _endPoints.Values) SendPacket(endPoint, Packet.Ping, ReadOnlySpan<byte>.Empty);
            }

            foreach (PeerId peer in _peers)
            {
                if (Now - _lastHeard[peer] > _timeout) _timedOut.Add(peer);
            }

            foreach (PeerId peer in _timedOut) RemoveClient(peer);
            _timedOut.Clear();
        }

        private void UpdateClient()
        {
            if (!InSession)
            {
                if (_hostEndPoint == null || _lastJoinSent < 0) return;

                if (Now > _joinGiveUpAfter)
                {
                    Console.WriteLine("[Vertex] Couldn't reach the host.");
                    _lastJoinSent = -1;
                    SessionEnded?.Invoke();
                }
                else if (Now - _lastJoinSent >= _joinRetryInterval)
                {
                    _lastJoinSent = Now;
                    SendPacket(_hostEndPoint, Packet.Join, ReadOnlySpan<byte>.Empty);
                }

                return;
            }

            if (Now - _lastPingSent >= _pingInterval)
            {
                _lastPingSent = Now;
                SendPacket(_hostEndPoint!, Packet.Ping, ReadOnlySpan<byte>.Empty);
            }

            if (Now - _lastHeard[_hostId] > _timeout)
            {
                Console.WriteLine("[Vertex] Lost connection to the host.");
                EndSession();
            }
        }

        private void SendWelcome(PeerId client)
        {
            Span<byte> body = stackalloc byte[8 * _peers.Count];
            BinaryPrimitives.WriteUInt64LittleEndian(body, client.Value);

            int offset = 8;
            foreach (PeerId other in _peers)
            {
                if (other == client) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(body[offset..], other.Value);
                offset += 8;
            }

            SendPacket(_endPoints[client], Packet.Welcome, body[..offset]);
        }

        private void RemoveClient(PeerId peer)
        {
            if (!_peers.Remove(peer)) return;

            _peersByEndPoint.Remove(_endPoints[peer]);
            _endPoints.Remove(peer);
            _lastHeard.Remove(peer);

            foreach (IPEndPoint endPoint in _endPoints.Values) SendPacket(endPoint, Packet.PeerLeft, ReadOnlySpan<byte>.Empty, peer.Value);
            PeerLeft?.Invoke(peer);
        }

        private void AddPeer(PeerId peer)
        {
            if (peer != LocalPeer && _peers.Add(peer)) PeerJoined?.Invoke(peer);
        }

        private void EndSession()
        {
            if (!InSession) return;

            InSession = false;
            foreach (PeerId peer in _peers.ToArray())
            {
                _peers.Remove(peer);
                PeerLeft?.Invoke(peer);
            }

            SessionEnded?.Invoke();
        }

        /// <summary>Sends a packet: its type, then an optional 8-byte id, then the body.</summary>
        private void SendPacket(IPEndPoint to, Packet type, ReadOnlySpan<byte> body, ulong? id = null)
        {
            int header = id.HasValue ? 9 : 1;
            byte[] packet = new byte[header + body.Length];
            packet[0] = (byte)type;
            if (id.HasValue) BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(1), id.Value);
            body.CopyTo(packet.AsSpan(header));

            try
            {
                _socket.Send(packet, packet.Length, to);
            }
            catch (SocketException)
            {
                // The other end has gone; the timeout will notice.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Tell the others straight away, rather than leaving them to time out.
            if (_isHost)
            {
                foreach (IPEndPoint endPoint in _endPoints.Values) SendPacket(endPoint, Packet.Leave, ReadOnlySpan<byte>.Empty);
            }
            else if (InSession)
            {
                SendPacket(_hostEndPoint!, Packet.Leave, ReadOnlySpan<byte>.Empty);
            }

            InSession = false;
            _socket.Dispose();
        }
    }
}
