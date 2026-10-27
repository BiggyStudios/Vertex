using System.Numerics;

using Vertex;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Multiplayer in rooms, over Steam or the local network (see <see cref="MainMenu"/> for finding and hosting them).
    /// Every player is in charge of their own player: they send where they are many times a second, and when they
    /// shoot someone they tell that player, who takes the damage and announces their own death. Each player also
    /// counts their own kills and deaths.
    /// <para>Friends can also join through Steam invites or "Join Game". Enter to chat, Tab for the scoreboard.</para>
    /// </summary>
    public class MultiplayerManager : Component
    {
        // Valve's Spacewar test app: anyone can use it to try out Steamworks. A released game needs its own app ID.
        private const uint _steamAppId = 480;
        private const string _steamGameTag = "vertex-sample";

        // Every other physics step: 30 updates a second.
        private const int _stateInterval = 2;

        // How often the host pings everyone, in seconds, and tells them all how it went, for the scoreboard.
        private const float _pingInterval = 1f;

        // How someone was hit or died, in a Hit or Died message's flags byte.
        private const byte _flagHeadshot = 1;
        private const byte _flagFell = 2;
        private const byte _flagNoScope = 4;
        private const byte _flagBackstab = 8;

        // A knife stab counts as in the back when the attacker is more than this far round behind the victim:
        // 0.5 is 60 degrees either side of straight behind.
        private const float _backstabAngle = 0.5f;

        // Sniper kills from at least this far away, in metres, show the distance in the kill feed.
        private const float _longShotDistance = 30f;

        // The most pellets a Shot message carries, which keeps it small.
        private const int _maxPellets = 16;

        // The most bots at once, which keeps the Bots message small.
        private const int _maxBots = 32;

        private static readonly Color[] _colors =
        [
            new(0.95f, 0.35f, 0.35f), new(0.35f, 0.55f, 1f), new(0.35f, 0.85f, 0.4f), new(0.95f, 0.8f, 0.25f),
            new(0.75f, 0.4f, 0.95f), new(0.25f, 0.85f, 0.85f), new(1f, 0.6f, 0.25f), new(0.9f, 0.9f, 0.9f),
            new(1f, 0.5f, 0.78f), new(0.65f, 0.95f, 0.2f), new(0.72f, 0.55f, 0.36f), new(0.4f, 0.4f, 0.45f)
        ];

        public Player LocalPlayer { get; set; } = null!;

        /// <summary>Connect to Steam when the game starts, so friends' invites work. Off, Steam only starts when it's needed.</summary>
        public bool ConnectToSteamOnStart { get; set; } = true;

        public ChatBox Chat { get; private set; } = null!;
        public KillFeed KillFeed { get; private set; } = null!;

        /// <summary>Votes to kick someone out of the room.</summary>
        public VoteKick Votes { get; private set; } = null!;

        /// <summary>The local player's score.</summary>
        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        /// <summary>A short description of the connection, for the scoreboard.</summary>
        public string SessionStatus => Network.Transport switch
        {
            SteamTransport { InSession: true } => $"{Network.RoomName} · Steam · {Network.Peers.Count + 1} players",
            UdpTransport { InSession: true } => $"{Network.RoomName} · Local network · {Network.Peers.Count + 1} players",
            _ => "Playing offline"
        };

        /// <summary>
        /// Whether the server settings (sv_cheats, sv_jumpheight and the rest) can be changed: playing alone, or in a
        /// private room. A public room always plays on the normal ones.
        /// </summary>
        public static bool ServerSettingsUnlocked => !Network.InSession || Network.RoomIsPrivate;

        /// <summary>Connected to Steam, so its rooms can be found and hosted.</summary>
        public bool SteamConnected => _steam != null && Network.Transport == _steam;

        /// <summary>Why Steam couldn't be reached, the last time it was tried; null if it could, or hasn't been tried.</summary>
        public string? SteamError { get; private set; }

        /// <summary>Your Steam name, when connected to Steam.</summary>
        public string? SteamName => SteamConnected ? _steam!.GetName(_steam.LocalPeer) : null;

        /// <summary>The lobby Steam last tried to join, so a room that wanted a password can be tried again with one.</summary>
        public ulong SteamJoinTarget => _steam?.JoinTarget ?? 0;

        /// <summary>Raised with Steam's rooms, some time after <see cref="FindSteamRooms"/>.</summary>
        public event Action<IReadOnlyList<RoomInfo>>? SteamRoomsFound;

        // Each server variable's normal value, from before anyone changed it, for public rooms.
        private float[] _defaults = [];

        private readonly Dictionary<PeerId, RemotePlayer> _remotePlayers = new();

        // Which colour in the palette each player has, as the host handed them out.
        private readonly Dictionary<PeerId, int> _colorIndex = new();
        private SteamTransport? _steam;
        private int _tick;
        private float _sincePing;

        // Everyone's ping to the host, as the host last said.
        private readonly Dictionary<PeerId, int> _hostPings = new();

        // Practice targets, from bot_add. The host's game decides how they're doing; everyone else's are copies.
        private readonly List<Bot> _bots = new();

        private string LocalName => Network.InSession ? Network.GetName(Network.LocalPeer) : "You";
        private Color LocalColor => Network.InSession ? ColorOf(Network.LocalPeer) : _colors[6];

        protected override void Start()
        {
            _defaults = ServerVariables.All.Select(variable => variable.Get(LocalPlayer)).ToArray();
            Chat = GameObject.AddComponent<ChatBox>();
            KillFeed = GameObject.AddComponent<KillFeed>();
            Votes = GameObject.AddComponent<VoteKick>();
            Votes.Multiplayer = this;
            GameObject.AddComponent<Scoreboard>().Source = this;
            Chat.MessageSent += OnChatSent;

            Network.PeerJoined += OnPeerJoined;
            Network.PeerLeft += OnPeerLeft;
            Network.SessionStarted += OnSessionStarted;
            Network.SessionEnded += OnSessionEnded;
            Network.JoinFailed += OnJoinFailed;
            Network.Kicked += OnKicked;
            Network.MessageReceived += OnMessage;

            LocalPlayer.Died += OnLocalPlayerDied;
            LocalPlayer.Gun.Fired += OnShotFired;
            LocalPlayer.Gun.Struck += OnStruck;
            LocalPlayer.Gun.RocketFired += OnRocketFired;
            Rocket.Exploded += OnRocketExploded;
            Rocket.Detonated += OnRocketDetonated;

            Announce("Escape for the menu, Enter to chat, Tab for scores.");

            // Started from a Steam invite while the game wasn't running: Steam passes the lobby on the command line.
            string[] args = Environment.GetCommandLineArgs();
            int connect = Array.IndexOf(args, "+connect_lobby");
            if (connect >= 0 && connect + 1 < args.Length && ulong.TryParse(args[connect + 1], out ulong lobby))
            {
                StartSteam()?.JoinLobby(lobby);
            }
            else if (ConnectToSteamOnStart)
            {
                // Connect to Steam straight away anyway, so invites from friends work.
                StartSteam();
            }
        }

        protected override void OnDestroy()
        {
            Network.PeerJoined -= OnPeerJoined;
            Network.PeerLeft -= OnPeerLeft;
            Network.SessionStarted -= OnSessionStarted;
            Network.SessionEnded -= OnSessionEnded;
            Network.JoinFailed -= OnJoinFailed;
            Network.Kicked -= OnKicked;
            Network.MessageReceived -= OnMessage;
            Rocket.Exploded -= OnRocketExploded;
            Rocket.Detonated -= OnRocketDetonated;
        }

        /// <summary>Hosts a Steam room: listed for everyone, and joinable from the overlay. With no room, it's named after you.</summary>
        public void HostSteam(RoomSettings? room = null) => StartSteam()?.HostLobby(room);

        /// <summary>Joins a Steam room. A room with a password refuses you without the right one.</summary>
        public void JoinSteam(ulong lobby, string password = "") => StartSteam()?.JoinLobby(lobby, password);

        /// <summary>Joins any Steam room with space and no password.</summary>
        public void FindSteam() => StartSteam()?.FindLobby();

        /// <summary>Starts listing Steam's rooms, raising <see cref="SteamRoomsFound"/> when they arrive. False if Steam isn't available.</summary>
        public bool FindSteamRooms()
        {
            SteamTransport? steam = StartSteam();
            steam?.FindRooms();
            return steam != null;
        }

        /// <summary>Hosts a room on this network on <paramref name="port"/>. False, saying why in chat, if that port is taken.</summary>
        public bool HostLocal(int port, RoomSettings? room = null)
        {
            try
            {
                Network.Start(UdpTransport.Host(port, room));
                _steam = null;
                Announce($"Hosting on port {port}. Others on this network find it under Local network, or join with: connect <this PC's address> {port}");
                return true;
            }
            catch (System.Net.Sockets.SocketException exception)
            {
                Announce($"Couldn't host on port {port}: {exception.Message}");
                return false;
            }
        }

        /// <summary>
        /// Hosts a room on this network, on the first free port of those a room search asks on, so a few copies of the
        /// game on one PC can each host. False if they're all taken.
        /// </summary>
        public bool HostLocal(RoomSettings room)
        {
            for (int port = UdpTransport.DefaultPort; port < UdpTransport.DefaultPort + UdpTransport.SearchPorts; port++)
            {
                if (HostLocal(port, room)) return true;
            }

            return false;
        }

        /// <summary>Joins a room on this network. False, saying why in chat, if the address isn't one.</summary>
        public bool JoinLocal(string address, int port, string password = "")
        {
            try
            {
                Network.Start(UdpTransport.Join(address, port, password));
                _steam = null;
                Announce($"Joining {address}:{port}...");
                return true;
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
            {
                Announce($"Couldn't join {address}: {exception.Message}");
                return false;
            }
        }

        /// <summary>Leaves the room, or stops joining one.</summary>
        public void Leave()
        {
            if (_steam != null && Network.Transport == _steam) _steam.LeaveLobby();
            else Network.Stop();
        }

        /// <summary>
        /// Puts a bot where the local player stands, facing the way they face (see <see cref="Bot"/>); in someone else's
        /// room, asks the host to, as the host's game looks after them. False if there are as many as there can be.
        /// </summary>
        public bool AddBot()
        {
            if (_bots.Count >= _maxBots) return false;

            if (Network.IsHost)
            {
                PutBot(LocalPlayer.Transform.WorldPosition, LocalPlayer.Yaw);
            }
            else
            {
                var add = new MessageWriter(stackalloc byte[32], MessageType.BotAdd);
                add.Write(LocalPlayer.Transform.WorldPosition);
                add.Write(LocalPlayer.Yaw);
                Network.Send(Network.HostPeer, add.Data, SendMode.Reliable);
            }

            return true;
        }

        /// <summary>Takes every bot away, for everyone in the room. How many there were.</summary>
        public int KickBots()
        {
            int count = _bots.Count;
            if (Network.IsHost)
            {
                RemoveBots();
                SendBots(to: null);
            }
            else
            {
                Network.Send(Network.HostPeer, new MessageWriter(stackalloc byte[1], MessageType.BotKick).Data, SendMode.Reliable);
            }

            return count;
        }

        /// <summary>The host puts down a bot, numbered after the others, and tells everyone.</summary>
        private void PutBot(Vector3 position, float yaw)
        {
            if (_bots.Count >= _maxBots) return;

            int number = 1;
            while (_bots.Any(bot => bot.Number == number)) number++;

            _bots.Add(NewBot(number, position, yaw, Player.MaxHealth));
            SendBots(to: null);
        }

        private Bot NewBot(int number, Vector3 position, float yaw, int health)
        {
            var bot = new Bot(number, position, yaw, health, LocalPlayer);
            bot.GotUp += _ => SendBots(to: null);
            return bot;
        }

        /// <summary>Takes every bot away in this game only.</summary>
        private void RemoveBots()
        {
            foreach (Bot bot in _bots) bot.Destroy();
            _bots.Clear();
        }

        /// <summary>The host sends every bot: to one player who just joined, or to everyone after a change.</summary>
        private void SendBots(PeerId? to)
        {
            if (!Network.InSession) return;

            var message = new MessageWriter(stackalloc byte[2 + _maxBots * 18], MessageType.Bots);
            message.Write((byte)_bots.Count);
            foreach (Bot bot in _bots)
            {
                message.Write((byte)bot.Number);
                message.Write(bot.Spot);
                message.Write(bot.Facing);
                message.Write((byte)bot.HitPoints);
            }

            if (to is PeerId peer) Network.Send(peer, message.Data, SendMode.Reliable);
            else Network.Broadcast(message.Data, SendMode.Reliable);
        }

        /// <summary>Every bot, from the host: new ones are put down, missing ones taken away, and the rest get their health.</summary>
        private void ReadBots(ref MessageReader message)
        {
            var listed = new HashSet<int>();
            int count = Math.Min((int)message.ReadByte(), _maxBots);
            for (int i = 0; i < count; i++)
            {
                int number = message.ReadByte();
                Vector3 position = message.ReadVector3();
                float yaw = message.ReadFloat();
                int health = Math.Min((int)message.ReadByte(), Player.MaxHealth);
                if (!float.IsFinite(position.X + position.Y + position.Z + yaw) || !listed.Add(number)) continue;

                if (_bots.Find(bot => bot.Number == number) is { } known) known.SetHealth(health);
                else _bots.Add(NewBot(number, position, yaw, health));
            }

            foreach (Bot gone in _bots.Where(bot => !listed.Contains(bot.Number)).ToList())
            {
                gone.Destroy();
                _bots.Remove(gone);
            }
        }

        /// <summary>Sends a chat message from the local player (the chat box and the console's say command).</summary>
        public void SendChat(string text) => OnChatSent(text);

        public void PrintStatus()
        {
            if (!Network.InSession)
            {
                DevConsole.Print("Not in a game. Playing alone, you're the host.");
                return;
            }

            DevConsole.Print(SessionStatus);
            DevConsole.Print($"  {LocalName} (you){(Network.IsHost ? ", host" : "")}  {Kills} kills, {Deaths} deaths");
            foreach (RemotePlayer remote in _remotePlayers.Values)
            {
                DevConsole.Print($"  {remote.Name}{(remote.Peer == Network.HostPeer ? ", host" : "")}  {remote.Kills} kills, {remote.Deaths} deaths");
            }
        }

        /// <summary>A server variable changed from the console. Only the host can, and the change goes to everyone.</summary>
        internal void SetServerVariable(ServerVariables.Variable variable, float value)
        {
            if (!Network.IsHost)
            {
                DevConsole.Error($"Only the host can change {variable.Name}.");
                return;
            }

            if (!ServerSettingsUnlocked)
            {
                DevConsole.Error($"Server settings like {variable.Name} can only be changed in a private room.");
                return;
            }

            variable.Set(LocalPlayer, value);
            string shown = variable.Get(LocalPlayer).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

            if (Network.InSession)
            {
                SendServerVariables(to: null);
                Announce($"Server setting {variable.Name} changed to {shown}.");
            }
            else
            {
                DevConsole.Print($"{variable.Name} = {shown}");
            }
        }

        /// <summary>The host sends every server variable: to one player who just joined, or to everyone after a change.</summary>
        private void SendServerVariables(PeerId? to)
        {
            var message = new MessageWriter(stackalloc byte[1024], MessageType.ServerVariables);
            message.Write((byte)ServerVariables.All.Length);
            foreach (ServerVariables.Variable variable in ServerVariables.All)
            {
                message.Write(variable.Name);
                message.Write(variable.Get(LocalPlayer));
            }

            if (to is PeerId peer) Network.Send(peer, message.Data, SendMode.Reliable);
            else Network.Broadcast(message.Data, SendMode.Reliable);
        }

        private void ApplyServerVariables(ref MessageReader message)
        {
            int count = message.ReadByte();
            for (int i = 0; i < count; i++)
            {
                string name = message.ReadString();
                float value = message.ReadFloat();
                if (ServerVariables.Find(name) is not { } variable) continue;

                // A public room plays on the normal settings, whatever its host says, even with a changed copy of the game.
                if (!ServerSettingsUnlocked) value = _defaults[Array.IndexOf(ServerVariables.All, variable)];

                float before = variable.Get(LocalPlayer);
                variable.Set(LocalPlayer, value);
                float after = variable.Get(LocalPlayer);
                if (MathF.Abs(after - before) > 0.0001f) Announce($"Server setting {variable.Name} is {after.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}.");
            }
        }

        protected override void Update()
        {
            // Bots are a cheat: they go when sv_cheats does, as in a public room. That's the same for everyone, so
            // everyone takes their own away.
            if (_bots.Count > 0 && !LocalPlayer.CheatsAllowed) RemoveBots();

            // The host plays the part of the server for pings: it measures everyone's, and tells everyone.
            if (!Network.InSession || !Network.IsHost) return;

            _sincePing += Time.DeltaTime;
            if (_sincePing < _pingInterval) return;

            // Our clock goes out; whoever gets it sends it straight back, and the difference is the round trip.
            _sincePing = 0f;
            var ping = new MessageWriter(stackalloc byte[8], MessageType.Ping);
            ping.Write(Time.TotalTime);
            Network.Broadcast(ping.Data, SendMode.Unreliable);

            var pings = new MessageWriter(stackalloc byte[2 + 255 * 10], MessageType.Pings);
            List<RemotePlayer> measured = _remotePlayers.Values.Where(remote => remote.Ping != null).Take(byte.MaxValue).ToList();
            pings.Write((byte)measured.Count);
            foreach (RemotePlayer remote in measured)
            {
                pings.Write(remote.Peer.Value);
                pings.Write((ushort)Math.Clamp(MathF.Round(remote.Ping!.Value), 0f, ushort.MaxValue));
            }

            Network.Broadcast(pings.Data, SendMode.Unreliable);
        }

        protected override void FixedUpdate()
        {
            if (!Network.InSession || ++_tick % _stateInterval != 0) return;

            var state = new PlayerState(
                Time.TotalTime,
                LocalPlayer.Transform.WorldPosition,
                LocalPlayer.Controller.Velocity,
                LocalPlayer.Yaw,
                LocalPlayer.Pitch,
                (byte)LocalPlayer.Health,
                !LocalPlayer.IsDead,
                (ushort)Math.Min(Kills, ushort.MaxValue),
                (ushort)Math.Min(Deaths, ushort.MaxValue),
                LocalPlayer.Gun.Weapon.Id,
                LocalPlayer.Grapple.Anchor?.Position);

            var message = new MessageWriter(stackalloc byte[64], MessageType.State);
            message.Write(state);
            Network.Broadcast(message.Data, SendMode.Unreliable);
        }

        /// <summary>
        /// A player's ping, for the scoreboard: to the host, which plays the part of the server, as the host measured it.
        /// The host's own is 0. Null playing alone, or before it's measured.
        /// </summary>
        private int? PingOf(PeerId peer)
        {
            if (!Network.InSession) return null;
            if (peer == Network.HostPeer) return 0;

            if (!Network.IsHost) return _hostPings.TryGetValue(peer, out int ms) ? ms : null;
            return _remotePlayers.TryGetValue(peer, out RemotePlayer? remote) && remote.Ping is float ping ? (int)MathF.Round(ping) : null;
        }

        /// <summary>Everyone in the game, for the scoreboard.</summary>
        public IEnumerable<ScoreEntry> GetScores()
        {
            yield return new ScoreEntry(LocalName, LocalColor, Kills, Deaths, IsLocal: true, Alive: !LocalPlayer.IsDead, PingOf(Network.LocalPeer));

            foreach (RemotePlayer remote in _remotePlayers.Values)
            {
                yield return new ScoreEntry(remote.Name, remote.Color, remote.Kills, remote.Deaths, IsLocal: false, remote.Alive,
                    PingOf(remote.Peer));
            }
        }

        /// <summary>Connects to Steam if not already, making it the active transport. Null if Steam isn't available.</summary>
        private SteamTransport? StartSteam()
        {
            if (_steam != null && Network.Transport == _steam) return _steam;

            _steam = SteamTransport.TryStart(_steamAppId, _steamGameTag, out string error);
            if (_steam == null)
            {
                SteamError = error;
                Announce($"Steam isn't available ({error}). Rooms on the local network still work.");
                return null;
            }

            SteamError = null;
            _steam.RoomsFound += rooms => SteamRoomsFound?.Invoke(rooms);
            Network.Start(_steam);
            Announce($"Connected to Steam as {_steam.GetName(_steam.LocalPeer)}.");
            return _steam;
        }

        private void OnPeerJoined(PeerId peer)
        {
            _remotePlayers[peer] = new RemotePlayer(peer, Network.GetName(peer), ColorOf(peer));
            Announce($"{Network.GetName(peer)} joined.");

            // So they play by the same rules as everyone else, in a colour of their own, with the same bots.
            if (!Network.IsHost) return;

            SendServerVariables(peer);
            AssignColors();
            if (_bots.Count > 0) SendBots(peer);
        }

        private void OnPeerLeft(PeerId peer)
        {
            if (!_remotePlayers.Remove(peer, out RemotePlayer? remote)) return;

            // Their colour is free for whoever comes next.
            _colorIndex.Remove(peer);
            remote.Destroy();
            Announce($"{remote.Name} left.");
        }

        /// <summary>
        /// Into a room: every room starts on the normal server settings, whatever was changed playing alone. A private
        /// room's host can change them from there, and the host's are sent to anyone joining.
        /// </summary>
        private void OnSessionStarted()
        {
            // Bots put down playing alone stay behind; a room's come from its host. You start the room at one of the
            // sandbox's spawns, like anyone respawning, not wherever you were before (a console connect or a Steam
            // invite can join from anywhere).
            RemoveBots();
            LocalPlayer.StartOver(Zone.Sandbox.RandomSpawn());
            AssignColors();

            bool changed = false;
            for (int i = 0; i < ServerVariables.All.Length; i++)
            {
                ServerVariables.Variable variable = ServerVariables.All[i];
                if (MathF.Abs(variable.Get(LocalPlayer) - _defaults[i]) < 0.0001f) continue;

                variable.Set(LocalPlayer, _defaults[i]);
                changed = true;
            }

            if (changed) Announce("Server settings are back to normal for the room.");
        }

        private void OnSessionEnded()
        {
            foreach (RemotePlayer remote in _remotePlayers.Values) remote.Destroy();
            _remotePlayers.Clear();
            RemoveBots();
            _colorIndex.Clear();
            _hostPings.Clear();
            Announce("Left the room.");
        }

        private void OnJoinFailed(JoinError error) => Announce(JoinErrorText(error));

        /// <summary>What went wrong joining a room, for the player.</summary>
        public static string JoinErrorText(JoinError error) => error switch
        {
            JoinError.WrongPassword => "Wrong password.",
            JoinError.Full => "That room is full.",
            JoinError.Kicked => "You were kicked from that room a moment ago. Try again in a few seconds.",
            JoinError.Banned => "You were kicked from that room twice, so you can't go back.",
            JoinError.WrongVersion => $"That room is on a different version of the game. You're on version {Network.GameVersion}.",
            _ => "Couldn't reach the room. It may have closed."
        };

        private void OnMessage(PeerId from, ReadOnlySpan<byte> data)
        {
            if (!_remotePlayers.TryGetValue(from, out RemotePlayer? remote)) return;

            try
            {
                var message = new MessageReader(data);
                switch (message.Type)
                {
                    case MessageType.State:
                        remote.AddState(message.ReadPlayerState());
                        break;

                    case MessageType.Shot:
                        message.ReadVector3();
                        Weapon shotWeapon = Weapon.Get((WeaponId)message.ReadByte());
                        var ends = new Vector3[Math.Min((int)message.ReadByte(), _maxPellets)];
                        for (int i = 0; i < ends.Length; i++) ends[i] = message.ReadVector3();
                        remote.ShowShot(ends, shotWeapon);
                        break;

                    case MessageType.Hit:
                        byte damage = message.ReadByte();
                        byte hitFlags = message.ReadByte();
                        var hitWeapon = (WeaponId)message.ReadByte();
                        LocalPlayer.TakeDamage(damage, from, (hitFlags & _flagHeadshot) != 0, hitWeapon,
                            noScope: (hitFlags & _flagNoScope) != 0, distance: message.ReadUShort(), backstab: (hitFlags & _flagBackstab) != 0);
                        break;

                    case MessageType.Died:
                        PeerId killer = message.ReadPeer();
                        byte how = message.ReadByte();
                        Weapon weapon = Weapon.Get((WeaponId)message.ReadByte());
                        ushort distance = message.ReadUShort();
                        remote.MarkDead();
                        OnRemoteDied(remote, killer, new Death(killer, (how & _flagHeadshot) != 0, (how & _flagFell) != 0, weapon.Id,
                            (how & _flagNoScope) != 0, distance, (how & _flagBackstab) != 0));
                        break;

                    case MessageType.Chat:
                        string text = Clean(message.ReadString());
                        if (text.Length > 0) Chat.AddMessage(remote.Name, remote.Color, text);
                        break;

                    case MessageType.Rocket:
                        ushort rocketId = message.ReadUShort();
                        Vector3 origin = message.ReadVector3();
                        Vector3 direction = message.ReadVector3();
                        if (direction.LengthSquared() < 1e-6f || !float.IsFinite(direction.X + direction.Y + direction.Z + origin.X + origin.Y + origin.Z)) break;
                        Rocket.Spawn(LocalPlayer, from, mine: false, origin, direction, rocketId);
                        remote.ShowShot([], Weapon.RocketLauncher);
                        break;

                    case MessageType.RocketExploded:
                        var owner = message.ReadPeer();
                        ushort explodedId = message.ReadUShort();
                        Vector3 at = message.ReadVector3();
                        bool wasDirect = message.ReadBool();
                        PeerId directHit = message.ReadPeer();
                        Rocket.Find(owner, explodedId)?.Explode(at, decidedHere: false, wasDirect ? directHit : null);
                        break;

                    case MessageType.Ping:
                        var pong = new MessageWriter(stackalloc byte[8], MessageType.Pong);
                        pong.Write(message.ReadFloat());
                        Network.Send(from, pong.Data, SendMode.Unreliable);
                        break;

                    case MessageType.Pong:
                        float roundTrip = (Time.TotalTime - message.ReadFloat()) * 1000f;
                        if (roundTrip is >= 0f and < 10000f) remote.AddPing(roundTrip);
                        break;

                    case MessageType.Pings:
                        if (from != Network.HostPeer) break;

                        _hostPings.Clear();
                        int pingCount = message.ReadByte();
                        for (int i = 0; i < pingCount; i++) _hostPings[message.ReadPeer()] = message.ReadUShort();
                        break;

                    case MessageType.Colors:
                        if (from == Network.HostPeer) ReadColors(ref message);
                        break;

                    case MessageType.VoteCall:
                        Votes.OnCall(from, message.ReadPeer());
                        break;

                    case MessageType.VoteCast:
                        Votes.OnCast(from, message.ReadBool());
                        break;

                    case MessageType.VoteState:
                        if (from == Network.HostPeer) Votes.OnState(ref message);
                        break;

                    case MessageType.ServerVariables:
                        // Only the host's say counts.
                        if (from == Network.HostPeer) ApplyServerVariables(ref message);
                        break;

                    case MessageType.BotAdd:
                        Vector3 botAt = message.ReadVector3();
                        float botYaw = message.ReadFloat();
                        if (Network.IsHost && LocalPlayer.CheatsAllowed && float.IsFinite(botAt.X + botAt.Y + botAt.Z + botYaw)) PutBot(botAt, botYaw);
                        break;

                    case MessageType.BotKick:
                        if (!Network.IsHost) break;

                        RemoveBots();
                        SendBots(to: null);
                        break;

                    case MessageType.Bots:
                        if (from == Network.HostPeer) ReadBots(ref message);
                        break;

                    case MessageType.BotHit:
                        int hitNumber = message.ReadByte();
                        byte botDamage = message.ReadByte();
                        byte botHitFlags = message.ReadByte();
                        Weapon botHitWeapon = Weapon.Get((WeaponId)message.ReadByte());
                        ushort botHitDistance = message.ReadUShort();
                        if (Network.IsHost && _bots.Find(bot => bot.Number == hitNumber) is { } hitBot)
                        {
                            HitBot(hitBot, from, botDamage, botHitFlags, botHitWeapon, botHitDistance);
                        }

                        break;

                    case MessageType.BotDied:
                        int diedNumber = message.ReadByte();
                        PeerId botKiller = message.ReadPeer();
                        byte botHow = message.ReadByte();
                        Weapon botWeapon = Weapon.Get((WeaponId)message.ReadByte());
                        ushort botDistance = message.ReadUShort();
                        if (from != Network.HostPeer || _bots.Find(bot => bot.Number == diedNumber) is not { } diedBot) break;

                        diedBot.SetHealth(0);
                        OnBotDied(diedBot, botKiller, botHow, botWeapon, botDistance);
                        break;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // A truncated or malformed message: ignore it.
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        private void OnRemoteDied(RemotePlayer victim, PeerId killer, Death death)
        {
            Weapon weapon = Weapon.Get(death.Weapon);
            float longShot = LongShot(death);
            bool byYou = killer == Network.LocalPeer;
            if (byYou)
            {
                Kills++;
                LocalPlayer.Hud.ShowKill(victim.Name, victim.Color, death.Headshot, death.NoScope, longShot, death.Backstab);
            }

            if (killer.Value == 0)
            {
                KillFeed.Add(null, default, victim.Name, victim.Color, weapon: null, headshot: false, death.Fell, involvesYou: false);
                return;
            }

            (string killerName, Color killerColor) = byYou ? (LocalName, LocalColor)
                : _remotePlayers.TryGetValue(killer, out RemotePlayer? other) ? (other.Name, other.Color)
                : (Network.GetName(killer), ColorOf(killer));

            KillFeed.Add(killerName, killerColor, victim.Name, victim.Color, weapon, death.Headshot, fell: false, involvesYou: byYou, death.NoScope, longShot,
                death.Backstab);
        }

        /// <summary>How far away a sniper kill was, to show in the kill feed: 0 for other weapons, and for close kills.</summary>
        private static float LongShot(Death death) => death.Weapon == WeaponId.Sniper && death.Distance >= _longShotDistance ? death.Distance : 0f;

        private void OnShotFired(Weapon weapon, Vector3 muzzle, IReadOnlyList<Gun.Pellet> pellets, bool noScope)
        {
            // Others see the shot; playing alone, there's nobody to tell, but there may be bots to hit.
            if (Network.InSession)
            {
                int count = Math.Min(pellets.Count, _maxPellets);
                var shot = new MessageWriter(stackalloc byte[16 + _maxPellets * 12], MessageType.Shot);
                shot.Write(muzzle);
                shot.Write((byte)weapon.Id);
                shot.Write((byte)count);
                for (int i = 0; i < count; i++) shot.Write(pellets[i].End);
                Network.Broadcast(shot.Data, SendMode.Unreliable);
            }

            // Add up every pellet that hit each player, so they take one hit for the whole shot. It counts as a headshot
            // if any pellet hit the head, and the distance is to the nearest pellet that hit them.
            var hits = new Dictionary<RemotePlayer, (int Damage, bool Headshot, float Distance)>();
            foreach (Gun.Pellet pellet in pellets)
            {
                if (pellet.Hit is not { GameObject: RemotePlayer { Alive: true } target } result) continue;

                bool headshot = result.Point.Y - target.Transform.WorldPosition.Y >= RemotePlayer.HeadHeight;
                (int damage, bool anyHeadshot, float nearest) = hits.TryGetValue(target, out var sofar) ? sofar : (0, false, float.MaxValue);
                hits[target] = (damage + (headshot ? weapon.HeadDamage : weapon.BodyDamage), anyHeadshot || headshot, MathF.Min(nearest, result.Distance));
            }

            foreach ((RemotePlayer target, (int total, bool headshot, float distance)) in hits)
            {
                Hit(target, total, (byte)((headshot ? _flagHeadshot : 0) | (noScope ? _flagNoScope : 0)), weapon, distance);
            }

            if (hits.Count > 0) LocalPlayer.Hud.ShowHit(hits.Values.Any(h => h.Headshot));
        }

        /// <summary>Tells everyone about a rocket just fired, so their games fly it too.</summary>
        private static void OnRocketFired(Rocket rocket)
        {
            if (!Network.InSession) return;

            var fired = new MessageWriter(stackalloc byte[32], MessageType.Rocket);
            fired.Write(rocket.Id);
            fired.Write(rocket.Transform.WorldPosition);
            fired.Write(Vector3.Normalize(rocket.Velocity));
            Network.Broadcast(fired.Data, SendMode.Reliable);
        }

        /// <summary>This game decided a rocket blew up: tell everyone, so theirs blows up in the same place.</summary>
        private static void OnRocketExploded(Rocket rocket, Vector3 position)
        {
            if (!Network.InSession) return;

            var exploded = new MessageWriter(stackalloc byte[32], MessageType.RocketExploded);
            exploded.Write(rocket.Owner.Value);
            exploded.Write(rocket.Id);
            exploded.Write(position);
            exploded.Write(rocket.DirectHit != null);
            exploded.Write(rocket.DirectHit?.Value ?? 0);
            Network.Broadcast(exploded.Data, SendMode.Reliable);
        }

        /// <summary>
        /// One of your rockets went off: a hit marker if it caught anyone. Each player works out their own damage from
        /// the blast, so nothing is sent.
        /// </summary>
        private void OnRocketDetonated(Rocket rocket, Vector3 position)
        {
            if (!rocket.IsMine) return;

            bool hit = _remotePlayers.Values.Any(remote => remote.Alive && rocket.Strength(position, remote.Transform.WorldPosition) > 0f);

            // Bots don't work out their own damage, so it's done here, as the rocket would for a player.
            foreach (Bot bot in _bots.Where(bot => bot.Alive).ToList())
            {
                Vector3 centre = bot.Transform.WorldPosition + new Vector3(0f, 0.9f, 0f);
                float strength = rocket.Strength(position, bot.Transform.WorldPosition);
                bool direct = rocket.DirectHit == bot.Peer;
                if (strength <= 0f && !direct) continue;

                int damage = direct ? Player.MaxHealth : (int)MathF.Ceiling(Weapon.RocketLauncher.BodyDamage * strength);
                Hit(bot, damage, flags: 0, Weapon.RocketLauncher, Vector3.Distance(position, centre));
                hit = true;
            }

            if (hit) LocalPlayer.Hud.ShowHit(headshot: false);
        }

        /// <summary>
        /// A knife swing. Everyone hears it, as a Shot with no pellets. A slash or stab that reaches another player hurts
        /// them; a stab in the back kills outright. The knife never counts a headshot.
        /// </summary>
        private void OnStruck(Weapon weapon, Vector3 blade, RaycastHit? hit, bool stab)
        {
            if (Network.InSession)
            {
                var swing = new MessageWriter(stackalloc byte[16], MessageType.Shot);
                swing.Write(blade);
                swing.Write((byte)weapon.Id);
                swing.Write((byte)0);
                Network.Broadcast(swing.Data, SendMode.Unreliable);
            }

            if (hit is not { GameObject: RemotePlayer { Alive: true } target } result) return;

            bool backstab = stab && IsBehind(target, LocalPlayer.Transform.WorldPosition);
            int damage = backstab ? Player.MaxHealth : stab ? weapon.StabDamage : weapon.BodyDamage;
            Hit(target, damage, backstab ? _flagBackstab : (byte)0, weapon, result.Distance);
            LocalPlayer.Hud.ShowHit(headshot: false);
        }

        /// <summary>Whether <paramref name="position"/> is behind <paramref name="target"/>, going by which way they face.</summary>
        private static bool IsBehind(RemotePlayer target, Vector3 position)
        {
            Vector3 away = (position - target.Transform.WorldPosition) with { Y = 0f };
            Vector3 facing = target.Transform.Forward with { Y = 0f };
            if (away.LengthSquared() < 1e-6f || facing.LengthSquared() < 1e-6f) return false;

            return Vector3.Dot(Vector3.Normalize(away), Vector3.Normalize(facing)) < -_backstabAngle;
        }

        /// <summary>
        /// A hit on someone: how hard, how (the flags), with what, and from how far, in metres. A player is told, and
        /// takes it themselves; a bot takes it in the host's game, so the host takes it here or is told.
        /// </summary>
        private void Hit(RemotePlayer target, int damage, byte flags, Weapon weapon, float distance)
        {
            if (target is Bot bot && Network.IsHost) HitBot(bot, Network.LocalPeer, damage, flags, weapon, distance);
            else if (Network.InSession) SendHit(target, damage, flags, weapon, distance);
        }

        /// <summary>The host's game hurts a bot, and tells everyone how it's doing, and if that killed it, who did.</summary>
        private void HitBot(Bot bot, PeerId attacker, int damage, byte flags, Weapon weapon, float distance)
        {
            if (bot.Hurt(damage))
            {
                if (Network.InSession)
                {
                    var died = new MessageWriter(stackalloc byte[16], MessageType.BotDied);
                    died.Write((byte)bot.Number);
                    died.Write(attacker.Value);
                    died.Write(flags);
                    died.Write((byte)weapon.Id);
                    died.Write((ushort)Math.Min(MathF.Round(distance), ushort.MaxValue));
                    Network.Broadcast(died.Data, SendMode.Reliable);
                }

                OnBotDied(bot, attacker, flags, weapon, distance);
            }

            SendBots(to: null);
        }

        /// <summary>A bot died: the kill popup for whoever killed it, and the kill feed, as for a player, but no score.</summary>
        private void OnBotDied(Bot bot, PeerId killer, byte flags, Weapon weapon, float distance)
        {
            bool headshot = (flags & _flagHeadshot) != 0;
            bool noScope = (flags & _flagNoScope) != 0;
            bool backstab = (flags & _flagBackstab) != 0;
            float longShot = weapon.Id == WeaponId.Sniper && distance >= _longShotDistance ? distance : 0f;
            bool byYou = killer == Network.LocalPeer;
            if (byYou) LocalPlayer.Hud.ShowKill(bot.Name, bot.Color, headshot, noScope, longShot, backstab);

            (string killerName, Color killerColor) = byYou ? (LocalName, LocalColor) : (NameOf(killer), ColorOf(killer));
            KillFeed.Add(killerName, killerColor, bot.Name, bot.Color, weapon, headshot, fell: false, involvesYou: byYou, noScope, longShot, backstab);
        }

        /// <summary>Tells a player they were hit, or the host that a bot was, as <see cref="Hit"/> says.</summary>
        private static void SendHit(RemotePlayer target, int damage, byte flags, Weapon weapon, float distance)
        {
            var hit = new MessageWriter(stackalloc byte[16], target is Bot ? MessageType.BotHit : MessageType.Hit);
            if (target is Bot bot) hit.Write((byte)bot.Number);
            hit.Write((byte)Math.Min(damage, byte.MaxValue));
            hit.Write(flags);
            hit.Write((byte)weapon.Id);
            hit.Write((ushort)Math.Min(MathF.Round(distance), ushort.MaxValue));
            Network.Send(target is Bot ? Network.HostPeer : target.Peer, hit.Data, SendMode.Reliable);
        }

        private void OnLocalPlayerDied(Death death)
        {
            Deaths++;

            if (death.Killer is PeerId killer && _remotePlayers.TryGetValue(killer, out RemotePlayer? by))
            {
                KillFeed.Add(by.Name, by.Color, LocalName, LocalColor, Weapon.Get(death.Weapon), death.Headshot, fell: false, involvesYou: true,
                    death.NoScope, LongShot(death), death.Backstab);
            }
            else
            {
                KillFeed.Add(null, default, LocalName, LocalColor, weapon: null, headshot: false, death.Fell, involvesYou: true);
            }

            if (!Network.InSession) return;

            var died = new MessageWriter(stackalloc byte[16], MessageType.Died);
            died.Write(death.Killer?.Value ?? 0);
            died.Write((byte)((death.Headshot ? _flagHeadshot : 0) | (death.Fell ? _flagFell : 0) | (death.NoScope ? _flagNoScope : 0)
                | (death.Backstab ? _flagBackstab : 0)));
            died.Write((byte)death.Weapon);
            died.Write((ushort)Math.Min(MathF.Round(death.Distance), ushort.MaxValue));
            Network.Broadcast(died.Data, SendMode.Reliable);
        }

        private void OnChatSent(string text)
        {
            text = Clean(text);
            if (text.Length == 0) return;

            Chat.AddMessage(LocalName, LocalColor, text);
            if (!Network.InSession) return;

            var message = new MessageWriter(stackalloc byte[260], MessageType.Chat);
            message.Write(text);
            Network.Broadcast(message.Data, SendMode.Reliable);
        }

        /// <summary>Shows a game message in chat (which also writes it to the console).</summary>
        private void Announce(string text) => Chat.AddSystemMessage(text);

        /// <summary>No control characters, and no longer than the chat box allows, whoever sent it.</summary>
        private static string Clean(string text)
        {
            string cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return cleaned.Length > ChatBox.MaxMessageLength ? cleaned[..ChatBox.MaxMessageLength] : cleaned;
        }

        /// <summary>
        /// A player's colour: the one the host gave them, or until it says, one picked from their id (which two players
        /// can share, so the host hands them out instead).
        /// </summary>
        internal Color ColorOf(PeerId peer) => _colors[_colorIndex.TryGetValue(peer, out int index) ? index : (int)(peer.Value % (ulong)_colors.Length)];

        /// <summary>A player's name: yours, someone in the room, or failing those, whatever the network calls them.</summary>
        internal string NameOf(PeerId peer) => peer == Network.LocalPeer ? LocalName
            : _remotePlayers.TryGetValue(peer, out RemotePlayer? remote) ? remote.Name
            : Network.GetName(peer);

        /// <summary>Everyone else in the room.</summary>
        internal IEnumerable<RemotePlayer> RemotePlayers => _remotePlayers.Values;

        /// <summary>
        /// The host gives everyone without a colour the least used one, earliest in the palette first, so nobody
        /// shares until every colour's taken, then tells everyone.
        /// </summary>
        private void AssignColors()
        {
            if (!Network.InSession || !Network.IsHost) return;

            var members = new List<PeerId> { Network.LocalPeer };
            members.AddRange(Network.Peers);
            foreach (PeerId gone in _colorIndex.Keys.Where(peer => !members.Contains(peer)).ToList()) _colorIndex.Remove(gone);

            foreach (PeerId member in members)
            {
                if (_colorIndex.ContainsKey(member)) continue;

                int[] uses = new int[_colors.Length];
                foreach (int index in _colorIndex.Values) uses[index]++;

                int least = 0;
                for (int i = 1; i < uses.Length; i++)
                {
                    if (uses[i] < uses[least]) least = i;
                }

                _colorIndex[member] = least;
            }

            var message = new MessageWriter(stackalloc byte[2 + 255 * 9], MessageType.Colors);
            int count = Math.Min(_colorIndex.Count, byte.MaxValue);
            message.Write((byte)count);
            foreach ((PeerId peer, int index) in _colorIndex.Take(count))
            {
                message.Write(peer.Value);
                message.Write((byte)index);
            }

            Network.Broadcast(message.Data, SendMode.Reliable);
            Recolor();
        }

        /// <summary>Everyone's colours, from the host.</summary>
        private void ReadColors(ref MessageReader message)
        {
            _colorIndex.Clear();
            int count = message.ReadByte();
            for (int i = 0; i < count; i++)
            {
                PeerId peer = message.ReadPeer();
                int index = message.ReadByte();
                if (index < _colors.Length) _colorIndex[peer] = index;
            }

            Recolor();
        }

        private void Recolor()
        {
            foreach (RemotePlayer remote in _remotePlayers.Values) remote.Color = ColorOf(remote.Peer);
        }

        private void OnKicked(TimeSpan? banFor) => Announce(KickedText(banFor));

        /// <summary>What being kicked means for the player: back in a while, or not at all.</summary>
        public static string KickedText(TimeSpan? banFor) => banFor is TimeSpan wait
            ? $"You were kicked from the room. You can rejoin in {wait.TotalSeconds:0} seconds."
            : "You were kicked from the room again, so you can't go back.";
    }
}
