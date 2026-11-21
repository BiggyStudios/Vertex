using Vertex.Graphs;

namespace Vertex.Scripting
{
    /// <summary>A node going wrong as a visual script runs: which node, and what happened.</summary>
    public sealed class ScriptException(GraphNode node, string title, Exception inner) : Exception($"{title}: {inner.Message}", inner)
    {
        public GraphNode Node { get; } = node;
    }

    /// <summary>
    /// What a node sees as it runs: its inputs (followed back along links, or the values set on it), its outputs, the
    /// object running the script, and the script's variables. One for each node, used every time it runs.
    /// </summary>
    public sealed class ScriptRun
    {
        private readonly ScriptInstance _instance;
        internal readonly ScriptInstance.Compiled Node;

        internal ScriptRun(ScriptInstance instance, ScriptInstance.Compiled node)
        {
            _instance = instance;
            Node = node;
        }

        /// <summary>The object the script is on.</summary>
        public GameObject Self => _instance.Self;

        /// <summary>The flow input the flow came in by, for a node with more than one.</summary>
        public string? EnteredBy { get; internal set; }

        /// <summary>Input <paramref name="port"/>'s value, as its port's type.</summary>
        public object? In(string port) => _instance.Read(Node, port);

        /// <summary>Input <paramref name="port"/>'s value as a <typeparamref name="T"/> (or T's default if it can't be one).</summary>
        public T In<T>(string port) => In(port) is T value ? value : default!;

        public void Out(string port, object? value) => Node.Outputs[port] = value;

        /// <summary>Whether anything's linked to port <paramref name="port"/>: a flow output to something to run, or an input from a value.</summary>
        public bool IsLinked(string port) => Node.Flows.ContainsKey(port) || Node.Inputs.ContainsKey(port);

        /// <summary>Runs what's linked to flow output <paramref name="port"/>, and comes back once it's done.</summary>
        public void Continue(string port) => _instance.Continue(Node, port);

        /// <summary>Runs what's linked to flow output <paramref name="port"/> <paramref name="seconds"/> from now, carrying on at once meanwhile.</summary>
        public void ContinueAfter(string port, float seconds) => _instance.Later(Node, port, seconds);

        /// <summary>Something the node keeps between runs, on this object: when it last let the flow through, say.</summary>
        public object? State
        {
            get => Node.State;
            set => Node.State = value;
        }

        /// <summary>A value the event that started the flow came with (its name, what was hit), whether or not the node shows it.</summary>
        public object? Given(string port) => Node.Outputs.GetValueOrDefault(port);

        public object? GetVariable(string name) => _instance.Variables.GetValueOrDefault(name);

        /// <summary>Sets a variable, as the type it was made as (a number stays a number).</summary>
        public void SetVariable(string name, object? value)
        {
            GraphVariable? variable = _instance.Graph.Variable(name);
            if (variable == null) throw new InvalidOperationException(name.Length == 0 ? "No variable chosen" : $"There's no variable called {name}");
            _instance.Variables[name] = variable.Type.Convert(value);
        }
    }

    /// <summary>
    /// A visual script running on one object: its graph worked out into nodes linked up ready to run, and its own
    /// variables. The graph can be changed while it runs: it's worked out again, keeping the variables.
    /// </summary>
    public sealed class ScriptInstance
    {
        internal sealed class Compiled
        {
            public required GraphNode Node;
            public required ScriptNode Type;
            public ScriptRun Run = null!;
            public bool Pure;
            public object? State;

            // Where each linked input comes from, and where each flow output goes.
            public readonly Dictionary<string, (Compiled Node, string Port)> Inputs = new();
            public readonly Dictionary<string, (Compiled Node, string Port)> Flows = new();

            // Outputs as they were last worked out.
            public readonly Dictionary<string, object?> Outputs = new();
        }

        public Graph Graph { get; }
        public GameObject Self { get; }

        /// <summary>The script's variables as they are now, by name.</summary>
        public Dictionary<string, object?> Variables { get; } = new();

        private List<Compiled> _nodes = new();
        private int _compiledVersion = -1;

        // Flow waiting to carry on (Wait): when, from which node, and which output.
        private readonly List<(float At, Compiled Node, string Port)> _waiting = new();

        // Steps run since an event started: too many, and it's stuck in a loop.
        private int _steps;
        private int _depth;

        /// <summary>The most nodes one event may run, so a loop that never ends stops instead of freezing the game.</summary>
        public const int StepLimit = 100_000;

        public ScriptInstance(Graph graph, GameObject self)
        {
            Graph = graph;
            Self = self;
            foreach (GraphVariable variable in graph.Variables) Variables[variable.Name] = variable.Value;
            Compile();
        }

        /// <summary>Works the graph out again if it's changed since (edited while the game runs).</summary>
        private void Compile()
        {
            if (_compiledVersion == Graph.Version) return;
            _compiledVersion = Graph.Version;

            // Variables added since are given their starting values; ones still there keep theirs.
            foreach (GraphVariable variable in Graph.Variables) Variables.TryAdd(variable.Name, variable.Value);

            var byId = new Dictionary<int, Compiled>();
            var nodes = new List<Compiled>();
            foreach (GraphNode node in Graph.Nodes)
            {
                if (ScriptNodes.Library.Find(node.Type) is not ScriptNode type) continue;
                var compiled = new Compiled { Node = node, Type = type, Pure = !type.HasFlowInput && type.Event == null };
                compiled.Run = new ScriptRun(this, compiled);
                byId[node.Id] = compiled;
                nodes.Add(compiled);
            }

            foreach (GraphLink link in Graph.Links)
            {
                if (!byId.TryGetValue(link.FromNode, out Compiled? from) || !byId.TryGetValue(link.ToNode, out Compiled? to)) continue;
                if (Graph.Output(from.Node, link.FromPort) is not { } output) continue;

                if (output.Type.IsFlow) from.Flows[link.FromPort] = (to, link.ToPort);
                else to.Inputs[link.ToPort] = (from, link.FromPort);
            }

            _nodes = nodes;
            _waiting.Clear();
        }

        /// <summary>
        /// Starts every event node for <paramref name="name"/> ("Start", "Update", "Collision") whose condition holds,
        /// with <paramref name="outputs"/> set on it first (what was hit, for "Collision").
        /// </summary>
        public void Fire(string name, params (string Port, object? Value)[] outputs)
        {
            Compile();
            foreach (Compiled node in _nodes)
            {
                if (node.Type.Event != name) continue;

                _steps = 0;
                _depth = 0;
                foreach ((string port, object? value) in outputs) node.Outputs[port] = value;
                if (node.Type.When is { } when && !Guarded(node, () => when(node.Run))) continue;
                Step(node, null);
            }
        }

        internal void Later(Compiled from, string port, float seconds) => _waiting.Add((Time.TotalTime + MathF.Max(seconds, 0f), from, port));

        /// <summary>Carries on the flow that's waited long enough (Wait nodes). Called each frame, before Update.</summary>
        public void RunWaiting()
        {
            Compile();
            if (_waiting.Count == 0) return;

            float now = Time.TotalTime;
            List<(float At, Compiled Node, string Port)> due = _waiting.Where(w => w.At <= now).ToList();
            if (due.Count == 0) return;
            _waiting.RemoveAll(w => w.At <= now);
            foreach ((_, Compiled node, string port) in due)
            {
                _steps = 0;
                _depth = 0;
                Continue(node, port);
            }
        }

        internal void Continue(Compiled from, string port)
        {
            // Counted even when nothing's linked, so a loop with nothing in it can't go on for ever either.
            if (++_steps > StepLimit) throw new ScriptException(from.Node, from.Type.Title, new InvalidOperationException($"Ran more than {StepLimit:N0} steps at once: a loop that never ends?"));
            if (from.Flows.TryGetValue(port, out var next)) Step(next.Node, next.Port);
        }

        private void Step(Compiled node, string? enteredBy)
        {
            if (++_steps > StepLimit) throw new ScriptException(node.Node, node.Type.Title, new InvalidOperationException($"Ran more than {StepLimit:N0} steps at once: a loop that never ends?"));
            if (++_depth > 2000) throw new ScriptException(node.Node, node.Type.Title, new InvalidOperationException("Too many nodes one after another (over 2,000)"));

            try
            {
                Graph.Ran(node.Node.Id);
                node.Run.EnteredBy = enteredBy;
                Guarded(node, () =>
                {
                    node.Type.Run(node.Run);
                    return true;
                });
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>Runs <paramref name="action"/> for <paramref name="node"/>, so anything thrown says which node it was.</summary>
        private static bool Guarded(Compiled node, Func<bool> action)
        {
            try
            {
                return action();
            }
            catch (ScriptException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ScriptException(node.Node, node.Type.Title, exception);
            }
        }

        /// <summary>
        /// The value at input <paramref name="port"/> of <paramref name="node"/>: worked out by what's linked to it (a
        /// node that only works out values does it now; one in the flow gives what it last gave), or the value set on the
        /// node, or the port's default. An object input with nothing means the script's own object.
        /// </summary>
        internal object? Read(Compiled node, string port)
        {
            Port? info = node.Type.Input(port) ?? Graph.Input(node.Node, port);
            if (info == null) throw new InvalidOperationException($"{node.Type.Title} has no input {port}");

            object? value;
            if (node.Node.SplitInputs.Count > 0 && node.Node.SplitInputs.Contains(port))
            {
                // Split into its parts: each read on its own, and put together.
                PortType whole = Graph.TypeOf(node.Node, info);
                Span<float> parts = stackalloc float[whole.Parts.Count];
                for (int i = 0; i < parts.Length; i++) parts[i] = (float)Read(node, Graph.PartName(port, whole.Parts[i]))!;
                value = whole.Join(parts);
            }
            else if (node.Inputs.TryGetValue(port, out var source))
            {
                if (source.Node.Pure)
                {
                    if (++_depth > 500) throw new ScriptException(source.Node.Node, source.Node.Type.Title, new InvalidOperationException("Values worked out in a circle"));
                    try
                    {
                        Graph.Ran(source.Node.Node.Id);
                        Guarded(source.Node, () =>
                        {
                            source.Node.Type.Run(source.Node.Run);
                            return true;
                        });
                    }
                    finally
                    {
                        _depth--;
                    }
                }

                // From a part of a split output: that part of the whole.
                value = source.Node.Node.SplitOutputs.Count > 0 && Graph.PartOf(source.Node.Node, source.Port, output: true) is var (whole, wholeType, index)
                    ? wholeType.Part(source.Node.Outputs.GetValueOrDefault(whole.Name), index)
                    : source.Node.Outputs.GetValueOrDefault(source.Port);
            }
            else
            {
                value = node.Node.Values.TryGetValue(port, out object? set) ? set : info.DefaultValue;
            }

            // As the type it is on this node (a variable's value, the variable's type).
            PortType type = Graph.TypeOf(node.Node, info);
            value = type.Convert(value);
            if (value == null && type == PortType.Object && !node.Inputs.ContainsKey(port)) value = Self;
            return value;
        }
    }
}
