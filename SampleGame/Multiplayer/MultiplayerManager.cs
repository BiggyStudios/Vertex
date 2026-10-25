using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Multiplayer over Steam (or local UDP for testing on one PC). Every player is in charge of their own player:
    /// they send where they are many times a second, and when they shoot someone they tell that player, who takes
    /// the damage and announces their own death. Each player also counts their own kills and deaths.
    /// <para>F1 host a Steam lobby, F2 join any Steam lobby, F3 host locally, F4 join a local host, F5 leave.
    /// Friends can also join through Steam invites or "Join Game". Enter to chat, Tab for the scoreboard.</para>
    /// </summary>
    public class MultiplayerManager : Component
    {
        // Valve's Spacewar test app: anyone can use it to try out Steamworks. A released game needs its own app ID.
        private const uint _steamAppId = 480;
        private const string _steamGameTag = "vertex-sample";

        // Every other physics step: 30 updates a second.
        private const int _stateInterval = 2;

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

        private static readonly Color[] _colors =
        [
            new(0.95f, 0.35f, 0.35f), new(0.35f, 0.55f, 1f), new(0.35f, 0.85f, 0.4f), new(0.95f, 0.8f, 0.25f),
            new(0.75f, 0.4f, 0.95f), new(0.25f, 0.85f, 0.85f), new(1f, 0.6f, 0.25f), new(0.9f, 0.9f, 0.9f)
        ];

        public Player LocalPlayer { get; set; } = null!;

        /// <summary>Connect to Steam when the game starts, so friends' invites work. Off, Steam only starts on F1 or F2.</summary>
        public bool ConnectToSteamOnStart { get; set; } = true;

        public ChatBox Chat { get; private set; } = null!;
        public KillFeed KillFeed { get; private set; } = null!;

        /// <summary>The local player's score.</summary>
        public int Kills { get; private set; }
        public int Deaths { get; private set; }

        /// <summary>A short description of the connection, for the scoreboard.</summary>
        public string SessionStatus => Network.Transport switch
        {
            SteamTransport { InSession: true } => $"Steam lobby · {Network.Peers.Count + 1} players",
            UdpTransport { InSession: true } => $"Local game · {Network.Peers.Count + 1} players",
            _ => "Not in a game · F1 host, F2 join"
        };

        private readonly Dictionary<PeerId, RemotePlayer> _remotePlayers = new();
        private SteamTransport? _steam;
        private int _tick;

        private string LocalName => Network.InSession ? Network.GetName(Network.LocalPeer) : "You";
        private Color LocalColor => Network.InSession ? ColorOf(Network.LocalPeer) : _colors[6];

        protected override void Start()
        {
            Chat = GameObject.AddComponent<ChatBox>();
            KillFeed = GameObject.AddComponent<KillFeed>();
            GameObject.AddComponent<Scoreboard>().Source = this;
            Chat.MessageSent += OnChatSent;

            Network.PeerJoined += OnPeerJoined;
            Network.PeerLeft += OnPeerLeft;
            Network.SessionEnded += OnSessionEnded;
            Network.MessageReceived += OnMessage;

            LocalPlayer.Died += OnLocalPlayerDied;
            LocalPlayer.Gun.Fired += OnShotFired;
            LocalPlayer.Gun.Struck += OnStruck;
            LocalPlayer.Gun.RocketFired += OnRocketFired;
            Rocket.Exploded += OnRocketExploded;
            Rocket.Detonated += OnRocketDetonated;

            Announce("F1 host a Steam lobby, F2 join one, F3/F4 host/join on this PC, F5 leave. Enter to chat, Tab for scores.");

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
            Network.SessionEnded -= OnSessionEnded;
            Network.MessageReceived -= OnMessage;
            Rocket.Exploded -= OnRocketExploded;
            Rocket.Detonated -= OnRocketDetonated;
        }

        protected override void Update()
        {
            if (GameInput.Blocked) return;

            if (InputSystem.GetKeyDown(Key.F1)) HostSteam();
            if (InputSystem.GetKeyDown(Key.F2)) FindSteam();
            if (InputSystem.GetKeyDown(Key.F3)) HostLocal(UdpTransport.DefaultPort);
            if (InputSystem.GetKeyDown(Key.F4)) JoinLocal("127.0.0.1", UdpTransport.DefaultPort);
            if (InputSystem.GetKeyDown(Key.F5)) Leave();
        }

        public void HostSteam() => StartSteam()?.HostLobby();

        public void FindSteam() => StartSteam()?.FindLobby();

        public void HostLocal(int port)
        {
            _steam = null;
            try
            {
                Network.Start(UdpTransport.Host(port));
                Announce($"Hosting on port {port}. Others on this network can join with: connect <this PC's address>. On this PC, press F4 in another copy.");
            }
            catch (System.Net.Sockets.SocketException exception)
            {
                Announce($"Couldn't host on port {port}: {exception.Message}");
            }
        }

        public void JoinLocal(string address, int port)
        {
            _steam = null;
            try
            {
                Network.Start(UdpTransport.Join(address, port));
                Announce($"Joining {address}:{port}...");
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
            {
                Announce($"Couldn't join {address}: {exception.Message}");
            }
        }

        public void Leave()
        {
            if (_steam != null && Network.Transport == _steam) _steam.LeaveLobby();
            else Network.Stop();
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

                float before = variable.Get(LocalPlayer);
                variable.Set(LocalPlayer, value);
                float after = variable.Get(LocalPlayer);
                if (MathF.Abs(after - before) > 0.0001f) Announce($"Server setting {variable.Name} is {after.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}.");
            }
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

        /// <summary>Everyone in the game, for the scoreboard.</summary>
        public IEnumerable<ScoreEntry> GetScores()
        {
            yield return new ScoreEntry(LocalName, LocalColor, Kills, Deaths, IsLocal: true, Alive: !LocalPlayer.IsDead);

            foreach (RemotePlayer remote in _remotePlayers.Values)
            {
                yield return new ScoreEntry(remote.Name, remote.Color, remote.Kills, remote.Deaths, IsLocal: false, remote.Alive);
            }
        }

        /// <summary>Connects to Steam if not already, making it the active transport. Null if Steam isn't available.</summary>
        private SteamTransport? StartSteam()
        {
            if (_steam != null && Network.Transport == _steam) return _steam;

            _steam = SteamTransport.TryStart(_steamAppId, _steamGameTag, out string error);
            if (_steam == null)
            {
                Announce($"Steam isn't available ({error}). For a local game, press F3 in one copy and F4 in another.");
                return null;
            }

            Network.Start(_steam);
            Announce($"Connected to Steam as {_steam.GetName(_steam.LocalPeer)}.");
            return _steam;
        }

        private void OnPeerJoined(PeerId peer)
        {
            _remotePlayers[peer] = new RemotePlayer(peer, Network.GetName(peer), ColorOf(peer));
            Announce($"{Network.GetName(peer)} joined.");

            // So they play by the same rules as everyone else.
            if (Network.IsHost) SendServerVariables(peer);
        }

        private void OnPeerLeft(PeerId peer)
        {
            if (!_remotePlayers.Remove(peer, out RemotePlayer? remote)) return;

            remote.Destroy();
            Announce($"{remote.Name} left.");
        }

        private void OnSessionEnded()
        {
            foreach (RemotePlayer remote in _remotePlayers.Values) remote.Destroy();
            _remotePlayers.Clear();
            Announce("Left the game.");
        }

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

                    case MessageType.ServerVariables:
                        // Only the host's say counts.
                        if (from == Network.HostPeer) ApplyServerVariables(ref message);
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
            if (!Network.InSession) return;

            int count = Math.Min(pellets.Count, _maxPellets);
            var shot = new MessageWriter(stackalloc byte[16 + _maxPellets * 12], MessageType.Shot);
            shot.Write(muzzle);
            shot.Write((byte)weapon.Id);
            shot.Write((byte)count);
            for (int i = 0; i < count; i++) shot.Write(pellets[i].End);
            Network.Broadcast(shot.Data, SendMode.Unreliable);

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
                SendHit(target, total, (byte)((headshot ? _flagHeadshot : 0) | (noScope ? _flagNoScope : 0)), weapon, distance);
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
            if (hit) LocalPlayer.Hud.ShowHit(headshot: false);
        }

        /// <summary>
        /// A knife swing. Everyone hears it, as a Shot with no pellets. A slash or stab that reaches another player hurts
        /// them; a stab in the back kills outright. The knife never counts a headshot.
        /// </summary>
        private void OnStruck(Weapon weapon, Vector3 blade, RaycastHit? hit, bool stab)
        {
            if (!Network.InSession) return;

            var swing = new MessageWriter(stackalloc byte[16], MessageType.Shot);
            swing.Write(blade);
            swing.Write((byte)weapon.Id);
            swing.Write((byte)0);
            Network.Broadcast(swing.Data, SendMode.Unreliable);

            if (hit is not { GameObject: RemotePlayer { Alive: true } target } result) return;

            bool backstab = stab && IsBehind(target, LocalPlayer.Transform.WorldPosition);
            int damage = backstab ? Player.MaxHealth : stab ? weapon.StabDamage : weapon.BodyDamage;
            SendHit(target, damage, backstab ? _flagBackstab : (byte)0, weapon, result.Distance);
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

        /// <summary>Tells <paramref name="target"/> they were hit: how hard, how (the flags), with what, and from how far, in metres.</summary>
        private static void SendHit(RemotePlayer target, int damage, byte flags, Weapon weapon, float distance)
        {
            var hit = new MessageWriter(stackalloc byte[8], MessageType.Hit);
            hit.Write((byte)Math.Min(damage, byte.MaxValue));
            hit.Write(flags);
            hit.Write((byte)weapon.Id);
            hit.Write((ushort)Math.Min(MathF.Round(distance), ushort.MaxValue));
            Network.Send(target.Peer, hit.Data, SendMode.Reliable);
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

        private static Color ColorOf(PeerId peer) => _colors[(int)(peer.Value % (ulong)_colors.Length)];
    }
}
