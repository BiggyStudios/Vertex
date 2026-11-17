namespace Vertex.Tests
{
    public class SceneTests : EngineTest
    {
        [Fact]
        public void Start_runs_once_before_the_first_update()
        {
            Recorder recorder = new GameObject().AddComponent<Recorder>();

            Scene.Active.Update();
            Scene.Active.Update();

            Assert.Equal(["Start", "Update", "Update"], recorder.Calls);
        }

        [Fact]
        public void Destroy_waits_for_the_end_of_the_frame_and_takes_children_with_it()
        {
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.Transform.Parent = parent.Transform;
            Recorder childRecorder = child.AddComponent<Recorder>();
            Scene.Active.Update();

            parent.Destroy();
            Assert.False(parent.IsDestroyed);

            Scene.Active.Update();

            Assert.True(parent.IsDestroyed);
            Assert.True(child.IsDestroyed);
            Assert.Equal("OnDestroy", childRecorder.Calls[^1]);
            Assert.Empty(Scene.Active.GameObjects);
        }

        [Fact]
        public void Inactive_objects_do_not_update()
        {
            var gameObject = new GameObject();
            Recorder recorder = gameObject.AddComponent<Recorder>();
            gameObject.Active = false;

            Scene.Active.Update();

            Assert.Empty(recorder.Calls);
        }

        [Fact]
        public void Objects_can_be_built_in_a_scene_that_is_not_active()
        {
            var level = new Scene();
            var inLevel = new GameObject("In level", level);
            var inActive = new GameObject("In active");

            Assert.Same(level, inLevel.Scene);
            Assert.Same(Scene.Active, inActive.Scene);
            Assert.Null(Scene.Active.Find("In level"));
            Assert.Same(inLevel, level.Find("In level"));
        }

        [Fact]
        public void Unload_destroys_everything_straight_away()
        {
            var gameObject = new GameObject();
            Recorder recorder = gameObject.AddComponent<Recorder>();
            Scene.Active.Update();
            var notStartedYet = new GameObject();

            Scene.Active.Unload();

            Assert.True(gameObject.IsDestroyed);
            Assert.True(notStartedYet.IsDestroyed);
            Assert.Equal("OnDestroy", recorder.Calls[^1]);
            Assert.Empty(Scene.Active.GameObjects);
            Assert.Null(Scene.Active.Find("GameObject"));
        }

        [Fact]
        public void Switching_scenes_switches_what_updates()
        {
            Recorder first = new GameObject().AddComponent<Recorder>();
            Scene firstScene = Scene.Active;

            var secondScene = new Scene();
            Recorder second = new GameObject("Second", secondScene).AddComponent<Recorder>();
            Scene.Active = secondScene;
            Scene.Active.Update();
            firstScene.Unload();

            Assert.Equal(["OnDestroy"], first.Calls);
            Assert.Equal(["Start", "Update"], second.Calls);
        }
    }
}
