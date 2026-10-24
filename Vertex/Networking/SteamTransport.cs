using Steamworks;

namespace Vertex.Networking
{
    /// <summary>
    /// Networking over Steam. A session is a Steam lobby, and messages go peer to peer through Valve's relay network,
    /// so nobody forwards ports and players' IP addresses stay private. Needs the Steam client running and logged in.
    /// Players join with Steam invites or the overlay's "Join Game" (both handled automatically), or <see cref="FindLobby"/>.
    /// </summary>
    public sealed class SteamTransport : INetworkTransport
    {
        // Every game testing with the same app ID (like Spacewar's 480) shares one lobby list, so lobbies are tagged.
        private const string _gameKey = "vertex_game";
        private const int _channel = 0;
        private const int _maxMessagesPerUpdate = 256;

        private readonly string _gameTag;
        private readonly HashSet<PeerId> _peers = new();
        private readonly IntPtr[] _messages = new IntPtr[_maxMessagesPerUpdate];
        private CSteamID _lobby = CSteamID.Nil;

        // Steam calls these back from SteamAPI.RunCallbacks, during Update. They must be kept alive.
        private readonly Callback<LobbyEnter_t> _lobbyEntered;
        private readonly Callback<LobbyChatUpdate_t> _lobbyMembersChanged;
        private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
        private readonly Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequested;
        private readonly CallResult<LobbyCreated_t> _lobbyCreated;
        private readonly CallResult<LobbyMatchList_t> _lobbyListReceived;

        public PeerId LocalPeer { get; }
        public bool InSession => _lobby != CSteamID.Nil;
        public IReadOnlyCollection<PeerId> Peers => _peers;

        /// <summary>The current lobby's ID, or 0. Others can join it with <see cref="JoinLobby"/>.</summary>
        public ulong LobbyId => _lobby.m_SteamID;

        public event Action<PeerId>? PeerJoined;
        public event Action<PeerId>? PeerLeft;
        public event MessageHandler? MessageReceived;
        public event Action? SessionStarted;
        public event Action? SessionEnded;

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
        /// <paramref name="gameTag"/> marks this game's lobbies so <see cref="FindLobby"/> skips other games using the same app ID.
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

        /// <summary>Creates a lobby and joins it. Friends can then join through Steam.</summary>
        public void HostLobby(int maxPlayers = 8)
        {
            LeaveLobby();
            _lobbyCreated.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxPlayers));
            Console.WriteLine("[Vertex] Creating a Steam lobby...");
        }

        public void JoinLobby(ulong lobbyId)
        {
            if (lobbyId == _lobby.m_SteamID) return;

            LeaveLobby();
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
            Console.WriteLine("[Vertex] Joining a Steam lobby...");
        }

        /// <summary>Joins the first lobby of this game that has room, from anywhere in the world.</summary>
        public void FindLobby()
        {
            SteamMatchmaking.AddRequestLobbyListStringFilter(_gameKey, _gameTag, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            _lobbyListReceived.Set(SteamMatchmaking.RequestLobbyList());
            Console.WriteLine("[Vertex] Looking for a Steam lobby...");
        }

        public void LeaveLobby()
        {
            if (!InSession) return;

            foreach (PeerId peer in _peers)
            {
                SteamNetworkingIdentity identity = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }

            SteamMatchmaking.LeaveLobby(_lobby);
            _lobby = CSteamID.Nil;

            foreach (PeerId peer in _peers.ToArray())
            {
                _peers.Remove(peer);
                PeerLeft?.Invoke(peer);
            }

            SessionEnded?.Invoke();
        }

        public unsafe void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode)
        {
            if (!_peers.Contains(peer)) return;

            int flags = mode == SendMode.Reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
            flags |= Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;

            SteamNetworkingIdentity identity = Identity(peer);
            fixed (byte* bytes = data)
            {
                SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)bytes, (uint)data.Length, flags, _channel);
            }
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

                        // Ignore anyone who isn't in the lobby.
                        if (_peers.Contains(from)) MessageReceived?.Invoke(from, new ReadOnlySpan<byte>((void*)message.m_pData, message.m_cbSize));
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(_messages[i]);
                    }
                }
            }
            while (count == _messages.Length);
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Console.WriteLine($"[Vertex] Couldn't create a Steam lobby: {result.m_eResult}");
                return;
            }

            SteamMatchmaking.SetLobbyData(new CSteamID(result.m_ulSteamIDLobby), _gameKey, _gameTag);
            Console.WriteLine("[Vertex] Lobby created. Invite friends from the Steam overlay (Shift+Tab).");
        }

        private void OnLobbyListReceived(LobbyMatchList_t result, bool ioFailure)
        {
            for (int i = 0; i < result.m_nLobbiesMatching; i++)
            {
                CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobby == _lobby) continue;

                JoinLobby(lobby.m_SteamID);
                return;
            }

            Console.WriteLine("[Vertex] No lobbies found. Host one instead.");
        }

        private void OnLobbyEntered(LobbyEnter_t entered)
        {
            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Console.WriteLine($"[Vertex] Couldn't join the lobby: {(EChatRoomEnterResponse)entered.m_EChatRoomEnterResponse}");
                return;
            }

            _lobby = new CSteamID(entered.m_ulSteamIDLobby);
            Console.WriteLine($"[Vertex] In lobby {_lobby.m_SteamID} as {GetName(LocalPeer)}.");
            SessionStarted?.Invoke();

            int members = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            for (int i = 0; i < members; i++)
            {
                AddPeer(new PeerId(SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID));
            }
        }

        private void OnLobbyMembersChanged(LobbyChatUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != _lobby.m_SteamID) return;

            var peer = new PeerId(update.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;

            if (change.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeEntered))
            {
                AddPeer(peer);
            }
            else if (_peers.Remove(peer))
            {
                // Left, disconnected, kicked or banned.
                SteamNetworkingIdentity identity = Identity(peer);
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
                PeerLeft?.Invoke(peer);
            }
        }

        private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t request)
        {
            SteamNetworkingIdentity identity = request.m_identityRemote;
            if (_peers.Contains(new PeerId(identity.GetSteamID64()))) SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
        }

        private void AddPeer(PeerId peer)
        {
            if (peer != LocalPeer && _peers.Add(peer)) PeerJoined?.Invoke(peer);
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
