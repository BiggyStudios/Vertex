using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Graphs;
using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.Scripting;
using Vertex.UI;

namespace Vertex.Editor.Tests
{
    /// <summary>Editing graphs (visual scripts) in their tab.</summary>
    public class GraphEditorTests : EditorTest
    {
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

        private sealed class Setup
        {
            public readonly EditorContext Context = new();
            public readonly Graph Graph = new(ScriptNodes.Kind);
            public readonly GraphDocument Doc;
            public readonly GraphView View;

            public Setup()
            {
                Doc = new GraphDocument(Graph, "Test.vscript");
                View = new GraphView(Context);
            }

            public void Frame(int count = 1)
            {
                for (int i = 0; i < count; i++) UiFrame(() => View.Draw(_area, Doc));
            }

            public Vector2 Port(GraphNode node, string port, bool output) => View.PortOnScreen(Doc, node.Id, port, output)!.Value;

            /// <summary>Presses at one point, moves through to another, and lets go there.</summary>
            public void Drag(Vector2 from, Vector2 to, MouseButton button = MouseButton.Left)
            {
                InputSystem.MoveMouse(from);
                Frame();
                InputSystem.PressButton(button);
                Frame();
                for (int i = 1; i <= 4; i++)
                {
                    InputSystem.MoveMouse(Vector2.Lerp(from, to, i / 4f));
                    Frame();
                }

                InputSystem.ReleaseButton(button);
                Frame();
            }

            public void Type(string text)
            {
                foreach (char c in text) InputSystem.TypeCharacter(c);
                Frame();
            }

            public void Press(Key key)
            {
                InputSystem.PressKey(key);
                Frame();
                InputSystem.ReleaseKey(key);
                Frame();
            }
        }

        [Fact]
        public void Dragging_from_an_output_to_an_input_links_them_and_undo_takes_it_back()
        {
            var setup = new Setup();
            GraphNode add = setup.Graph.AddNode("Math/Add", new Vector2(0f, 0f));
            GraphNode print = setup.Graph.AddNode("Actions/Print", new Vector2(320f, 0f));
            setup.Frame(2);

            setup.Drag(setup.Port(add, "Result", output: true), setup.Port(print, "Value", output: false));

            Assert.Single(setup.Graph.LinksTo(print.Id, "Value"));
            setup.Context.Undo();
            Assert.Empty(setup.Graph.Links);
            setup.Context.Redo();
            Assert.Single(setup.Graph.Links);

            // Dragging the linked input away unlinks it.
            setup.Frame();
            setup.Drag(setup.Port(print, "Value", output: false), new Vector2(700f, 600f));
            Assert.Empty(setup.Graph.Links);
            Assert.True(Popup.AnyOpen);
        }

        [Fact]
        public void A_link_let_go_of_on_nothing_offers_nodes_that_fit_and_links_the_one_chosen()
        {
            var setup = new Setup();
            GraphNode start = setup.Graph.AddNode("Events/On Start", new Vector2(0f, 0f));
            setup.Frame(2);

            setup.Drag(setup.Port(start, "Then", output: true), new Vector2(700f, 400f));
            Assert.True(Popup.AnyOpen);

            setup.Type("Print");
            setup.Press(Key.Enter);

            GraphNode print = Assert.Single(setup.Graph.Nodes, n => n.Type == "Actions/Print");
            Assert.Single(setup.Graph.LinksTo(print.Id, "In"));
            Assert.Contains(print.Id, setup.Doc.Selected);

            // Adding it (and its link) is one undo.
            setup.Context.Undo();
            Assert.Single(setup.Graph.Nodes);
            Assert.Empty(setup.Graph.Links);
        }

        [Fact]
        public void Right_click_adds_a_node_and_delete_takes_the_selected_away()
        {
            var setup = new Setup();
            setup.Graph.AddNode("Events/On Start", new Vector2(0f, 0f));
            setup.Frame(2);

            InputSystem.MoveMouse(new Vector2(900f, 650f));
            setup.Frame();
            InputSystem.PressButton(MouseButton.Right);
            setup.Frame();
            InputSystem.ReleaseButton(MouseButton.Right);
            setup.Frame(2);
            Assert.True(Popup.AnyOpen);

            setup.Type("Branch");
            setup.Press(Key.Enter);
            GraphNode branch = Assert.Single(setup.Graph.Nodes, n => n.Type == "Flow/Branch");
            Assert.Equal([branch.Id], setup.Doc.Selected);

            setup.Press(Key.Delete);
            Assert.DoesNotContain(setup.Graph.Nodes, n => n.Type == "Flow/Branch");
            setup.Context.Undo();
            Assert.Contains(setup.Graph.Nodes, n => n.Type == "Flow/Branch");
        }

        [Fact]
        public void A_value_typed_into_a_node_is_kept_and_undone_in_one_go()
        {
            var setup = new Setup();
            GraphNode add = setup.Graph.AddNode("Math/Add", new Vector2(0f, 0f));
            setup.Frame(2);

            int view = Ui.Id(RuntimeHelpers.GetHashCode(setup.Doc), "graphView");
            int field = Ui.Id(Ui.Id(view, add.Id), "value.A");
            NumberField.BeginTyping(field);
            setup.Frame();
            setup.Type("4.5");
            setup.Press(Key.Enter);

            Assert.Equal(4.5f, add.Values["A"]);
            setup.Context.Undo();
            Assert.False(add.Values.ContainsKey("A"));
        }

        [Fact]
        public void Nodes_dragged_by_their_header_move_together_and_a_box_selects()
        {
            var setup = new Setup();
            GraphNode first = setup.Graph.AddNode("Events/On Start", new Vector2(0f, 0f));
            GraphNode second = setup.Graph.AddNode("Events/On Update", new Vector2(0f, 200f));
            setup.Frame(2);

            // A box round both.
            Vector2 topLeft = setup.Port(first, "Then", output: true) + new Vector2(-260f, -80f);
            Vector2 bottomRight = setup.Port(second, "Then", output: true) + new Vector2(40f, 80f);
            setup.Drag(topLeft, bottomRight);
            Assert.Equal(2, setup.Doc.Selected.Count);

            // Dragged by the first's title: both move as far.
            Vector2 header = setup.Port(first, "Then", output: true) + new Vector2(-60f, -24f * setup.Doc.Zoom);
            setup.Drag(header, header + new Vector2(100f, 50f));
            Vector2 moved = first.Position;
            Assert.NotEqual(Vector2.Zero, moved);
            Assert.Equal(second.Position - new Vector2(0f, 200f), moved);

            setup.Context.Undo();
            Assert.Equal(Vector2.Zero, first.Position);
            Assert.Equal(new Vector2(0f, 200f), second.Position);
        }

        [Fact]
        public void A_graph_opens_in_a_tab_beside_the_scene_and_play_goes_back_to_it_after()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexEditorTests", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new EditorApp();
                string path = app.Context.CreateVisualScript(folder);
                app.Context.OpenGraph(path);
                Frame(app, 2);
                Assert.NotNull(app.SceneView.ShowingGraph);
                Assert.Equal(3, app.SceneView.ShowingGraph!.Graph.Nodes.Count);

                app.Context.Play.Start();
                Frame(app);
                Assert.Null(app.SceneView.ShowingGraph);
                app.Context.Play.Stop();
                Frame(app);
                Assert.NotNull(app.SceneView.ShowingGraph);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }
}
