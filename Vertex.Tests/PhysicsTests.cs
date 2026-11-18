using System.Numerics;

using Vertex.Physics;

namespace Vertex.Tests
{
    public class PhysicsTests : EngineTest
    {
        private static GameObject Ground()
        {
            // A box whose top is at y = 0.
            var ground = new GameObject("Ground");
            ground.Transform.Position = new Vector3(0f, -0.5f, 0f);
            ground.AddComponent<BoxCollider>().Size = new Vector3(20f, 1f, 20f);
            return ground;
        }

        [Fact]
        public void A_ray_down_hits_the_top_of_the_ground()
        {
            GameObject ground = Ground();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 3);

            bool hit = Scene.Active.Physics.Raycast(new Vector3(1f, 5f, 2f), -Vector3.UnitY, 100f, out RaycastHit result);

            Assert.True(hit);
            Assert.Same(ground, result.GameObject);
            Assert.Equal(5f, result.Distance, 0.01f);
            Assert.Equal(0f, result.Point.Y, 0.01f);
            Assert.True(Vector3.Dot(result.Normal, Vector3.UnitY) > 0.99f);
        }

        [Fact]
        public void A_ray_that_misses_hits_nothing()
        {
            Ground();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 3);

            Assert.False(Scene.Active.Physics.Raycast(new Vector3(0f, 5f, 0f), Vector3.UnitY, 100f, out _));
        }

        [Fact]
        public void A_rigidbody_falls_and_comes_to_rest_on_the_ground()
        {
            Ground();
            var ball = new GameObject("Ball");
            ball.Transform.Position = new Vector3(0f, 3f, 0f);
            ball.AddComponent<SphereCollider>();
            ball.AddComponent<Rigidbody>();

            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 20);
            Assert.True(ball.Transform.Position.Y < 2.9f, "It should be falling.");

            RunFrames(loop, 300);

            // A sphere of radius 0.5 resting on y = 0.
            Assert.Equal(0.5f, ball.Transform.Position.Y, 0.05f);
        }

        [Fact]
        public void Unloading_the_scene_frees_its_physics()
        {
            Ground();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 3);

            Scene.Active.Unload();
            RunFrames(loop, 3);

            Assert.False(Scene.Active.Physics.Raycast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 100f, out _));
        }

        [Fact]
        public void A_character_moved_only_by_its_transform_can_still_be_hit()
        {
            // Another machine's player: its Transform is set from the network, and it never moves itself.
            var remote = new GameObject("Remote");
            remote.AddComponent<CharacterController>();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 2);

            Assert.True(Scene.Active.Physics.Raycast(new Vector3(0f, 1f, 5f), -Vector3.UnitZ, 20f, out RaycastHit hit));
            Assert.Same(remote, hit.GameObject);

            // It's moved somewhere else: rays find it there, and not where it was.
            remote.Transform.Position = new Vector3(10f, 0f, 0f);
            RunFrames(loop, 2);
            Assert.False(Scene.Active.Physics.Raycast(new Vector3(0f, 1f, 5f), -Vector3.UnitZ, 20f, out _));
            Assert.True(Scene.Active.Physics.Raycast(new Vector3(10f, 1f, 5f), -Vector3.UnitZ, 20f, out hit));
            Assert.Same(remote, hit.GameObject);
            Assert.Equal(new Vector3(10f, 0f, 0f), remote.Transform.Position);
        }
    }
}
