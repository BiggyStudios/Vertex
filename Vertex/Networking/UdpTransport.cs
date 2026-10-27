using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Vertex.Networking
{
    /// <summary>
    /// A simple UDP transport for testing on one PC or a home network: one game hosts a room, the others join its
    /// address (or find it with <see cref="FindRoomsAsync"/>), and the host passes messages between them. The host
    /// checks the room's password and player limit before letting anyone in. Reliable messages aren't resent if lost,
    /// which is rare on one PC or a LAN. Joining over the internet needs port forwarding; use
    /// <see cref="SteamTransport"/> for that instead.
    /// </summary>
    public sealed class UdpTransport : INetworkTransport
    {
        public const int DefaultPort = 27015;

        /// <summary>
        /// How many ports from <see cref="DefaultPort"/> up a room search asks on, so a few rooms can be hosted on one PC
        /// (each needs its own port).
        /// </summary>
        public const int SearchPorts = 4;

        private static readonly PeerId _hostId = new(1);
        private const double _pingInterval = 1.0;
        private const double _timeout = 5.0;
        private const double _joinRetryInterval = 1.0;
        private const double _joinGiveUpAfter = 10.0;

        private enum Packet : byte
        {
            Join,       // client -> host: the game version, then the password, if any (see JoinRequest)
            Welcome,    // host -> client: your id, how many others, their ids, whether it's private, then the room's name
            PeerJoined, // host -> clients
            PeerLeft,   // host -> clients
            Data,       // client -> host: a message for the host. host -> client: sender id, then a message.
            Relay,      // client -> host: recipient id, then a message to pass on
            Ping,
            Leave,
            Refused,    // host -> client: why they can't join (a JoinError)
            Query,      // anyone -> host: what's your room?
            Room,       // host -> asker: room id, players, player limit, has a password, the game version, then the room's name
            Kicked      // host -> client: you're out, and for how many seconds (65535: for good)
        }

        private readonly UdpClient _socket;
        private readonly bool _isHost;
        private readonly IPEndPoint? _hostEndPoint;

        // The host's room, and a random id for it so a search hearing back from it twice (over the network and
        // over this PC's loopback) lists it once. A client keeps the password it joins with.
        private readonly RoomSettings _room = new("");
        private readonly byte[] _password = [];
        private readonly ulong _roomId = (ulong)Random.Shared.NextInt64();

        // The host's kicked players: how many times each was kicked, and until when they're kept out. By address, as
        // rejoining gets a new port, except on this PC, where every copy of the game shares an address.
        private readonly Dictionary<string, (int Kicks, double Until)> _bans = new();
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
        public PeerId HostPeer => _hostId;
        public string RoomName { get; private set; } = "";
        public bool RoomIsPrivate { get; private set; }

        public event Action<PeerId>? PeerJoined;
        public event Action<PeerId>? PeerLeft;
        public event MessageHandler? MessageReceived;
        public event Action? SessionStarted;
        public event Action? SessionEnded;
        public event Action<JoinError>? JoinFailed;
        public event Action<TimeSpan?>? Kicked;

        private double Now => _clock.Elapsed.TotalSeconds;

        private UdpTransport(UdpClient socket, bool isHost, IPEndPoint? hostEndPoint, RoomSettings? room = null, string password = "")
        {
            _socket = socket;
            _isHost = isHost;
            _hostEndPoint = hostEndPoint;
            if (room != null) _room = room;
            _password = Encoding.UTF8.GetBytes(isHost ? _room.Password : password);

            IgnoreClosedPorts(_socket);
        }

        /// <summary>
        /// Hosts a room that others can join on <paramref name="port"/>. With no <paramref name="room"/>, it's called
        /// "Local game", has no password and takes 8 players.
        /// </summary>
        public static UdpTransport Host(int port = DefaultPort, RoomSettings? room = null)
        {
            room ??= new RoomSettings("Local game");
            var transport = new UdpTransport(new UdpClient(port), isHost: true, hostEndPoint: null, room)
            {
                LocalPeer = _hostId,
                InSession = true,
                RoomName = room.Name,
                RoomIsPrivate = room.HasPassword,
                _startedPending = true
            };

            Console.WriteLine($"[Vertex] Hosting \"{room.Name}\" on UDP port {port}.");
            return transport;
        }

        /// <summary>
        /// Joins the room hosted at <paramref name="address"/>. <see cref="SessionStarted"/> is raised once the host
        /// lets you in, or <see cref="JoinFailed"/> if it refuses or never answers.
        /// </summary>
        public static UdpTransport Join(string address, int port = DefaultPort, string password = "")
        {
            // The host listens on IPv4, so prefer that when a name like "localhost" also has an IPv6 address.
            IPAddress ip = IPAddress.TryParse(address, out IPAddress? parsed) ? parsed
                : Dns.GetHostAddresses(address).OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).First();
            var transport = new UdpTransport(new UdpClient(0, ip.AddressFamily), isHost: false, new IPEndPoint(ip, port), password: password);

            transport.SendPacket(transport._hostEndPoint!, Packet.Join, JoinRequest.Write(transport._password));
            transport._lastJoinSent = transport.Now;

            Console.WriteLine($"[Vertex] Joining {ip}:{port}...");
            return transport;
        }

        /// <summary>
        /// Asks around this network, and this PC, for rooms: every host on ports <see cref="DefaultPort"/> to
        /// <see cref="DefaultPort"/> + <see cref="SearchPorts"/> - 1 that answers within <paramref name="wait"/>, with how
        /// long each took to answer as its ping.
        /// </summary>
        public static async Task<IReadOnlyList<RoomInfo>> FindRoomsAsync(TimeSpan wait)
        {
            using var socket = new UdpClient(0) { EnableBroadcast = true };
            IgnoreClosedPorts(socket);

            // Every query goes out at once, so how long each answer takes is its room's ping.
            byte[] query = [(byte)Packet.Query];
            var clock = Stopwatch.StartNew();
            foreach (IPAddress address in SearchAddresses())
            {
                for (int port = DefaultPort; port < DefaultPort + SearchPorts; port++)
                {
                    try
                    {
                        socket.Send(query, query.Length, new IPEndPoint(address, port));
                    }
                    catch (SocketException)
                    {
                        // That network can't be reached; the others still can.
                    }
                }
            }

            var rooms = new Dictionary<ulong, RoomInfo>();
            using var timeout = new CancellationTokenSource(wait);
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult reply;
                try
                {
                    reply = await socket.ReceiveAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                if (ReadRoom(reply.Buffer, reply.RemoteEndPoint) is (ulong id, RoomInfo room)) rooms.TryAdd(id, room with { Ping = (int)clock.ElapsedMilliseconds });
            }

            return rooms.Values.ToList();
        }

        /// <summary>This PC, everywhere on the networks it's on, and the general broadcast address.</summary>
        private static List<IPAddress> SearchAddresses()
        {
            var addresses = new List<IPAddress> { IPAddress.Loopback, IPAddress.Broadcast };

            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation unicast in network.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                        // The network's own broadcast address: the address with every bit outside the mask set.
                        uint address = BinaryPrimitives.ReadUInt32BigEndian(unicast.Address.GetAddressBytes());
                        uint mask = BinaryPrimitives.ReadUInt32BigEndian(unicast.IPv4Mask.GetAddressBytes());
                        byte[] broadcast = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(broadcast, address | ~mask);

                        var directed = new IPAddress(broadcast);
                        if (!addresses.Contains(directed)) addresses.Add(directed);
                    }
                }
            }
            catch (NetworkInformationException)
            {
                // The general broadcast still reaches the main network.
            }

            return addresses;
        }

        private static (ulong Id, RoomInfo Room)? ReadRoom(byte[] packet, IPEndPoint from)
        {
            if (packet.Length < 13 || packet[0] != (byte)Packet.Room || packet.Length < 13 + packet[12]) return null;

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(1));
            string version = Encoding.UTF8.GetString(packet, 13, packet[12]);
            int nameStart = 13 + packet[12];
            string name = CleanName(Encoding.UTF8.GetString(packet, nameStart, packet.Length - nameStart));
            return (id, new RoomInfo(name, packet[9], packet[10], packet[11] != 0) { EndPoint = from, Version = version });
        }

        /// <summary>No control characters, no longer than a room name can be, and never blank.</summary>
        private static string CleanName(string name)
        {
            string cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (cleaned.Length > RoomSettings.MaxNameLength) cleaned = cleaned[..RoomSettings.MaxNameLength];
            return cleaned.Length > 0 ? cleaned : "Local game";
        }

        // On Windows, a message to a closed port makes the next receive throw. Turn that off.
        private static void IgnoreClosedPorts(UdpClient socket)
        {
            if (OperatingSystem.IsWindows()) socket.Client.IOControl(unchecked((int)0x9800000C), [0], null);
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
            if (type == Packet.Query)
            {
                SendRoom(from);
                return;
            }

            if (type == Packet.Join)
            {
                bool isNew = !_peersByEndPoint.TryGetValue(from, out PeerId joined);
                if (isNew)
                {
                    // Only someone not in yet is checked: a repeated join from someone already in is just a lost welcome.
                    bool sameVersion = JoinRequest.Read(body, out string version, out ReadOnlySpan<byte> password) && version == Network.GameVersion;
                    JoinError? refusal = BanOn(from) is { } ban ? (ban.Kicks > 1 ? JoinError.Banned : JoinError.Kicked)
                        : !sameVersion ? JoinError.WrongVersion
                        : _room.HasPassword && !CryptographicOperations.FixedTimeEquals(password, _password) ? JoinError.WrongPassword
                        : _peers.Count + 1 >= _room.MaxPlayers ? JoinError.Full
                        : null;

                    if (refusal is JoinError error)
                    {
                        SendPacket(from, Packet.Refused, [(byte)error]);
                        return;
                    }

                    joined = new PeerId(_nextPeerId++);
                    _endPoints[joined] = from;
                    _peersByEndPoint[from] = joined;

                    foreach (PeerId other in _peers) SendPacket(_endPoints[other], Packet.PeerJoined, ReadOnlySpan<byte>.Empty, joined.Value);
                    _peers.Add(joined);
                }

                // Sent again if the client asks again, in case the first welcome was lost.
                _lastHeard[joined] = Now;
                SendWelcome(joined);

                // Only after the welcome: the game may send them something straight away, and they'd drop it
                // if it arrived before they knew they were in.
                if (isNew) PeerJoined?.Invoke(joined);
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
                    // Only while asking: a late copy of an old welcome mustn't put us back in a room we've left.
                    if (InSession || _lastJoinSent < 0 || body.Length < 10) return;

                    LocalPeer = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                    int others = Math.Min(body[8], (body.Length - 10) / 8);
                    int idsEnd = 9 + others * 8;
                    RoomIsPrivate = body[idsEnd] != 0;
                    RoomName = CleanName(Encoding.UTF8.GetString(body[(idsEnd + 1)..]));
                    InSession = true;

                    // In: no more asking, even once the room ends.
                    _lastJoinSent = -1;
                    Console.WriteLine($"[Vertex] Joined \"{RoomName}\" as {GetName(LocalPeer)}.");
                    SessionStarted?.Invoke();

                    AddPeer(_hostId);
                    for (int offset = 9; offset < idsEnd; offset += 8)
                    {
                        AddPeer(new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body[offset..])));
                    }
                    break;

                case Packet.Refused:
                    if (InSession || _lastJoinSent < 0 || body.Length < 1) return;

                    _lastJoinSent = -1;
                    Console.WriteLine($"[Vertex] The host refused: {(JoinError)body[0]}.");
                    JoinFailed?.Invoke((JoinError)body[0]);
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

                case Packet.Kicked:
                    if (!InSession) return;

                    Console.WriteLine("[Vertex] The host kicked us.");
                    int seconds = body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body) : ushort.MaxValue;
                    Kicked?.Invoke(seconds == ushort.MaxValue ? null : TimeSpan.FromSeconds(seconds));
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
                    JoinFailed?.Invoke(JoinError.NoAnswer);
                }
                else if (Now - _lastJoinSent >= _joinRetryInterval)
                {
                    _lastJoinSent = Now;
                    SendPacket(_hostEndPoint, Packet.Join, JoinRequest.Write(_password));
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
            byte[] name = Encoding.UTF8.GetBytes(_room.Name);
            var body = new byte[10 + 8 * _peers.Count + name.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(body, client.Value);
            body[8] = (byte)(_peers.Count - 1);

            int offset = 9;
            foreach (PeerId other in _peers)
            {
                if (other == client) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(offset), other.Value);
                offset += 8;
            }

            body[offset++] = (byte)(_room.HasPassword ? 1 : 0);
            name.CopyTo(body, offset);
            SendPacket(_endPoints[client], Packet.Welcome, body.AsSpan(0, offset + name.Length));
        }

        /// <summary>Answers a room search.</summary>
        private void SendRoom(IPEndPoint to)
        {
            byte[] name = Encoding.UTF8.GetBytes(_room.Name);
            byte[] version = Encoding.UTF8.GetBytes(Network.GameVersion);
            int versionLength = Math.Min(version.Length, byte.MaxValue);
            var body = new byte[12 + versionLength + name.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(body, _roomId);
            body[8] = (byte)Math.Min(_peers.Count + 1, byte.MaxValue);
            body[9] = (byte)Math.Clamp(_room.MaxPlayers, 1, byte.MaxValue);
            body[10] = (byte)(_room.HasPassword ? 1 : 0);
            body[11] = (byte)versionLength;
            version.AsSpan(0, versionLength).CopyTo(body.AsSpan(12));
            name.CopyTo(body, 12 + versionLength);
            SendPacket(to, Packet.Room, body);
        }

        public void Kick(PeerId peer, TimeSpan firstBan)
        {
            if (!_isHost || !_endPoints.TryGetValue(peer, out IPEndPoint? endPoint)) return;

            // Out for a while the first time, for good the next.
            string key = BanKey(endPoint);
            int kicks = (_bans.TryGetValue(key, out var before) ? before.Kicks : 0) + 1;
            bool forGood = kicks > 1;
            _bans[key] = (kicks, forGood ? double.PositiveInfinity : Now + firstBan.TotalSeconds);

            Span<byte> seconds = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(seconds, forGood ? ushort.MaxValue : (ushort)Math.Clamp(Math.Ceiling(firstBan.TotalSeconds), 0, ushort.MaxValue - 1));
            SendPacket(endPoint, Packet.Kicked, seconds);
            RemoveClient(peer);
        }

        /// <summary>The ban keeping <paramref name="endPoint"/> out right now, if there is one.</summary>
        private (int Kicks, double Until)? BanOn(IPEndPoint endPoint) =>
            _bans.TryGetValue(BanKey(endPoint), out var ban) && Now < ban.Until ? ban : null;

        private static string BanKey(IPEndPoint endPoint) => IPAddress.IsLoopback(endPoint.Address) ? endPoint.ToString() : endPoint.Address.ToString();

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
