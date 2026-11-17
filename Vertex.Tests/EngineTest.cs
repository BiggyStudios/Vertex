using Vertex.Input;
using Vertex.Rendering;

// The engine keeps its state in statics (the active scene, input, time), so tests mustn't run at the same time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Vertex.Tests
{
    /// <summary>Starts each test with a fresh scene and nothing pressed, and drives frames without a window.</summary>
    public abstract class EngineTest : IDisposable
    {
        protected EngineTest()
        {
            Scene.Active = new Scene();
            Camera.Main = null;
            InputSystem.Reset();
            Time.FixedDeltaTime = 1f / 60f;
        }

        /// <summary>Runs <paramref name="frames"/> frames of <paramref name="game"/>, each <paramref name="deltaTime"/> seconds long.</summary>
        protected static void RunFrames(GameLoop loop, int frames, double deltaTime = 1.0 / 60.0)
        {
            for (int i = 0; i < frames; i++)
            {
                loop.Update(deltaTime, 1280, 720, 720);
            }
        }

        public virtual void Dispose()
        {
            Scene.Active.Unload();
            Camera.Main = null;
            InputSystem.Reset();
            Time.FixedDeltaTime = 1f / 60f;
        }
    }

    /// <summary>A game that does nothing itself, for tests that build their scene by hand.</summary>
    internal sealed class EmptyGame : Game
    {
        public int Loads;
        public int Updates;

        public override void Load() => Loads++;
        public override void Update() => Updates++;
    }

    /// <summary>Records which of its lifecycle methods ran, in order.</summary>
    internal sealed class Recorder : Component
    {
        public readonly List<string> Calls = new();
        public int FixedUpdates;

        protected internal override void Start() => Calls.Add("Start");
        protected internal override void Update() => Calls.Add("Update");
        protected internal override void FixedUpdate() => FixedUpdates++;
        protected internal override void OnDestroy() => Calls.Add("OnDestroy");
    }
}
