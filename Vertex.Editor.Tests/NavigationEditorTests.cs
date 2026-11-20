using System.Diagnostics;
using System.Numerics;

using Vertex.AI;
using Vertex.Editor.Editing;
using Vertex.Editor.Graphs;
using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Physics;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Navigation and behaviour trees in the editor: baking the navmesh, showing it, and editing trees.</summary>
    public class NavigationEditorTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));

        public override void Dispose()
        {
            base.Dispose();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        /// <summary>A saved scene with a floor and a wall (colliders, no rigidbodies), opened in the editor.</summary>
        private string Yard(EditorContext context)
        {
            context.NewScene();
            var floor = new GameObject("Floor");
            floor.Transform.Position = new Vector3(0f, -0.5f, 0f);
            floor.AddComponent<BoxCollider>().Size = new Vector3(12f, 1f, 12f);
            var wall = new GameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 1f, 0f);
            wall.AddComponent<BoxCollider>().Size = new Vector3(1f, 2f, 6f);

            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, "Yard.scene");
            Vertex.Serialization.SceneFile.Save(context.Scene, path);
            context.OpenScene(path);
            return path;
        }

        private static void WaitForBake(EditorContext context)
        {
            var waited = Stopwatch.StartNew();
            while (context.BakingNavMesh)
            {
                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), "The bake didn't finish.");
                Thread.Sleep(5);
                context.Update();
            }
        }

        [Fact]
        public void Baking_keeps_the_navmesh_beside_the_scene_which_opens_with_it()
        {
            var context = new EditorContext();
            string scene = Yard(context);
            context.BakeNavMesh();
            Assert.True(context.BakingNavMesh);
            WaitForBake(context);

            string file = Path.Combine(_folder, "Yard.navmesh");
            Assert.True(File.Exists(file));
            Assert.False(context.Dirty);
            Assert.EndsWith("Yard.navmesh", context.Scene.Navigation.NavMeshFile);
            Assert.False(context.Scene.Navigation.NavMesh!.IsEmpty);

            context.OpenScene(scene);
            NavMesh? opened = context.Scene.Navigation.NavMesh;
            Assert.NotNull(opened);
            var path = new NavMeshPath();
            Assert.True(opened.FindPath(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), path));
            Assert.Equal(NavMeshPathStatus.Complete, path.Status);
            Assert.True(path.Corners.Count > 2, "It goes round the wall");

            context.ClearNavMesh();
            Assert.False(File.Exists(file));
            Assert.Equal("", context.Scene.Navigation.NavMeshFile);
        }

        [Fact]
        public void The_navmesh_shows_over_the_scene_while_the_navigation_panel_is_open()
        {
            var app = new EditorApp();
            Yard(app.Context);
            app.Context.BakeNavMesh();
            WaitForBake(app.Context);

            Frame(app, 2);
            Assert.Null(app.Context.NavigationOverlay);

            app.Dock.Toggle("Navigation");
            app.Dock.Show("Navigation");
            Frame(app, 2);
            IReadOnlyList<Vertex.Rendering.WorldQuad>? overlay = app.Context.NavigationOverlay;
            Assert.NotNull(overlay);
            Assert.NotEmpty(overlay);

            // Hidden with the panel's checkbox, and gone once the panel is.
            app.Context.Preferences.ShowNavMesh = false;
            Assert.Null(app.Context.NavigationOverlay);
            app.Context.Preferences.ShowNavMesh = true;
            app.Dock.Close("Navigation");
            Frame(app, 60);
            Assert.Null(app.Context.NavigationOverlay);
        }

        [Fact]
        public void A_new_behavior_tree_starts_as_a_guard_and_opens_in_the_graph_editor()
        {
            var app = new EditorApp();
            string path = app.Context.CreateBehaviorTree(_folder);
            Assert.EndsWith(".btree", path);
            Assert.Equal(AssetKind.BehaviorTree, AssetDatabase.KindOf(path));

            app.Context.OpenGraph(path);
            Frame(app, 2);
            Graph graph = app.SceneView.ShowingGraph!.Graph;
            Assert.Equal(BehaviorNodes.Kind, graph.Kind);
            Assert.Single(graph.Nodes, n => n.Type == "Root/Root");
            Assert.Equal("Target", Assert.Single(graph.Variables).Name);
        }

        private static readonly Rect _area = new(0f, 0f, 1200f, 800f);

        private static void UiFrame(Action draw)
        {
            Time.Advance(1.0 / 60.0);
            Canvas.BeginFrame(ScreenWidth, ScreenHeight, ScreenHeight);
            Ui.BeginFrame(1f / 60f);
            draw();
            Ui.EndFrame();
            Canvas.EndFrame();
            InputSystem.EndFrame();
        }

        [Fact]
        public void A_task_dragged_under_another_parent_leaves_its_old_one()
        {
            var context = new EditorContext();
            Graph graph = BehaviorNodes.Starter();
            var doc = new GraphDocument(graph, "Guard.btree");
            var view = new GraphView(context);
            void Frames(int count)
            {
                for (int i = 0; i < count; i++) UiFrame(() => view.Draw(_area, doc));
            }

            Frames(3);
            GraphNode wait = graph.Nodes.First(n => n.Type == "Actions/Wait");
            GraphNode chase = graph.Nodes.First(n => n.Type == "Composites/Sequence" && n.Values.ContainsKey("Recheck"));
            GraphNode idle = graph.Nodes.First(n => n.Type == "Composites/Sequence" && n != chase);
            Assert.Contains(wait, BehaviorNodes.ChildrenOf(graph, idle));

            Vector2 from = view.PortOnScreen(doc, chase.Id, "Children", output: true)!.Value;
            Vector2 to = view.PortOnScreen(doc, wait.Id, "Parent", output: false)!.Value;
            InputSystem.MoveMouse(from);
            Frames(1);
            InputSystem.PressButton(MouseButton.Left);
            Frames(1);
            for (int i = 1; i <= 4; i++)
            {
                InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                Frames(1);
            }

            InputSystem.ReleaseButton(MouseButton.Left);
            Frames(1);
            Assert.Contains(wait, BehaviorNodes.ChildrenOf(graph, chase));
            Assert.DoesNotContain(wait, BehaviorNodes.ChildrenOf(graph, idle));

            // One change to undo.
            context.Undo();
            Assert.Contains(wait, BehaviorNodes.ChildrenOf(graph, idle));
        }

        [Fact]
        public void Ai_components_have_their_own_icons_and_place_in_the_list()
        {
            Assert.Equal("AI", Inspection.ComponentCatalog.Category(typeof(NavMeshAgent)));
            Assert.Equal("AI", Inspection.ComponentCatalog.Category(typeof(BehaviorTree)));
            Assert.NotSame(ComponentIcons.For(typeof(Component)), ComponentIcons.For(typeof(BehaviorTree)));
        }
    }
}
