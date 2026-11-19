using System.Numerics;

using Vertex.Physics;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>Joints between rigidbodies, and rigidbodies locked along some axes.</summary>
    public class JointTests : EngineTest
    {
        private static GameObject Ball(string name, Vector3 position, bool gravity = true)
        {
            var ball = new GameObject(name);
            ball.Transform.Position = position;
            ball.AddComponent<SphereCollider>().Radius = 0.25f;
            ball.AddComponent<Rigidbody>().UseGravity = gravity;
            return ball;
        }

        private static GameObject Box(string name, Vector3 position, Vector3 size, bool gravity = true)
        {
            var box = new GameObject(name);
            box.Transform.Position = position;
            box.AddComponent<BoxCollider>().Size = size;
            box.AddComponent<Rigidbody>().UseGravity = gravity;
            return box;
        }

        private static GameLoop Run(int frames)
        {
            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, frames);
            return loop;
        }

        [Fact]
        public void A_ball_on_a_rope_swings_but_never_goes_further_than_the_rope()
        {
            GameObject ball = Ball("Ball", new Vector3(2f, 3f, 0f));
            var rope = ball.AddComponent<DistanceJoint>();
            rope.ConnectedAnchor = new Vector3(0f, 3f, 0f);

            GameLoop loop = Run(30);
            float furthest = 0f;
            for (int i = 0; i < 120; i++)
            {
                RunFrames(loop, 1);
                furthest = MathF.Max(furthest, Vector3.Distance(ball.Transform.Position, new Vector3(0f, 3f, 0f)));
            }

            Assert.True(furthest < 2.05f, $"It went {furthest} m from where the rope's tied.");
            Assert.True(ball.Transform.Position.Y < 2.5f, "It should have swung down.");
            Assert.True(ball.Transform.Position.X < 1.5f, "It should have swung in under the rope's end.");
        }

        [Fact]
        public void A_fixed_joint_carries_one_body_along_with_the_other()
        {
            GameObject a = Box("A", new Vector3(0f, 5f, 0f), Vector3.One);
            GameObject b = Box("B", new Vector3(1.5f, 5f, 0f), Vector3.One);
            b.AddComponent<FixedJoint>().ConnectedBody = a.GetComponent<Rigidbody>();

            Run(40);

            Assert.True(a.Transform.Position.Y < 4f, "They should be falling.");
            Assert.Equal(1.5f, Vector3.Distance(a.Transform.Position, b.Transform.Position), 0.05f);
        }

        [Fact]
        public void A_hinged_arm_falls_until_its_limit_and_reports_its_angle()
        {
            // An arm sticking out along X from a pin at the origin, turning about Z: gravity swings it down.
            GameObject arm = Box("Arm", new Vector3(1f, 0f, 0f), new Vector3(2f, 0.2f, 0.2f));
            var hinge = arm.AddComponent<HingeJoint>();
            hinge.Anchor = new Vector3(-1f, 0f, 0f);
            hinge.Axis = Vector3.UnitZ;
            hinge.UseLimits = true;
            hinge.MinAngle = -30f;
            hinge.MaxAngle = 30f;

            Run(180);

            Assert.InRange(MathF.Abs(hinge.Angle), 25f, 31f);
            Assert.True(arm.Transform.Position.Y < -0.3f, "It should have swung down.");

            // The pin stays put.
            Vector3 pin = Vector3.Transform(new Vector3(-1f, 0f, 0f), arm.Transform.WorldMatrix);
            Assert.True(pin.Length() < 0.05f, $"The pin moved to {pin}.");
        }

        [Fact]
        public void A_hinge_motor_turns_its_wheel_at_its_speed()
        {
            GameObject wheel = Box("Wheel", Vector3.Zero, new Vector3(1f, 0.2f, 1f), gravity: false);
            var hinge = wheel.AddComponent<HingeJoint>();
            hinge.Motor = JointMotor.Speed;
            hinge.MotorSpeed = 90f;

            Run(60);

            Assert.Equal(MathF.PI / 2f, MathF.Abs(wheel.GetComponent<Rigidbody>()!.AngularVelocity.Y), 0.1f);
        }

        [Fact]
        public void A_slider_moves_only_along_its_axis_and_stops_at_its_limit()
        {
            GameObject drawer = Box("Drawer", Vector3.Zero, new Vector3(0.5f), gravity: false);
            var slider = drawer.AddComponent<SliderJoint>();
            slider.UseLimits = true;
            slider.MinDistance = 0f;
            slider.MaxDistance = 1f;
            drawer.GetComponent<Rigidbody>()!.Velocity = new Vector3(4f, 3f, 0f);

            Run(90);

            Assert.Equal(1f, drawer.Transform.Position.X, 0.05f);
            Assert.Equal(0f, drawer.Transform.Position.Y, 0.02f);
            Assert.Equal(1f, slider.Position, 0.05f);
        }

        [Fact]
        public void A_spring_stretches_under_its_weight_and_settles()
        {
            GameObject weight = Ball("Weight", new Vector3(0f, 2f, 0f));
            var spring = weight.AddComponent<SpringJoint>();
            spring.ConnectedAnchor = new Vector3(0f, 3f, 0f);
            spring.Stiffness = 3f;
            spring.Damping = 0.5f;

            Run(600);

            float below = 3f - weight.Transform.Position.Y;
            Assert.True(below > 1.01f, "Its weight should stretch it.");
            Assert.True(below < 1.2f, $"A stiff spring shouldn't stretch far: it's {below} m.");
            Assert.True(weight.GetComponent<Rigidbody>()!.Velocity.Length() < 0.05f, "It should have settled.");
        }

        private sealed class BreakListener : Component
        {
            public Joint? Heard;
            protected internal override void OnJointBreak(Joint joint) => Heard = joint;
        }

        [Fact]
        public void A_joint_pulled_harder_than_its_break_force_breaks_and_lets_go()
        {
            GameObject lamp = Ball("Lamp", new Vector3(0f, 3f, 0f));
            lamp.GetComponent<Rigidbody>()!.Mass = 10f;
            var chain = lamp.AddComponent<FixedJoint>();
            chain.BreakForce = 1000f;
            var listener = lamp.AddComponent<BreakListener>();
            Joint? raised = null;
            chain.Broken += joint => raised = joint;

            GameLoop loop = Run(30);
            Assert.False(chain.IsBroken);
            Assert.Equal(3f, lamp.Transform.Position.Y, 0.02f);
            Assert.InRange(chain.CurrentForce, 80f, 120f);

            // Something heavy hits it: a pull of far more than it can take.
            lamp.GetComponent<Rigidbody>()!.AddImpulse(new Vector3(0f, -200f, 0f));
            RunFrames(loop, 30);

            Assert.True(chain.IsBroken);
            Assert.Same(chain, listener.Heard);
            Assert.Same(chain, raised);
            Assert.True(lamp.Transform.Position.Y < 2.5f, $"It should fall once the chain's broken: it's at {lamp.Transform.Position}.");
        }

        [Fact]
        public void Joined_bodies_pass_through_each_other_unless_they_collide_connected()
        {
            float Apart(bool collide)
            {
                Scene.Active.Unload();
                Scene.Active = new Scene();
                GameObject a = Box("A", Vector3.Zero, Vector3.One, gravity: false);
                GameObject b = Box("B", new Vector3(0.5f, 0f, 0f), Vector3.One, gravity: false);
                var joint = b.AddComponent<DistanceJoint>();
                joint.ConnectedBody = a.GetComponent<Rigidbody>();
                joint.MaxDistance = 5f;
                joint.CollideConnected = collide;
                Run(60);
                return Vector3.Distance(a.Transform.Position, b.Transform.Position);
            }

            Assert.Equal(0.5f, Apart(collide: false), 0.01f);
            Assert.True(Apart(collide: true) > 0.9f, "Colliding, they should push each other out.");
        }

        [Fact]
        public void A_rigidbody_locked_along_y_neither_falls_nor_stops_moving_sideways()
        {
            GameObject puck = Ball("Puck", new Vector3(0f, 2f, 0f));
            Rigidbody body = puck.GetComponent<Rigidbody>()!;
            body.LockPosition = Axes.Y;
            body.Drag = 0f;
            body.Velocity = new Vector3(1f, 0f, 0f);

            Run(60);

            Assert.Equal(2f, puck.Transform.Position.Y, 0.001f);
            Assert.Equal(1f, puck.Transform.Position.X, 0.05f);
        }

        [Fact]
        public void A_rigidbody_locked_about_every_axis_doesnt_turn_when_pushed_off_centre()
        {
            GameObject crate = Box("Crate", new Vector3(0f, 2f, 0f), Vector3.One, gravity: false);
            Rigidbody body = crate.GetComponent<Rigidbody>()!;
            body.LockRotation = Axes.All;
            body.AngularVelocity = new Vector3(3f, 2f, 1f);

            Run(30);

            Assert.Equal(Quaternion.Identity, crate.Transform.Rotation);
        }

        [Fact]
        public void A_joint_and_what_it_holds_on_to_come_back_from_a_saved_scene()
        {
            var scene = new Scene();
            Scene.Active = scene;
            GameObject door = Box("Door", Vector3.Zero, Vector3.One);
            GameObject frame = Box("Frame", new Vector3(1f, 0f, 0f), Vector3.One);
            var hinge = door.AddComponent<HingeJoint>();
            hinge.ConnectedBody = frame.GetComponent<Rigidbody>();
            hinge.UseLimits = true;
            hinge.MaxAngle = 110f;
            hinge.Motor = JointMotor.Target;
            frame.GetComponent<Rigidbody>()!.LockPosition = Axes.X | Axes.Z;
            scene.ApplyChanges();

            var loaded = new Scene();
            SceneFile.LoadJson(SceneFile.Save(scene), loaded);
            loaded.ApplyChanges();

            var loadedHinge = loaded.Find("Door")!.GetComponent<HingeJoint>()!;
            Assert.Same(loaded.Find("Frame")!.GetComponent<Rigidbody>(), loadedHinge.ConnectedBody);
            Assert.True(loadedHinge.UseLimits);
            Assert.Equal(110f, loadedHinge.MaxAngle);
            Assert.Equal(JointMotor.Target, loadedHinge.Motor);
            Assert.Equal(Axes.X | Axes.Z, loaded.Find("Frame")!.GetComponent<Rigidbody>()!.LockPosition);
        }
    }
}
