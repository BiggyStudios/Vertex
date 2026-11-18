using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Vertex.Graphs
{
    /// <summary>A node in a graph: which kind it is, where it sits, and the values set on it (for inputs not linked, and settings).</summary>
    public sealed class GraphNode
    {
        /// <summary>Names it within its graph, for links: never reused while the graph's open.</summary>
        public int Id { get; }

        /// <summary>Its <see cref="NodeType.Id"/>.</summary>
        public string Type { get; }

        /// <summary>Its top-left corner, for an editor.</summary>
        public Vector2 Position { get; set; }

        /// <summary>Values set on it, by port name: numbers, true/false, text, vectors and enum choices.</summary>
        public Dictionary<string, object?> Values { get; } = new();

        public GraphNode(int id, string type, Vector2 position)
        {
            Id = id;
            Type = type;
            Position = position;
        }

        public override string ToString() => $"{Type} #{Id}";
    }

    /// <summary>A link from one node's output to another's input.</summary>
    public sealed record GraphLink(int FromNode, string FromPort, int ToNode, string ToPort);

    /// <summary>A value the graph keeps while it runs (a visual script's health or speed), with what it starts as.</summary>
    public sealed class GraphVariable
    {
        public string Name { get; set; }
        public PortType Type { get; set; }
        public object? Value { get; set; }

        public GraphVariable(string name, PortType type, object? value = null)
        {
            Name = name;
            Type = type;
            Value = value ?? type.Default;
        }
    }

    /// <summary>
    /// Nodes and the links between them, of one kind (<see cref="Kind"/>): a visual script, and later a shader, an audio
    /// graph or an animation graph. Saved as JSON, in a file of its own (a .vscript for a visual script), and loaded with
    /// <see cref="Assets.LoadGraph"/> so everything using one file shares it.
    /// <para>
    /// It's only the data: what the nodes do comes from the <see cref="NodeLibrary"/> for its kind.
    /// </para>
    /// </summary>
    public sealed class Graph
    {
        public const int FormatVersion = 1;

        /// <summary>The kind of graph: which <see cref="NodeLibrary"/> its nodes are from.</summary>
        public string Kind { get; set; }

        /// <summary>The file it was loaded from, in full; null if it wasn't.</summary>
        public string? Source { get; internal set; }

        public string Name => Source != null ? Path.GetFileNameWithoutExtension(Source) : "Graph";

        public List<GraphNode> Nodes { get; } = new();
        public List<GraphLink> Links { get; } = new();
        public List<GraphVariable> Variables { get; } = new();

        private int _nextId = 1;

        /// <summary>Counts changes: anything that keeps something worked out from the graph (a running script) knows to do it again.</summary>
        public int Version { get; private set; }

        public Graph(string kind = "script")
        {
            Kind = kind;
        }

        public NodeLibrary? Library => NodeLibrary.ForKind(Kind);

        /// <summary>Marks the graph changed. Changes made through its own methods do this themselves.</summary>
        public void Changed() => Version++;

        // ---------------------------------------------------------------- Nodes and links

        public GraphNode? Node(int id)
        {
            foreach (GraphNode node in Nodes)
            {
                if (node.Id == id) return node;
            }

            return null;
        }

        public GraphNode AddNode(string type, Vector2 position)
        {
            var node = new GraphNode(_nextId++, type, position);
            Nodes.Add(node);
            Changed();
            return node;
        }

        /// <summary>Takes a node out, with its links.</summary>
        public void RemoveNode(int id)
        {
            Nodes.RemoveAll(n => n.Id == id);
            Links.RemoveAll(l => l.FromNode == id || l.ToNode == id);
            Changed();
        }

        public IEnumerable<GraphLink> LinksTo(int node, string port) => Links.Where(l => l.ToNode == node && l.ToPort == port);
        public IEnumerable<GraphLink> LinksFrom(int node, string port) => Links.Where(l => l.FromNode == node && l.FromPort == port);

        /// <summary>
        /// Whether output <paramref name="fromPort"/> of one node can be linked to input <paramref name="toPort"/> of
        /// another: both there, of types that go together (see <see cref="PortType.CanLinkTo"/>), and not a node to itself.
        /// </summary>
        public bool CanLink(int fromNode, string fromPort, int toNode, string toPort)
        {
            if (fromNode == toNode || Library is not { } library) return false;
            if (Node(fromNode) is not { } from || Node(toNode) is not { } to) return false;
            if (library.Find(from.Type)?.Output(fromPort) is not { } output || library.Find(to.Type)?.Input(toPort) is not { } input) return false;
            return input.Linkable && output.Type.CanLinkTo(input.Type);
        }

        /// <summary>
        /// Links an output to an input, if they can be (see <see cref="CanLink"/>). An input takes one value, so what was
        /// linked to it is let go; a flow output goes on to one thing, so its old link is let go too.
        /// </summary>
        public bool Link(int fromNode, string fromPort, int toNode, string toPort)
        {
            if (!CanLink(fromNode, fromPort, toNode, toPort)) return false;

            bool flow = Library!.Find(Node(fromNode)!.Type)!.Output(fromPort)!.Type.IsFlow;
            if (flow) Links.RemoveAll(l => l.FromNode == fromNode && l.FromPort == fromPort);
            else Links.RemoveAll(l => l.ToNode == toNode && l.ToPort == toPort);

            Links.Add(new GraphLink(fromNode, fromPort, toNode, toPort));
            Changed();
            return true;
        }

        public void Unlink(GraphLink link)
        {
            if (Links.Remove(link)) Changed();
        }

        public GraphVariable? Variable(string name)
        {
            foreach (GraphVariable variable in Variables)
            {
                if (variable.Name == name) return variable;
            }

            return null;
        }

        // ---------------------------------------------------------------- While it runs

        private readonly Dictionary<int, double> _ran = new();
        private readonly Dictionary<int, string> _errors = new();

        /// <summary>A clock in seconds that keeps going whatever the game's time does: for <see cref="LastRan"/>.</summary>
        public static double Clock => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>When node <paramref name="id"/> last ran, by <see cref="Clock"/>, so an editor can light it up; null if it hasn't.</summary>
        public double? LastRan(int id)
        {
            lock (_ran) return _ran.TryGetValue(id, out double at) ? at : null;
        }

        /// <summary>What went wrong the last time node <paramref name="id"/> ran, if anything did.</summary>
        public string? Error(int id)
        {
            lock (_errors) return _errors.GetValueOrDefault(id);
        }

        internal void Ran(int id)
        {
            lock (_ran) _ran[id] = Clock;
        }

        internal void Failed(int id, string message)
        {
            lock (_errors) _errors[id] = message;
        }

        /// <summary>Forgets what ran and went wrong: when play stops, say.</summary>
        public void ClearActivity()
        {
            lock (_ran) _ran.Clear();
            lock (_errors) _errors.Clear();
        }

        // ---------------------------------------------------------------- Files

        /// <summary>The JSON last saved or loaded: a file written with the same isn't read in again.</summary>
        internal string? SavedJson { get; set; }

        /// <summary>Saves it to <paramref name="path"/>, which it then comes from.</summary>
        public void Save(string path)
        {
            string json = ToJson();
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory != null) Directory.CreateDirectory(directory);
            File.WriteAllText(path, json);
            Source = Path.GetFullPath(path);
            SavedJson = json;
        }

        /// <summary>Saves it back to the file it came from, if it changed since. True if it was written.</summary>
        public bool SaveIfChanged()
        {
            if (Source == null) return false;
            string json = ToJson();
            if (json == SavedJson) return false;
            File.WriteAllText(Source, json);
            SavedJson = json;
            return true;
        }

        public static Graph Load(string path)
        {
            string json = File.ReadAllText(path);
            Graph graph = FromJson(json);
            graph.Source = Path.GetFullPath(path);
            graph.SavedJson = json;
            return graph;
        }

        public static Graph FromJson(string json)
        {
            var graph = new Graph();
            graph.ReadJson(json);
            return graph;
        }

        /// <summary>Replaces everything in it with what's in <paramref name="json"/>, keeping it the same object (everything using it sees the change).</summary>
        public void ReadJson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            Nodes.Clear();
            Links.Clear();
            Variables.Clear();
            Kind = root.TryGetProperty("kind", out JsonElement kind) ? kind.GetString() ?? "script" : "script";

            if (root.TryGetProperty("variables", out JsonElement variables))
            {
                foreach (JsonElement saved in variables.EnumerateArray())
                {
                    PortType type = PortType.Named(saved.GetProperty("type").GetString() ?? "Any");
                    object? value = saved.TryGetProperty("value", out JsonElement v) ? type.Convert(ReadValue(v)) : null;
                    Variables.Add(new GraphVariable(saved.GetProperty("name").GetString() ?? "Variable", type, value));
                }
            }

            int highest = 0;
            if (root.TryGetProperty("nodes", out JsonElement nodes))
            {
                foreach (JsonElement saved in nodes.EnumerateArray())
                {
                    int id = saved.GetProperty("id").GetInt32();
                    var node = new GraphNode(id, saved.GetProperty("type").GetString() ?? "", new Vector2(Number(saved, "x"), Number(saved, "y")));
                    if (saved.TryGetProperty("values", out JsonElement values))
                    {
                        foreach (JsonProperty value in values.EnumerateObject()) node.Values[value.Name] = ReadValue(value.Value);
                    }

                    Nodes.Add(node);
                    highest = Math.Max(highest, id);
                }
            }

            if (root.TryGetProperty("links", out JsonElement links))
            {
                foreach (JsonElement saved in links.EnumerateArray())
                {
                    Links.Add(new GraphLink(saved.GetProperty("from").GetInt32(), saved.GetProperty("out").GetString() ?? "",
                        saved.GetProperty("to").GetInt32(), saved.GetProperty("in").GetString() ?? ""));
                }
            }

            _nextId = Math.Max(_nextId, highest + 1);
            Changed();
        }

        private static float Number(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) ? value.GetSingle() : 0f;

        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("format", FormatVersion);
                json.WriteString("kind", Kind);

                json.WriteStartArray("variables");
                foreach (GraphVariable variable in Variables)
                {
                    json.WriteStartObject();
                    json.WriteString("name", variable.Name);
                    json.WriteString("type", variable.Type.Name);
                    if (Writable(variable.Value))
                    {
                        json.WritePropertyName("value");
                        WriteValue(json, variable.Value);
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();

                json.WriteStartArray("nodes");
                foreach (GraphNode node in Nodes)
                {
                    json.WriteStartObject();
                    json.WriteNumber("id", node.Id);
                    json.WriteString("type", node.Type);
                    json.WriteNumber("x", MathF.Round(node.Position.X, 1));
                    json.WriteNumber("y", MathF.Round(node.Position.Y, 1));
                    if (node.Values.Any(v => Writable(v.Value)))
                    {
                        json.WriteStartObject("values");
                        foreach ((string name, object? value) in node.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
                        {
                            if (!Writable(value)) continue;
                            json.WritePropertyName(name);
                            WriteValue(json, value);
                        }

                        json.WriteEndObject();
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();

                json.WriteStartArray("links");
                foreach (GraphLink link in Links)
                {
                    json.WriteStartObject();
                    json.WriteNumber("from", link.FromNode);
                    json.WriteString("out", link.FromPort);
                    json.WriteNumber("to", link.ToNode);
                    json.WriteString("in", link.ToPort);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static bool Writable(object? value) => value is float or double or int or bool or string or Vector2 or Vector3 or Vector4 or Color or Enum;

        private static void WriteValue(Utf8JsonWriter json, object? value)
        {
            switch (value)
            {
                case float f: json.WriteNumberValue(f); break;
                case double d: json.WriteNumberValue(d); break;
                case int i: json.WriteNumberValue(i); break;
                case bool b: json.WriteBooleanValue(b); break;
                case string s: json.WriteStringValue(s); break;
                case Enum e: json.WriteStringValue(e.ToString()); break;
                case Vector2 v: Floats(v.X, v.Y); break;
                case Vector3 v: Floats(v.X, v.Y, v.Z); break;
                case Vector4 v: Floats(v.X, v.Y, v.Z, v.W); break;
                case Color c: Floats(c.R, c.G, c.B, c.A); break;
                default: json.WriteNullValue(); break;
            }

            void Floats(params float[] values)
            {
                json.WriteStartArray();
                foreach (float f in values) json.WriteNumberValue(f);
                json.WriteEndArray();
            }
        }

        /// <summary>A saved value: numbers as floats, arrays of two, three or four numbers as vectors; enum choices stay text until used.</summary>
        private static object? ReadValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number: return element.GetSingle();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Array:
                    float[] values = element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 0f).ToArray();
                    return values.Length switch
                    {
                        2 => new Vector2(values[0], values[1]),
                        3 => new Vector3(values[0], values[1], values[2]),
                        4 => new Vector4(values[0], values[1], values[2], values[3]),
                        _ => null
                    };
                default: return null;
            }
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Kind}, {Nodes.Count} nodes)");
    }
}
