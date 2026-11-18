using System.Numerics;

using Vertex.Graphs;
using Vertex.Input;
using Vertex.Scripting;
using Vertex.Serialization;

namespace Vertex.Tests
{
    public class VisualScriptTests : EngineTest
    {
        private readonly List<LogEntry> _logged = new();

        public VisualScriptTests() => Log.Written += _logged.Add;

        public override void Dispose()
        {
            Log.Written -= _logged.Add;
            base.Dispose();
        }

        private static Graph NewGraph() => new(ScriptNodes.Kind);

        private static GameObject Run(Graph graph, int frames = 1)
        {
            var gameObject = new GameObject("Runner");
            gameObject.AddComponent<VisualScript>().Script = graph;
            for (int i = 0; i < frames; i++) Scene.Active.Update();
            return gameObject;
        }

        [Fact]
        public void On_start_runs_the_flow_once_with_values_worked_out_from_what_is_linked()
        {
            Graph graph = NewGraph();
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode print = graph.AddNode("Actions/Print", Vector2.Zero);
            GraphNode add = graph.AddNode("Math/Add", Vector2.Zero);
            add.Values["A"] = 2f;
            add.Values["B"] = 3f;
            Assert.True(graph.Link(start.Id, "Then", print.Id, "In"));
            Assert.True(graph.Link(add.Id, "Result", print.Id, "Value"));

            Run(graph, frames: 3);

            Assert.Equal(["5"], _logged.Where(e => e.Level == LogLevel.Info).Select(e => e.Message));
        }

        [Fact]
        public void On_update_moves_the_object_every_frame_and_a_branch_picks_a_way()
        {
            Graph graph = NewGraph();
            GraphNode update = graph.AddNode("Events/On Update", Vector2.Zero);
            GraphNode branch = graph.AddNode("Flow/Branch", Vector2.Zero);
            GraphNode compare = graph.AddNode("Logic/Compare", Vector2.Zero);
            GraphNode position = graph.AddNode("Objects/Get Position", Vector2.Zero);
            GraphNode split = graph.AddNode("Vectors/Split Vector", Vector2.Zero);
            GraphNode move = graph.AddNode("Actions/Move By", Vector2.Zero);

            // Moves right while X is below 3, then stops.
            graph.Link(update.Id, "Then", branch.Id, "In");
            graph.Link(position.Id, "Position", split.Id, "Vector");
            graph.Link(split.Id, "X", compare.Id, "A");
            compare.Values["B"] = 3f;
            compare.Values["Is"] = "Less";
            graph.Link(compare.Id, "Result", branch.Id, "Condition");
            graph.Link(branch.Id, "True", move.Id, "In");
            move.Values["Offset"] = new Vector3(1f, 0f, 0f);

            GameObject mover = Run(graph, frames: 10);

            Assert.Equal(new Vector3(3f, 0f, 0f), mover.Transform.Position);
        }

        [Fact]
        public void Variables_belong_to_each_object_and_keep_their_type()
        {
            Graph graph = NewGraph();
            graph.Variables.Add(new GraphVariable("Count", PortType.Number, 10f));
            GraphNode update = graph.AddNode("Events/On Update", Vector2.Zero);
            GraphNode set = graph.AddNode("Variables/Set Variable", Vector2.Zero);
            GraphNode get = graph.AddNode("Variables/Get Variable", Vector2.Zero);
            GraphNode add = graph.AddNode("Math/Add", Vector2.Zero);
            set.Values["Variable"] = "Count";
            get.Values["Variable"] = "Count";
            add.Values["B"] = 1f;
            graph.Link(update.Id, "Then", set.Id, "In");
            graph.Link(get.Id, "Value", add.Id, "A");
            graph.Link(add.Id, "Result", set.Id, "Value");

            GameObject first = Run(graph, frames: 3);
            var second = new GameObject("Second");
            second.AddComponent<VisualScript>().Script = graph;
            Scene.Active.Update();

            Assert.Equal(14f, first.GetComponent<VisualScript>()!.Instance!.Variables["Count"]);
            Assert.Equal(11f, second.GetComponent<VisualScript>()!.Instance!.Variables["Count"]);
        }

        [Fact]
        public void A_variable_nodes_value_takes_the_variables_type()
        {
            Graph graph = NewGraph();
            var alive = new GraphVariable("Alive", PortType.Boolean, false);
            graph.Variables.Add(alive);
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode set = graph.AddNode("Variables/Set Variable", Vector2.Zero);
            GraphNode get = graph.AddNode("Variables/Get Variable", Vector2.Zero);
            GraphNode name = graph.AddNode("Objects/Get Name", Vector2.Zero);
            GraphNode compare = graph.AddNode("Logic/Compare", Vector2.Zero);
            set.Values["Variable"] = "Alive";
            get.Values["Variable"] = "Alive";

            ScriptNode setType = (ScriptNode)ScriptNodes.Library.Find(set.Type)!;
            Assert.Same(PortType.Boolean, graph.TypeOf(set, setType.Input("Value")!));
            Assert.Same(PortType.Boolean, graph.TypeOf(get, ScriptNodes.Library.Find(get.Type)!.Output("Value")!));

            // Only what fits a true/false goes in.
            Assert.False(graph.Link(name.Id, "Name", set.Id, "Value"));
            Assert.True(graph.Link(compare.Id, "Result", set.Id, "Value"));

            // Changing the variable's type takes away links that no longer fit.
            alive.Type = PortType.Vector;
            graph.Tidy();
            Assert.Empty(graph.LinksTo(set.Id, "Value"));

            // A true/false set on the node is set as one.
            alive.Type = PortType.Boolean;
            set.Values["Value"] = true;
            graph.Link(start.Id, "Then", set.Id, "In");
            GameObject runner = Run(graph);
            Assert.Equal(true, runner.GetComponent<VisualScript>()!.Instance!.Variables["Alive"]);
        }

        [Fact]
        public void Key_events_run_when_their_key_goes_down()
        {
            Graph graph = NewGraph();
            GraphNode pressed = graph.AddNode("Events/On Key Pressed", Vector2.Zero);
            pressed.Values["Key"] = "J";
            GraphNode print = graph.AddNode("Actions/Print", Vector2.Zero);
            print.Values["Value"] = "jump";
            graph.Link(pressed.Id, "Then", print.Id, "In");

            GameObject runner = Run(graph, frames: 2);
            InputSystem.PressKey(Key.J);
            Scene.Active.Update();
            InputSystem.EndFrame();
            Scene.Active.Update();

            Assert.Single(_logged, e => e.Message == "jump");
            _ = runner;
        }

        [Fact]
        public void A_node_that_goes_wrong_is_reported_and_the_script_stops()
        {
            Graph graph = NewGraph();
            GraphNode update = graph.AddNode("Events/On Update", Vector2.Zero);
            GraphNode set = graph.AddNode("Variables/Set Variable", Vector2.Zero);
            set.Values["Variable"] = "Missing";
            graph.Link(update.Id, "Then", set.Id, "In");

            GameObject runner = Run(graph, frames: 3);

            LogEntry error = Assert.Single(_logged, e => e.Level == LogLevel.Error);
            Assert.Contains("Set Variable", error.Message);
            Assert.False(runner.GetComponent<VisualScript>()!.Enabled);
            Assert.NotNull(graph.Error(set.Id));
        }

        [Fact]
        public void A_loop_that_never_ends_is_stopped()
        {
            Graph graph = NewGraph();
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode loop = graph.AddNode("Flow/For Loop", Vector2.Zero);
            loop.Values["To"] = 1e9f;
            graph.Link(start.Id, "Then", loop.Id, "In");

            Run(graph);

            Assert.Contains(_logged, e => e.Level == LogLevel.Error && e.Message.Contains("never ends"));
        }

        [Fact]
        public void Links_join_only_ports_whose_types_go_together()
        {
            Graph graph = NewGraph();
            GraphNode start = graph.AddNode("Events/On Start", Vector2.Zero);
            GraphNode add = graph.AddNode("Math/Add", Vector2.Zero);
            GraphNode move = graph.AddNode("Actions/Move By", Vector2.Zero);
            GraphNode name = graph.AddNode("Objects/Get Name", Vector2.Zero);

            Assert.False(graph.Link(start.Id, "Then", add.Id, "A"));
            Assert.False(graph.Link(name.Id, "Name", move.Id, "Offset"));
            Assert.True(graph.Link(add.Id, "Result", move.Id, "Offset"));
            Assert.False(graph.Link(add.Id, "Result", add.Id, "A"));

            // An input takes one link: linking again replaces it.
            GraphNode other = graph.AddNode("Math/Add", Vector2.Zero);
            Assert.True(graph.Link(other.Id, "Result", move.Id, "Offset"));
            Assert.Single(graph.LinksTo(move.Id, "Offset"));
        }

        [Fact]
        public void A_graph_file_saves_and_loads_the_same_and_scenes_refer_to_it_by_path()
        {
            string folder = Path.Combine(Path.GetTempPath(), "VertexTests", Guid.NewGuid().ToString("N"));
            try
            {
                Graph graph = ScriptNodes.Starter();
                graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 2.5f));
                graph.Nodes[1].Values["Value"] = "Hi";
                graph.AddNode("Actions/Move By", new Vector2(10f, 20f)).Values["Offset"] = new Vector3(1f, 2f, 3f);
                string path = Path.Combine(folder, "Mover.vscript");
                graph.Save(path);

                Graph loaded = Graph.Load(path);
                Assert.Equal(graph.ToJson(), loaded.ToJson());
                Assert.Equal(new Vector3(1f, 2f, 3f), loaded.Nodes[^1].Values["Offset"]);
                Assert.Equal(2.5f, loaded.Variable("Speed")!.Value);

                // A scene keeps the script by where its file is.
                var gameObject = new GameObject("Scripted");
                gameObject.AddComponent<VisualScript>().Script = Assets.LoadGraph(path);
                string scene = SceneFile.Save(Scene.Active, new[] { folder });
                var reloaded = new Scene();
                SceneFile.LoadJson(scene, reloaded, folder);
                Assert.Same(Assets.LoadGraph(path), reloaded.Find("Scripted")!.GetComponent<VisualScript>()!.Script);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        }
    }
}
