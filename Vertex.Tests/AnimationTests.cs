using System.Numerics;
using System.Text.Json;

using Vertex.Animation;
using Vertex.Graphs;
using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.Tests
{
    /// <summary>Animation clips, the Animator, animation graphs, and models with bones.</summary>
    public class AnimationTests : EngineTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexAnimationTests", Guid.NewGuid().ToString("N"));

        public AnimationTests()
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

        /// <summary>A clip that moves "Arm" along X from <paramref name="from"/> to <paramref name="to"/> in a second, steadily.</summary>
        private static AnimationClip Slide(float from, float to, bool loop = true, string path = "Arm")
        {
            var clip = new AnimationClip { Length = 1f, Loop = loop };
            AnimationTrack track = clip.AddTrack(path, "Position", 3);
            track.SetKey(0f, new Vector4(from, 0f, 0f, 0f), KeyInterpolation.Linear);
            track.SetKey(1f, new Vector4(to, 0f, 0f, 0f), KeyInterpolation.Linear);
            return clip;
        }

        private static (GameObject Root, GameObject Arm) Character()
        {
            var root = new GameObject("Character");
            var arm = new GameObject("Arm");
            arm.Transform.Parent = root.Transform;
            arm.Transform.Position = new Vector3(0f, 5f, 0f);
            return (root, arm);
        }

        // ---------------------------------------------------------------- Tracks

        [Fact]
        public void Keys_go_steadily_eased_or_in_steps_and_hold_before_the_first_and_after_the_last()
        {
            var track = new AnimationTrack("", "Light.Intensity", 1);
            track.SetKey(0f, new Vector4(0f), KeyInterpolation.Linear);
            track.SetKey(1f, new Vector4(10f));
            Assert.Equal(2.5f, track.Sample(0.25f).X, 3);
            Assert.Equal(0f, track.Sample(-1f).X);
            Assert.Equal(10f, track.Sample(5f).X);

            // Smooth between two keys eases in and out: slower than steady near the ends, halfway at the middle.
            track.Keys[0] = track.Keys[0] with { Interpolation = KeyInterpolation.Smooth };
            Assert.True(track.Sample(0.25f).X < 2.5f);
            Assert.Equal(5f, track.Sample(0.5f).X, 3);

            track.Keys[0] = track.Keys[0] with { Interpolation = KeyInterpolation.Step };
            Assert.Equal(0f, track.Sample(0.99f).X);
            Assert.Equal(10f, track.Sample(1f).X);
        }

        [Fact]
        public void A_smooth_curve_passes_through_its_keys_without_overshooting_them()
        {
            var track = new AnimationTrack("", "Position", 3);
            track.SetKey(0f, new Vector4(0f));
            track.SetKey(1f, new Vector4(1f));
            track.SetKey(2f, new Vector4(1f));
            track.SetKey(3f, new Vector4(0f));
            for (float t = 0f; t <= 3f; t += 0.05f)
            {
                float value = track.Sample(t).X;
                Assert.InRange(value, -1e-4f, 1f + 1e-4f);
            }

            Assert.Equal(1f, track.Sample(1f).X, 4);
        }

        [Fact]
        public void A_rotation_key_is_kept_on_the_same_side_as_the_one_before_so_it_turns_the_short_way()
        {
            var track = new AnimationTrack("", "Rotation", 4);
            Quaternion a = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f);
            Quaternion b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f);
            track.SetKey(0f, new Vector4(a.X, a.Y, a.Z, a.W));
            track.SetKey(1f, -new Vector4(b.X, b.Y, b.Z, b.W));
            Assert.True(Vector4.Dot(track.Keys[0].Value, track.Keys[1].Value) > 0f);
        }

        // ---------------------------------------------------------------- Animator

        [Fact]
        public void An_animator_plays_its_clip_on_the_objects_under_it_and_leaves_alone_what_the_clip_doesnt_animate()
        {
            (GameObject root, GameObject arm) = Character();
            var other = new GameObject("Leg");
            other.Transform.Parent = root.Transform;
            other.Transform.Position = new Vector3(3f, 0f, 0f);
            root.AddComponent<Animator>(a => a.Clip = Slide(0f, 10f));

            Step(0f);
            Step(0.5f);
            Assert.Equal(5f, arm.Transform.Position.X, 3);
            Assert.Equal(new Vector3(3f, 0f, 0f), other.Transform.Position);

            // It loops: half a second more is the end, a quarter more the start again.
            Step(0.75f);
            Assert.Equal(2.5f, arm.Transform.Position.X, 3);
        }

        [Fact]
        public void A_clip_can_animate_rotation_in_degrees_past_a_whole_turn_and_any_components_settings()
        {
            (GameObject root, GameObject arm) = Character();
            Light light = arm.AddComponent<Light>();
            light.Intensity = 1f;
            var clip = new AnimationClip { Length = 2f };
            AnimationTrack spin = clip.AddTrack("Arm", "EulerAngles", 3);
            spin.SetKey(0f, Vector4.Zero, KeyInterpolation.Linear);
            spin.SetKey(2f, new Vector4(0f, 720f, 0f, 0f), KeyInterpolation.Linear);
            AnimationTrack glow = clip.AddTrack("Arm", "Light.Intensity", 1);
            glow.SetKey(0f, new Vector4(1f), KeyInterpolation.Linear);
            glow.SetKey(2f, new Vector4(5f), KeyInterpolation.Linear);
            AnimationTrack shown = clip.AddTrack("Arm", "Active", 1);
            shown.SetKey(0f, new Vector4(1f), KeyInterpolation.Step);
            shown.SetKey(1.5f, new Vector4(0f), KeyInterpolation.Step);

            Animator animator = root.AddComponent<Animator>();
            animator.Sample(clip, 0.25f);
            // A quarter of a second is 90 degrees round.
            Vector3 forward = arm.Transform.Forward;
            Assert.Equal(-1f, forward.X, 3);
            Assert.Equal(1.5f, light.Intensity, 3);
            Assert.True(arm.Active);

            animator.Sample(clip, 1.75f);
            Assert.False(arm.Active);

            animator.ResetPose();
            Assert.Equal(1f, light.Intensity);
            Assert.True(arm.Active);
            Assert.Equal(Quaternion.Identity, arm.Transform.Rotation);
        }

        [Fact]
        public void Playing_another_clip_blends_into_it_over_the_fade()
        {
            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.Clip = Slide(0f, 0f));
            Step(0f);
            Step(0.1f);
            Assert.Equal(0f, arm.Transform.Position.X, 3);

            animator.Play(Slide(10f, 10f), fade: 0.5f);
            Step(0.25f);
            Assert.Equal(5f, arm.Transform.Position.X, 2);
            Step(0.25f);
            Assert.Equal(10f, arm.Transform.Position.X, 2);
            Step(0.1f);
            Assert.Equal(10f, arm.Transform.Position.X, 2);
        }

        [Fact]
        public void A_clips_events_are_sent_as_they_pass_each_time_round_and_to_visual_scripts()
        {
            (GameObject root, _) = Character();
            AnimationClip clip = Slide(0f, 1f);
            clip.Events.Add(new AnimationEvent(0.5f, "Step"));
            var heard = new List<string>();
            Animator animator = root.AddComponent<Animator>(a => a.Clip = clip);
            animator.EventReached += e => heard.Add(e.Name);

            Step(0f);
            Step(0.4f);
            Assert.Empty(heard);
            Step(0.2f);
            Assert.Equal(["Step"], heard);
            Step(1f);
            Assert.Equal(["Step", "Step"], heard);
        }

        [Fact]
        public void LateUpdate_comes_after_animation_so_it_can_change_what_the_animation_did()
        {
            (GameObject root, GameObject arm) = Character();
            root.AddComponent<Animator>(a => a.Clip = Slide(0f, 10f));
            arm.AddComponent<Raise>();
            Step(0f);
            Step(0.5f);
            Assert.Equal(new Vector3(5f, 1f, 0f), arm.Transform.Position);
        }

        private sealed class Raise : Component
        {
            protected internal override void LateUpdate() => Transform.Position += Vector3.UnitY;
        }

        // ---------------------------------------------------------------- Files

        [Fact]
        public void A_clip_saves_to_a_file_comes_back_the_same_and_a_scene_keeps_which_clip_an_animator_plays()
        {
            AnimationClip clip = Slide(1f, 2f, loop: false);
            clip.Tracks[0].Keys[1] = clip.Tracks[0].Keys[1] with { Interpolation = KeyInterpolation.Step };
            clip.Events.Add(new AnimationEvent(0.25f, "Swing"));
            string path = Path.Combine(_folder, "Swing.anim");
            clip.Save(path);

            AnimationClip loaded = Assets.LoadAnimation(path);
            Assert.Equal("Swing", loaded.Name);
            Assert.False(loaded.Loop);
            Assert.Equal(clip.ToJson(), loaded.ToJson());

            (GameObject root, _) = Character();
            root.AddComponent<Animator>(a => a.Clip = loaded);
            string json = SceneFile.Save(Scene.Active, new[] { _folder });
            Assert.Contains("\"animation\": \"file:Swing.anim\"", json);

            var scene = new Scene();
            SceneFile.LoadJson(json, scene, _folder);
            scene.ApplyChanges();
            Assert.Same(loaded, scene.GameObjects.Single(o => o.Name == "Character").GetComponent<Animator>()!.Clip);
        }

        // ---------------------------------------------------------------- Animation graphs

        private string SaveClip(AnimationClip clip, string name)
        {
            string path = Path.Combine(_folder, name + ".anim");
            clip.Save(path);
            return path;
        }

        private static GraphNode ClipNode(Graph graph, string path)
        {
            GraphNode node = graph.AddNode(AnimationNodes.Clip, Vector2.Zero);
            node.Values["Animation"] = path;
            return node;
        }

        [Fact]
        public void Blend_by_value_turns_a_parameter_into_a_mix_of_the_poses_either_side()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            GraphNode speed = graph.AddNode(AnimationNodes.GetParameter, Vector2.Zero);
            speed.Values["Variable"] = "Speed";
            GraphNode idle = ClipNode(graph, SaveClip(Slide(0f, 0f), "Idle"));
            GraphNode walk = ClipNode(graph, SaveClip(Slide(4f, 4f), "Walk"));
            GraphNode run = ClipNode(graph, SaveClip(Slide(10f, 10f), "Run"));
            GraphNode blend = graph.AddNode(AnimationNodes.BlendByValue, Vector2.Zero);
            blend.Values["At"] = "0, 2, 6";
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            Assert.True(graph.Link(speed.Id, "Value", blend.Id, "Value"));
            Assert.True(graph.Link(idle.Id, "Pose", blend.Id, "Pose 1"));
            Assert.True(graph.Link(walk.Id, "Pose", blend.Id, "Pose 2"));
            Assert.True(graph.Link(run.Id, "Pose", blend.Id, "Pose 3"));
            Assert.True(graph.Link(blend.Id, "Pose", output.Id, "Pose"));

            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.AnimationGraph = graph);
            Step(0f);
            Step();
            Assert.Equal(0f, arm.Transform.Position.X, 3);

            animator.Set("Speed", 1f);
            Step();
            Assert.Equal(2f, arm.Transform.Position.X, 3);

            animator.Set("Speed", 4f);
            Step();
            Assert.Equal(7f, arm.Transform.Position.X, 3);

            animator.Set("Speed", 100f);
            Step();
            Assert.Equal(10f, arm.Transform.Position.X, 3);
            Assert.Null(graph.Error(blend.Id));
        }

        [Fact]
        public void Blend_by_bool_fades_from_one_pose_to_the_other_when_its_condition_changes()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Crouching", PortType.Boolean, false));
            GraphNode crouching = graph.AddNode(AnimationNodes.GetParameter, Vector2.Zero);
            crouching.Values["Variable"] = "Crouching";
            GraphNode stand = ClipNode(graph, SaveClip(Slide(0f, 0f), "Stand"));
            GraphNode crouch = ClipNode(graph, SaveClip(Slide(8f, 8f), "Crouch"));
            GraphNode choose = graph.AddNode(AnimationNodes.BlendByBool, Vector2.Zero);
            choose.Values["Fade"] = 0.4f;
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            graph.Link(crouching.Id, "Value", choose.Id, "Condition");
            graph.Link(crouch.Id, "Pose", choose.Id, "True");
            graph.Link(stand.Id, "Pose", choose.Id, "False");
            graph.Link(choose.Id, "Pose", output.Id, "Pose");

            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.AnimationGraph = graph);
            Step(0f);
            Step(0.1f);
            Assert.Equal(0f, arm.Transform.Position.X, 3);

            animator.Set("Crouching", true);
            Step(0.2f);
            Assert.Equal(4f, arm.Transform.Position.X, 2);
            Step(0.2f);
            Assert.Equal(8f, arm.Transform.Position.X, 2);
        }

        [Fact]
        public void Switch_fades_to_the_pose_whose_number_its_given()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("State", PortType.Number, 1f));
            GraphNode state = graph.AddNode(AnimationNodes.GetParameter, Vector2.Zero);
            state.Values["Variable"] = "State";
            GraphNode choose = graph.AddNode(AnimationNodes.Switch, Vector2.Zero);
            choose.Values["Fade"] = 0f;
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            graph.Link(state.Id, "Value", choose.Id, "Which");
            for (int i = 1; i <= 3; i++)
            {
                GraphNode clip = ClipNode(graph, SaveClip(Slide(i * 10f, i * 10f), "State" + i));
                Assert.True(graph.Link(clip.Id, "Pose", choose.Id, $"Pose {i}"));
            }

            graph.Link(choose.Id, "Pose", output.Id, "Pose");

            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.AnimationGraph = graph);
            Step(0f);
            Step();
            Assert.Equal(10f, arm.Transform.Position.X, 3);
            animator.Set("State", 3f);
            Step();
            Assert.Equal(30f, arm.Transform.Position.X, 3);
        }

        [Fact]
        public void A_layer_moves_only_the_object_its_from_and_whats_under_it()
        {
            var clip = new AnimationClip { Length = 1f };
            clip.AddTrack("Arm", "Position", 3).SetKey(0f, new Vector4(1f, 0f, 0f, 0f));
            clip.AddTrack("Arm/Hand", "Position", 3).SetKey(0f, new Vector4(2f, 0f, 0f, 0f));
            clip.AddTrack("Leg", "Position", 3).SetKey(0f, new Vector4(3f, 0f, 0f, 0f));

            var graph = new Graph(AnimationNodes.Kind);
            GraphNode rest = graph.AddNode(AnimationNodes.RestPose, Vector2.Zero);
            GraphNode wave = ClipNode(graph, SaveClip(clip, "Wave"));
            GraphNode layer = graph.AddNode(AnimationNodes.Layer, Vector2.Zero);
            layer.Values["From"] = "Arm";
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            graph.Link(rest.Id, "Pose", layer.Id, "Base");
            graph.Link(wave.Id, "Pose", layer.Id, "Layer");
            graph.Link(layer.Id, "Pose", output.Id, "Pose");

            (GameObject root, GameObject arm) = Character();
            var hand = new GameObject("Hand");
            hand.Transform.Parent = arm.Transform;
            var leg = new GameObject("Leg");
            leg.Transform.Parent = root.Transform;
            root.AddComponent<Animator>(a => a.AnimationGraph = graph);
            Step(0f);
            Step();
            Assert.Equal(1f, arm.Transform.Position.X);
            Assert.Equal(2f, hand.Transform.Position.X);
            Assert.Equal(0f, leg.Transform.Position.X);
        }

        [Fact]
        public void Play_once_plays_an_action_over_the_pose_when_a_trigger_is_set_then_blends_back()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Attack", AnimationNodes.Trigger, false));
            GraphNode attack = graph.AddNode(AnimationNodes.GetParameter, Vector2.Zero);
            attack.Values["Variable"] = "Attack";
            GraphNode idle = ClipNode(graph, SaveClip(Slide(0f, 0f), "Idle"));
            GraphNode swing = ClipNode(graph, SaveClip(Slide(6f, 6f, loop: false), "Swing"));
            GraphNode once = graph.AddNode(AnimationNodes.PlayOnce, Vector2.Zero);
            once.Values["Fade In"] = 0f;
            once.Values["Fade Out"] = 0f;
            GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);
            Assert.True(graph.Link(attack.Id, "Value", once.Id, "Play"));
            graph.Link(idle.Id, "Pose", once.Id, "Base");
            graph.Link(swing.Id, "Pose", once.Id, "Action");
            graph.Link(once.Id, "Pose", output.Id, "Pose");

            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.AnimationGraph = graph);
            Step(0f);
            Step(0.1f);
            Assert.Equal(0f, arm.Transform.Position.X);

            animator.SetTrigger("Attack");
            Step(0.1f);
            Assert.Equal(6f, arm.Transform.Position.X, 3);
            Assert.Equal(false, animator.Get("Attack"));
            Step(0.5f);
            Assert.Equal(6f, arm.Transform.Position.X, 3);
            Step(0.5f);
            Assert.Equal(0f, arm.Transform.Position.X, 3);
        }

        [Fact]
        public void Each_object_can_start_an_animation_graphs_parameters_at_its_own_values()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            (GameObject root, _) = Character();
            Animator animator = root.AddComponent<Animator>(a =>
            {
                a.AnimationGraph = graph;
                a.Parameters.Add(new Scripting.ScriptVariableValue("Speed", PortType.Number, 3f));
            });
            Assert.Equal(3f, animator.Get("Speed"));
        }

        [Fact]
        public void A_visual_script_sets_an_animators_parameters_and_plays_clips_on_it()
        {
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            (GameObject root, GameObject arm) = Character();
            Animator animator = root.AddComponent<Animator>(a => a.AnimationGraph = graph);

            var script = new Graph();
            GraphNode start = script.AddNode("Events/On Start", Vector2.Zero);
            GraphNode set = script.AddNode("Animation/Set Animation Parameter", Vector2.Zero);
            set.Values["Parameter"] = "Speed";
            set.Values["Value"] = "2.5";
            GraphNode play = script.AddNode("Animation/Play Animation", Vector2.Zero);
            play.Values["Animation"] = SaveClip(Slide(7f, 7f), "Wave");
            play.Values["Fade"] = 0f;
            script.Link(start.Id, "Then", set.Id, "In");
            script.Link(set.Id, "Then", play.Id, "In");
            root.AddComponent<Scripting.VisualScript>(s => s.Script = script);

            Step(0f);
            Step();
            Assert.Equal(2.5f, animator.Get("Speed"));
            Assert.Equal(7f, arm.Transform.Position.X, 3);
            Assert.Null(script.Error(set.Id));
            Assert.Null(script.Error(play.Id));
        }

        [Fact]
        public void A_clips_events_reach_the_visual_scripts_on_its_object_as_events()
        {
            AnimationClip clip = Slide(0f, 1f);
            clip.Events.Add(new AnimationEvent(0.1f, "Footstep"));
            (GameObject root, _) = Character();
            root.AddComponent<Animator>(a => a.Clip = clip);

            var script = new Graph();
            script.Variables.Add(new GraphVariable("Steps", PortType.Number, 0f));
            GraphNode heard = script.AddNode("Events/On Event", Vector2.Zero);
            heard.Values["Name"] = "Footstep";
            GraphNode count = script.AddNode("Variables/Set Variable", Vector2.Zero);
            count.Values["Variable"] = "Steps";
            count.Values["Value"] = 1f;
            script.Link(heard.Id, "Then", count.Id, "In");
            Scripting.VisualScript visual = root.AddComponent<Scripting.VisualScript>(s => s.Script = script);

            Step(0f);
            Step(0.2f);
            Assert.Equal(1f, visual.GetVariable("Steps"));
        }

        [Fact]
        public void Every_animation_node_works_out_without_going_wrong()
        {
            string clipPath = SaveClip(Slide(0f, 1f), "Any");
            foreach (NodeType type in AnimationNodes.Library.All)
            {
                if (type.Id == AnimationNodes.Output) continue;

                var graph = new Graph(AnimationNodes.Kind);
                graph.Variables.Add(new GraphVariable("Value", PortType.Number, 1f));
                GraphNode node = graph.AddNode(type.Id, Vector2.Zero);
                if (type.Id == AnimationNodes.GetParameter) node.Values["Variable"] = "Value";
                GraphNode output = graph.AddNode(AnimationNodes.Output, Vector2.Zero);

                // Every pose input gets a clip.
                foreach (Port input in type.InputsOf(graph, node).Where(p => p.Type == AnimationNodes.Pose).ToList())
                {
                    GraphNode clip = ClipNode(graph, clipPath);
                    graph.Link(clip.Id, "Pose", node.Id, input.Name);
                }

                if (type.Id == AnimationNodes.Clip) node.Values["Animation"] = clipPath;

                Port result = type.Outputs[0];
                if (result.Type == AnimationNodes.Pose)
                {
                    Assert.True(graph.Link(node.Id, result.Name, output.Id, "Pose"), type.Id);
                }
                else
                {
                    GraphNode blend = graph.AddNode(result.Type == PortType.Boolean ? AnimationNodes.BlendByBool : AnimationNodes.Blend, Vector2.Zero);
                    Assert.True(graph.Link(node.Id, result.Name, blend.Id, result.Type == PortType.Boolean ? "Condition" : "Weight"), type.Id);
                    graph.Link(blend.Id, "Pose", output.Id, "Pose");
                }

                Scene.Active.Unload();
                (GameObject root, _) = Character();
                root.AddComponent<Animator>(a => a.AnimationGraph = graph);
                for (int frame = 0; frame < 5; frame++) Step(0.1f);
                foreach (GraphNode each in graph.Nodes) Assert.True(graph.Error(each.Id) == null, $"{type.Id}: {graph.Error(each.Id)}");
            }
        }

        // ---------------------------------------------------------------- Models with bones

        [Fact]
        public void A_model_with_bones_brings_its_skeleton_skin_and_animations_and_its_mesh_bends_with_the_bones()
        {
            string path = SkinnedLeg.Write(_folder);
            GameObject model = Model.Load(path);
            Scene.Active.ApplyChanges();

            Animator animator = Assert.IsType<Animator>(model.GetComponent<Animator>());
            IReadOnlyList<AnimationClip> clips = Model.LoadAnimations(path);
            Assert.Equal(["Bend", "Lift"], clips.Select(c => c.Name));
            Assert.Equal(1f, clips[0].Length, 3);
            Assert.Same(clips[0], animator.Clip);
            Assert.Same(clips[1], Assets.LoadAnimation(path + "#Lift"));
            Assert.True(clips[0].IsImported);

            SkinnedMeshRenderer leg = Scene.Active.FindComponents<SkinnedMeshRenderer>().Single();
            Assert.Equal(["Hip", "Knee"], leg.Bones.Select(b => b!.GameObject.Name));

            // At rest, it's as it was made: a column two tall.
            Mesh rest = leg.DrawnMesh!;
            Assert.Equal(2f, rest.Vertices.Max(v => v.Position.Y), 3);

            // Bent: the knee turns a quarter round, so the top ring swings out to the side; the middle ring, moved half by
            // each bone, goes halfway.
            animator.Sample(clips[0], 1f);
            Mesh bent = leg.DrawnMesh!;
            Assert.True(bent.Version > 0);
            Assert.Contains(bent.Vertices, v => Vector3.Distance(v.Position, new Vector3(-1f, 1.1f, 0.1f)) < 0.01f);
            Assert.Contains(bent.Vertices, v => Vector3.Distance(v.Position, new Vector3(0.05f, 1.05f, 0.1f)) < 0.01f);
            Assert.DoesNotContain(bent.Vertices, v => v.Position.Y > 1.5f);
            Assert.True(bent.BoundsCentre.X < -0.1f);

            // Lift moves the hip, so everything goes up.
            animator.Sample(clips[1], 2f);
            Assert.Equal(3f, leg.DrawnMesh!.Vertices.Max(v => v.Position.Y), 3);
        }

        [Fact]
        public void A_saved_scene_brings_a_skinned_model_back_bending_with_its_bones()
        {
            string path = SkinnedLeg.Write(_folder);
            GameObject model = Model.Load(path);
            string json = SceneFile.Save(Scene.Active, new[] { _folder });
            Assert.Contains("\"animation\": \"file:Leg.gltf#Bend\"", json);

            var scene = new Scene();
            SceneFile.LoadJson(json, scene, _folder);
            scene.ApplyChanges();
            SkinnedMeshRenderer leg = scene.GameObjects.SelectMany(o => o.Components).OfType<SkinnedMeshRenderer>().Single();
            Assert.NotNull(leg.Mesh!.Skin);
            Animator animator = scene.GameObjects.Select(o => o.GetComponent<Animator>()).Single(a => a != null)!;
            animator.Sample(animator.Clip!, 1f);
            Assert.DoesNotContain(leg.DrawnMesh!.Vertices, v => v.Position.Y > 1.5f);
        }

        /// <summary>
        /// A glTF of a leg: a column two tall, of three rings, bent by a Hip bone at the bottom and a Knee bone halfway up
        /// (the middle ring half by each). Its animations: Bend turns the knee a quarter round Z in a second; Lift raises
        /// the hip by one in two seconds.
        /// </summary>
        private static class SkinnedLeg
        {
            public static string Write(string folder)
            {
                var data = new MemoryStream();
                var writer = new BinaryWriter(data);
                var views = new List<(int Offset, int Length)>();
                int View(Action write)
                {
                    int start = (int)data.Position;
                    write();
                    views.Add((start, (int)data.Position - start));
                    return views.Count - 1;
                }

                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                Vector2[] corners = [new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)];
                for (int ring = 0; ring < 3; ring++)
                {
                    foreach (Vector2 corner in corners)
                    {
                        positions.Add(new Vector3(corner.X, ring, corner.Y));
                        normals.Add(Vector3.Normalize(new Vector3(corner.X, 0f, corner.Y)));
                    }
                }

                var indices = new List<ushort>();
                for (int ring = 0; ring < 2; ring++)
                {
                    for (int side = 0; side < 4; side++)
                    {
                        ushort a = (ushort)(ring * 4 + side), b = (ushort)(ring * 4 + (side + 1) % 4);
                        ushort c = (ushort)(a + 4), d = (ushort)(b + 4);
                        indices.AddRange([a, c, b, b, c, d]);
                    }
                }

                int positionView = View(() => { foreach (Vector3 p in positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); } });
                int normalView = View(() => { foreach (Vector3 n in normals) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); } });
                int jointView = View(() =>
                {
                    for (int i = 0; i < 12; i++) writer.Write(new byte[] { 0, 1, 0, 0 });
                });
                int weightView = View(() =>
                {
                    for (int i = 0; i < 12; i++)
                    {
                        int ring = i / 4;
                        float knee = ring * 0.5f;
                        writer.Write(1f - knee);
                        writer.Write(knee);
                        writer.Write(0f);
                        writer.Write(0f);
                    }
                });
                int indexView = View(() => { foreach (ushort i in indices) writer.Write(i); });
                int bindView = View(() =>
                {
                    float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
                    float[] knee = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -1, 0, 1];
                    foreach (float f in identity) writer.Write(f);
                    foreach (float f in knee) writer.Write(f);
                });
                int bendTimes = View(() => { writer.Write(0f); writer.Write(1f); });
                float s = MathF.Sin(MathF.PI / 4f), co = MathF.Cos(MathF.PI / 4f);
                int bendTurns = View(() => { foreach (float f in new[] { 0f, 0f, 0f, 1f, 0f, 0f, s, co }) writer.Write(f); });
                int liftTimes = View(() => { writer.Write(0f); writer.Write(2f); });
                int liftMoves = View(() => { foreach (float f in new[] { 0f, 0f, 0f, 0f, 1f, 0f }) writer.Write(f); });

                object Accessor(int view, int componentType, int count, string type, float[]? min = null, float[]? max = null) => min == null
                    ? new { bufferView = view, componentType, count, type }
                    : new { bufferView = view, componentType, count, type, min, max };

                var gltf = new
                {
                    asset = new { version = "2.0" },
                    scene = 0,
                    scenes = new[] { new { nodes = new[] { 0 } } },
                    nodes = new object[]
                    {
                        new { name = "Rig", children = new[] { 1, 3 } },
                        new { name = "Hip", children = new[] { 2 } },
                        new { name = "Knee", translation = new[] { 0f, 1f, 0f } },
                        new { name = "Leg", mesh = 0, skin = 0 }
                    },
                    meshes = new[]
                    {
                        new { name = "Leg", primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = 0, ["NORMAL"] = 1, ["JOINTS_0"] = 2, ["WEIGHTS_0"] = 3 }, indices = 4 } } }
                    },
                    skins = new[] { new { joints = new[] { 1, 2 }, inverseBindMatrices = 5 } },
                    animations = new[]
                    {
                        new { name = "Bend", samplers = new[] { new { input = 6, output = 7, interpolation = "LINEAR" } }, channels = new[] { new { sampler = 0, target = new { node = 2, path = "rotation" } } } },
                        new { name = "Lift", samplers = new[] { new { input = 8, output = 9, interpolation = "LINEAR" } }, channels = new[] { new { sampler = 0, target = new { node = 1, path = "translation" } } } }
                    },
                    accessors = new[]
                    {
                        Accessor(positionView, 5126, 12, "VEC3", [-0.1f, 0f, -0.1f], [0.1f, 2f, 0.1f]),
                        Accessor(normalView, 5126, 12, "VEC3"),
                        Accessor(jointView, 5121, 12, "VEC4"),
                        Accessor(weightView, 5126, 12, "VEC4"),
                        Accessor(indexView, 5123, indices.Count, "SCALAR"),
                        Accessor(bindView, 5126, 2, "MAT4"),
                        Accessor(bendTimes, 5126, 2, "SCALAR", [0f], [1f]),
                        Accessor(bendTurns, 5126, 2, "VEC4"),
                        Accessor(liftTimes, 5126, 2, "SCALAR", [0f], [2f]),
                        Accessor(liftMoves, 5126, 2, "VEC3")
                    },
                    bufferViews = views.Select(v => new { buffer = 0, byteOffset = v.Offset, byteLength = v.Length }).ToArray(),
                    buffers = new[] { new { byteLength = (int)data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()) } }
                };

                string path = Path.Combine(folder, "Leg.gltf");
                File.WriteAllText(path, JsonSerializer.Serialize(gltf));
                return path;
            }
        }
    }
}
