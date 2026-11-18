using System.Numerics;

namespace Vertex.Networking
{
    /// <summary>How a <see cref="NetworkManager"/> finds other players.</summary>
    public enum NetworkService
    {
        /// <summary>Through Steam, anywhere in the world (Steam must be running).</summary>
        Steam,

        /// <summary>On this network (or this PC, for trying it with two copies), with no Steam needed.</summary>
        LocalNetwork
    }

    /// <summary>What a <see cref="NetworkManager"/> does when the game starts.</summary>
    public enum ConnectOnStart
    {
        /// <summary>Nothing: a script connects (Quick Match, Host Game, Join Game nodes).</summary>
        Nothing,

        /// <summary>Joins a game that has room, or hosts one if there's none.</summary>
        QuickMatch,

        Host
    }

    /// <summary>
    /// Runs a network game: connects through Steam or the local network, and gives each player their own copy of
    /// <see cref="PlayerPrefab"/>, owned by them, at a <see cref="SpawnPoint"/>. Put one in the scene; visual scripts
    /// connect with Quick Match, Host Game or Join Game, and talk with Send Network Event.
    /// <para>
    /// Steam uses <see cref="SteamAppId"/>: 480 is Valve's Spacewar app, which anyone can use to try things out. Rooms are
    /// tagged with <see cref="GameTag"/>, so only this game's are found among everything else testing with it.
    /// </para>
    /// </summary>
    public sealed class NetworkManager : Component
    {
        [Header("Connection")]
        public NetworkService Service { get; set; } = NetworkService.Steam;

        /// <summary>The Steam app the game runs as. 480 (Spacewar) is free for anyone to test with; a released game needs its own.</summary>
        public int SteamAppId { get; set; } = 480;

        /// <summary>Marks this game's rooms, so it only finds its own among every other game using the same Steam app.</summary>
        public string GameTag { get; set; } = "vertex-game";

        /// <summary>What a room this machine hosts is called.</summary>
        public string RoomName { get; set; } = "Vertex Game";

        /// <summary>The most players in a room: 2 for a duel.</summary>
        public int MaxPlayers { get; set; } = 2;

        /// <summary>The port a local network game is hosted on.</summary>
        public int Port { get; set; } = UdpTransport.DefaultPort;

        public ConnectOnStart ConnectOnStart { get; set; } = ConnectOnStart.Nothing;

        /// <summary>Each player's own object (with a <see cref="NetworkTransform"/>, so the others see it move).</summary>
        [Header("Players")]
        public Prefab? PlayerPrefab { get; set; }

        /// <summary>Gives this machine its player while it isn't connected too, to play alone (it's swapped for a networked one on connecting).</summary>
        public bool SpawnWhenOffline { get; set; } = true;

        /// <summary>The network manager in the scene, if there is one.</summary>
        public static NetworkManager? Instance { get; private set; }

        /// <summary>This machine's player, once it's been made.</summary>
        public GameObject? LocalPlayer { get; private set; }

        /// <summary>How it's going, for showing: "Offline", "Looking for a game...", "In Vertex Game (2 players)".</summary>
        public string Status { get; private set; } = "Offline";

        private SteamTransport? _steam;
        private Task<IReadOnlyList<RoomInfo>>? _localSearch;

        protected internal override void Start()
        {
            Instance = this;

            // Started with -lan: the local network instead of Steam, to try a game with two copies on one PC.
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-lan", StringComparison.OrdinalIgnoreCase))) Service = NetworkService.LocalNetwork;
            NetworkScene.Listen();
            Network.SessionStarted += OnSessionStarted;
            Network.SessionEnded += OnSessionEnded;
            Network.JoinFailed += OnJoinFailed;

            // Started already connected (a scene loaded in a game), the player's made now; alone, only if it's wanted.
            if (Network.InSession || SpawnWhenOffline) SpawnLocalPlayer();
            if (ConnectOnStart == ConnectOnStart.QuickMatch) QuickMatch();
            else if (ConnectOnStart == ConnectOnStart.Host) Host();
        }

        protected internal override void OnDestroy()
        {
            Network.SessionStarted -= OnSessionStarted;
            Network.SessionEnded -= OnSessionEnded;
            Network.JoinFailed -= OnJoinFailed;
            if (Instance == this)
            {
                Instance = null;
                Leave();
            }
        }

        protected internal override void Update()
        {
            if (Network.InSession) Status = $"In {Network.RoomName} ({Network.Peers.Count + 1} player{(Network.Peers.Count == 0 ? "" : "s")})";

            // A search of the local network, finished: join the first room with space, or host one.
            if (_localSearch is not { IsCompleted: true } search) return;
            _localSearch = null;
            RoomInfo? room = search.IsCompletedSuccessfully ? search.Result.FirstOrDefault(Joinable) : null;
            if (room?.EndPoint is { } endPoint) Network.Start(UdpTransport.Join(endPoint.Address.ToString(), endPoint.Port));
            else Host();
        }

        private static bool Joinable(RoomInfo room) => !room.IsFull && !room.HasPassword && room.IsSameVersion;

        /// <summary>Hosts a room for others to join.</summary>
        public void Host()
        {
            var room = new RoomSettings(RoomName, "", Math.Max(MaxPlayers, 1));
            Status = "Hosting...";
            if (Service == NetworkService.LocalNetwork)
            {
                try
                {
                    Network.Start(UdpTransport.Host(Port, room));
                }
                catch (Exception exception)
                {
                    Status = $"Couldn't host: {exception.Message}";
                    Log.Warning(Status);
                }

                return;
            }

            if (StartSteam() is { } steam) steam.HostLobby(room);
        }

        /// <summary>Joins a game that has room, or hosts one if there isn't one.</summary>
        public void QuickMatch()
        {
            Status = "Looking for a game...";
            if (Service == NetworkService.LocalNetwork)
            {
                Network.Stop();
                _localSearch = UdpTransport.FindRoomsAsync(TimeSpan.FromSeconds(1.5));
                return;
            }

            if (StartSteam() is not { } steam) return;
            steam.RoomsFound -= OnSteamRooms;
            steam.RoomsFound += OnSteamRooms;
            steam.FindRooms();
        }

        /// <summary>Joins the game hosted at <paramref name="address"/> on the local network ("192.168.1.20", or "localhost" on this PC).</summary>
        public void Join(string address)
        {
            Status = $"Joining {address}...";
            try
            {
                Network.Start(UdpTransport.Join(address, Port));
            }
            catch (Exception exception)
            {
                Status = $"Couldn't join {address}: {exception.Message}";
                Log.Warning(Status);
            }
        }

        /// <summary>Leaves the game. Other players' objects go; this machine's player stays, to carry on alone.</summary>
        public void Leave()
        {
            _localSearch = null;
            _steam = null;
            Network.Stop();
            Status = "Offline";
        }

        private SteamTransport? StartSteam()
        {
            if (_steam != null && Network.Transport == _steam) return _steam;

            Network.Stop();
            _steam = SteamTransport.TryStart((uint)Math.Max(SteamAppId, 0), GameTag, out string error);
            if (_steam == null)
            {
                Status = $"Steam isn't available: {error}";
                Log.Warning($"{Status}. Is Steam running? (Or use the local network.)");
                return null;
            }

            Network.Start(_steam);
            return _steam;
        }

        private void OnSteamRooms(IReadOnlyList<RoomInfo> rooms)
        {
            if (_steam == null) return;
            _steam.RoomsFound -= OnSteamRooms;
            if (rooms.FirstOrDefault(Joinable) is { } room)
            {
                Status = $"Joining {room.Name}...";
                _steam.JoinLobby(room.LobbyId);
            }
            else
            {
                Host();
            }
        }

        private void OnJoinFailed(JoinError error)
        {
            Status = $"Couldn't join: {error}";
            Log.Warning(Status);
        }

        private void OnSessionStarted()
        {
            // The player made for playing alone is swapped for one everyone can see.
            if (LocalPlayer != null && !LocalPlayer.IsDestroyed)
            {
                // Never told anyone about, so there's nobody to tell it's gone.
                if (LocalPlayer.GetComponent<NetworkObject>() is { } networked) networked.Despawned = true;
                LocalPlayer.Destroy();
            }
            LocalPlayer = null;
            SpawnLocalPlayer();
        }

        private void OnSessionEnded() => Status = "Offline";

        /// <summary>
        /// This machine's place among the players, in the order they came, so each starts at a different spawn point: the
        /// host first, and someone joining after everyone already there (who may not be known yet as it starts: the host at
        /// least is there). Peer IDs don't say who came first.
        /// </summary>
        private static int LocalIndex() => !Network.InSession || Network.IsHost ? 0 : Math.Max(Network.Peers.Count, 1);

        private void SpawnLocalPlayer()
        {
            if (PlayerPrefab == null) return;

            SpawnPoint? point = SpawnPoint.At(LocalIndex());
            Vector3 position = point?.Transform.WorldPosition ?? Transform.WorldPosition;
            Quaternion rotation = point?.Transform.WorldRotation ?? Quaternion.Identity;
            LocalPlayer = NetworkScene.Spawn(PlayerPrefab, position, rotation, isPlayer: true);
        }
    }
}
