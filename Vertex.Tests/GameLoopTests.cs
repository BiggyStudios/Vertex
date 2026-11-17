namespace Vertex.Tests
{
    public class GameLoopTests : EngineTest
    {
        [Fact]
        public void Leftover_time_carries_into_the_next_frame()
        {
            Time.FixedDeltaTime = 1f / 50f;
            var game = new EmptyGame();
            var loop = new GameLoop(game);
            loop.Load();

            Recorder recorder = new GameObject().AddComponent<Recorder>();

            // Not yet a whole step. (Times stay clear of whole steps, so rounding can't decide the count.)
            RunFrames(loop, 1, deltaTime: 0.001);
            Assert.Equal(0, recorder.FixedUpdates);

            // 0.051 seconds in all: two steps, and 0.011 left over, a bit over half a step...
            RunFrames(loop, 1, deltaTime: 0.05);
            Assert.Equal(2, recorder.FixedUpdates);
            Assert.InRange(Time.FixedAlpha, 0.5f, 0.6f);

            // ...which makes up a third step with the next 0.01.
            RunFrames(loop, 1, deltaTime: 0.01);
            Assert.Equal(3, recorder.FixedUpdates);

            Assert.Equal(1, game.Loads);
            Assert.Equal(3, game.Updates);
        }

        [Fact]
        public void A_long_frame_only_catches_up_a_quarter_second()
        {
            Time.FixedDeltaTime = 1f / 50f;
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            Recorder recorder = new GameObject().AddComponent<Recorder>();
            RunFrames(loop, 1, deltaTime: 0.001);

            RunFrames(loop, 1, deltaTime: 2.0);

            Assert.Equal(12, recorder.FixedUpdates);
        }

        [Fact]
        public void The_game_can_switch_scenes_in_its_own_update()
        {
            var level = new Scene();
            Recorder recorder = new GameObject("In level", level).AddComponent<Recorder>();
            var game = new SwitchingGame(level);
            var loop = new GameLoop(game);
            loop.Load();

            RunFrames(loop, 1);

            Assert.Same(level, Scene.Active);
            Assert.Equal(["Start", "Update"], recorder.Calls);
        }

        [Fact]
        public void Application_quit_reaches_the_host()
        {
            int quits = 0;
            Action onQuit = () => quits++;
            Application.QuitRequested += onQuit;

            try
            {
                Application.Quit();
            }
            finally
            {
                Application.QuitRequested -= onQuit;
            }

            Assert.Equal(1, quits);
        }

        private sealed class SwitchingGame(Scene next) : Game
        {
            public override void Update() => Scene.Active = next;
        }
    }
}
