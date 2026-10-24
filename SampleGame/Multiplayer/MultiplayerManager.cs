using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// Multiplayer over Steam (or local UDP for testing on one PC). Every player is in charge of their own player:
    /// they send where they are many times a second, and when they shoot someone they tell that player, who takes
    /// the damage and announces their own death. Status and the kill feed are printed to the console.
    /// <para>F1 host a Steam lobby, F2 join any Steam lobby, F3 host locally, F4 join a local host, F5 leave.
    /// Friends can also join through Steam invites or "Join Game".</para>
    /// </summary>
    public class MultiplayerManager : Component
    {
        // Valve's Spacewar test app: anyone can use it to try out Steamworks. A released game needs its own app ID.
        private const uint _steamAppId = 480;
        private const string _steamGameTag = "vertex-sample";

        // Every other physics step: 30 updates a second.
        private const int _stateInterval = 2;

        private const int _bodyDamage = 20;
        private const int _headshotDamage = 50;

        private static readonly Color[] _colors =
        [
            new(0.85f, 0.25f, 0.25f), new(0.25f, 0.45f, 0.9f), new(0.3f, 0.75f, 0.35f), new(0.85f, 0.7f, 0.2f),
            new(0.65f, 0.3f, 0.8f), new(0.2f, 0.75f, 0.75f), new(0.9f, 0.5f, 0.2f), new(0.85f, 0.85f, 0.85f)
        ];

        public Player LocalPlayer { get; set; } = null!;

        /// <summary>Connect to Steam when the game starts, so friends' invites work. Off, Steam only starts on F1 or F2.</summary>
        public bool ConnectToSteamOnStart { get; set; } = true;

        private readonly Dictionary<PeerId, RemotePlayer> _remotePlayers = new();
        private SteamTransport? _steam;
        private int _tick;

        protected override void Start()
        {
            Network.PeerJoined += OnPeerJoined;
            Network.PeerLeft += OnPeerLeft;
            Network.SessionEnded += OnSessionEnded;
            Network.MessageReceived += OnMessage;

            LocalPlayer.Died += OnLocalPlayerDied;
            LocalPlayer.Gun.Fired += OnShotFired;

            Console.WriteLine("Multiplayer: F1 host Steam lobby, F2 find Steam lobby, F3 host local, F4 join local, F5 leave.");

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
        }

        protected override void Update()
        {
            if (InputSystem.GetKeyDown(Key.F1)) StartSteam()?.HostLobby();
            if (InputSystem.GetKeyDown(Key.F2)) StartSteam()?.FindLobby();

            if (InputSystem.GetKeyDown(Key.F3))
            {
                _steam = null;
                Network.Start(UdpTransport.Host());
            }

            if (InputSystem.GetKeyDown(Key.F4))
            {
                _steam = null;
                Network.Start(UdpTransport.Join("127.0.0.1"));
            }

            if (InputSystem.GetKeyDown(Key.F5))
            {
                if (_steam != null) _steam.LeaveLobby();
                else Network.Stop();
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
                !LocalPlayer.IsDead);

            var message = new MessageWriter(stackalloc byte[64], MessageType.State);
            message.Write(state);
            Network.Broadcast(message.Data, SendMode.Unreliable);
        }

        /// <summary>Connects to Steam if not already, making it the active transport. Null if Steam isn't available.</summary>
        private SteamTransport? StartSteam()
        {
            if (_steam != null && Network.Transport == _steam) return _steam;

            _steam = SteamTransport.TryStart(_steamAppId, _steamGameTag, out string error);
            if (_steam == null)
            {
                Console.WriteLine($"Steam isn't available ({error}). For local play, press F3 in one game and F4 in another.");
                return null;
            }

            Network.Start(_steam);
            Console.WriteLine($"Connected to Steam as {_steam.GetName(_steam.LocalPeer)}.");
            return _steam;
        }

        private void OnPeerJoined(PeerId peer)
        {
            Color color = _colors[(int)(peer.Value % (ulong)_colors.Length)];
            _remotePlayers[peer] = new RemotePlayer(peer, Network.GetName(peer), color);
            Console.WriteLine($"{Network.GetName(peer)} joined.");
        }

        private void OnPeerLeft(PeerId peer)
        {
            if (!_remotePlayers.Remove(peer, out RemotePlayer? remote)) return;

            remote.Destroy();
            Console.WriteLine($"{remote.Name} left.");
        }

        private void OnSessionEnded()
        {
            foreach (RemotePlayer remote in _remotePlayers.Values) remote.Destroy();
            _remotePlayers.Clear();
            Console.WriteLine("Left the session.");
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
                        remote.ShowShot(message.ReadVector3());
                        break;

                    case MessageType.Hit:
                        LocalPlayer.TakeDamage(message.ReadByte(), from);
                        break;

                    case MessageType.Died:
                        PeerId killer = message.ReadPeer();
                        remote.MarkDead();
                        if (killer == Network.LocalPeer) LocalPlayer.Hud.ShowHitMarker(kill: true);
                        Console.WriteLine(killer == Network.LocalPeer ? $"You killed {remote.Name}."
                            : killer.Value != 0 ? $"{Network.GetName(killer)} killed {remote.Name}."
                            : $"{remote.Name} died.");
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

        private void OnShotFired(Vector3 muzzle, Vector3 end, RaycastHit? hit)
        {
            if (!Network.InSession) return;

            var shot = new MessageWriter(stackalloc byte[32], MessageType.Shot);
            shot.Write(muzzle);
            shot.Write(end);
            Network.Broadcast(shot.Data, SendMode.Unreliable);

            if (hit is not { GameObject: RemotePlayer { Alive: true } target } result) return;

            bool headshot = result.Point.Y - target.Transform.WorldPosition.Y >= RemotePlayer.HeadHeight;
            var damage = new MessageWriter(stackalloc byte[8], MessageType.Hit);
            damage.Write((byte)(headshot ? _headshotDamage : _bodyDamage));
            Network.Send(target.Peer, damage.Data, SendMode.Reliable);

            LocalPlayer.Hud.ShowHitMarker();
        }

        private void OnLocalPlayerDied(PeerId? killer)
        {
            Console.WriteLine(killer is PeerId by ? $"Killed by {Network.GetName(by)}." : "You died.");
            if (!Network.InSession) return;

            var died = new MessageWriter(stackalloc byte[16], MessageType.Died);
            died.Write(killer?.Value ?? 0);
            Network.Broadcast(died.Data, SendMode.Reliable);
        }
    }
}
