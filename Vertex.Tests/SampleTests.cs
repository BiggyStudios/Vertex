using System.Numerics;

using Vertex.Input;
using Vertex.Networking;
using Vertex.Rendering;
using Vertex.Scripting;
using Vertex.UI;

namespace Vertex.Tests
{
    /// <summary>The samples the documentation's tutorials follow, played headless: they still work as the tutorials say.</summary>
    public class SampleTests : EngineTest
    {
        private readonly string[] _assetFolders = Application.AssetFolders;
        private readonly List<LogEntry> _logged = new();

        public SampleTests() => Log.Written += _logged.Add;

        public override void Dispose()
        {
            Log.Written -= _logged.Add;
            Network.Stop();
            InputSystem.CursorLocked = false;
            Application.AssetFolders = _assetFolders;
            base.Dispose();
        }

        /// <summary>The engine repository's folder: the one above this file's.</summary>
        private static string RepoFolder([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

        [Fact]
        public void The_code_only_sample_builds_its_world_and_counts_coins()
        {
            var game = new CoinGame();
            var loop = new GameLoop(game);
            loop.Load();
            RunFrames(loop, 2);

            Assert.Equal(CoinGame.CoinCount, Scene.Active.GameObjects.Count(o => o.Name == "Coin"));
            GameObject ball = GameObject.Find("Ball")!;

            // A coin where the ball is: picked up.
            Coin.Make(ball.Transform.Position);
            RunFrames(loop, 10);
            Assert.Equal(1, CoinGame.Score);

            // Rolling: W pushes the ball away from the camera.
            float z = ball.Transform.Position.Z;
            InputSystem.PressKey(Key.W);
            RunFrames(loop, 60);
            InputSystem.ReleaseAll();
            Assert.True(ball.Transform.Position.Z < z - 0.5f, $"The ball didn't roll: {z} to {ball.Transform.Position.Z}");
            Assert.True(game.TimeLeft < CoinGame.TimeLimit);
        }

        [Theory]
        [InlineData("Scenes/Duel (CSharp).scene")]
        [InlineData("Scenes/Duel (Visual Scripting).scene")]
        public void The_duel_walks_shoots_and_scores_playing_alone(string scene)
        {
            Application.AssetFolders = [Path.Combine(RepoFolder(), "Samples", "Duel", "Assets")];
            var loop = new GameLoop(new Game { StartScene = scene });
            loop.Load();
            RunFrames(loop, 3);

            // Alone, the network manager gives this machine its player at the first spawn point.
            GameObject player = NetworkManager.Instance!.LocalPlayer!;
            Assert.Equal(new Vector3(0f, 0f, 15f), player.Transform.Position, new Vector3Comparer(0.3f));
            Assert.Empty(_logged.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message));

            // W walks forwards (towards the middle).
            InputSystem.PressKey(Key.W);
            RunFrames(loop, 30);
            InputSystem.ReleaseAll();
            RunFrames(loop, 2);
            float walkedTo = player.Transform.Position.Z;
            Assert.True(walkedTo < 14f, $"The player didn't walk: at {walkedTo}");

            // Someone to shoot, standing in front. Playing alone it would count as this machine's too, so it's made like
            // another machine's player: its camera off, and nothing moving or shooting it here.
            Prefab prefab = NetworkManager.Instance.PlayerPrefab!;
            GameObject other = prefab.Instantiate(new Vector3(0f, 0f, walkedTo - 6f));
            other.Transform.Children.First(c => c.GameObject.Name == "Head").GameObject.GetComponent<Camera>()!.Enabled = false;
            foreach (Component component in other.Components)
            {
                if (component is Duel.PlayerMovement or Duel.PlayerGun) component.Enabled = false;
                if (component is VisualScript { Script.Name: "Player Movement" or "Player Gun" } script) script.Enabled = false;
            }
            RunFrames(loop, 3);
            Assert.Equal(100f, HealthOf(other));

            // Click to take the mouse, then hold the button: shots hit, four take all their health, and it's a point.
            InputSystem.PressButton(MouseButton.Left);
            RunFrames(loop, 1);
            Assert.True(InputSystem.CursorLocked);
            RunFrames(loop, 30);
            Assert.True(HealthOf(other) < 100f, "No shot hit");

            RunFrames(loop, 60);
            InputSystem.ReleaseAll();
            RunFrames(loop, 2);
            Assert.Equal("1 - 0", TextOf("Score"));
            Assert.Equal("Health 100", TextOf("Health"));

            // The one who lost came back, full, at a spawn point.
            Assert.True(MathF.Abs(other.Transform.Position.Z) > 14f, $"Not respawned: at {other.Transform.Position}");
            Assert.Equal(100f, HealthOf(other), 0.01f);
            Assert.Empty(_logged.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message));
        }

        private static float HealthOf(GameObject player)
        {
            if (player.GetComponent<Duel.Health>() is { } health) return health.Current;
            object? value = player.GetComponents<VisualScript>().Select(s => s.GetVariable("Health")).FirstOrDefault(v => v != null);
            return value is float f ? f : float.NaN;
        }

        private static string TextOf(string name) => GameObject.Find(name)?.GetComponent<UIText>()?.Text ?? "";

        private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
            public int GetHashCode(Vector3 v) => 0;
        }
    }
}
