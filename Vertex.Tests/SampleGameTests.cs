using System.Numerics;

using SampleGame;

using Vertex.Input;

namespace Vertex.Tests
{
    /// <summary>
    /// Loads the whole sample shooter and plays it for a while without a window: the check that engine changes
    /// haven't broken a real game. Steam stays off, so it runs anywhere.
    /// </summary>
    public class SampleGameTests : EngineTest
    {
        [Fact]
        public void The_sample_game_loads_and_plays()
        {
            var loop = new GameLoop(new global::SampleGame.SampleGame());
            loop.Load();

            // Found by name: until the first frame they're still waiting to join the scene, where only Find looks.
            GameObject multiplayer = Scene.Active.Find("Multiplayer")!;
            multiplayer.GetComponent<MultiplayerManager>()!.ConnectToSteamOnStart = false;
            multiplayer.GetComponent<MainMenu>()!.OpenOnStart = false;
            var player = (Player)Scene.Active.Find("Player")!;

            // Straight into practice: land on the ground first.
            RunFrames(loop, 60);
            Vector3 start = player.Transform.Position;

            // Then walk forward for two seconds, shooting.
            InputSystem.PressKey(Key.W);
            for (int i = 0; i < 120; i++)
            {
                if (i % 20 == 0) InputSystem.PressButton(MouseButton.Left);
                if (i % 20 == 10) InputSystem.ReleaseButton(MouseButton.Left);
                RunFrames(loop, 1);
            }
            InputSystem.ReleaseKey(Key.W);
            RunFrames(loop, 30);

            Vector3 end = player.Transform.Position;
            float walked = Vector2.Distance(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z));

            Assert.False(player.IsDestroyed);
            Assert.True(walked > 2f, $"Holding W for two seconds should walk somewhere, but went {walked} m ({start} to {end}).");
            Assert.InRange(end.Y, -0.1f, 0.5f);
        }
    }
}
