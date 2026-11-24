using System.Net;

using Vertex.Networking;

namespace Vertex.Tests
{
    /// <summary>A network manager finding rooms to list, and joining the one picked: what a game's lobby screen does.</summary>
    public class NetworkManagerTests : EngineTest
    {
        public override void Dispose()
        {
            Network.Stop();
            base.Dispose();
        }

        [Fact]
        public void Rooms_on_the_local_network_can_be_listed_and_one_picked_to_join()
        {
            var manager = new GameObject("Network").AddComponent<NetworkManager>();
            manager.Service = NetworkService.LocalNetwork;
            manager.SpawnWhenOffline = false;
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 2);

            var lists = new List<IReadOnlyList<RoomInfo>>();
            manager.RoomsFound += rooms => lists.Add(rooms);
            manager.FindRooms();
            Assert.True(manager.Searching);

            // The search takes a moment: the list comes in a frame after it's done, and nothing's joined.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (manager.Searching && waited.Elapsed.TotalSeconds < 6)
            {
                RunFrames(loop, 1);
                Thread.Sleep(20);
            }

            Assert.False(manager.Searching);
            Assert.Single(lists);
            Assert.Same(manager.Rooms, lists[0]);
            Assert.Null(Network.Transport);

            // Picking one joins it the local network's way.
            var room = new RoomInfo("Kitchen", 1, 4, false) { EndPoint = new IPEndPoint(IPAddress.Loopback, UdpTransport.DefaultPort + 7) };
            manager.Join(room);
            Assert.IsType<UdpTransport>(Network.Transport);
            Assert.Equal("Joining Kitchen...", manager.Status);
        }
    }
}
