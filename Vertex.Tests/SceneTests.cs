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

        /// <summary>Destroys what it made when it's destroyed itself, as a component cleaning up after itself does.</summary>
        private sealed class Owner : Component
        {
            public GameObject? Made { get; set; }

            protected internal override void OnDestroy() => Made?.Destroy();
        }

        [Fact]
        public void What_an_objects_OnDestroy_destroys_goes_too()
        {
            var owner = new GameObject("Owner");
            var made = new GameObject("Made elsewhere");
            owner.AddComponent<Owner>().Made = made;
            Scene.Active.Update();

            owner.Destroy();
            Scene.Active.Update();

            Assert.True(made.IsDestroyed);
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

        [Fact]
        public void Finding_components_sees_what_joins_and_leaves_the_scene_and_what_is_switched_off()
        {
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.Transform.Parent = parent.Transform;
            Rendering.Light light = child.AddComponent<Rendering.Light>();

            // Made this frame: found from the next.
            Assert.Empty(Scene.Active.FindComponents<Rendering.Light>());
            Scene.Active.Update();
            Assert.Same(light, Scene.Active.FindComponent<Rendering.Light>());

            // Added to an object already in the scene, a subclass is found as its base type straight away.
            Physics.BoxCollider box = parent.AddComponent<Physics.BoxCollider>();
            Assert.Same(box, Scene.Active.FindComponent<Physics.Collider>());

            // Off, or under something off, it isn't found; on again, it is.
            light.Enabled = false;
            Assert.Empty(Scene.Active.FindComponents<Rendering.Light>());
            light.Enabled = true;
            parent.Active = false;
            Assert.Empty(Scene.Active.FindComponents<Rendering.Light>());
            parent.Active = true;
            Assert.Same(light, Scene.Active.FindComponent<Rendering.Light>());

            // Moved out from under the switched-off parent, it's found again.
            parent.Active = false;
            child.Transform.Parent = null;
            Assert.Same(light, Scene.Active.FindComponent<Rendering.Light>());

            // Taken off, or its object destroyed, it's gone.
            parent.RemoveComponent(box);
            Assert.Empty(Scene.Active.FindComponents<Physics.Collider>());
            child.Destroy();
            Scene.Active.Update();
            Assert.Empty(Scene.Active.FindComponents<Rendering.Light>());
        }

        [Fact]
        public void Components_found_stay_right_after_hundreds_come_and_go()
        {
            var kept = new List<Rendering.Light>();
            for (int round = 0; round < 4; round++)
            {
                var made = new List<GameObject>();
                for (int i = 0; i < 200; i++)
                {
                    var gameObject = new GameObject($"Light {round} {i}");
                    Rendering.Light light = gameObject.AddComponent<Rendering.Light>();
                    if (i % 10 == 0) kept.Add(light);
                    else made.Add(gameObject);
                }

                Scene.Active.Update();
                foreach (GameObject gameObject in made) gameObject.Destroy();
                Scene.Active.Update();
            }

            // Hundreds gone (enough to tidy the lists), and only the kept ones found, in the order they came.
            Assert.Equal(kept, Scene.Active.FindComponents<Rendering.Light>());
        }

        [Fact]
        public void Objects_with_nothing_to_run_are_skipped_but_ones_given_code_later_run_it()
        {
            var scenery = new GameObject("Scenery");
            scenery.AddComponent<Rendering.MeshRenderer>();
            Scene.Active.Update();

            // A component with code of its own, added to scenery already in the scene, starts and updates.
            Recorder recorder = scenery.AddComponent<Recorder>();
            Scene.Active.Update();
            Assert.Equal(["Start", "Update"], recorder.Calls);
        }
    }
}
