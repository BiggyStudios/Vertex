using System.Numerics;

using Vertex.Animation;
using Vertex.Graphs;
using Vertex.Physics;
using Vertex.Scripting;

namespace Vertex.Tests
{
    /// <summary>Root motion, IK, curves with set slopes, and animation graphs' state machines.</summary>
    public class AnimationMotionTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexAnimationMotionTests", Guid.NewGuid().ToString("N"));

        public AnimationMotionTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) Assets.Forget(file);
            Directory.Delete(_folder, recursive: true);
        }

        private static void Step(float seconds = 1f / 60f)
        {
            Time.Advance(seconds);
            Scene.Active.Update();
        }

        private static void Steps(int count, float seconds)
        {
            for (int i = 0; i < count; i++) Step(seconds);
        }

        private static AnimationClip Moving(string path, string property, Vector3 from, Vector3 to, float length = 1f, bool loop = true)
        {
            var clip = new AnimationClip { Length = length, Loop = loop };
            AnimationTrack track = clip.AddTrack(path, property, 3);
            track.SetKey(0f, new Vector4(from, 0f), KeyInterpolation.Linear);
            track.SetKey(length, new Vector4(to, 0f), KeyInterpolation.Linear);
            return clip;
        }

        private string Save(AnimationClip clip, string name)
        {
            string path = Path.Combine(_folder, name + ".anim");
            clip.Save(path);
            return path;
        }

        private static void Near(Vector3 expected, Vector3 actual, float within = 0.02f) =>
            Assert.True(Vector3.Distance(expected, actual) <= within, $"Expected {expected}, got {actual}");

        // ---------------------------------------------------------------- Root motion

        [Fact]
        public void With_root_motion_an_animation_moving_the_object_moves_it_on_each_time_round_the_way_it_faces()
        {
            var character = new GameObject("Character");
            Animator animator = character.AddComponent<Animator>(a =>
            {
                a.Clip = Moving("", "Position", Vector3.Zero, new Vector3(0f, 0f, -2f));
                a.RootMotion = true;
            });

            Step(0f);
            // Turned by the game once it's going: the walk goes the way it faces now.
            character.Transform.EulerAngles = new Vector3(0f, 90f, 0f);
            Steps(25, 0.1f);

            // Two metres a second forward, for two and a half seconds: round the loop twice and more, and turned to face -X.
            Near(new Vector3(-5f, 0f, 0f), character.Transform.Position, 0.05f);
            Near(new Vector3(-2f, 0f, 0f), animator.RootMotionVelocity, 0.05f);
        }

        [Fact]
        public void Root_motion_from_a_bone_moves_the_character_along_the_ground_and_keeps_the_bone_in_place_with_its_bob()
        {
            var character = new GameObject("Character");
            var hips = new GameObject("Hips");
            hips.Transform.Parent = character.Transform;
            hips.Transform.Position = new Vector3(0f, 1f, 0f);
            character.AddComponent<Animator>(a =>
            {
                a.Clip = Moving("Hips", "Position", new Vector3(0f, 1f, 0f), new Vector3(0f, 1.2f, -2f));
                a.RootMotion = true;
            });

            Step(0f);
            Steps(5, 0.1f);

            Near(new Vector3(0f, 0f, -1f), character.Transform.Position);
            Near(new Vector3(0f, 1.1f, 0f), hips.Transform.Position);
        }

        [Fact]
        public void Root_motion_turns_the_character_and_a_blend_moves_it_by_the_mix()
        {
            string turn = Save(Moving("", "EulerAngles", Vector3.Zero, new Vector3(0f, 90f, 0f)), "Turn");
            string still = Save(new AnimationClip { Length = 1f }, "Still");

            var graph = new Graph(AnimationNodes.Kind);
            GraphNode a = graph.AddNode(AnimationNodes.Clip, Vector2.Zero);
            a.Values["Animation"] = still;
            GraphNode b = graph.AddNode(AnimationNodes.Clip, Vector2.Zero);
            b.Values["Animation"] = turn;
            GraphNode blend = graph.AddNode(AnimationNodes.Blend, Vector2.Zero);
            blend.Values["Weight"] = 0.5f;
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            graph.Link(a.Id, "Pose", blend.Id, "A");
            graph.Link(b.Id, "Pose", blend.Id, "B");
            graph.Link(blend.Id, "Pose", output.Id, "Pose");

            var character = new GameObject("Character");
            character.AddComponent<Animator>(animator =>
            {
                animator.AnimationGraph = graph;
                animator.RootMotion = true;
                animator.RootMotionFrom = "Character";
            });

            Step(0f);
            Steps(10, 0.1f);

            // Half of a quarter turn a second, for a second.
            Assert.Equal(45f, character.Transform.EulerAngles.Y, 1f);
        }

        [Fact]
        public void Root_motion_sets_a_character_controllers_velocity_along_the_ground_and_keeps_its_fall()
        {
            var character = new GameObject("Character");
            CharacterController controller = character.AddComponent<CharacterController>();
            controller.Velocity = new Vector3(0f, -3f, 0f);
            character.AddComponent<Animator>(a =>
            {
                a.Clip = Moving("", "Position", Vector3.Zero, new Vector3(4f, 0f, 0f));
                a.RootMotion = true;
            });

            Step(0f);
            Step(0.1f);

            Near(new Vector3(4f, -3f, 0f), controller.Velocity, 0.05f);
            Near(Vector3.Zero, character.Transform.Position);
        }

        // ---------------------------------------------------------------- IK

        /// <summary>A shoulder at the origin, an elbow a metre along X, and a hand a metre on.</summary>
        private static (Transform Shoulder, Transform Elbow, Transform Hand) Arm()
        {
            var character = new GameObject("Character");
            var shoulder = new GameObject("Shoulder");
            var elbow = new GameObject("Elbow");
            var hand = new GameObject("Hand");
            shoulder.Transform.Parent = character.Transform;
            elbow.Transform.Parent = shoulder.Transform;
            hand.Transform.Parent = elbow.Transform;
            elbow.Transform.Position = new Vector3(1f, 0f, 0f);
            hand.Transform.Position = new Vector3(1f, 0f, 0f);
            return (shoulder.Transform, elbow.Transform, hand.Transform);
        }

        [Fact]
        public void Two_bone_IK_bends_the_limb_so_its_end_reaches_the_target_with_the_middle_towards_the_pole()
        {
            (Transform shoulder, Transform elbow, Transform hand) = Arm();
            var target = new GameObject("Target");
            target.Transform.Position = new Vector3(1.2f, 0.4f, 0.3f);
            var pole = new GameObject("Pole");
            pole.Transform.Position = new Vector3(0.5f, 0f, 3f);
            hand.GameObject.AddComponent<TwoBoneIK>(ik =>
            {
                ik.Target = target.Transform;
                ik.Pole = pole.Transform;
            });

            Step(0f);
            Step();

            Near(target.Transform.Position, hand.WorldPosition);
            Assert.Equal(1f, Vector3.Distance(shoulder.WorldPosition, elbow.WorldPosition), 3);
            Assert.Equal(1f, Vector3.Distance(elbow.WorldPosition, hand.WorldPosition), 3);
            Assert.True(elbow.WorldPosition.Z > 0.3f, $"The elbow should point towards the pole, at {elbow.WorldPosition}");

            // Out of reach, it reaches straight towards it.
            target.Transform.Position = new Vector3(0f, 5f, 0f);
            Step();
            Near(new Vector3(0f, 2f, 0f), hand.WorldPosition, 0.01f);
        }

        [Fact]
        public void Two_bone_IK_reaches_part_way_by_its_weight_and_can_match_the_targets_rotation()
        {
            (_, _, Transform hand) = Arm();
            var target = new GameObject("Target");
            target.Transform.Position = new Vector3(1f, 1f, 0f);
            target.Transform.EulerAngles = new Vector3(0f, 0f, 45f);
            TwoBoneIK ik = hand.GameObject.AddComponent<TwoBoneIK>(i =>
            {
                i.Target = target.Transform;
                i.Weight = 0.5f;
            });

            Step(0f);
            Step();
            Near(new Vector3(1.5f, 0.5f, 0f), hand.WorldPosition, 0.01f);

            ik.Weight = 1f;
            ik.MatchTargetRotation = true;
            Step();
            Near(new Vector3(1f, 1f, 0f), hand.WorldPosition);
            Assert.True(Quaternion.Dot(hand.WorldRotation, target.Transform.WorldRotation) > 0.9999f);
        }

        [Fact]
        public void Look_at_IK_turns_the_head_and_the_bones_above_it_towards_the_target_no_further_than_its_limit()
        {
            var character = new GameObject("Character");
            character.AddComponent<Animator>();
            var neck = new GameObject("Neck");
            var head = new GameObject("Head");
            neck.Transform.Parent = character.Transform;
            head.Transform.Parent = neck.Transform;
            neck.Transform.Position = new Vector3(0f, 1.5f, 0f);
            head.Transform.Position = new Vector3(0f, 0.2f, 0f);
            var target = new GameObject("Target");
            target.Transform.Position = new Vector3(-3f, 1.7f, -3f);
            LookAtIK look = head.AddComponent<LookAtIK>(ik =>
            {
                ik.Target = target.Transform;
                ik.Bones = 2;
            });

            Step(0f);
            Step();

            Vector3 facing = head.Transform.Forward;
            Near(Vector3.Normalize(new Vector3(-1f, 0f, -1f)), facing, 0.01f);
            // Shared: the neck turned some of the way.
            Assert.InRange(neck.Transform.EulerAngles.Y, 10f, 35f);

            look.MaxAngle = 30f;
            neck.Transform.Rotation = Quaternion.Identity;
            head.Transform.Rotation = Quaternion.Identity;
            Step();
            float angle = MathF.Acos(Vector3.Dot(head.Transform.Forward, -Vector3.UnitZ)) * 180f / MathF.PI;
            Assert.Equal(30f, angle, 1f);
        }

        [Fact]
        public void A_grounded_foot_stands_on_the_step_under_it()
        {
            var step = new GameObject("Step");
            step.Transform.Position = new Vector3(0f, 0.15f, 0f);
            step.AddComponent<BoxCollider>().Size = new Vector3(2f, 0.3f, 2f);

            var character = new GameObject("Character");
            character.Transform.Position = new Vector3(0f, 0f, 3f);
            character.AddComponent<Animator>();
            var hip = new GameObject("Hip");
            var knee = new GameObject("Knee");
            var foot = new GameObject("Foot");
            hip.Transform.Parent = character.Transform;
            knee.Transform.Parent = hip.Transform;
            foot.Transform.Parent = knee.Transform;
            hip.Transform.Position = new Vector3(0f, 1f, -2.9f);
            knee.Transform.Position = new Vector3(0f, -0.45f, -0.05f);
            foot.Transform.Position = new Vector3(0f, -0.45f, 0.05f);
            TwoBoneIK ik = foot.AddComponent<TwoBoneIK>(i =>
            {
                i.Ground = true;
                i.FootHeight = 0.1f;
            });

            var loop = new GameLoop(new EmptyGame());
            loop.Load();
            RunFrames(loop, 3);

            // The foot was 0.1 above the character's feet: it's 0.1 above the step's top now.
            Assert.Equal(0.4f, foot.Transform.WorldPosition.Y, 2);
        }

        // ---------------------------------------------------------------- Curves

        [Fact]
        public void A_keys_set_slopes_shape_the_curve_and_are_saved()
        {
            var clip = new AnimationClip { Length = 1f };
            AnimationTrack track = clip.AddTrack("", "Light.Intensity", 1);
            track.SetKey(0f, new Vector4(0f));
            track.SetKey(1f, new Vector4(0f));
            Assert.Equal(0f, track.Sample(0.25f).X, 4);

            track.Keys[0] = track.Keys[0] with { OutSlope = new Vector4(4f, 0f, 0f, 0f) };
            Assert.True(track.Sample(0.25f).X > 0.3f);
            Assert.Equal(new Vector4(4f, 0f, 0f, 0f), track.OutSlopeAt(0));
            // Only one side set: the other is the same, so the curve's smooth through it.
            Assert.Equal(new Vector4(4f, 0f, 0f, 0f), track.InSlopeAt(0));

            var loaded = new AnimationClip();
            loaded.ReadJson(clip.ToJson());
            Assert.Equal(track.Sample(0.25f).X, loaded.Tracks[0].Sample(0.25f).X, 5);
            Assert.Null(loaded.Tracks[0].Keys[1].OutSlope);
        }

        // ---------------------------------------------------------------- State machines

        private Graph Machine(out GraphNode node)
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            graph.Variables.Add(new GraphVariable("Jump", AnimationNodes.Trigger, false));
            node = graph.AddNode(AnimationNodes.StateMachine, Vector2.Zero);
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            graph.Link(node.Id, "Pose", output.Id, "Pose");

            var machine = new AnimationStateMachine { Entry = "Idle" };
            machine.States.Add(new AnimationState { Name = "Idle", Animation = Save(Moving("Arm", "Position", Vector3.Zero, Vector3.Zero), "Idle") });
            machine.States.Add(new AnimationState { Name = "Walk", Animation = Save(Moving("Arm", "Position", new Vector3(4f, 0f, 0f), new Vector3(4f, 0f, 0f)), "Walk") });
            machine.States.Add(new AnimationState { Name = "Jump", Animation = Save(Moving("Arm", "Position", new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 0f), 0.5f, loop: false), "Jump") });

            var toWalk = new StateTransition { From = "Idle", To = "Walk", Fade = 0f };
            toWalk.Conditions.Add(new TransitionCondition { Parameter = "Speed", Is = Comparison.Greater, Value = 0.5f });
            var toIdle = new StateTransition { From = "Walk", To = "Idle", Fade = 0.2f };
            toIdle.Conditions.Add(new TransitionCondition { Parameter = "Speed", Is = Comparison.LessOrEqual, Value = 0.5f });
            var jump = new StateTransition { From = AnimationStateMachine.AnyState, To = "Jump", Fade = 0f };
            jump.Conditions.Add(new TransitionCondition { Parameter = "Jump", Is = Comparison.Equal, Value = 1f });
            var landed = new StateTransition { From = "Jump", To = "Idle", Fade = 0f, AtEnd = true };
            machine.Transitions.AddRange([toWalk, toIdle, jump, landed]);
            machine.Store(graph, node);
            return graph;
        }

        [Fact]
        public void A_state_machine_goes_from_state_to_state_as_its_transitions_conditions_say()
        {
            Graph graph = Machine(out _);
            var character = new GameObject("Character");
            var arm = new GameObject("Arm");
            arm.Transform.Parent = character.Transform;
            Animator animator = character.AddComponent<Animator>(a => a.AnimationGraph = graph);

            Step(0f);
            Step(0.1f);
            Assert.Equal("Idle", animator.State);
            Assert.Equal(0f, arm.Transform.Position.X, 3);

            animator.Set("Speed", 2f);
            Step(0.1f);
            Assert.Equal("Walk", animator.State);
            Assert.Equal(4f, arm.Transform.Position.X, 3);

            // Back to idle, fading over 0.2 seconds.
            animator.Set("Speed", 0f);
            Step(0.1f);
            Assert.Equal("Idle", animator.State);
            Assert.Equal(2f, arm.Transform.Position.X, 2);
            Step(0.1f);
            Assert.Equal(0f, arm.Transform.Position.X, 2);

            // A jump from any state, back to idle once it's played through.
            animator.SetTrigger("Jump");
            Step(0.1f);
            Assert.Equal("Jump", animator.State);
            Assert.Equal(10f, arm.Transform.Position.X, 3);
            Step(0.2f);
            Assert.Equal("Jump", animator.State);
            Step(0.3f);
            Assert.Equal("Idle", animator.State);
        }

        [Fact]
        public void A_state_plays_whats_linked_into_its_input_and_the_machine_is_kept_in_the_graphs_file()
        {
            Graph graph = Machine(out GraphNode node);
            GraphNode clip = graph.AddNode(AnimationNodes.Clip, Vector2.Zero);
            clip.Values["Animation"] = Save(Moving("Arm", "Position", new Vector3(7f, 0f, 0f), new Vector3(7f, 0f, 0f)), "Linked");

            // Each state is an input on the node.
            NodeType type = AnimationNodes.Library.Find(AnimationNodes.StateMachine)!;
            Assert.Equal(["Idle", "Walk", "Jump"], type.InputsOf(graph, node).Select(p => p.Name));
            Assert.True(graph.Link(clip.Id, "Pose", node.Id, "Walk"));

            string path = Path.Combine(_folder, "Machine.animgraph");
            graph.Save(path);
            Graph loaded = Graph.Load(path);
            GraphNode loadedNode = loaded.Nodes.Single(n => n.Type == AnimationNodes.StateMachine);
            Assert.Equal(4, AnimationStateMachine.Of(loadedNode).Transitions.Count);

            var character = new GameObject("Character");
            var arm = new GameObject("Arm");
            arm.Transform.Parent = character.Transform;
            Animator animator = character.AddComponent<Animator>(a => a.AnimationGraph = loaded);
            Step(0f);
            animator.Set("Speed", 2f);
            Step(0.1f);
            Assert.Equal(7f, arm.Transform.Position.X, 3);
            Assert.Equal("Walk", AnimationStateMachine.PlayingState(loaded, loadedNode.Id));
        }
    }
}
