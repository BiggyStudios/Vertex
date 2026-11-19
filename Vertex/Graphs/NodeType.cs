namespace Vertex.Graphs
{
    /// <summary>
    /// One of a node's inputs or outputs. An input that isn't linked takes the value set on the node (typed into it in an
    /// editor), or <see cref="Default"/>. One that can't be linked at all (<see cref="Linkable"/> false) is a setting:
    /// which key a key node listens for.
    /// </summary>
    public sealed record Port(string Name, PortType Type, object? Default = null, bool Linkable = true, string? Tooltip = null)
    {
        /// <summary>What it is when nothing's linked and nothing's been set.</summary>
        public object? DefaultValue => Default ?? Type.Default;

        /// <summary>
        /// For a setting that names one of the graph's variables (a Get Variable node's): its dropdown lists them.
        /// </summary>
        public bool IsVariableName { get; init; }

        /// <summary>
        /// Takes the type of the variable the node's variable setting names (a Set Variable node's value is a true/false
        /// for a Boolean variable): see <see cref="Graph.TypeOf"/>. <see cref="Type"/> is what it is before one's chosen.
        /// </summary>
        public bool TypeFromVariable { get; init; }
    }

    /// <summary>
    /// A kind of node: its title, where it's listed, and its ports. The graph's nodes each say which kind they are by
    /// <see cref="Id"/>; what a kind does is up to the kind of graph (a visual script's nodes run, a shader graph's would
    /// write shader code).
    /// </summary>
    public class NodeType
    {
        /// <summary>Names it in saved graphs, so it mustn't change: "Math/Add".</summary>
        public string Id { get; }

        public string Title { get; }

        /// <summary>Where it's listed when adding a node, and the colour of its header.</summary>
        public string Category { get; }

        /// <summary>What it does, in a sentence, for the list of nodes to add.</summary>
        public string? Description { get; init; }

        public IReadOnlyList<Port> Inputs { get; }
        public IReadOnlyList<Port> Outputs { get; }

        /// <summary>Other words it's found by when searching ("plus" for Add).</summary>
        public string Keywords { get; init; } = "";

        public NodeType(string id, string title, string category, IReadOnlyList<Port> inputs, IReadOnlyList<Port> outputs)
        {
            Id = id;
            Title = title;
            Category = category;
            Inputs = inputs;
            Outputs = outputs;
        }

        /// <summary>
        /// An input that repeats, after the others: "Sound 1", "Sound 2"... as many as are linked, and one more to link the
        /// next to, so a node can take any number (an audio graph's Random, choosing from 3 sounds or 20). Null for none.
        /// </summary>
        public Port? Repeated { get; init; }

        /// <summary>How many of <see cref="Repeated"/> there are with none linked.</summary>
        public int RepeatedAtLeast { get; init; } = 2;

        public Port? Input(string name)
        {
            foreach (Port port in Inputs)
            {
                if (port.Name == name) return port;
            }

            return RepeatIndex(name) is int index ? RepeatedPort(index) : null;
        }

        /// <summary>
        /// A node's inputs, as it shows them: <see cref="Inputs"/>, then <see cref="Repeated"/> as many times as it needs
        /// (up to the highest one linked, and one more).
        /// </summary>
        public IReadOnlyList<Port> InputsOf(Graph graph, GraphNode node)
        {
            if (Repeated == null) return Inputs;

            int count = RepeatedCount(graph, node);
            var all = new List<Port>(Inputs.Count + count);
            all.AddRange(Inputs);
            for (int i = 1; i <= count; i++) all.Add(RepeatedPort(i));
            return all;
        }

        /// <summary>How many of <see cref="Repeated"/> a node shows: up to the highest one linked, and one more.</summary>
        public int RepeatedCount(Graph graph, GraphNode node)
        {
            if (Repeated == null) return 0;
            int highest = 0;
            foreach (GraphLink link in graph.Links)
            {
                if (link.ToNode == node.Id && RepeatIndex(link.ToPort) is int index) highest = Math.Max(highest, index);
            }

            return Math.Max(RepeatedAtLeast, highest + 1);
        }

        /// <summary>Which of <see cref="Repeated"/> a port is, counting from 1 ("Sound 3" is 3); null if it isn't one.</summary>
        public int? RepeatIndex(string port)
        {
            if (Repeated == null || port.Length <= Repeated.Name.Length + 1 || !port.StartsWith(Repeated.Name + " ", StringComparison.Ordinal)) return null;
            return int.TryParse(port.AsSpan(Repeated.Name.Length + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) && index >= 1 ? index : null;
        }

        private readonly List<Port> _repeated = new();

        private Port RepeatedPort(int index)
        {
            lock (_repeated)
            {
                while (_repeated.Count < index) _repeated.Add(Repeated! with { Name = $"{Repeated!.Name} {_repeated.Count + 1}" });
                return _repeated[index - 1];
            }
        }

        public Port? Output(string name)
        {
            foreach (Port port in Outputs)
            {
                if (port.Name == name) return port;
            }

            return null;
        }

        /// <summary>Whether it takes part in the flow (has a flow input), rather than only working out values.</summary>
        public bool HasFlowInput => Inputs.Any(p => p.Type.IsFlow);

        public override string ToString() => Id;
    }

    /// <summary>
    /// The kinds of node one kind of graph can have: what an editor offers to add, and what a saved graph's nodes are
    /// looked up in.
    /// </summary>
    public class NodeLibrary
    {
        private readonly Dictionary<string, NodeType> _types = new();
        private readonly List<NodeType> _ordered = new();

        /// <summary>The kind of graph it's for, saved in graph files: "script".</summary>
        public string Kind { get; }

        /// <summary>What the graphs are called, for an editor: "Visual Script".</summary>
        public string DisplayName { get; }

        public NodeLibrary(string kind, string displayName)
        {
            Kind = kind;
            DisplayName = displayName;
        }

        public IReadOnlyList<NodeType> All => _ordered;

        public T Add<T>(T type) where T : NodeType
        {
            if (!_types.TryAdd(type.Id, type)) throw new InvalidOperationException($"There's already a node {type.Id}.");
            _ordered.Add(type);
            return type;
        }

        public NodeType? Find(string id) => _types.GetValueOrDefault(id);

        /// <summary>The colour of a category's node headers.</summary>
        public virtual Color CategoryColor(string category) => new(0.4f, 0.42f, 0.48f);

        /// <summary>What a graph of this kind calls its variables, one and several ("Variable", "Variables"), and what they're for.</summary>
        public virtual (string One, string Several, string Note) VariableWords => ("Variable", "Variables", "kept per object while it runs");

        private static readonly Dictionary<string, NodeLibrary> _libraries = new();

        /// <summary>Makes a library known by its kind, so graphs of that kind can find it.</summary>
        public static void Register(NodeLibrary library)
        {
            lock (_libraries) _libraries[library.Kind] = library;
        }

        public static NodeLibrary? ForKind(string kind)
        {
            // The engine's own libraries register themselves when first used.
            if (kind == Scripting.ScriptNodes.Kind) _ = Scripting.ScriptNodes.Library;
            if (kind == Effects.VfxNodes.Kind) _ = Effects.VfxNodes.Library;
            if (kind == Audio.AudioNodes.Kind) _ = Audio.AudioNodes.Library;
            lock (_libraries) return _libraries.GetValueOrDefault(kind);
        }
    }
}
