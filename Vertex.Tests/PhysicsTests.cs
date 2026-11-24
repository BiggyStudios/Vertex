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
        public void A_ray_sees_through_colliders_it_is_told_to_skip()
        {
            GameObject ground = Ground();
            var crate = new GameObject("Crate");
            crate.Transform.Position = new Vector3(0f, 1f, 0f);
            crate.AddComponent<BoxCollider>();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 3);

            Assert.True(Scene.Active.Physics.Raycast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 100f, out RaycastHit hit));
            Assert.Same(crate, hit.GameObject);

            Assert.True(Scene.Active.Physics.Raycast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 100f, out hit, c => c.GameObject != crate));
            Assert.Same(ground, hit.GameObject);
            Assert.Equal(5f, hit.Distance, 0.01f);

            // Rays after it are as before.
            Assert.True(Scene.Active.Physics.Raycast(new Vector3(0f, 5f, 0f), -Vector3.UnitY, 100f, out hit));
            Assert.Same(crate, hit.GameObject);
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

        /// <summary>Moves a character the way games usually do: gravity in the air, a small push down on the ground to keep it there.</summary>
        private sealed class Walker : Component
        {
            public Vector3 Walk { get; set; }

            protected internal override void FixedUpdate()
            {
                CharacterController character = GetComponent<CharacterController>()!;
                float rise = character.IsGrounded ? -2f : character.Velocity.Y - 17f * Time.FixedDeltaTime;
                character.Velocity = new Vector3(Walk.X, rise, Walk.Z);
                character.Move(Time.FixedDeltaTime);
            }
        }

        /// <summary>
        /// Moves a character the way a first-person game does: it keeps its speed from the last step and speeds up
        /// towards where it's heading, so whatever the character loses to what it touches it has to win back.
        /// </summary>
        private sealed class Runner : Component
        {
            public Vector3 Heading { get; set; }

            protected internal override void FixedUpdate()
            {
                CharacterController character = GetComponent<CharacterController>()!;
                var speed = new Vector3(character.Velocity.X, 0f, character.Velocity.Z);
                Vector3 change = Heading - speed;
                float step = 60f * Time.FixedDeltaTime;
                speed = change.Length() <= step ? Heading : speed + Vector3.Normalize(change) * step;
                float rise = character.IsGrounded ? -2f : character.Velocity.Y - 17f * Time.FixedDeltaTime;
                character.Velocity = new Vector3(speed.X, rise, speed.Z);
                character.Move(Time.FixedDeltaTime);
            }
        }

        [Fact]
        public void A_character_keeping_its_speed_walks_up_a_walkable_slope_nearly_as_fast_as_on_the_flat()
        {
            float Travelled(float degrees)
            {
                Scene.Active = new Scene();
                Ramp(degrees);
                var character = new GameObject("Character");
                character.Transform.Position = new Vector3(0f, 0.6f, 0f);
                character.AddComponent<CharacterController>();
                Runner runner = character.AddComponent<Runner>();
                var loop = new GameLoop(new EmptyGame());
                loop.Load();
                RunFrames(loop, 40);
                Vector3 start = character.Transform.Position;
                runner.Heading = new Vector3(0f, 0f, -5f);
                RunFrames(loop, 60);
                return Vector3.Distance(start, character.Transform.Position);
            }

            float flat = Travelled(0f);
            float uphill = Travelled(34f);

            Assert.True(flat > 4.5f, $"On the flat it went {flat:0.00} m");
            Assert.True(uphill > flat * 0.8f, $"Up a 34° slope it went {uphill:0.00} m in a second, on the flat {flat:0.00} m");
        }

        [Fact]
        public void A_character_running_into_a_wall_loses_its_speed_into_it()
        {
            Ground();
            var wall = new GameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 1f, -2f);
            wall.AddComponent<BoxCollider>().Size = new Vector3(10f, 2f, 0.5f);
            var character = new GameObject("Character");
            character.AddComponent<CharacterController>();
            Runner runner = character.AddComponent<Runner>();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 20);

            // Running at the wall at an angle: the part into it goes, the part along it stays.
            runner.Heading = new Vector3(3f, 0f, -4f);
            RunFrames(loop, 60);

            CharacterController controller = character.GetComponent<CharacterController>()!;
            Assert.True(character.Transform.Position.Z > -1.8f, "It should have stopped at the wall");
            Assert.True(MathF.Abs(controller.Velocity.Z) < 0.5f, $"The speed into the wall should be gone: {controller.Velocity}");
            Assert.True(controller.Velocity.X > 2.5f, $"The speed along the wall should stay: {controller.Velocity}");
        }

        /// <summary>A ramp <paramref name="degrees"/> steep, rising towards -Z, with its middle at the origin.</summary>
        private static void Ramp(float degrees)
        {
            var ramp = new GameObject("Ramp");
            ramp.Transform.Position = new Vector3(0f, -0.5f, 0f);
            ramp.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f);
            ramp.AddComponent<BoxCollider>().Size = new Vector3(20f, 1f, 40f);
        }

        [Fact]
        public void A_character_standing_on_a_walkable_slope_does_not_slide_down_it()
        {
            Ramp(25f);
            var character = new GameObject("Character");
            character.Transform.Position = new Vector3(0f, 0.3f, 0f);
            character.AddComponent<CharacterController>();
            character.AddComponent<Walker>();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 40);
            Assert.True(character.GetComponent<CharacterController>()!.IsGrounded);
            Vector3 settled = character.Transform.Position;

            RunFrames(loop, 180);

            Vector3 moved = character.Transform.Position - settled;
            Assert.True(new Vector2(moved.X, moved.Z).Length() < 0.02f, $"It slid {moved.Length():0.00} m standing still");
        }

        [Fact]
        public void A_character_still_slides_down_a_slope_too_steep_to_stand_on()
        {
            Ramp(60f);
            var character = new GameObject("Character");
            character.Transform.Position = new Vector3(0f, 2f, 0f);
            character.AddComponent<CharacterController>();
            character.AddComponent<Walker>();
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 180);

            Assert.True(character.Transform.Position.Z > 1f, $"It should have slid down, towards +Z: it is at {character.Transform.Position}");
        }
    }
}
