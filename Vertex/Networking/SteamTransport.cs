using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using Steamworks;

namespace Vertex.Networking
{
    /// <summary>
    /// Networking over Steam. A room is a Steam lobby, and messages go peer to peer through Valve's relay network,
    /// so nobody forwards ports and players' IP addresses stay private. Needs the Steam client running and logged in.
    /// Players find rooms with <see cref="FindRooms"/>, or join through Steam invites and the overlay's "Join Game"
    /// (both handled automatically).
    /// <para>
    /// Joining the lobby isn't enough to be in the room: the lobby's owner (the host, or whoever Steam hands the lobby
    /// to if they leave) checks the newcomer's password first, then tells everyone to let them in. Until then nobody
    /// sends them anything or listens to them.
    /// </para>
    /// </summary>
    public sealed class SteamTransport : INetworkTransport
    {
        // Every game testing with the same app ID (like Spacewar's 480) shares one lobby list, so lobbies are tagged.
        private const string _gameKey = "vertex_game";
        private const string _nameKey = "vertex_name";
        private const string _lockedKey = "vertex_locked";
        private const string _versionKey = "vertex_version";
        private const string _pingKey = "vertex_ping";
        private const int _channel = 0;
        private const int _maxMessagesPerUpdate = 256;
        private const double _joinRetryInterval = 1.0;
        private const double _joinGiveUpAfter = 10.0;

        // Every message starts with one of these. Letting people in shares the game's channel, so a welcome always
        // arrives before the game's first messages to the newcomer.
        private enum Packet : byte
        {
            Data,     // a game message
            Join,     // newcomer -> owner: the game version, then the password, if any (see JoinRequest)
            Welcome,  // owner -> newcomer: you're in, with everyone else who is
            Refused,  // owner -> newcomer: why not (a JoinError)
            Admitted, // owner -> everyone in: let this newcomer in too
            Kicked,   // owner -> one of us: you're out, for this many seconds (65535: for good)
            Removed   // owner -> everyone else: drop this one, and keep them out for this many seconds
        }

        private readonly string _gameTag;
        private readonly HashSet<PeerId> _peers = new();
        private readonly IntPtr[] _messages = new IntPtr[_maxMessagesPerUpdate];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private CSteamID _lobby = CSteamID.Nil;

        // Whether the owner has let this machine in. The owner of a lobby is always in.
        private bool _admitted;

        // The room's password: the one set when hosting, or the one given when joining, which is the room's too once
        // it's let us in. Kept for checking newcomers should the lobby pass to us.
        private byte[] _password = [];

        // Who's been kicked, how many times, and until when they're kept out. Everyone keeps the list, so whoever owns
        // the lobby next keeps them out too.
        private readonly Dictionary<PeerId, (int Kicks, double Until)> _bans = new();

        private RoomSettings? _hosting;
        private bool _quickJoin;

        // The lobby being created or joined, so one that comes through after leaving (a cancelled join) is left again.
        private bool _creating;
        private ulong _expectedLobby;

        // Whether the lobby we own lists where we are on Steam's network, for others to estimate their ping to it.
        private bool _pingListed;
        private double _joinStarted;
        private double _lastJoinSent;

        // Steam calls these back from SteamAPI.RunCallbacks, during Update. They must be kept alive.
        private readonly Callback<LobbyEnter_t> _lobbyEntered;
        private readonly Callback<LobbyChatUpdate_t> _lobbyMembersChanged;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequested;
        private readonly CallResult<LobbyCreated_t> _lobbyCreated;
        private readonly CallResult<LobbyMatchList_t> _lobbyListReceived;

        public PeerId LocalPeer { get; }
        public bool InSession => _lobby != CSteamID.Nil && _admitted;
        public IReadOnlyCollection<PeerId> Peers => _peers;
        public PeerId HostPeer => InSession ? Owner : LocalPeer;
        public string RoomName => InSession ? SteamMatchmaking.GetLobbyData(_lobby, _nameKey) : "";
        // Only a private room's password survives being let in (see Handle), and the host knows it from the start,
        // before Steam may have the lobby's data.
        public bool RoomIsPrivate => InSession && _password.Length > 0;

        /// <summary>The current lobby's ID, or 0. Others can join it with <see cref="JoinLobby"/>.</summary>
        public ulong LobbyId => _lobby.m_SteamID;

        /// <summary>The lobby last joined or tried, so one that refused (say, for a missing password) can be tried again.</summary>
        public ulong JoinTarget { get; private set; }

        public event Action<PeerId>? PeerJoined;
        public event Action<PeerId>? PeerLeft;
        public event MessageHandler? MessageReceived;
        public event Action? SessionStarted;
        public event Action? SessionEnded;
        public event Action<JoinError>? JoinFailed;
        public event Action<TimeSpan?>? Kicked;

        /// <summary>Raised with this game's rooms after <see cref="FindRooms"/>.</summary>
        public event Action<IReadOnlyList<RoomInfo>>? RoomsFound;

        private PeerId Owner => new(SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID);
        private double Now => _clock.Elapsed.TotalSeconds;

        private SteamTransport(string gameTag)
        {
            _gameTag = gameTag;
            LocalPeer = new PeerId(SteamUser.GetSteamID().m_SteamID);

            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyMembersChanged = Callback<LobbyChatUpdate_t>.Create(OnLobbyMembersChanged);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(request => JoinLobby(request.m_steamIDLobby.m_SteamID));
            _sessionRequested = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequested);
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyListReceived = CallResult<LobbyMatchList_t>.Create(OnLobbyListReceived);

            // Start finding the nearest relays now, so the first connection doesn't have to wait for it.
            SteamNetworkingUtils.InitRelayNetworkAccess();
        }

        /// <summary>
        /// Connects to the running Steam client as <paramref name="appId"/> (480 is Valve's Spacewar test app).
        /// <paramref name="gameTag"/> marks this game's lobbies so <see cref="FindRooms"/> skips other games using the same app ID.
        /// Returns null, with the reason in <paramref name="error"/>, if Steam isn't running or refuses.
        /// </summary>
        public static SteamTransport? TryStart(uint appId, string gameTag, out string error)
        {
            // Steam looks for the app ID in steam_appid.txt next to the exe, or in this variable.
            Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());

            try
            {
                if (SteamAPI.InitEx(out string message) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    error = message;
                    return null;
                }
            }
            catch (DllNotFoundException exception)
            {
                error = $"The Steam API library is missing: {exception.Message}";
                return null;
            }

            error = "";
            return new SteamTransport(gameTag);
        }

        /// <summary>The Steam name of <see cref="LocalPeer"/> or anyone else.</summary>
        public string GetName(PeerId peer) => SteamFriends.GetFriendPersonaName(new CSteamID(peer.Value));

        /// <summary>
        /// Creates a lobby for <paramref name="room"/> and joins it. It's listed for anyone to find, and friends can
        /// join through Steam; with a password, only those who give it get in. With no room, it's named after you.
        /// </summary>
        public void HostLobby(RoomSettings? room = null)
        {
            LeaveLobby();
            _creating = true;
            _hosting = room ?? new RoomSettings($"{GetName(LocalPeer)}'s room");
            _password = Encoding.UTF8.GetBytes(_hosting.Password);
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, Math.Clamp(_hosting.MaxPlayers, 1, 250)));
            Console.WriteLine("[Vertex] Creating a Steam lobby...");
        }

        /// <summary>
        /// Joins a lobby, giving <paramref name="password"/> to its owner. <see cref="SessionStarted"/> is raised once
        /// the owner lets you in, or <see cref="JoinFailed"/> if they refuse or the lobby can't be joined.
        /// </summary>
        public void JoinLobby(ulong lobbyId, string password = "")
        {
            if (lobbyId == _lobby.m_SteamID) return;

            LeaveLobby();
            JoinTarget = _expectedLobby = lobbyId;
            _password = Encoding.UTF8.GetBytes(password);
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            Console.WriteLine("[Vertex] Joining a Steam lobby...");
        }

        /// <summary>Lists this game's rooms from anywhere in the world, full ones included: see <see cref="RoomsFound"/>.</summary>
        public void FindRooms()
        {
            _quickJoin = false;
            RequestLobbies();
            Console.WriteLine("[Vertex] Looking for Steam lobbies...");
        }

        /// <summary>Joins the first room of this game that has space and no password, from anywhere in the world.</summary>
        public void FindLobby()
        {
            _quickJoin = true;
            RequestLobbies();
            Console.WriteLine("[Vertex] Looking for a Steam lobby...");
        }

        private void RequestLobbies()
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(_gameKey, _gameTag, ELobbyComparison.k_ELobbyComparisonEqual);
            if (_quickJoin)
            {
                SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
                SteamMatchmaking.AddRequestLobbyListStringFilter(_versionKey, Network.GameVersion, ELobbyComparison.k_ELobbyComparisonEqual);
            }
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            _lobbyListReceived.Set(SteamMatchmaking.RequestLobbyList());
        }

        /// <summary>
        /// The owner only: tells <paramref name="peer"/> to leave and everyone else to drop them, keeping them out for
        /// <paramref name="firstBan"/>, or for good if they've been kicked before. Steam can't take anyone out of a lobby,
        /// so a changed copy of the game could stay in it, but nobody sends it anything or listens to it.
        /// </summary>
        public void Kick(PeerId peer, TimeSpan firstBan)
        {
            if (!InSession || Owner != LocalPeer || !_peers.Contains(peer)) return;

            bool forGood = _bans.ContainsKey(peer);
            ushort seconds = forGood ? ushort.MaxValue : (ushort)Math.Clamp(Math.Ceiling(firstBan.TotalSeconds), 0, ushort.MaxValue - 1);
            Span<byte> removed = stackalloc byte[10];
            BinaryPrimitives.WriteUInt64LittleEndian(removed, peer.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(removed[8..], seconds);
            foreach (PeerId other in _peers)
            {
                if (other != peer) SendPacket(other, Packet.Removed, removed);
            }

            // Their session is left open, so closing it doesn't drop this before it's sent; Steam closes it once idle.
            SendPacket(peer, Packet.Kicked, removed[8..]);
            Remove(peer, seconds, closeSession: false);
        }

        /// <summary>Drops a kicked player, and keeps them out for <paramref name="seconds"/> (65535: for good).</summary>
        private void Remove(PeerId peer, ushort seconds, bool closeSession = true)
        {
            int kicks = (_bans.TryGetValue(peer, out var before) ? before.Kicks : 0) + 1;
            _bans[peer] = (kicks, seconds == ushort.MaxValue ? double.PositiveInfinity : Now + seconds);
            if (!_peers.Remove(peer)) return;

            if (closeSession)
            {
                SteamNetworkingIdentity identity = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }

            PeerLeft?.Invoke(peer);
        }

        /// <summary>Leaves the room, or stops joining or creating one.</summary>
        public void LeaveLobby()
        {
            _creating = false;
            _expectedLobby = 0;
            if (_lobby == CSteamID.Nil) return;

            bool wasInSession = InSession;
            foreach (PeerId peer in _peers)
            {
                SteamNetworkingIdentity identity = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }

            SteamMatchmaking.LeaveLobby(_lobby);
            _lobby = CSteamID.Nil;
            _admitted = false;
            _pingListed = false;
            _bans.Clear();

            foreach (PeerId peer in _peers.ToArray())
            {
                _peers.Remove(peer);
                PeerLeft?.Invoke(peer);
            }

            if (wasInSession) SessionEnded?.Invoke();
        }

        public void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode)
        {
            if (_peers.Contains(peer)) SendPacket(peer, Packet.Data, data, mode == SendMode.Reliable);
        }

        public unsafe void Update()
        {
            SteamAPI.RunCallbacks();

            int count;
            do
            {
                count = SteamNetworkingMessages.ReceiveMessagesOnChannel(_channel, _messages, _messages.Length);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(_messages[i]);
                        var from = new PeerId(message.m_identityPeer.GetSteamID64());
                        var packet = new ReadOnlySpan<byte>((void*)message.m_pData, message.m_cbSize);
                        if (packet.Length > 0) Handle(from, (Packet)packet[0], packet[1..]);
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(_messages[i]);
                    }
                }
            }
            while (count == _messages.Length);

            if (InSession && !_pingListed && Owner == LocalPeer) ListPingLocation();

            // Waiting to be let in: ask again now and then (whoever owns the lobby now), and give up eventually.
            if (_lobby != CSteamID.Nil && !_admitted)
            {
                if (Now - _joinStarted > _joinGiveUpAfter)
                {
                    Console.WriteLine("[Vertex] The lobby's owner never answered.");
                    LeaveLobby();
                    JoinFailed?.Invoke(JoinError.NoAnswer);
                }
                else if (Now - _lastJoinSent >= _joinRetryInterval)
                {
                    SendJoin();
                }
            }
        }

        private void Handle(PeerId from, Packet type, ReadOnlySpan<byte> body)
        {
            switch (type)
            {
                case Packet.Data:
                    // Ignore anyone who isn't in the room.
                    if (_admitted && _peers.Contains(from)) MessageReceived?.Invoke(from, body);
                    break;

                case Packet.Join:
                    if (_admitted && Owner == LocalPeer && IsMember(from)) LetIn(from, body);
                    break;

                case Packet.Welcome:
                    if (_admitted || _lobby == CSteamID.Nil || from != Owner) return;

                    // Whatever was typed for a room with no password isn't its password, should the lobby pass to us.
                    _admitted = true;
                    if (SteamMatchmaking.GetLobbyData(_lobby, _lockedKey) != "1") _password = [];
                    Console.WriteLine($"[Vertex] In lobby {_lobby.m_SteamID} as {GetName(LocalPeer)}.");
                    SessionStarted?.Invoke();

                    AddPeer(from);
                    for (int offset = 0; offset + 8 <= body.Length; offset += 8)
                    {
                        AddPeer(new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body[offset..])));
                    }
                    break;

                case Packet.Refused:
                    if (_admitted || _lobby == CSteamID.Nil || from != Owner || body.Length < 1) return;

                    var error = (JoinError)body[0];
                    Console.WriteLine($"[Vertex] The lobby's owner refused: {error}.");
                    LeaveLobby();
                    JoinFailed?.Invoke(error);
                    break;

                case Packet.Admitted:
                    if (_admitted && from == Owner && body.Length >= 8)
                    {
                        var admitted = new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body));
                        if (BanOn(admitted) == null) AddPeer(admitted);
                    }
                    break;

                case Packet.Kicked:
                    if (!_admitted || from != Owner) return;

                    Console.WriteLine("[Vertex] The lobby's owner kicked us.");
                    int seconds = body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(body) : ushort.MaxValue;
                    Kicked?.Invoke(seconds == ushort.MaxValue ? null : TimeSpan.FromSeconds(seconds));
                    LeaveLobby();
                    break;

                case Packet.Removed:
                    if (_admitted && from == Owner && body.Length >= 10)
                    {
                        Remove(new PeerId(BinaryPrimitives.ReadUInt64LittleEndian(body)), BinaryPrimitives.ReadUInt16LittleEndian(body[8..]));
                    }
                    break;
            }
        }

        /// <summary>
        /// The owner checks a newcomer's game version and password, then welcomes them and tells everyone else, or
        /// refuses them.
        /// </summary>
        private void LetIn(PeerId newcomer, ReadOnlySpan<byte> request)
        {
            if (BanOn(newcomer) is { } ban)
            {
                SendPacket(newcomer, Packet.Refused, [(byte)(ban.Kicks > 1 ? JoinError.Banned : JoinError.Kicked)]);
                return;
            }

            if (!JoinRequest.Read(request, out string version, out ReadOnlySpan<byte> password) || version != Network.GameVersion)
            {
                SendPacket(newcomer, Packet.Refused, [(byte)JoinError.WrongVersion]);
                return;
            }

            // A room with no password lets anyone in, whatever they typed.
            if (_password.Length > 0 && !CryptographicOperations.FixedTimeEquals(password, _password))
            {
                SendPacket(newcomer, Packet.Refused, [(byte)JoinError.WrongPassword]);
                return;
            }

            // Asked again if the first welcome was slow or lost: just welcome them again.
            bool isNew = !_peers.Contains(newcomer);
            Span<byte> id = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(id, newcomer.Value);
            if (isNew)
            {
                foreach (PeerId peer in _peers) SendPacket(peer, Packet.Admitted, id);
            }

            var others = new byte[8 * _peers.Count];
            int offset = 0;
            foreach (PeerId peer in _peers)
            {
                if (peer == newcomer) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(others.AsSpan(offset), peer.Value);
                offset += 8;
            }

            SendPacket(newcomer, Packet.Welcome, others.AsSpan(0, offset));

            // Only after the welcome, so the game's first messages to them arrive after it.
            if (isNew) AddPeer(newcomer);
        }

        private void SendJoin()
        {
            _lastJoinSent = Now;
            SendPacket(Owner, Packet.Join, JoinRequest.Write(_password));
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                if (!_creating) return;

                _creating = false;
                Console.WriteLine($"[Vertex] Couldn't create a Steam lobby: {result.m_eResult}");
                JoinFailed?.Invoke(JoinError.NoAnswer);
                return;
            }

            // Created, but no longer wanted.
            var lobby = new CSteamID(result.m_ulSteamIDLobby);
            if (!_creating && lobby != _lobby)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                return;
            }

            RoomSettings room = _hosting ?? new RoomSettings($"{GetName(LocalPeer)}'s room");
            SteamMatchmaking.SetLobbyData(lobby, _gameKey, _gameTag);
            SteamMatchmaking.SetLobbyData(lobby, _nameKey, room.Name);
            SteamMatchmaking.SetLobbyData(lobby, _lockedKey, room.HasPassword ? "1" : "0");
            SteamMatchmaking.SetLobbyData(lobby, _versionKey, Network.GameVersion);
            Console.WriteLine("[Vertex] Lobby created. Invite friends from the Steam overlay (Shift+Tab).");
        }

        private void OnLobbyListReceived(LobbyMatchList_t result, bool ioFailure)
        {
            var rooms = new List<RoomInfo>();
            for (int i = 0; !ioFailure && i < result.m_nLobbiesMatching; i++)
            {
                CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
                string name = Clean(SteamMatchmaking.GetLobbyData(lobby, _nameKey));
                rooms.Add(new RoomInfo(name.Length > 0 ? name : "Steam room", SteamMatchmaking.GetNumLobbyMembers(lobby),
                    SteamMatchmaking.GetLobbyMemberLimit(lobby), SteamMatchmaking.GetLobbyData(lobby, _lockedKey) == "1")
                {
                    LobbyId = lobby.m_SteamID,
                    Version = SteamMatchmaking.GetLobbyData(lobby, _versionKey),
                    Ping = EstimatePing(SteamMatchmaking.GetLobbyData(lobby, _pingKey))
                });
            }

            if (!_quickJoin)
            {
                RoomsFound?.Invoke(rooms);
                return;
            }

            foreach (RoomInfo room in rooms)
            {
                if (room.HasPassword || room.IsFull || !room.IsSameVersion || room.LobbyId == _lobby.m_SteamID) continue;

                JoinLobby(room.LobbyId);
                return;
            }

            Console.WriteLine("[Vertex] No lobbies found. Host one instead.");
        }

        private void OnLobbyEntered(LobbyEnter_t entered)
        {
            var lobby = new CSteamID(entered.m_ulSteamIDLobby);
            bool created = _creating && SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == LocalPeer.Value;
            bool wanted = created || lobby.m_SteamID == _expectedLobby;

            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                if (!wanted) return;

                _expectedLobby = 0;
                var response = (EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse;
                Console.WriteLine($"[Vertex] Couldn't join the lobby: {response}");
                JoinFailed?.Invoke(response == EChatRoomEnterResponse.k_EChatRoomEnterResponseFull ? JoinError.Full : JoinError.NoAnswer);
                return;
            }

            // Joining was cancelled while Steam was still getting us in.
            if (!wanted)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                return;
            }

            _lobby = lobby;
            _creating = false;
            _expectedLobby = 0;
            JoinTarget = _lobby.m_SteamID;

            // Our own lobby: nobody to ask.
            if (created)
            {
                _admitted = true;
                Console.WriteLine($"[Vertex] In lobby {_lobby.m_SteamID} as {GetName(LocalPeer)}.");
                SessionStarted?.Invoke();
                return;
            }

            _joinStarted = Now;
            SendJoin();
        }

        private void OnLobbyMembersChanged(LobbyChatUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != _lobby.m_SteamID) return;

            // Someone entering waits to be let in (see LetIn); this only notices them going.
            var peer = new PeerId(update.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
            if (change.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeEntered)) return;

            if (_peers.Remove(peer))
            {
                // Left, disconnected, kicked or banned.
                SteamNetworkingIdentity identity = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                PeerLeft?.Invoke(peer);
            }
        }

        private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t request)
        {
            // Anyone in the lobby, including newcomers asking to be let in.
            SteamNetworkingIdentity identity = request.m_identityRemote;
            var peer = new PeerId(identity.GetSteamID64());
            if (_peers.Contains(peer) || IsMember(peer)) SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }

        /// <summary>The ban keeping <paramref name="peer"/> out right now, if there is one.</summary>
        private (int Kicks, double Until)? BanOn(PeerId peer) => _bans.TryGetValue(peer, out var ban) && Now < ban.Until ? ban : null;

        /// <summary>
        /// Lists where this machine is on Steam's relay network in the lobby we own, once Steam knows (it takes a few
        /// seconds after starting), so anyone looking at the lobby can estimate their ping to it.
        /// </summary>
        private void ListPingLocation()
        {
            if (SteamNetworkingUtils.GetLocalPingLocation(out SteamNetworkPingLocation_t location) < 0f) return;

            SteamNetworkingUtils.ConvertPingLocationToString(ref location, out string text, Constants.k_cchMaxSteamNetworkingPingLocationString);
            SteamMatchmaking.SetLobbyData(_lobby, _pingKey, text);
            _pingListed = true;
        }

        /// <summary>The estimated ping to a lobby from where its owner listed, or null if they didn't or Steam can't tell yet.</summary>
        private static int? EstimatePing(string location)
        {
            if (location.Length == 0 || !SteamNetworkingUtils.ParsePingLocationString(location, out SteamNetworkPingLocation_t parsed)) return null;

            int ping = SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref parsed);
            return ping >= 0 ? ping : null;
        }

        private bool IsMember(PeerId peer)
        {
            if (_lobby == CSteamID.Nil) return false;

            int members = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            for (int i = 0; i < members; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID == peer.Value) return true;
            }

            return false;
        }

        private void AddPeer(PeerId peer)
        {
            if (peer != LocalPeer && _peers.Add(peer)) PeerJoined?.Invoke(peer);
        }

        /// <summary>Sends a packet: its type, then the body.</summary>
        private unsafe void SendPacket(PeerId to, Packet type, ReadOnlySpan<byte> body, bool reliable = true)
        {
            int length = body.Length + 1;
            Span<byte> packet = length <= 1024 ? stackalloc byte[length] : new byte[length];
            packet[0] = (byte)type;
            body.CopyTo(packet[1..]);

            int flags = reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
            flags |= Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

            SteamNetworkingIdentity identity = Identity(to);
            fixed (byte* bytes = packet)
            {
                SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)bytes, (uint)length, flags, _channel);
            }
        }

        /// <summary>No control characters, and no longer than a room name can be: lobby data is whatever its owner wrote.</summary>
        private static string Clean(string name)
        {
            string cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return cleaned.Length > RoomSettings.MaxNameLength ? cleaned[..RoomSettings.MaxNameLength] : cleaned;
        }

        private static SteamNetworkingIdentity Identity(PeerId peer)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(peer.Value);
            return identity;
        }

        public void Dispose()
        {
            LeaveLobby();

            _lobbyEntered.Dispose();
            _lobbyMembersChanged.Dispose();
            _joinRequested.Dispose();
            _sessionRequested.Dispose();
            _lobbyCreated.Dispose();
            _lobbyListReceived.Dispose();

            SteamAPI.Shutdown();
        }
    }
}
