using System.Numerics;

using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.Tests
{
    /// <summary>A sphere cast, and the spring arm that holds a third-person camera out behind a character with one.</summary>
    public class SpringArmTests : EngineTest
    {
        private readonly GameLoop _loop = new(new EmptyGame());

        private static GameObject Box(Vector3 position, Vector3 size)
        {
            var box = new GameObject("Box");
            box.Transform.Position = position;
            box.AddComponent<BoxCollider>().Size = size;
            return box;
        }

        /// <summary>A character standing at the middle, with a camera on an arm as its child, set to stay still while it settles.</summary>
        private (GameObject Character, SpringArm Arm) Character(Action<SpringArm>? setUp = null)
        {
            var character = new GameObject("Character");
            character.AddComponent<CharacterController>();
            var camera = new GameObject("Camera");
            camera.Transform.Parent = character.Transform;
            camera.AddComponent<Camera>();
            SpringArm arm = camera.AddComponent<SpringArm>();
            arm.TurnWithMouse = false;
            arm.LockCursor = false;
            arm.FollowLag = 0f;
            arm.Pitch = 0f;
            setUp?.Invoke(arm);
            return (character, arm);
        }

        [Fact]
        public void A_sphere_cast_stops_where_the_ball_first_touches_and_catches_what_a_ray_would_miss()
        {
            Box(new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
            Box(new Vector3(3f, 2f, 0f), new Vector3(1f, 1f, 1f));
            _loop.Load();
            RunFrames(_loop, 3);

            // Down onto the ground: the ball's centre stops its radius above it.
            Assert.True(Scene.Active.Physics.SphereCast(new Vector3(0f, 5f, 0f), 0.5f, -Vector3.UnitY, 10f, out RaycastHit down));
            Assert.Equal(4.5f, down.Distance, 0.02f);
            Assert.Equal(0f, down.Point.Y, 0.02f);
            Assert.True(Vector3.Dot(down.Normal, Vector3.UnitY) > 0.99f, $"{down.Normal}");

            // Past the box's top edge: a ray goes over it, a thick enough ball doesn't.
            var over = new Vector3(0f, 2.8f, 0f);
            Assert.False(Scene.Active.Physics.Raycast(over, Vector3.UnitX, 10f, out _));
            Assert.True(Scene.Active.Physics.SphereCast(over, 0.5f, Vector3.UnitX, 10f, out RaycastHit edge));
            Assert.InRange(edge.Distance, 2f, 2.5f);

            // Starting out in the ground: touching at once.
            Assert.True(Scene.Active.Physics.SphereCast(new Vector3(0f, 0.2f, 0f), 0.5f, Vector3.UnitX, 5f, out RaycastHit inside));
            Assert.Equal(0f, inside.Distance, 0.01f);
        }

        [Fact]
        public void The_arm_holds_the_camera_behind_and_above_what_it_follows_looking_the_same_way()
        {
            (GameObject character, SpringArm arm) = Character();

            // Before the game runs, where the editor shows it'll be.
            (Vector3 pivot, Vector3 end) = arm.Resting();
            Assert.Equal(new Vector3(0f, 1.6f, 0f), pivot, new Vector3Comparer(0.001f));
            Assert.Equal(new Vector3(0f, 1.6f, 4f), end, new Vector3Comparer(0.001f));

            _loop.Load();
            RunFrames(_loop, 3);

            Assert.Equal(new Vector3(0f, 1.6f, 4f), arm.Transform.WorldPosition, new Vector3Comparer(0.01f));
            Assert.True(Vector3.Dot(arm.Transform.Forward, -Vector3.UnitZ) > 0.999f);

            // Turned to face +X, without the mouse it swings round behind, taking a moment.
            character.Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2f);
            RunFrames(_loop, 2);
            Assert.True(arm.Transform.WorldPosition.X > -3.9f, "It shouldn't swing round at once");
            RunFrames(_loop, 120);
            Assert.Equal(new Vector3(-4f, 1.6f, 0f), arm.Transform.WorldPosition, new Vector3Comparer(0.05f));
            Assert.Equal(Vector3.UnitX, arm.FlatForward, new Vector3Comparer(0.01f));
        }

        [Fact]
        public void A_wall_pulls_the_arm_in_at_once_and_it_springs_back_out_when_the_wall_goes()
        {
            (_, SpringArm arm) = Character();
            GameObject wall = Box(new Vector3(0f, 1.5f, 2.5f), new Vector3(6f, 4f, 0.2f));
            _loop.Load();
            RunFrames(_loop, 3);

            // The wall's near face is at 2.4; the camera stays its thickness in front of it, not behind.
            float z = arm.Transform.WorldPosition.Z;
            Assert.InRange(z, 1.9f, 2.4f - arm.Thickness + 0.02f);
            Assert.Equal(z, arm.CurrentLength, 0.01f);

            wall.Destroy();
            RunFrames(_loop, 3);
            Assert.InRange(arm.Transform.WorldPosition.Z, z + 0.1f, 3.9f);
            RunFrames(_loop, 120);
            Assert.Equal(4f, arm.Transform.WorldPosition.Z, 0.02f);
        }

        [Fact]
        public void What_it_follows_doesnt_count_as_a_wall()
        {
            // The pivot's inside the character's own capsule.
            (_, SpringArm arm) = Character(a => a.PivotOffset = new Vector3(0f, 1f, 0f));
            _loop.Load();
            RunFrames(_loop, 3);

            Assert.Equal(4f, arm.CurrentLength, 0.01f);
        }

        [Fact]
        public void The_mouse_turns_it_round_the_pivot_and_up_and_down_within_its_limits()
        {
            (_, SpringArm arm) = Character(a =>
            {
                a.TurnWithMouse = true;
                a.Sensitivity = 0.1f;
                a.MaxPitch = 30f;
            });
            _loop.Load();
            InputSystem.MoveMouse(new Vector2(400f, 300f));
            RunFrames(_loop, 3);

            // 300 pixels right turns it 30 degrees right.
            InputSystem.MoveMouse(new Vector2(700f, 300f));
            RunFrames(_loop, 1);
            Assert.Equal(-30f, arm.Yaw, 0.01f);
            Assert.Equal(new Vector3(MathF.Sin(MathF.PI / 6f), 0f, -MathF.Cos(MathF.PI / 6f)), arm.FlatForward, new Vector3Comparer(0.001f));
            Assert.Equal(Vector3.UnitY * 1.6f - arm.FlatForward * 4f, arm.Transform.WorldPosition, new Vector3Comparer(0.01f));

            // Far up the screen looks up, but no further than it's allowed; the camera dips below the pivot.
            InputSystem.MoveMouse(new Vector2(700f, -2000f));
            RunFrames(_loop, 1);
            Assert.Equal(30f, arm.Pitch, 0.01f);
            Assert.True(arm.Transform.WorldPosition.Y < 1.6f);
        }

        [Fact]
        public void It_lags_behind_as_what_it_follows_moves_and_catches_up_until_snapped()
        {
            (GameObject character, SpringArm arm) = Character(a => a.FollowLag = 0.2f);
            _loop.Load();
            RunFrames(_loop, 3);

            character.Transform.Position += new Vector3(2f, 0f, 0f);
            RunFrames(_loop, 1);
            float x = arm.Transform.WorldPosition.X;
            Assert.InRange(x, 0.05f, 1f);
            RunFrames(_loop, 120);
            Assert.Equal(2f, arm.Transform.WorldPosition.X, 0.02f);

            character.Transform.Position += new Vector3(10f, 0f, 0f);
            arm.Snap();
            RunFrames(_loop, 1);
            Assert.Equal(12f, arm.Transform.WorldPosition.X, 0.02f);
        }

        [Fact]
        public void Sway_drifts_where_it_looks_a_little_but_not_where_it_is()
        {
            (_, SpringArm arm) = Character(a => a.Sway = 2f);
            _loop.Load();
            float biggest = 0f;
            for (int i = 0; i < 240; i++)
            {
                RunFrames(_loop, 1);
                Assert.Equal(new Vector3(0f, 1.6f, 4f), arm.Transform.WorldPosition, new Vector3Comparer(0.01f));
                float off = MathF.Acos(Math.Clamp(Vector3.Dot(arm.Transform.Forward, -Vector3.UnitZ), -1f, 1f)) * 180f / MathF.PI;
                biggest = MathF.Max(biggest, off);
            }

            Assert.InRange(biggest, 0.5f, 3f);
        }

        private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
            public int GetHashCode(Vector3 value) => 0;
        }
    }
}
