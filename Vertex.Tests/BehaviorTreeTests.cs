using System.Numerics;

using Vertex.AI;
using Vertex.Graphs;
using Vertex.Physics;

namespace Vertex.Tests
{
    /// <summary>Behaviour trees: the order their tasks run in, conditions checked again, and characters walking where they say.</summary>
    public class BehaviorTreeTests : EngineTest
    {
        private readonly Graph _graph = new(BehaviorNodes.Kind);
        private readonly GameLoop _loop = new(new EmptyGame());

        private GraphNode Add(string type, float x, float y, params (string Port, object? Value)[] values)
        {
            GraphNode node = _graph.AddNode(type, new Vector2(x, y));
            foreach ((string port, object? value) in values) node.Values[port] = value;
            return node;
        }

        private void Under(GraphNode parent, params GraphNode[] children)
        {
            string port = BehaviorNodes.Library.Find(parent.Type)!.Outputs[0].Name;
            foreach (GraphNode child in children) Assert.True(_graph.Link(parent.Id, port, child.Id, "Parent"), $"{child} under {parent}");
        }

        private GraphNode Root(GraphNode first)
        {
            GraphNode root = Add(BehaviorNodes.Root, -300f, 0f);
            Under(root, first);
            return root;
        }

        private GraphNode SetText(string variable, string text, float y)
        {
            if (_graph.Variable(variable) == null) _graph.Variables.Add(new GraphVariable(variable, PortType.Text));
            return Add(BehaviorNodes.SetVariable, 300f, y, ("Variable", variable), ("Value", text));
        }

        private BehaviorTree Run(GameObject? on = null)
        {
            on ??= new GameObject("AI");
            BehaviorTree tree = on.AddComponent<BehaviorTree>();
            tree.Tree = _graph;
            return tree;
        }

        [Fact]
        public void A_sequence_runs_whats_under_it_from_the_top_down()
        {
            _graph.Variables.Add(new GraphVariable("Order", PortType.Text));
            GraphNode sequence = Add(BehaviorNodes.Sequence, 0f, 0f);
            Root(sequence);

            // Placed out of the order they're made in: it's where they are that counts.
            GraphNode last = Add(BehaviorNodes.SetVariable, 300f, 200f, ("Variable", "Order"));
            GraphNode first = Add(BehaviorNodes.SetVariable, 300f, -100f, ("Variable", "Order"), ("Value", "first"));
            Under(sequence, last, first);
            Assert.Equal([first, last], BehaviorNodes.ChildrenOf(_graph, sequence));

            // The second reads the first's value back, through a Get Variable.
            GraphNode read = Add(BehaviorNodes.GetVariable, 0f, 400f, ("Variable", "Order"));
            Assert.True(_graph.Link(read.Id, "Value", last.Id, "Value"));

            BehaviorTree tree = Run();
            RunFrames(_loop, 1);
            Assert.Equal("first", tree.GetVariable("Order"));
            Assert.Equal(BehaviorStatus.Success, tree.Status);
        }

        [Fact]
        public void A_selector_takes_the_first_that_works()
        {
            GraphNode selector = Add(BehaviorNodes.Selector, 0f, 0f);
            Root(selector);
            GraphNode fails = Add(BehaviorNodes.Check, 300f, 0f, ("Condition", false));
            GraphNode works = SetText("Chosen", "second", 100f);
            GraphNode never = SetText("Chosen", "third", 200f);
            Under(selector, fails, works, never);

            BehaviorTree tree = Run();
            RunFrames(_loop, 1);
            Assert.Equal("second", tree.GetVariable("Chosen"));
        }

        [Fact]
        public void A_wait_takes_its_time_and_the_tree_starts_again_when_its_done()
        {
            _graph.Variables.Add(new GraphVariable("Count", PortType.Number));
            GraphNode sequence = Add(BehaviorNodes.Sequence, 0f, 0f);
            Root(sequence);
            GraphNode wait = Add(BehaviorNodes.Wait, 300f, 0f, ("Seconds", 0.5f));
            GraphNode count = Add(BehaviorNodes.SetVariable, 300f, 100f, ("Variable", "Count"));
            GraphNode plus = Add(BehaviorNodes.Add, 0f, 200f, ("B", 1f));
            GraphNode get = Add(BehaviorNodes.GetVariable, -200f, 200f, ("Variable", "Count"));
            _graph.Link(get.Id, "Value", plus.Id, "A");
            _graph.Link(plus.Id, "Result", count.Id, "Value");
            Under(sequence, wait, count);

            BehaviorTree tree = Run();
            RunFrames(_loop, 20);
            Assert.Equal(0f, tree.GetVariable("Count"));
            Assert.Equal(BehaviorStatus.Running, tree.Status);
            RunFrames(_loop, 20);
            Assert.Equal(1f, tree.GetVariable("Count"));

            // Round again from the Root, and again.
            RunFrames(_loop, 70);
            Assert.Equal(3f, tree.GetVariable("Count"));
        }

        [Fact]
        public void Conditions_checked_again_interrupt_what_runs_after_them_and_stop_what_they_guard()
        {
            _graph.Variables.Add(new GraphVariable("Alert", PortType.Boolean));
            GraphNode selector = Add(BehaviorNodes.Selector, 0f, 0f);
            Root(selector);

            // Chase while alert; otherwise patrol: each sets what it's doing, then keeps doing it.
            GraphNode chase = Add(BehaviorNodes.Sequence, 250f, 0f, ("Recheck", nameof(Recheck.Both)));
            GraphNode alert = Add(BehaviorNodes.Check, 500f, 0f);
            GraphNode get = Add(BehaviorNodes.GetVariable, 300f, -100f, ("Variable", "Alert"));
            _graph.Link(get.Id, "Value", alert.Id, "Condition");
            Under(chase, alert, SetText("Doing", "chase", 100f), Add(BehaviorNodes.Wait, 500f, 200f, ("Seconds", 100f)));

            GraphNode patrol = Add(BehaviorNodes.Sequence, 250f, 300f);
            Under(patrol, SetText("Doing", "patrol", 300f), Add(BehaviorNodes.Wait, 500f, 400f, ("Seconds", 100f)));
            Under(selector, chase, patrol);

            BehaviorTree tree = Run();
            RunFrames(_loop, 3);
            Assert.Equal("patrol", tree.GetVariable("Doing"));

            tree.SetVariable("Alert", true);
            RunFrames(_loop, 1);
            Assert.Equal("chase", tree.GetVariable("Doing"));

            tree.SetVariable("Alert", false);
            RunFrames(_loop, 2);
            Assert.Equal("patrol", tree.GetVariable("Doing"));
        }

        [Fact]
        public void Decorators_change_what_comes_back()
        {
            GraphNode sequence = Add(BehaviorNodes.Sequence, 0f, 0f);
            Root(sequence);
            GraphNode invert = Add(BehaviorNodes.Invert, 250f, 0f);
            GraphNode inverted = Add(BehaviorNodes.Check, 500f, 0f, ("Condition", false));
            Under(invert, inverted);
            GraphNode always = Add(BehaviorNodes.AlwaysSucceed, 250f, 100f);
            Under(always, Add(BehaviorNodes.Check, 500f, 100f, ("Condition", false)));
            Under(sequence, invert, always, SetText("Reached", "yes", 200f));

            // A decorator has one task under it: linking another moves the link.
            GraphNode other = Add(BehaviorNodes.Wait, 500f, 600f);
            _graph.Link(invert.Id, "Child", other.Id, "Parent");
            Assert.Equal([other], BehaviorNodes.ChildrenOf(_graph, invert));
            _graph.Link(invert.Id, "Child", inverted.Id, "Parent");
            Assert.Equal([inverted], BehaviorNodes.ChildrenOf(_graph, invert));

            BehaviorTree tree = Run();
            RunFrames(_loop, 1);
            Assert.Equal("yes", tree.GetVariable("Reached"));
        }

        [Fact]
        public void A_task_has_one_parent()
        {
            GraphNode a = Add(BehaviorNodes.Sequence, 0f, 0f), b = Add(BehaviorNodes.Selector, 0f, 200f);
            GraphNode task = Add(BehaviorNodes.Wait, 300f, 0f);
            Under(a, task);
            Under(b, task);
            Assert.Empty(BehaviorNodes.ChildrenOf(_graph, a));
            Assert.Equal([task], BehaviorNodes.ChildrenOf(_graph, b));
        }

        private sealed class Door : Component
        {
            public int Opened;
            public bool Locked;

            public bool Open()
            {
                Opened++;
                return !Locked;
            }
        }

        [Fact]
        public void Call_method_runs_a_components_own_code()
        {
            GraphNode selector = Add(BehaviorNodes.Selector, 0f, 0f);
            Root(selector);
            Under(selector, Add(BehaviorNodes.CallMethod, 300f, 0f, ("Method", "Open")), SetText("Then", "it was locked", 100f));

            var house = new GameObject("House");
            Door door = house.AddComponent<Door>();
            door.Locked = true;
            BehaviorTree tree = Run(house);
            RunFrames(_loop, 1);
            Assert.Equal(1, door.Opened);
            Assert.Equal("it was locked", tree.GetVariable("Then"));
        }

        [Fact]
        public void A_task_that_goes_wrong_stops_the_tree_and_says_where()
        {
            _graph.Variables.Add(new GraphVariable("Target", PortType.Vector, new Vector3(3f, 0f, 0f)));
            GraphNode move = Add(BehaviorNodes.MoveTo, 0f, 0f, ("Target", "Target"));
            Root(move);

            BehaviorTree tree = Run();
            RunFrames(_loop, 2);
            Assert.False(tree.Enabled);
            Assert.Contains("Nav Mesh Agent", _graph.Error(move.Id));
        }

        [Fact]
        public void Changes_to_a_running_tree_take_effect_straight_away()
        {
            GraphNode set = SetText("Said", "hello", 0f);
            Root(set);
            BehaviorTree tree = Run();
            RunFrames(_loop, 1);
            Assert.Equal("hello", tree.GetVariable("Said"));

            set.Values["Value"] = "goodbye";
            _graph.Changed();
            RunFrames(_loop, 1);
            Assert.Equal("goodbye", tree.GetVariable("Said"));
        }

        // ---------------------------------------------------------------- With a navmesh

        private static void Floor()
        {
            var floor = new GameObject("Floor");
            floor.Transform.Position = new Vector3(0f, -0.5f, 0f);
            floor.AddComponent<BoxCollider>().Size = new Vector3(30f, 1f, 30f);
        }

        private static GameObject Walker(Vector3 at)
        {
            var walker = new GameObject("Guard");
            walker.Transform.Position = at;
            walker.AddComponent<NavMeshAgent>();
            return walker;
        }

        [Fact]
        public void Move_to_walks_to_an_object_and_follows_it()
        {
            Floor();
            _graph.Variables.Add(new GraphVariable("Target", PortType.Object));
            Root(Add(BehaviorNodes.MoveTo, 0f, 0f, ("Target", "Target"), ("Stop Within", 1f)));

            var player = new GameObject("Player");
            player.Transform.Position = new Vector3(6f, 0f, 0f);
            GameObject guard = Walker(new Vector3(-6f, 0f, 0f));
            BehaviorTree tree = Run(guard);
            tree.SetVariable("Target", player);

            RunFrames(_loop, 60 * 2);
            player.Transform.Position = new Vector3(6f, 0f, 8f);
            RunFrames(_loop, 60 * 4);

            float apart = Vector3.Distance(guard.Transform.WorldPosition, player.Transform.WorldPosition);
            Assert.True(apart <= 1.1f, $"The guard's {apart} away, at {guard.Transform.WorldPosition}");
            Assert.Equal(BehaviorStatus.Success, tree.Status);
        }

        [Fact]
        public void Can_see_looks_out_of_its_own_body_but_not_through_walls()
        {
            Floor();
            _graph.Variables.Add(new GraphVariable("Target", PortType.Object));
            Root(Add(BehaviorNodes.CanSee, 0f, 0f, ("Target", "Target")));

            // Its eyes are inside its body, a collider on an object under it.
            var guard = new GameObject("Guard");
            var body = new GameObject("Body");
            body.Transform.Parent = guard.Transform;
            body.Transform.Position = new Vector3(0f, 0.9f, 0f);
            body.AddComponent<BoxCollider>().Size = new Vector3(0.6f, 1.8f, 0.6f);
            var player = new GameObject("Player");
            player.Transform.Position = new Vector3(0f, 0f, -6f);
            player.AddComponent<BoxCollider>().Size = new Vector3(0.6f, 1.8f, 0.6f);

            BehaviorTree tree = Run(guard);
            tree.SetVariable("Target", player);
            RunFrames(_loop, 3);
            Assert.Equal(BehaviorStatus.Success, tree.Status);

            var wall = new GameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 1.5f, -3f);
            wall.AddComponent<BoxCollider>().Size = new Vector3(4f, 3f, 0.5f);
            RunFrames(_loop, 3);
            Assert.Equal(BehaviorStatus.Failure, tree.Status);
        }

        [Fact]
        public void Patrol_goes_round_the_waypoints_in_turn()
        {
            Floor();
            var route = new GameObject("Route");
            foreach (Vector3 point in (Vector3[])[new(4f, 0f, 0f), new(4f, 0f, 4f), new(0f, 0f, 4f)])
            {
                var waypoint = new GameObject("Point");
                waypoint.Transform.Parent = route.Transform;
                waypoint.Transform.Position = point;
            }

            _graph.Variables.Add(new GraphVariable("Route", PortType.Object));
            Root(Add(BehaviorNodes.Patrol, 0f, 0f, ("Waypoints", "Route")));
            GameObject guard = Walker(Vector3.Zero);
            BehaviorTree tree = Run(guard);
            tree.SetVariable("Route", route);

            var visited = new List<int>();
            for (int frame = 0; frame < 60 * 12; frame++)
            {
                RunFrames(_loop, 1);
                for (int i = 0; i < 3; i++)
                {
                    if (Vector3.Distance(guard.Transform.WorldPosition, route.Transform.Children[i].WorldPosition) < 0.3f && (visited.Count == 0 || visited[^1] != i)) visited.Add(i);
                }
            }

            Assert.True(visited.Count >= 4, string.Join(",", visited));
            Assert.Equal([0, 1, 2, 0], visited.Take(4));
        }

        [Fact]
        public void The_starter_tree_chases_what_it_sees_and_wanders_otherwise()
        {
            Floor();
            Graph starter = BehaviorNodes.Starter();
            var player = new GameObject("Player");
            player.Transform.Position = new Vector3(0f, 0f, -10f);

            // Facing away from the player (forward is -z, the player's behind it at +z later).
            GameObject guard = Walker(new Vector3(0f, 0f, 0f));
            BehaviorTree tree = guard.AddComponent<BehaviorTree>();
            tree.Tree = starter;
            tree.SetVariable("Target", player);

            RunFrames(_loop, 60 * 4);
            float apart = Vector3.Distance(guard.Transform.WorldPosition, player.Transform.WorldPosition);
            Assert.True(apart < 2f, $"It sees the player in front of it and goes to them: {apart} away");

            // Out of sight behind a wall, far off: it goes back to wandering round where it started.
            player.Transform.Position = new Vector3(0f, 0f, 14f);
            var wall = new GameObject("Wall");
            wall.Transform.Position = new Vector3(0f, 1.5f, 12f);
            wall.AddComponent<BoxCollider>().Size = new Vector3(8f, 3f, 0.5f);
            RunFrames(_loop, 60 * 6);
            Assert.Contains(tree.Instance!.Running, n => n.Type == BehaviorNodes.Wander || n.Type == BehaviorNodes.Wait);
        }
    }
}
