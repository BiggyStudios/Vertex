using System.Numerics;
using System.Text;

using Vertex.Scripting;

namespace Vertex.Networking
{
    /// <summary>Who a network event goes to.</summary>
    public enum NetworkTarget
    {
        /// <summary>Everyone in the game, this machine included.</summary>
        Everyone,

        /// <summary>Everyone but this machine.</summary>
        Others,

        /// <summary>Whoever owns the event's object (for a player, the one playing it): to hit someone, tell its owner.</summary>
        Owner,

        /// <summary>The host only.</summary>
        Host
    }

    /// <summary>A network event arriving: its name, the object it's about (if any), who sent it, and up to three values.</summary>
    public sealed record NetworkEvent(string Name, GameObject? Target, string From, IReadOnlyList<object?> Values);

    /// <summary>
    /// Objects shared between everyone in a network game (<see cref="NetworkObject"/>): made on everyone's machine when
    /// one makes them, moved by their owner (<see cref="NetworkTransform"/>), and taken away when they're destroyed or
    /// their owner leaves. Also network events: a name and a few values sent to everyone, an object's owner, or the host,
    /// which visual scripts get with On Network Event.
    /// <para>
    /// The engine's messages start with a byte of 0xE0 or more; a game's own messages (see <see cref="Network"/>) should
    /// start with smaller ones, and each ignores the other's.
    /// </para>
    /// </summary>
    public static class NetworkScene
    {
        internal const byte FirstMessage = 0xE0;
        private const byte SpawnMessage = 0xE0;
        private const byte DespawnMessage = 0xE1;
        private const byte TransformMessage = 0xE2;
        private const byte EventMessage = 0xE3;

        private static readonly Dictionary<uint, NetworkObject> _objects = new();
        private static bool _listening;

        /// <summary>Every network object there is now, everyone's.</summary>
        public static IEnumerable<NetworkObject> Objects => _objects.Values.Where(o => !o.GameObject.IsDestroyed && !o.Despawned);

        /// <summary>The players' objects: each player's own, made from the <see cref="NetworkManager.PlayerPrefab"/>.</summary>
        public static IEnumerable<NetworkObject> Players => Objects.Where(o => o.IsPlayer);

        /// <summary>Raised with each network event as it arrives (one sent to Everyone, on the sender too), after visual scripts have it.</summary>
        public static event Action<NetworkEvent>? EventReceived;

        /// <summary>The name of this machine's player: their Steam name online, "You" otherwise.</summary>
        public static string LocalName => Network.InSession ? Network.GetName(Network.LocalPeer) : "You";

        /// <summary>Starts listening for the engine's messages and session changes. Done by <see cref="NetworkManager"/>.</summary>
        public static void Listen()
        {
            if (_listening) return;
            _listening = true;
            Network.MessageReceived += OnMessage;
            Network.PeerJoined += OnPeerJoined;
            Network.PeerLeft += OnPeerLeft;
            Network.SessionStarted += OnSessionStarted;
            Network.SessionEnded += OnSessionEnded;
        }

        // ---------------------------------------------------------------- Objects

        internal static void Register(NetworkObject networked)
        {
            if (networked.NetworkId != 0) _objects[networked.NetworkId] = networked;
        }

        internal static void Unregister(NetworkObject networked)
        {
            if (_objects.TryGetValue(networked.NetworkId, out NetworkObject? known) && known == networked) _objects.Remove(networked.NetworkId);
        }

        public static NetworkObject? Find(uint id) => _objects.TryGetValue(id, out NetworkObject? found) && !found.GameObject.IsDestroyed ? found : null;

        /// <summary>A number for a new object of this machine's: random, so two machines making objects at once don't clash.</summary>
        private static uint NewId()
        {
            uint id;
            do id = (uint)Random.Shared.Next(1, int.MaxValue);
            while (_objects.ContainsKey(id));
            return id;
        }

        /// <summary>
        /// Makes a copy of <paramref name="prefab"/> owned by this machine, and the same copy on everyone else's: a player,
        /// a rocket, a pickup. Give it a <see cref="NetworkTransform"/> for its moves to be seen.
        /// </summary>
        public static GameObject Spawn(Prefab prefab, Vector3 position, Quaternion rotation, bool isPlayer = false)
        {
            GameObject made = prefab.Instantiate(position, rotation);
            NetworkObject networked = made.GetComponent<NetworkObject>() ?? made.AddComponent<NetworkObject>();
            networked.NetworkId = NewId();
            networked.Owner = Network.LocalPeer;
            networked.IsPlayer = isPlayer;
            networked.SpawnedFrom = Assets.RelativePath(prefab.Source);
            Register(networked);

            if (Network.InSession) Network.Broadcast(SpawnBytes(networked, position, rotation), SendMode.Reliable);
            return made;
        }

        private static byte[] SpawnBytes(NetworkObject networked, Vector3 position, Quaternion rotation)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(SpawnMessage);
            writer.Write(networked.NetworkId);
            writer.Write(networked.Owner.Value);
            writer.Write(networked.IsPlayer);
            writer.Write(networked.SpawnedFrom ?? "");
            Write(writer, position);
            Write(writer, rotation);
            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>Tells everyone a spawned object of this machine's is gone. Done for it when it's destroyed.</summary>
        internal static void SendDespawn(NetworkObject networked)
        {
            if (!Network.InSession) return;
            byte[] message = new byte[5];
            message[0] = DespawnMessage;
            BitConverter.TryWriteBytes(message.AsSpan(1), networked.NetworkId);
            Network.Broadcast(message, SendMode.Reliable);
        }

        /// <summary>Sends where an object of this machine's is, for <see cref="NetworkTransform"/>.</summary>
        internal static void SendTransform(NetworkObject networked, Vector3 position, Quaternion rotation)
        {
            if (!Network.InSession) return;
            Span<byte> message = stackalloc byte[1 + 4 + 12 + 16];
            message[0] = TransformMessage;
            BitConverter.TryWriteBytes(message[1..], networked.NetworkId);
            WriteFloats(message[5..], position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W);
            Network.Broadcast(message, SendMode.Unreliable);
        }

        private static void WriteFloats(Span<byte> into, params float[] values)
        {
            for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(into[(i * 4)..], values[i]);
        }

        // ---------------------------------------------------------------- Events

        /// <summary>
        /// Sends a network event called <paramref name="name"/>, about <paramref name="target"/> (or nothing), with up to
        /// three values (numbers, true/false, text, vectors, colours, network objects), to <paramref name="to"/>. Offline,
        /// it comes straight back here unless it's for <see cref="NetworkTarget.Others"/>.
        /// </summary>
        public static void SendEvent(string name, GameObject? target, NetworkTarget to, params object?[] values)
        {
            NetworkObject? networked = target?.GetComponent<NetworkObject>();
            bool online = Network.InSession;
            bool here = to switch
            {
                NetworkTarget.Everyone => true,
                NetworkTarget.Others => false,
                NetworkTarget.Owner => networked == null || networked.IsMine,
                _ => Network.IsHost
            };

            if (online && (to != NetworkTarget.Owner || !here) && (to != NetworkTarget.Host || !here))
            {
                byte[] message = EventBytes(name, networked?.NetworkId ?? 0, values);
                switch (to)
                {
                    case NetworkTarget.Owner:
                        Network.Send(networked!.Owner == default ? Network.HostPeer : networked.Owner, message, SendMode.Reliable);
                        break;
                    case NetworkTarget.Host:
                        Network.Send(Network.HostPeer, message, SendMode.Reliable);
                        break;
                    default:
                        Network.Broadcast(message, SendMode.Reliable);
                        break;
                }
            }

            if (here) Deliver(new NetworkEvent(name, target, LocalName, values));
        }

        private static byte[] EventBytes(string name, uint target, object?[] values)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(EventMessage);
            writer.Write(target);
            writer.Write(name);
            writer.Write((byte)Math.Min(values.Length, 8));
            foreach (object? value in values.Take(8))
            {
                switch (value)
                {
                    case float f: writer.Write((byte)1); writer.Write(f); break;
                    case bool b: writer.Write((byte)2); writer.Write(b); break;
                    case string s: writer.Write((byte)3); writer.Write(s); break;
                    case Vector3 v: writer.Write((byte)4); Write(writer, v); break;
                    case Color c: writer.Write((byte)5); writer.Write(c.R); writer.Write(c.G); writer.Write(c.B); writer.Write(c.A); break;
                    case GameObject gameObject: writer.Write((byte)6); writer.Write(gameObject.GetComponent<NetworkObject>()?.NetworkId ?? 0u); break;
                    default: writer.Write((byte)0); break;
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        /// <summary>Gives an event to the visual scripts it's for (those on its object, or every one), then to <see cref="EventReceived"/>.</summary>
        private static void Deliver(NetworkEvent received)
        {
            var outputs = new (string Port, object? Value)[] { ("Name", received.Name), ("From", received.From),
                ("A", received.Values.ElementAtOrDefault(0)), ("B", received.Values.ElementAtOrDefault(1)), ("C", received.Values.ElementAtOrDefault(2)) };

            IEnumerable<VisualScript> scripts = received.Target != null
                ? received.Target.GetComponents<VisualScript>().ToList()
                : Scene.Active.FindComponents<VisualScript>().ToList();
            foreach (VisualScript script in scripts) script.Fire("Network Event", outputs);

            // Then the C# side: each component's OnNetworkEvent, on the object or, for an event about none, everywhere.
            List<Component> components = received.Target != null ? received.Target.Components.ToList() : Scene.Active.FindComponents<Component>().ToList();
            foreach (Component component in components)
            {
                if (component is VisualScript) continue;
                try
                {
                    component.OnNetworkEvent(received);
                }
                catch (Exception exception)
                {
                    Log.Error($"{component.GetType().Name} on {component.GameObject.Name} failed handling network event {received.Name}: {exception.Message}", exception);
                }
            }

            EventReceived?.Invoke(received);
        }

        /// <summary>Starts an event (Connected, Player Joined...) on every visual script in the scene.</summary>
        private static void FireAll(string name, params (string Port, object? Value)[] outputs)
        {
            foreach (VisualScript script in Scene.Active.FindComponents<VisualScript>().ToList()) script.Fire(name, outputs);
        }

        // ---------------------------------------------------------------- Receiving

        private static void OnMessage(PeerId from, ReadOnlySpan<byte> data)
        {
            if (data.Length < 5 || data[0] < FirstMessage) return;

            try
            {
                switch (data[0])
                {
                    case SpawnMessage: ReceiveSpawn(data.ToArray()); break;
                    case DespawnMessage:
                        if (Find(BitConverter.ToUInt32(data[1..])) is { } gone)
                        {
                            gone.Despawned = true;
                            gone.GameObject.Destroy();
                        }

                        break;
                    case TransformMessage:
                        if (data.Length >= 33 && Find(BitConverter.ToUInt32(data[1..])) is { } moved && !moved.IsMine)
                        {
                            Span<float> f = stackalloc float[7];
                            for (int i = 0; i < 7; i++) f[i] = BitConverter.ToSingle(data[(5 + i * 4)..]);
                            moved.GetComponent<NetworkTransform>()?.Receive(new Vector3(f[0], f[1], f[2]), new Quaternion(f[3], f[4], f[5], f[6]));
                        }

                        break;
                    case EventMessage: ReceiveEvent(from, data.ToArray()); break;
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
            {
                Log.Warning($"A network message from {Network.GetName(from)} couldn't be read: {exception.Message}");
            }
        }

        private static void ReceiveSpawn(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1), Encoding.UTF8);
            uint id = reader.ReadUInt32();
            var owner = new PeerId(reader.ReadUInt64());
            bool isPlayer = reader.ReadBoolean();
            string prefabPath = reader.ReadString();
            Vector3 position = ReadVector(reader);
            Quaternion rotation = ReadQuaternion(reader);
            if (Find(id) != null) return;

            if (Assets.FindFile(prefabPath) is not { } file)
            {
                Log.Warning($"Another player made a {Path.GetFileNameWithoutExtension(prefabPath)}, which this copy of the game hasn't got");
                return;
            }

            GameObject made = Assets.LoadPrefab(file).Instantiate(position, rotation);
            NetworkObject networked = made.GetComponent<NetworkObject>() ?? made.AddComponent<NetworkObject>();
            networked.NetworkId = id;
            networked.Owner = owner;
            networked.IsPlayer = isPlayer;
            networked.SpawnedFrom = prefabPath;
            Register(networked);
        }

        private static void ReceiveEvent(PeerId from, byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data, 1, data.Length - 1), Encoding.UTF8);
            uint target = reader.ReadUInt32();
            string name = reader.ReadString();
            int count = reader.ReadByte();
            var values = new object?[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadByte() switch
                {
                    1 => reader.ReadSingle(),
                    2 => reader.ReadBoolean(),
                    3 => reader.ReadString(),
                    4 => ReadVector(reader),
                    5 => new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    6 => Find(reader.ReadUInt32())?.GameObject,
                    _ => null
                };
            }

            // An event about an object this machine no longer has is dropped.
            GameObject? about = target != 0 ? Find(target)?.GameObject : null;
            if (target != 0 && about == null) return;
            Deliver(new NetworkEvent(name, about, Network.GetName(from), values));
        }

        private static void OnPeerJoined(PeerId peer)
        {
            // Someone new gets everything this machine has made, where it is now.
            foreach (NetworkObject networked in Objects.ToList())
            {
                if (networked.SpawnedFrom == null || networked.Owner != Network.LocalPeer) continue;
                Transform transform = networked.Transform;
                Network.Send(peer, SpawnBytes(networked, transform.WorldPosition, transform.WorldRotation), SendMode.Reliable);
            }

            FireAll("Player Joined", ("Player", Network.GetName(peer)));
        }

        private static void OnPeerLeft(PeerId peer)
        {
            foreach (NetworkObject networked in Objects.Where(o => o.Owner == peer && o.SpawnedFrom != null).ToList())
            {
                networked.Despawned = true;
                networked.GameObject.Destroy();
            }

            FireAll("Player Left", ("Player", Network.GetName(peer)));
        }

        private static void OnSessionStarted() => FireAll("Connected");

        private static void OnSessionEnded()
        {
            // What others made goes with them.
            foreach (NetworkObject networked in Objects.Where(o => o.SpawnedFrom != null && o.Owner != Network.LocalPeer).ToList())
            {
                networked.Despawned = true;
                networked.GameObject.Destroy();
            }

            FireAll("Disconnected");
        }

        // ---------------------------------------------------------------- Reading and writing

        private static void Write(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        private static void Write(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.X);
            writer.Write(q.Y);
            writer.Write(q.Z);
            writer.Write(q.W);
        }

        private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static Quaternion ReadQuaternion(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
