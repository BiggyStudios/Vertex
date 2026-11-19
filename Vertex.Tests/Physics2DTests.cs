using System.Numerics;

using Vertex.Physics;

namespace Vertex.Tests
{
    /// <summary>2D physics: bodies kept to the X-Y plane, with flat colliders.</summary>
    public class Physics2DTests : EngineTest
    {
        private static GameObject Ground2D(float y = 0f, float tilt = 0f)
        {
            var ground = new GameObject("Ground");
            ground.Transform.Position = new Vector3(0f, y - 0.5f, 0f);
            ground.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, tilt);
            ground.AddComponent<BoxCollider2D>().Size = new Vector2(40f, 1f);
            return ground;
        }

        private static GameLoop Run(int frames)
        {
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, frames);
            return loop;
        }

        [Fact]
        public void A_2D_box_lands_on_the_ground_and_never_leaves_the_plane_or_tips_out_of_it()
        {
            Ground2D();
            var crate = new GameObject("Crate");
            crate.Transform.Position = new Vector3(0f, 3f, 0f);
            crate.AddComponent<BoxCollider2D>();
            Rigidbody2D body = crate.AddComponent<Rigidbody2D>();

            // Knocked sideways, off its middle and out of the picture: only what's in the picture counts.
            body.Velocity = new Vector3(2f, 0f, 5f);
            body.AngularVelocity = new Vector3(4f, 4f, 1f);

            Run(240);

            Assert.Equal(0f, crate.Transform.Position.Z, 0.001f);
            Assert.Equal(0.5f, crate.Transform.Position.Y, 0.05f);
            Vector3 forward = crate.Transform.Forward;
            Assert.True(MathF.Abs(forward.Z + 1f) < 1e-3f, $"It tipped out of the picture: forward is {forward}.");
        }

        [Fact]
        public void A_2D_ball_rolls_down_a_slope_turning_about_z()
        {
            Ground2D(tilt: -0.3f);
            var ball = new GameObject("Ball");
            ball.Transform.Position = new Vector3(0f, 1f, 0f);
            ball.AddComponent<CircleCollider2D>();
            Rigidbody2D body = ball.AddComponent<Rigidbody2D>();

            Run(120);

            Assert.True(ball.Transform.Position.X > 1f, "It should roll down the slope, to the right.");
            Assert.True(body.AngularVelocity2D < -0.5f, $"Rolling right, it turns clockwise: {body.AngularVelocity2D}.");
            Assert.Equal(0f, body.AngularVelocity.X, 0.001f);
            Assert.Equal(0f, ball.Transform.Position.Z, 0.001f);
        }

        [Fact]
        public void Two_2D_shapes_meet_even_a_little_apart_in_depth()
        {
            Ground2D();
            var ball = new GameObject("Ball");
            ball.Transform.Position = new Vector3(0f, 3f, 0.3f);
            ball.AddComponent<CircleCollider2D>();
            ball.AddComponent<Rigidbody2D>();

            Run(180);

            Assert.Equal(0.5f, ball.Transform.Position.Y, 0.05f);
        }

        [Fact]
        public void A_2D_capsule_stands_on_the_ground_by_its_rounded_end()
        {
            Ground2D();
            var player = new GameObject("Player");
            player.Transform.Position = new Vector3(0f, 3f, 0f);
            player.AddComponent<CapsuleCollider2D>();
            Rigidbody2D body = player.AddComponent<Rigidbody2D>();
            body.LockRotation = Axes.Z;

            Run(180);

            Assert.Equal(1f, player.Transform.Position.Y, 0.05f);
            Assert.Equal(Quaternion.Identity, player.Transform.Rotation);
        }

        [Fact]
        public void A_2D_raycast_finds_what_it_crosses_in_the_plane()
        {
            GameObject ground = Ground2D();
            Run(2);

            Assert.True(Scene.Active.Physics.Raycast(new Vector2(3f, 5f), -Vector2.UnitY, 20f, out RaycastHit hit));
            Assert.Same(ground, hit.GameObject);
            Assert.Equal(5f, hit.Distance, 0.01f);
            Assert.False(Scene.Active.Physics.Raycast(new Vector2(3f, 5f), Vector2.UnitY, 20f, out _));
        }
    }
}
