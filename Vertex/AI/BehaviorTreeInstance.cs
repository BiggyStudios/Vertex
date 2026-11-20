using System.Numerics;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.AI
{
    /// <summary>A task in a behaviour tree that went wrong: which node, and what happened.</summary>
    public sealed class BehaviorException(GraphNode node, string message, Exception? inner = null) : Exception(message, inner)
    {
        public GraphNode Node { get; } = node;
    }

    /// <summary>
    /// A behaviour tree running on one object: its variables (what the character knows) and its tasks, each keeping
    /// track of what it's doing. Built from the graph again whenever the graph's changed (a running game's tree edited).
    /// </summary>
    public sealed class BehaviorTreeInstance
    {
        public Graph Graph { get; }

        /// <summary>The object it's running on.</summary>
        public GameObject Self { get; }

        /// <summary>The tree's variables as they are now, by name.</summary>
        public Dictionary<string, object?> Variables { get; } = new();

        /// <summary>How the tree did the last time it was run.</summary>
        public BehaviorStatus Status { get; private set; } = BehaviorStatus.Running;

        /// <summary>Whether the tree has finished (succeeded or failed) since it last started.</summary>
        public bool Finished { get; private set; }

        /// <summary>The seconds it's been running: what waits and cooldowns go by.</summary>
        internal float Time { get; private set; }

        /// <summary>Where its object was when it started: what Wander wanders round.</summary>
        internal Vector3 Home { get; }

        internal Random Random { get; } = new();

        private BehaviorTask? _root;
        private int _builtVersion = -1;
        private bool _toldNoRoot;

        public BehaviorTreeInstance(Graph graph, GameObject self)
        {
            Graph = graph;
            Self = self;
            Home = self.Transform.WorldPosition;
            foreach (GraphVariable variable in graph.Variables) Variables[variable.Name] = variable.Value;
        }

        /// <summary>The names of the nodes running now, from the Root down: for seeing what it's doing.</summary>
        public IEnumerable<GraphNode> Running
        {
            get
            {
                var running = new List<GraphNode>();
                void Under(BehaviorTask task)
                {
                    if (!task.IsRunning) return;
                    running.Add(task.Node);
                    foreach (BehaviorTask child in task.Children) Under(child);
                }

                if (_root != null) Under(_root);
                return running;
            }
        }

        /// <summary>Runs the tree once: from the Root, through whatever's running, as far as it gets this frame.</summary>
        public void Tick(float deltaTime)
        {
            Time += deltaTime;
            if (_builtVersion != Graph.Version) Rebuild();
            if (_root == null) return;

            Status = _root.Run();
            Finished = Status != BehaviorStatus.Running;
        }

        /// <summary>Stops whatever's running (an agent stops walking), so the tree starts from the Root next time.</summary>
        public void Stop()
        {
            _root?.Abort();
            Finished = false;
        }

        private void Rebuild()
        {
            _root?.Abort();
            _root = null;
            _builtVersion = Graph.Version;

            // Variables added since it started begin as the tree says.
            foreach (GraphVariable variable in Graph.Variables)
            {
                if (!Variables.ContainsKey(variable.Name)) Variables[variable.Name] = variable.Value;
            }

            GraphNode? root = Graph.Nodes.Where(n => n.Type == BehaviorNodes.Root).OrderBy(n => n.Position.Y).FirstOrDefault();
            if (root == null)
            {
                if (!_toldNoRoot) Log.Warning($"Behaviour tree {Graph.Name} has no Root node, so {Self.Name} does nothing");
                _toldNoRoot = true;
                return;
            }

            _root = Build(root, new HashSet<int>());
        }

        /// <summary>A node's task, with its children's under it. A node can't be under itself: a loop of links is cut.</summary>
        private BehaviorTask Build(GraphNode node, HashSet<int> above)
        {
            BehaviorTask task = BehaviorTask.For(node.Type);
            task.Node = node;
            task.Tree = this;
            above.Add(node.Id);
            var children = new List<BehaviorTask>();
            foreach (GraphNode child in BehaviorNodes.ChildrenOf(Graph, node))
            {
                if (above.Contains(child.Id) || BehaviorNodes.Library.Find(child.Type) == null) continue;
                children.Add(Build(child, above));
            }

            above.Remove(node.Id);
            task.Children = children.ToArray();
            return task;
        }

        // ---------------------------------------------------------------- Values

        private const int _maxDepth = 64;

        /// <summary>
        /// Input <paramref name="port"/> of <paramref name="node"/>: what's linked to it, worked out now, or the value set
        /// on the node, made into the port's type.
        /// </summary>
        internal object? Input(GraphNode node, string port, int depth = 0)
        {
            if (BehaviorNodes.Library.Find(node.Type)?.Input(node, port) is not { } input) return null;
            PortType type = Graph.TypeOf(node, input);

            foreach (GraphLink link in Graph.Links)
            {
                if (link.ToNode != node.Id || link.ToPort != port || Graph.Node(link.FromNode) is not { } from) continue;
                if (depth >= _maxDepth) throw new BehaviorException(node, "Its values go round in a loop");
                return type.Convert(Output(from, link.FromPort, depth + 1));
            }

            return type.Convert(node.Values.TryGetValue(port, out object? set) ? set : input.DefaultValue);
        }

        /// <summary>What a value node gives out.</summary>
        private object? Output(GraphNode node, string port, int depth)
        {
            float N(string name) => Input(node, name, depth) is float f ? f : 0f;
            bool B(string name) => Input(node, name, depth) is true;

            switch (node.Type)
            {
                case BehaviorNodes.GetVariable:
                    return Variables.GetValueOrDefault(Text(node, "Variable"));
                case BehaviorNodes.Self:
                    return Self;
                case BehaviorNodes.PositionOf:
                    return Target(Text(node, "Target"), out Vector3 at, out _) ? at : Vector3.Zero;
                case BehaviorNodes.DistanceTo:
                    return Target(Text(node, "Target"), out Vector3 there, out _) ? Vector3.Distance(Self.Transform.WorldPosition, there) : float.MaxValue;
                case BehaviorNodes.RandomNumber:
                    float min = N("Min"), max = N("Max");
                    return min + Random.NextSingle() * (max - min);
                case BehaviorNodes.Add:
                    return N("A") + N("B");
                case BehaviorNodes.Subtract:
                    return N("A") - N("B");
                case BehaviorNodes.Multiply:
                    return N("A") * N("B");
                case BehaviorNodes.Compare:
                    float a = N("A"), b = N("B");
                    return (Input(node, "Is", depth) is Comparison comparison ? comparison : Comparison.Greater) switch
                    {
                        Comparison.Greater => a > b,
                        Comparison.GreaterOrEqual => a >= b,
                        Comparison.Less => a < b,
                        Comparison.LessOrEqual => a <= b,
                        Comparison.Equal => MathF.Abs(a - b) < 1e-5f,
                        _ => MathF.Abs(a - b) >= 1e-5f
                    };
                case BehaviorNodes.And:
                    return B("A") && B("B");
                case BehaviorNodes.Or:
                    return B("A") || B("B");
                case BehaviorNodes.Not:
                    return !B("Value");
                default:
                    throw new BehaviorException(node, $"{node.Type} doesn't give a value");
            }
        }

        /// <summary>A setting's text: a variable's name, an event's.</summary>
        internal static string Text(GraphNode node, string port) => node.Values.TryGetValue(port, out object? value) && value is string text ? text : "";

        /// <summary>
        /// Where the target held in variable <paramref name="variable"/> is: an object's position (and the object), or a
        /// place. False if the variable's empty (or the object's gone).
        /// </summary>
        internal bool Target(string variable, out Vector3 position, out GameObject? target)
        {
            target = null;
            position = default;
            switch (Variables.GetValueOrDefault(variable))
            {
                case GameObject { IsDestroyed: false } gameObject:
                    target = gameObject;
                    position = gameObject.Transform.WorldPosition;
                    return true;
                case Component { GameObject.IsDestroyed: false } component:
                    target = component.GameObject;
                    position = target.Transform.WorldPosition;
                    return true;
                case Vector3 place:
                    position = place;
                    return true;
                default:
                    return false;
            }
        }
    }
}
