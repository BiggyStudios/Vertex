using System.Numerics;

using Vertex.Animation;
using Vertex.Editor.Editing;
using Vertex.Editor.Graphs;
using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Making animations in the Animation panel, and animation graphs in their tab.</summary>
    public class AnimationEditorTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public AnimationEditorTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            foreach (string file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories)) Assets.Forget(file);
            Directory.Delete(_folder, recursive: true);
        }

        /// <summary>A robot with an arm under it, whose Animator plays an empty animation saved in the test's folder.</summary>
        private (EditorApp App, GameObject Arm, AnimationClip Clip, string Path) Robot()
        {
            var app = new EditorApp();
            string path = app.Context.CreateAnimation(_folder, "Wave");
            AnimationClip clip = Assets.LoadAnimation(path);
            Scene scene = app.Context.Scene;
            var root = new GameObject("Robot", scene);
            root.AddComponent<Animator>(a => a.Clip = clip);
            var arm = new GameObject("Arm", scene);
            arm.Transform.Parent = root.Transform;
            arm.Transform.Position = new Vector3(0f, 1f, 0f);
            scene.ApplyChanges();
            app.Context.Selection.Select(arm);
            app.Context.OpenAnimation(path);
            Frame(app, 2);
            return (app, arm, clip, path);
        }

        private static void Move(EditorApp app, GameObject gameObject, Vector3 to)
        {
            var before = SetPose.Of(gameObject.Transform);
            gameObject.Transform.Position = to;
            app.Context.History.Record(new SetPose($"Move {gameObject.Name}", gameObject.Transform, before, SetPose.Of(gameObject.Transform)));
        }

        [Fact]
        public void Recording_turns_a_move_into_keys_at_the_playhead_and_one_undo_takes_back_both()
        {
            (EditorApp app, GameObject arm, AnimationClip clip, _) = Robot();
            Assert.Same(clip, app.Animation.Clip);
            Assert.True(app.Dock.IsOpen("Animation"));

            app.Animation.Time = 0.5f;
            app.Animation.Recording = true;
            Frame(app);
            Move(app, arm, new Vector3(2f, 1f, 0f));
            Frame(app, 2);

            // Where it was, at the start; where it was moved to, at the playhead.
            AnimationTrack track = Assert.IsType<AnimationTrack>(clip.Track("Arm", "Position"));
            Assert.Equal([0f, 0.5f], track.Keys.Select(k => k.Time));
            Assert.Equal(new Vector4(0f, 1f, 0f, 0f), track.Keys[0].Value);
            Assert.Equal(new Vector4(2f, 1f, 0f, 0f), track.Keys[1].Value);
            Assert.Equal(new Vector3(2f, 1f, 0f), arm.Transform.Position);

            // Scrubbing back shows the animation in between.
            app.Animation.Time = 0.25f;
            Frame(app);
            Assert.Equal(1f, arm.Transform.Position.X, 3);

            // The move and its keys are one undo.
            app.Context.Undo();
            Frame(app);
            Assert.Null(clip.Track("Arm", "Position"));
            Assert.Equal(new Vector3(0f, 1f, 0f), arm.Transform.Position);
            Assert.Null(app.Context.History.NextUndo);
        }

        [Fact]
        public void The_preview_puts_the_objects_back_when_something_else_is_selected_and_before_saving()
        {
            (EditorApp app, GameObject arm, AnimationClip clip, _) = Robot();
            AnimationTrack track = clip.AddTrack("Arm", "Position", 3);
            track.SetKey(0f, new Vector4(0f, 1f, 0f, 0f));
            track.SetKey(1f, new Vector4(5f, 1f, 0f, 0f));
            clip.Changed();

            app.Animation.Time = 1f;
            Frame(app);
            Assert.Equal(5f, arm.Transform.Position.X, 3);

            // The scene is saved as it is, not as the preview poses it.
            app.Context.SettleScene();
            Assert.Equal(0f, arm.Transform.Position.X);
            Frame(app);
            Assert.Equal(5f, arm.Transform.Position.X, 3);

            app.Context.Selection.Clear();
            Frame(app);
            Assert.Equal(new Vector3(0f, 1f, 0f), arm.Transform.Position);
        }

        [Fact]
        public void A_key_dragged_along_the_timeline_snaps_to_frames_and_delete_removes_the_keys_picked()
        {
            (EditorApp app, GameObject arm, AnimationClip clip, _) = Robot();
            AnimationTrack track = clip.AddTrack("Arm", "Position", 3);
            track.SetKey(0f, new Vector4(0f, 1f, 0f, 0f));
            track.SetKey(0.5f, new Vector4(3f, 1f, 0f, 0f));
            clip.Changed();
            Frame(app, 2);

            Vector2 key = app.Animation.KeyOnScreen(track, 1)!.Value;
            Vector2 start = app.Animation.KeyOnScreen(track, 0)!.Value;
            float pixelsPerSecond = (key.X - start.X) / 0.5f;
            InputSystem.MoveMouse(key);
            Frame(app);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(key + new Vector2(pixelsPerSecond * 0.25f * i / 4f + 2f, 0f));
                Frame(app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app);

            // A quarter of a second on, on a frame (30 a second).
            Assert.Equal(2, track.Keys.Count);
            float moved = track.Keys[1].Time;
            Assert.InRange(moved, 0.74f, 0.77f);
            Assert.Equal(0f, MathF.Round(moved * 30f) - moved * 30f, 3);
            Assert.Equal("Move Keys", app.Context.History.NextUndo);

            // It's still picked: Delete takes it away.
            InputSystem.MoveMouse(app.Animation.KeyOnScreen(track, 1)!.Value + new Vector2(0f, 30f));
            Frame(app);
            Press(Key.Delete);
            Frame(app);
            ReleaseAll();
            Assert.Single(track.Keys);

            app.Context.Undo();
            app.Context.Undo();
            Assert.Equal([0f, 0.5f], track.Keys.Select(k => k.Time));
        }

        [Fact]
        public void A_changed_animation_is_saved_to_its_file_as_its_edited()
        {
            (EditorApp app, GameObject arm, AnimationClip clip, string path) = Robot();
            app.Animation.Recording = true;
            app.Animation.Time = 0.4f;
            Frame(app);
            Move(app, arm, new Vector3(0f, 3f, 0f));
            Frame(app, 3);

            var saved = AnimationClip.Load(path);
            Assert.NotNull(saved.Track("Arm", "Position"));
        }

        [Fact]
        public void Animations_and_animation_graphs_are_kinds_of_asset_and_a_models_own_animation_is_part_of_it()
        {
            Assert.Equal(AssetKind.Animation, AssetDatabase.KindOf("Walk.anim"));
            Assert.Equal(AssetKind.AnimationGraph, AssetDatabase.KindOf("Robot.animgraph"));
            Assert.Equal(AssetKind.Animation, AssetDatabase.KindOf(Path.Combine(_folder, "Robot.glb#Run")));
            var part = new AssetEntry(Path.Combine(_folder, "Robot.glb#Run"), AssetKind.Animation);
            Assert.Equal("Run", part.NameWithoutExtension);
            Assert.Equal("Robot", part.PartOf);

            var context = new EditorContext();
            string graph = context.CreateAnimationGraph(_folder);
            Graph loaded = Assets.LoadGraph(graph);
            Assert.Equal(AnimationNodes.Kind, loaded.Kind);
            Assert.Equal("Speed", Assert.Single(loaded.Variables).Name);
        }

        [Fact]
        public void Animations_dropped_onto_a_blend_are_linked_into_its_next_free_pose()
        {
            var context = new EditorContext();
            Graph graph = AnimationNodes.Starter();
            var doc = new GraphDocument(graph, Path.Combine(_folder, "Moves.animgraph"));
            var view = new GraphView(context);
            var area = new Rect(0f, 0f, 1200f, 800f);
            void Draw(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    Time.Advance(1.0 / 60.0);
                    Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
                    Ui.BeginFrame(1f / 60f);
                    view.Draw(area, doc);
                    Ui.EndFrame();
                    Canvas.EndFrame();
                    InputSystem.EndFrame();
                }
            }

            string clip = context.CreateAnimation(_folder, "Sprint");
            Draw(2);
            GraphNode blend = graph.Nodes.Single(n => n.Type == AnimationNodes.BlendByValue);
            Vector2 to = view.PortOnScreen(doc, blend.Id, "Pose 4", output: false)!.Value + new Vector2(40f, 0f);

            var from = new Vector2(600f, 780f);
            InputSystem.MoveMouse(from);
            Draw();
            InputSystem.PressButton(MouseButton.Left);
            Draw();
            DragDrop.Begin(new AssetEntry(clip, AssetKind.Animation), "Sprint");
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                Draw();
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Draw();

            GraphNode added = graph.Nodes.Last();
            Assert.Equal(AnimationNodes.Clip, added.Type);
            Assert.EndsWith("Sprint.anim", (string)added.Values["Animation"]!);
            Assert.Single(graph.LinksTo(blend.Id, "Pose 4"), l => l.FromNode == added.Id);
        }

        /// <summary>Presses at <paramref name="from"/>, drags to <paramref name="to"/> in a few steps and lets go.</summary>
        private static void Drag(EditorApp app, Vector2 from, Vector2 to)
        {
            InputSystem.MoveMouse(from);
            Frame(app);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                Frame(app);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app);
        }

        [Fact]
        public void On_the_curves_a_key_dragged_up_changes_its_value_and_is_one_undo()
        {
            (EditorApp app, _, AnimationClip clip, _) = Robot();
            AnimationTrack track = clip.AddTrack("Arm", "Position", 3);
            track.SetKey(0f, new Vector4(0f, 1f, 0f, 0f));
            track.SetKey(0.5f, new Vector4(3f, 1f, 0f, 0f));
            clip.Changed();
            app.Animation.Curves = true;
            Frame(app, 2);

            // How many pixels a unit, from the x curve's two keys.
            Vector2 low = app.Animation.CurveKeyOnScreen(track, 0, 0)!.Value, high = app.Animation.CurveKeyOnScreen(track, 1, 0)!.Value;
            float pixelsPerUnit = (low.Y - high.Y) / 3f;
            Assert.True(pixelsPerUnit > 1f);

            Drag(app, high, high - new Vector2(0f, pixelsPerUnit));

            Assert.Equal(4f, track.Keys[1].Value.X, 1);
            // Only the x was dragged: y stays.
            Assert.Equal(1f, track.Keys[1].Value.Y);
            Assert.Equal(0.5f, track.Keys[1].Time, 3);
            Assert.Equal("Move Keys", app.Context.History.NextUndo);

            app.Context.Undo();
            Assert.Equal(3f, track.Keys[1].Value.X);
        }

        [Fact]
        public void Dragging_a_picked_keys_handle_sets_its_slopes_both_sides()
        {
            (EditorApp app, _, AnimationClip clip, _) = Robot();
            AnimationTrack track = clip.AddTrack("Arm", "Position", 3);
            track.SetKey(0f, new Vector4(0f, 1f, 0f, 0f));
            track.SetKey(0.5f, new Vector4(3f, 1f, 0f, 0f));
            track.SetKey(1f, new Vector4(0f, 1f, 0f, 0f));
            clip.Changed();
            app.Animation.Curves = true;
            Frame(app, 2);

            // Picked, the middle key shows its handles: flat, as it's a peak.
            Vector2 key = app.Animation.CurveKeyOnScreen(track, 1, 0)!.Value;
            InputSystem.MoveMouse(key);
            Frame(app);
            InputSystem.PressButton(MouseButton.Left);
            Frame(app);
            InputSystem.ReleaseButton(MouseButton.Left);
            Frame(app);
            Vector2 handle = Assert.NotNull(app.Animation.HandleOnScreen(track, 1, 0, outgoing: true));
            Assert.Equal(key.Y, handle.Y, 1f);

            // Dragged down to the right: the curve leaves the key going down, and arrives going down too.
            Drag(app, handle, handle + new Vector2(0f, 30f));

            Keyframe changed = track.Keys[1];
            Assert.True(changed.HasSlopes);
            Assert.True(changed.OutSlope!.Value.X < -0.5f, $"Slope {changed.OutSlope}");
            Assert.Equal(changed.OutSlope.Value.X, changed.InSlope!.Value.X, 4);
            Assert.True(track.Sample(0.6f).X < 3f);
            Assert.Equal("Change Slope", app.Context.History.NextUndo);

            app.Context.Undo();
            Assert.False(track.Keys[1].HasSlopes);
        }

        [Fact]
        public void A_state_machine_opens_from_its_node_takes_animations_dropped_in_as_states_and_joins_them_with_a_drag()
        {
            var context = new EditorContext();
            var graph = new Graph(AnimationNodes.Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            GraphNode machine = graph.AddNode(AnimationNodes.StateMachine, Vector2.Zero);
            GraphNode output = graph.AddNode(AnimationNodes.Output, new Vector2(400f, 0f));
            graph.Link(machine.Id, "Pose", output.Id, "Pose");
            var doc = new GraphDocument(graph, Path.Combine(_folder, "Moves.animgraph"));
            var view = new GraphView(context);
            var area = new Rect(0f, 0f, 1200f, 800f);
            void Draw(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    Time.Advance(1.0 / 60.0);
                    Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
                    Ui.BeginFrame(1f / 60f);
                    view.Draw(area, doc);
                    Ui.EndFrame();
                    Canvas.EndFrame();
                    InputSystem.EndFrame();
                }
            }

            void Click(Vector2 at)
            {
                InputSystem.MoveMouse(at);
                Draw();
                InputSystem.PressButton(MouseButton.Left);
                Draw();
                InputSystem.ReleaseButton(MouseButton.Left);
                Draw();
            }

            // Double-clicked, the node opens to show its states: a new one has Idle.
            Draw(2);
            Vector2 node = view.PortOnScreen(doc, machine.Id, "Pose", output: true)!.Value - new Vector2(60f, 0f);
            Click(node);
            Click(node);
            Assert.Equal(machine.Id, doc.OpenMachine);
            Assert.NotNull(view.StateOnScreen(doc, "Idle"));

            // An animation dropped on empty space is a state of its own, playing it.
            string run = context.CreateAnimation(_folder, "Run");
            Vector2 idle = view.StateOnScreen(doc, "Idle")!.Value;
            var from = new Vector2(idle.X, 700f);
            var to = idle + new Vector2(320f, 0f);
            InputSystem.MoveMouse(from);
            Draw();
            InputSystem.PressButton(MouseButton.Left);
            Draw();
            DragDrop.Begin(new AssetEntry(run, AssetKind.Animation), "Run");
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                Draw();
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Draw(2);

            AnimationState state = Assert.IsType<AnimationState>(AnimationStateMachine.Of(machine).State("Run"));
            Assert.EndsWith("Run.anim", state.Animation);
            // It's an input on the node too.
            Assert.Contains("Run", AnimationNodes.Library.Find(AnimationNodes.StateMachine)!.InputsOf(graph, machine).Select(p => p.Name));

            // Dragged from Idle's dot to Run: a transition, testing the first parameter, picked.
            Vector2 dot = view.StateDotOnScreen(doc, "Idle")!.Value;
            Vector2 target = view.StateOnScreen(doc, "Run")!.Value;
            InputSystem.MoveMouse(dot);
            Draw();
            InputSystem.PressButton(MouseButton.Left);
            Draw();
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(dot, target, i / 4f));
                Draw();
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Draw();

            StateTransition transition = Assert.Single(AnimationStateMachine.Of(machine).Transitions);
            Assert.Equal(("Idle", "Run"), (transition.From, transition.To));
            Assert.Equal("Speed", Assert.Single(transition.Conditions).Parameter);
            Assert.Equal(0, doc.SelectedTransition);

            // Each is one undo; Escape goes back to the graph.
            context.Undo();
            Assert.Empty(AnimationStateMachine.Of(machine).Transitions);
            context.Undo();
            Assert.Null(AnimationStateMachine.Of(machine).State("Run"));
            Press(Key.Escape);
            Draw();
            ReleaseAll();
            Assert.Null(doc.OpenMachine);
        }
    }
}
