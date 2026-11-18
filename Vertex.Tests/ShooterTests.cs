using System.Numerics;
using System.Text;

using Vertex.Graphs;
using Vertex.Networking;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.Scripting;

namespace Vertex.Tests
{
    /// <summary>A network connection with nobody on the other end: it keeps what's sent, and can pretend messages arrive.</summary>
    internal sealed class FakeTransport : INetworkTransport
    {
        public readonly List<(PeerId To, byte[] Data, SendMode Mode)> Sent = new();
        private readonly List<PeerId> _peers = new();

        public PeerId LocalPeer { get; } = new(1);
        public bool InSession { get; set; }
        public IReadOnlyCollection<PeerId> Peers => _peers;
        public PeerId HostPeer => LocalPeer;
        public string RoomName => "Test room";
        public bool RoomIsPrivate => false;

        public string GetName(PeerId peer) => $"Player {peer.Value}";
        public void Send(PeerId peer, ReadOnlySpan<byte> data, SendMode mode) => Sent.Add((peer, data.ToArray(), mode));
        public void Kick(PeerId peer, TimeSpan firstBan) {}
        public void Update() {}
        public void Dispose() {}

        public void Start()
        {
            InSession = true;
            SessionStarted?.Invoke();
        }

        public void Join(PeerId peer)
        {
            _peers.Add(peer);
            PeerJoined?.Invoke(peer);
        }

        public void Leave(PeerId peer)
        {
            _peers.Remove(peer);
            PeerLeft?.Invoke(peer);
        }

        public void Receive(PeerId from, byte[] data) => MessageReceived?.Invoke(from, data);

        public event Action<PeerId>? PeerJoined;
        public event Action<PeerId>? PeerLeft;
        public event MessageHandler? MessageReceived;
        public event Action? SessionStarted;
        public event Action? SessionEnded;
        public event Action<JoinError>? JoinFailed;
        public event Action<TimeSpan?>? Kicked;

        public void Unused()
        {
            SessionEnded?.Invoke();
            JoinFailed?.Invoke(JoinError.Full);
            Kicked?.Invoke(null);
        }
    }

    /// <summary>The nodes a shooter is made of, and network games.</summary>
    public class ShooterTests : EngineTest
    {
        private readonly List<LogEntry> _logged = new();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));

        private readonly string[] _assetFolders = Application.AssetFolders;

        public ShooterTests()
        {
            Log.Written += _logged.Add;
            Application.AssetFolders = [_folder];
        }

        public override void Dispose()
        {
            Log.Written -= _logged.Add;
            Network.Stop();
            Application.AssetFolders = _assetFolders;
            base.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        private IEnumerable<string> Printed => _logged.Where(e => e.Level == LogLevel.Info).Select(e => e.Message);

        private static GameLoop Loop()
        {
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            return loop;
        }

        private static GameObject Scripted(string name, Graph graph)
        {
            var gameObject = new GameObject(name);
            gameObject.AddComponent<VisualScript>().Script = graph;
            return gameObject;
        }

        private static GraphNode Print(Graph graph, GraphNode from, string port, string text)
        {
            GraphNode print = graph.AddNode("Actions/Print", Vector2.Zero);
            print.Values["Value"] = text;
            Assert.True(graph.Link(from.Id, port, print.Id, "In"));
            return print;
        }

        [Fact]
        public void Wait_carries_the_flow_on_later()
        {
            var graph = new Graph(ScriptNodes.Kind);
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode wait = graph.AddNode("Flow/Wait", Vector2.Zero);
            wait.Values["Seconds"] = 0.5f;
            graph.Link(start.Id, "Then", wait.Id, "In");
            Print(graph, wait, "Then", "later");
            Scripted("Waiter", graph);

            GameLoop loop = Loop();
            RunFrames(loop, 3, 0.1);
            Assert.Empty(Printed);
            RunFrames(loop, 5, 0.1);
            Assert.Equal(["later"], Printed);
        }

        [Fact]
        public void A_cooldown_lets_the_flow_through_once_every_so_often()
        {
            var graph = new Graph(ScriptNodes.Kind);
            GraphNode update = graph.AddNode("Events/On Update", Vector2.Zero);
            GraphNode cooldown = graph.AddNode("Flow/Cooldown", Vector2.Zero);
            cooldown.Values["Seconds"] = 0.25f;
            graph.Link(update.Id, "Then", cooldown.Id, "In");
            Print(graph, cooldown, "Then", "bang");
            Scripted("Gun", graph);

            RunFrames(Loop(), 20, 0.05);

            Assert.InRange(Printed.Count(), 4, 5);
        }

        [Fact]
        public void A_raycast_finds_what_it_hits()
        {
            var ground = new GameObject("Ground");
            ground.Transform.Position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider>().Size = new Vector3(20f, 1f, 20f);

            var graph = new Graph(ScriptNodes.Kind);
            GraphNode interval = graph.AddNode("Events/On Interval", Vector2.Zero);
            interval.Values["Seconds"] = 0.1f;
            GraphNode once = graph.AddNode("Flow/Do Once", Vector2.Zero);
            GraphNode ray = graph.AddNode("Physics/Raycast", Vector2.Zero);
            ray.Values["Origin"] = new Vector3(0f, 5f, 0f);
            ray.Values["Direction"] = -Vector3.UnitY;
            GraphNode name = graph.AddNode("Objects/Get Name", Vector2.Zero);
            GraphNode print = graph.AddNode("Actions/Print", Vector2.Zero);
            graph.Link(interval.Id, "Then", once.Id, "In");
            graph.Link(once.Id, "Then", ray.Id, "In");
            graph.Link(ray.Id, "Hit", print.Id, "In");
            graph.Link(ray.Id, "Object", name.Id, "Target");
            graph.Link(name.Id, "Name", print.Id, "Value");
            Scripted("Shooter", graph);

            RunFrames(Loop(), 20);

            Assert.Equal(["Ground"], Printed);
        }

        [Fact]
        public void Move_character_walks_forward_and_lands_on_the_ground()
        {
            var ground = new GameObject("Ground");
            ground.Transform.Position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider>().Size = new Vector3(40f, 1f, 40f);

            var graph = new Graph(ScriptNodes.Kind);
            GraphNode update = graph.AddNode("Events/On Update", Vector2.Zero);
            GraphNode move = graph.AddNode("Physics/Move Character", Vector2.Zero);
            move.Values["Move"] = new Vector3(0f, 0f, -3f);
            graph.Link(update.Id, "Then", move.Id, "In");
            GameObject player = Scripted("Player", graph);
            player.Transform.Position = new Vector3(0f, 2f, 0f);
            player.AddComponent<CharacterController>();

            RunFrames(Loop(), 60);

            Assert.True(player.Transform.Position.Z < -2f);
            Assert.True(player.GetComponent<CharacterController>()!.IsGrounded);
        }

        [Fact]
        public void Events_reach_the_scripts_they_are_sent_to()
        {
            var graph = new Graph(ScriptNodes.Kind);
            GraphNode on = graph.AddNode("Events/On Event", Vector2.Zero);
            on.Values["Name"] = "Ping";
            GraphNode print = graph.AddNode("Actions/Print", Vector2.Zero);
            graph.Link(on.Id, "Then", print.Id, "In");
            graph.Link(on.Id, "Value", print.Id, "Value");
            Scripted("Listener", graph);

            var sender = new Graph(ScriptNodes.Kind);
            GraphNode start = sender.AddNode("Events/On Start", Vector2.Zero);
            GraphNode send = sender.AddNode("Actions/Send Event", Vector2.Zero);
            send.Values["Name"] = "Ping";
            send.Values["Value"] = "pong";
            sender.Link(start.Id, "Then", send.Id, "In");
            GraphNode other = sender.AddNode("Actions/Send Event", Vector2.Zero);
            other.Values["Name"] = "Nobody listens";
            Scripted("Sender", sender);

            RunFrames(Loop(), 2);

            Assert.Equal(["pong"], Printed);
        }

        /// <summary>A player prefab whose script takes A off its Health when a Hit network event comes to it.</summary>
        private Prefab PlayerPrefab()
        {
            var graph = new Graph(ScriptNodes.Kind);
            graph.Variables.Add(new GraphVariable("Health", PortType.Number, 100f));
            GraphNode hit = graph.AddNode("Events/On Network Event", Vector2.Zero);
            hit.Values["Name"] = "Hit";
            GraphNode get = graph.AddNode("Variables/Get Variable", Vector2.Zero);
            get.Values["Variable"] = "Health";
            GraphNode subtract = graph.AddNode("Math/Subtract", Vector2.Zero);
            GraphNode set = graph.AddNode("Variables/Set Variable", Vector2.Zero);
            set.Values["Variable"] = "Health";
            graph.Link(hit.Id, "Then", set.Id, "In");
            graph.Link(get.Id, "Value", subtract.Id, "A");
            graph.Link(hit.Id, "A", subtract.Id, "B");
            graph.Link(subtract.Id, "Result", set.Id, "Value");
            string script = Path.Combine(_folder, "Player.vscript");
            graph.Save(script);

            var player = new GameObject("Player", new Scene());
            player.AddComponent<VisualScript>().Script = Assets.LoadGraph(script);
            player.AddComponent<NetworkTransform>();
            var view = new GameObject("View", player.Scene);
            view.Transform.Parent = player.Transform;
            view.AddComponent<Camera>();
            return Prefab.Save(player, Path.Combine(_folder, "Player.prefab"), _folder);
        }

        private static byte[] HitEvent(uint target, float damage)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write((byte)0xE3);
            writer.Write(target);
            writer.Write("Hit");
            writer.Write((byte)1);
            writer.Write((byte)1);
            writer.Write(damage);
            writer.Flush();
            return stream.ToArray();
        }

        [Fact]
        public void Players_are_made_on_every_machine_and_hits_go_to_whoever_owns_the_player()
        {
            Directory.CreateDirectory(_folder);
            Prefab prefab = PlayerPrefab();
            var spawn = new GameObject("Spawn A");
            spawn.Transform.Position = new Vector3(3f, 0f, 0f);
            spawn.AddComponent<SpawnPoint>();
            var manager = new GameObject("Network").AddComponent<NetworkManager>();
            manager.PlayerPrefab = prefab;
            var transport = new FakeTransport();
            Network.Start(transport);
            GameLoop loop = Loop();
            RunFrames(loop, 2);

            // Playing alone, there's a player at the spawn point.
            GameObject offline = manager.LocalPlayer!;
            Assert.Equal(new Vector3(3f, 0f, 0f), offline.Transform.Position);

            // Connected: it's swapped for one everyone's told about.
            transport.Start();
            transport.Join(new PeerId(2));
            RunFrames(loop, 2);
            GameObject mine = manager.LocalPlayer!;
            Assert.NotSame(offline, mine);
            Assert.True(offline.IsDestroyed);
            byte[] spawned = Assert.Single(transport.Sent, s => s.Data[0] == 0xE0).Data;

            // The other player's, arriving: owned by them, its camera off.
            byte[] theirs = spawned.ToArray();
            BitConverter.TryWriteBytes(theirs.AsSpan(1), 1234u);
            BitConverter.TryWriteBytes(theirs.AsSpan(5), 2ul);
            transport.Receive(new PeerId(2), theirs);
            RunFrames(loop, 2);
            NetworkObject other = NetworkScene.Find(1234)!;
            Assert.False(other.IsMine);
            Assert.True(mine.GetComponent<NetworkObject>()!.IsMine);
            Assert.False(other.GameObject.Transform.Children[0].GameObject.GetComponent<Camera>()!.Enabled);
            Assert.True(mine.Transform.Children[0].GameObject.GetComponent<Camera>()!.Enabled);

            // Shooting them: the hit goes to its owner, not here.
            transport.Sent.Clear();
            NetworkScene.SendEvent("Hit", other.GameObject, NetworkTarget.Owner, 25f);
            (PeerId to, byte[] data, _) = Assert.Single(transport.Sent);
            Assert.Equal(new PeerId(2), to);
            Assert.Equal(0xE3, data[0]);
            Assert.Equal(100f, other.GameObject.GetComponent<VisualScript>()!.Instance!.Variables["Health"]);

            // Being shot: our player's script takes the damage.
            transport.Receive(new PeerId(2), HitEvent(mine.GetComponent<NetworkObject>()!.NetworkId, 25f));
            Assert.Equal(75f, mine.GetComponent<VisualScript>()!.Instance!.Variables["Health"]);

            // Its moves are sent, unreliably.
            mine.Transform.Position = new Vector3(5f, 0f, 1f);
            RunFrames(loop, 6);
            Assert.Contains(transport.Sent, s => s.Data[0] == 0xE2 && s.Mode == SendMode.Unreliable);

            // They leave: theirs goes.
            transport.Leave(new PeerId(2));
            RunFrames(loop, 1);
            Assert.True(other.GameObject.IsDestroyed);
        }

        [Fact]
        public void Someone_joining_later_is_told_about_what_is_already_here()
        {
            Directory.CreateDirectory(_folder);
            var manager = new GameObject("Network").AddComponent<NetworkManager>();
            manager.PlayerPrefab = PlayerPrefab();
            var transport = new FakeTransport();
            Network.Start(transport);
            transport.Start();
            GameLoop loop = Loop();
            RunFrames(loop, 2);
            Assert.NotNull(manager.LocalPlayer);

            transport.Sent.Clear();
            transport.Join(new PeerId(7));

            (PeerId to, byte[] data, SendMode mode) = Assert.Single(transport.Sent);
            Assert.Equal(new PeerId(7), to);
            Assert.Equal(0xE0, data[0]);
            Assert.Equal(SendMode.Reliable, mode);
        }
    }
}
