using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.ShaderGraphs
{
    /// <summary>The GLSL types a shader graph's values have.</summary>
    public enum ShaderType
    {
        Float = 1,
        Vector2 = 2,
        Vector3 = 3,
        Vector4 = 4,
        Bool = 5,

        /// <summary>A texture to sample: a sampler2D, which can only be handed to functions that sample it.</summary>
        Texture = 6
    }

    /// <summary>A value in a shader graph, as GLSL: an expression, and its type.</summary>
    public readonly record struct ShaderCode(string Code, ShaderType Type)
    {
        /// <summary>How many numbers it has: 1 for a float (and a bool), up to 4 for a vec4.</summary>
        public int Size => Type is >= ShaderType.Float and <= ShaderType.Vector4 ? (int)Type : 1;

        public override string ToString() => Code;
    }

    /// <summary>
    /// What a <see cref="ShaderNode"/> is given to write its GLSL with: its inputs' code, its settings, and ways to add
    /// lines, helper functions and textures to the shader.
    /// </summary>
    public sealed class ShaderNodeContext
    {
        private readonly ShaderGraphCompiler.Compilation _compilation;
        private readonly ShaderCode[] _inputs;
        private readonly bool[] _linked;
        private readonly Port[] _ports;

        internal ShaderNodeContext(ShaderGraphCompiler.Compilation compilation, GraphNode node, ShaderNode type, Port[] ports, ShaderCode[] inputs, bool[] linked)
        {
            _compilation = compilation;
            Node = node;
            Type = type;
            _ports = ports;
            _inputs = inputs;
            _linked = linked;
        }

        public GraphNode Node { get; }
        public ShaderNode Type { get; }

        /// <summary>The kind of shader being made: a post effect or a surface.</summary>
        public ShaderPurpose Target => _compilation.Target;

        /// <summary>
        /// Input <paramref name="index"/>'s code: what's linked to it, as its port's type (a dynamic port keeps what's
        /// linked's own), or its value typed on the node.
        /// </summary>
        public ShaderCode Input(int index) => _inputs[index];

        public bool IsLinked(int index) => _linked[index];

        /// <summary>
        /// The type the node's dynamic inputs (<see cref="ShaderNodes.Value"/> ports) work in: the biggest of what's
        /// linked to them (a number and a vector make a vector), a number if nothing is.
        /// </summary>
        public ShaderType DynamicType
        {
            get
            {
                int size = 1;
                for (int i = 0; i < _ports.Length; i++)
                {
                    if (_ports[i].Type == ShaderNodes.Value && _linked[i]) size = Math.Max(size, _inputs[i].Size);
                }

                return (ShaderType)size;
            }
        }

        /// <summary>Dynamic input <paramref name="index"/>, as <see cref="DynamicType"/>.</summary>
        public ShaderCode Dynamic(int index) => ShaderGraphCompiler.Cast(_inputs[index], DynamicType);

        /// <summary>Input <paramref name="index"/> as <paramref name="type"/>.</summary>
        public ShaderCode As(int index, ShaderType type) => ShaderGraphCompiler.Cast(_inputs[index], type);

        /// <summary>A texture coordinate input: what's linked, or the pixel's own (the mesh's, or the picture's).</summary>
        public ShaderCode UV(int index) => _linked[index] ? As(index, ShaderType.Vector2) : new ShaderCode("uv", ShaderType.Vector2);

        /// <summary>A setting on the node (one that isn't linked), as its port's type has it.</summary>
        public T Setting<T>(string name)
        {
            Port port = Type.Input(name) ?? throw new ArgumentException($"{Type.Title} has no setting {name}.");
            object? value = port.Type.Convert(Node.Values.TryGetValue(name, out object? set) ? set : port.DefaultValue);
            return value is T typed ? typed : (T)port.Type.Convert(port.DefaultValue)!;
        }

        /// <summary>
        /// Texture input <paramref name="index"/>: the texture property linked to it, or the file chosen on the node. With
        /// neither, it's <paramref name="fallback"/> (a flat one for normal maps).
        /// </summary>
        public ShaderCode Texture(int index, TextureFallback fallback = TextureFallback.White) =>
            _compilation.TextureInput(Node, _ports[index], _linked[index] ? _inputs[index] : null, fallback);

        /// <summary>One of the graph's properties, by name, as the shader reads it; null if there's none by that name.</summary>
        public ShaderCode? Property(string name) => _compilation.Variable(name);

        /// <summary>Writes a line into the shader's function, before the node's outputs are worked out.</summary>
        public void Line(string code) => _compilation.Line(code);

        /// <summary>Works out <paramref name="code"/> once into a variable of its own, and gives its name.</summary>
        public string Local(ShaderType type, string code) => _compilation.Local(Node, type, code);

        /// <summary>Adds a helper function to the shader (once, however many nodes need it), by its name.</summary>
        public void Function(string name, string code) => _compilation.Function(name, code);

        /// <summary>Says what's wrong with the node: it shows on the node, and the shader isn't drawn until it's put right.</summary>
        public void Fail(string message) => _compilation.Fail(Node, Type, message);

        /// <summary>A colour typed into the graph, as the shader works in: linear in a surface, as it is in a post effect.</summary>
        public string Color(Color color) => ShaderGraphCompiler.ColorLiteral(color, Target);

        /// <summary>A number as GLSL writes it: always with a decimal point.</summary>
        public static string Number(float value) => ShaderGraphCompiler.Number(value);
    }

    /// <summary>Turns a shader graph into GLSL, as a <see cref="Shader"/> made from the graph needs it.</summary>
    internal static class ShaderGraphCompiler
    {
        /// <summary>Works <paramref name="shader"/> out again from <paramref name="graph"/>: its code, settings and problems.</summary>
        public static void Compile(Graph graph, Shader shader)
        {
            var compilation = new Compilation(graph, shader.Name);
            compilation.Run();
            shader.Apply(compilation.Body, compilation.Body, compilation.Purpose, compilation.Properties, compilation.Problems);
        }

        public static string GlslType(ShaderType type) => type switch
        {
            ShaderType.Float => "float",
            ShaderType.Vector2 => "vec2",
            ShaderType.Vector3 => "vec3",
            ShaderType.Vector4 => "vec4",
            ShaderType.Bool => "bool",
            _ => "sampler2D"
        };

        /// <summary>The type of value a port carries, in GLSL. A dynamic port, unlinked, is a number.</summary>
        public static ShaderType TypeOf(PortType type)
        {
            if (type == PortType.Boolean) return ShaderType.Bool;
            if (type == ShaderNodes.Vector2) return ShaderType.Vector2;
            if (type == PortType.Vector) return ShaderType.Vector3;
            if (type == PortType.Color) return ShaderType.Vector4;
            if (type == PortType.Texture) return ShaderType.Texture;
            return ShaderType.Float;
        }

        /// <summary>
        /// <paramref name="value"/> as <paramref name="type"/>: a number fills a vector, a vector loses its last parts or
        /// gains zeros (and a solid alpha), a true/false is 1 or 0.
        /// </summary>
        public static ShaderCode Cast(ShaderCode value, ShaderType type)
        {
            if (value.Type == type) return value;
            if (value.Type == ShaderType.Texture || type == ShaderType.Texture) return Default(type);

            string code = value.Code;
            if (value.Type == ShaderType.Bool) return Cast(new ShaderCode($"({code} ? 1.0 : 0.0)", ShaderType.Float), type);
            if (type == ShaderType.Bool) return new ShaderCode(value.Type == ShaderType.Float ? $"({code} > 0.5)" : $"(({code}).x > 0.5)", ShaderType.Bool);

            string cast = (value.Type, type) switch
            {
                (ShaderType.Float, _) => $"{GlslType(type)}({code})",
                (_, ShaderType.Float) => $"({code}).x",
                (ShaderType.Vector2, ShaderType.Vector3) => $"vec3({code}, 0.0)",
                (ShaderType.Vector2, ShaderType.Vector4) => $"vec4({code}, 0.0, 1.0)",
                (ShaderType.Vector3, ShaderType.Vector4) => $"vec4({code}, 1.0)",
                (_, ShaderType.Vector2) => $"({code}).xy",
                _ => $"({code}).xyz"
            };
            return new ShaderCode(cast, type);
        }

        public static ShaderCode Default(ShaderType type) => type switch
        {
            ShaderType.Bool => new ShaderCode("false", type),
            ShaderType.Float => new ShaderCode("0.0", type),
            ShaderType.Texture => new ShaderCode("shaderTexture0", type),
            _ => new ShaderCode($"{GlslType(type)}(0.0)", type)
        };

        public static string Number(float value) =>
            float.IsFinite(value) ? value.ToString("0.0#######", CultureInfo.InvariantCulture) : "0.0";

        public static string ColorLiteral(Color color, ShaderPurpose target)
        {
            Vector3 rgb = target == ShaderPurpose.Surface ? Rendering.Shaders.FrameUniforms.ToLinear(color) : new Vector3(color.R, color.G, color.B);
            return $"vec4({Number(rgb.X)}, {Number(rgb.Y)}, {Number(rgb.Z)}, {Number(color.A)})";
        }

        private static readonly Regex _simple = new(@"^([A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*|-?[0-9]+\.[0-9]+)$", RegexOptions.Compiled);

        /// <summary>One compile of one graph.</summary>
        internal sealed class Compilation(Graph graph, string name)
        {
            private readonly NodeLibrary _library = ShaderNodes.Library;
            private readonly Dictionary<int, ShaderCode[]> _done = new();
            private readonly HashSet<int> _visiting = new();
            private readonly StringBuilder _lines = new();
            private readonly List<(string Name, string Code)> _functions = new();
            private readonly Dictionary<string, ShaderCode> _variables = new();
            private readonly Dictionary<(string? File, TextureFallback Fallback), ShaderCode> _ownTextures = new();
            private int _textures;
            private int _values;

            public ShaderPurpose Target { get; private set; }
            public ShaderPurpose? Purpose { get; private set; }
            public string Body { get; private set; } = "";
            public List<ShaderProperty> Properties { get; } = new();
            public List<string> Problems { get; } = new();

            public void Run()
            {
                graph.ClearActivity();

                List<GraphNode> outputs = graph.Nodes.Where(n => _library.Find(n.Type) is ShaderNode { IsOutput: true }).ToList();
                if (outputs.Count == 0)
                {
                    Problems.Add($"{name}: a shader graph needs an output: a Surface node (for a material) or a Post Effect node (for a camera)");
                    return;
                }

                GraphNode output = outputs[0];
                foreach (GraphNode extra in outputs.Skip(1)) Fail(extra, (ShaderNode)_library.Find(extra.Type)!, "A shader graph has one output, and this isn't it: take it out");

                var outputType = (ShaderNode)_library.Find(output.Type)!;
                Target = outputType.Only!.Value;
                Purpose = Target;

                AddProperties();
                Generate(output);

                var body = new StringBuilder();
                body.Append($"// Written by the shader graph {name}.\n\n");
                foreach ((string _, string code) in _functions) body.Append(code.Trim()).Append("\n\n");
                body.Append(Target == ShaderPurpose.Surface ? "void surface(inout Surface s)\n{\n" : "vec4 effect(vec2 uv)\n{\n");
                body.Append(_lines);
                body.Append("}\n");
                Body = body.ToString();
            }

            /// <summary>The graph's variables, as the shader's settings, in the order they're listed.</summary>
            private void AddProperties()
            {
                foreach (GraphVariable variable in graph.Variables)
                {
                    ShaderPropertyType? type = variable.Type == PortType.Number ? ShaderPropertyType.Float
                        : variable.Type == PortType.Boolean ? ShaderPropertyType.Bool
                        : variable.Type == ShaderNodes.Vector2 ? ShaderPropertyType.Vector2
                        : variable.Type == PortType.Vector ? ShaderPropertyType.Vector3
                        : variable.Type == PortType.Color ? ShaderPropertyType.Color
                        : variable.Type == PortType.Texture ? ShaderPropertyType.Texture
                        : null;
                    if (type == null)
                    {
                        Problems.Add($"{name}: the property {variable.Name} is a {variable.Type.Name}, which a shader can't have");
                        continue;
                    }

                    ShaderProperty property;
                    if (type == ShaderPropertyType.Texture)
                    {
                        if (_textures == Shader.MaxTextures)
                        {
                            Problems.Add($"{name}: a shader can have up to {Shader.MaxTextures} textures, and {variable.Name} is one too many");
                            continue;
                        }

                        property = new ShaderProperty(variable.Name, type.Value, Vector4.Zero, null, null, null, _textures++)
                        {
                            DefaultTexture = variable.Value as string is { Length: > 0 } file ? file : null
                        };
                    }
                    else
                    {
                        if (_values == Shader.MaxProperties)
                        {
                            Problems.Add($"{name}: a shader can have up to {Shader.MaxProperties} properties, and {variable.Name} is one too many");
                            continue;
                        }

                        property = new ShaderProperty(variable.Name, type.Value, Packed(variable), null, null, null, _values++);
                    }

                    Properties.Add(property);
                    _variables[variable.Name] = new ShaderCode(Shader.Swizzle(property), type switch
                    {
                        ShaderPropertyType.Float => ShaderType.Float,
                        ShaderPropertyType.Bool => ShaderType.Bool,
                        ShaderPropertyType.Vector2 => ShaderType.Vector2,
                        ShaderPropertyType.Vector3 => ShaderType.Vector3,
                        ShaderPropertyType.Texture => ShaderType.Texture,
                        _ => ShaderType.Vector4
                    });
                }
            }

            private static Vector4 Packed(GraphVariable variable)
            {
                object? value = variable.Type.Convert(variable.Value);
                return value switch
                {
                    float f => new Vector4(f, 0f, 0f, 0f),
                    bool b => new Vector4(b ? 1f : 0f, 0f, 0f, 0f),
                    System.Numerics.Vector2 v => new Vector4(v, 0f, 0f),
                    Vector3 v => new Vector4(v, 0f),
                    Color c => new Vector4(c.R, c.G, c.B, c.A),
                    _ => Vector4.Zero
                };
            }

            /// <summary>The shader code for one of the graph's properties, by name; null if there's no such property.</summary>
            public ShaderCode? Variable(string variable) => _variables.TryGetValue(variable, out ShaderCode code) ? code : null;

            /// <summary>Works out a node's outputs, once, working out first what's linked into it.</summary>
            private ShaderCode[] Generate(GraphNode node)
            {
                if (_done.TryGetValue(node.Id, out ShaderCode[]? done)) return done;

                if (_library.Find(node.Type) is not ShaderNode type)
                {
                    graph.Failed(node.Id, "There's no such node");
                    Problems.Add($"{name}: there's no node {node.Type}");
                    return [];
                }

                ShaderCode[] defaults = type.Outputs.Select(p => Default(TypeOf(p.Type))).ToArray();
                if (!_visiting.Add(node.Id))
                {
                    Fail(node, type, "It's linked round in a loop back into itself");
                    return defaults;
                }

                if (type.Only is { } only && only != Target)
                    Fail(node, type, only == ShaderPurpose.PostEffect ? "Only works in a post effect graph" : "Only works in a surface graph, for materials");

                Port[] ports = type.InputsOf(graph, node).ToArray();
                var inputs = new ShaderCode[ports.Length];
                var linked = new bool[ports.Length];
                for (int i = 0; i < ports.Length; i++)
                {
                    Port port = ports[i];
                    if (!port.Linkable) continue;

                    GraphLink? link = graph.LinksTo(node.Id, port.Name).FirstOrDefault();
                    if (link != null && graph.Node(link.FromNode) is { } from && _library.Find(from.Type) is ShaderNode fromType)
                    {
                        ShaderCode[] outputs = Generate(from);
                        int index = fromType.Outputs.ToList().FindIndex(p => p.Name == link.FromPort);
                        if (index >= 0 && index < outputs.Length)
                        {
                            ShaderCode value = outputs[index];
                            inputs[i] = port.Type == ShaderNodes.Value ? value : Cast(value, TypeOf(port.Type));
                            linked[i] = true;
                            continue;
                        }
                    }

                    inputs[i] = Literal(node, port);
                }

                ShaderCode[] results;
                try
                {
                    results = type.Generate?.Invoke(new ShaderNodeContext(this, node, type, ports, inputs, linked)) ?? [];
                }
                catch (Exception exception)
                {
                    Fail(node, type, exception.Message);
                    results = defaults;
                }

                // Each output worked out once, into a variable named after the node, so what uses it twice doesn't work
                // it out twice. Textures can't be kept in variables, and plain names needn't be.
                for (int i = 0; i < results.Length; i++)
                {
                    ShaderCode result = results[i];
                    if (result.Type == ShaderType.Texture || _simple.IsMatch(result.Code)) continue;
                    results[i] = new ShaderCode(Local(node, result.Type, result.Code), result.Type);
                }

                _visiting.Remove(node.Id);
                _done[node.Id] = results;
                return results;
            }

            /// <summary>An input with nothing linked: its value typed on the node, or its port's own.</summary>
            private ShaderCode Literal(GraphNode node, Port port)
            {
                object? raw = node.Values.TryGetValue(port.Name, out object? set) ? set : port.DefaultValue;
                ShaderType type = TypeOf(port.Type);
                switch (type)
                {
                    case ShaderType.Bool:
                        return new ShaderCode((bool)PortType.Boolean.Convert(raw)! ? "true" : "false", type);
                    case ShaderType.Vector2:
                        var v2 = raw is System.Numerics.Vector2 two ? two : raw is Vector3 three ? new System.Numerics.Vector2(three.X, three.Y) : raw is float f2 ? new System.Numerics.Vector2(f2) : System.Numerics.Vector2.Zero;
                        return new ShaderCode($"vec2({Number(v2.X)}, {Number(v2.Y)})", type);
                    case ShaderType.Vector3:
                        var v3 = (Vector3)PortType.Vector.Convert(raw)!;
                        return new ShaderCode($"vec3({Number(v3.X)}, {Number(v3.Y)}, {Number(v3.Z)})", type);
                    case ShaderType.Vector4:
                        return new ShaderCode(ColorLiteral((Color)PortType.Color.Convert(raw)!, Target), type);
                    case ShaderType.Texture:
                        return default;
                    default:
                        return new ShaderCode(Number((float)PortType.Number.Convert(raw)!), ShaderType.Float);
                }
            }

            /// <summary>
            /// A node's texture: the property linked to it (which, used as a normal map, starts as a flat one), or the
            /// file chosen on the node, kept as a setting of the shader's own that materials don't show.
            /// </summary>
            public ShaderCode TextureInput(GraphNode node, Port port, ShaderCode? linked, TextureFallback fallback)
            {
                if (linked is { Type: ShaderType.Texture } property)
                {
                    if (fallback != TextureFallback.White)
                    {
                        int index = Properties.FindIndex(p => p.Type == ShaderPropertyType.Texture && Shader.Swizzle(p) == property.Code);
                        if (index >= 0) Properties[index] = Properties[index] with { Fallback = fallback };
                    }

                    return property;
                }

                string? file = node.Values.TryGetValue(port.Name, out object? set) && set is string { Length: > 0 } chosen ? chosen : null;
                if (_ownTextures.TryGetValue((file, fallback), out ShaderCode own)) return own;
                if (_textures == Shader.MaxTextures)
                {
                    Fail(node, (ShaderNode)_library.Find(node.Type)!, $"A shader can have up to {Shader.MaxTextures} textures");
                    return Default(ShaderType.Texture);
                }

                var hidden = new ShaderProperty($"#{node.Id} {port.Name}", ShaderPropertyType.Texture, Vector4.Zero, null, null, null, _textures++)
                {
                    Fallback = fallback,
                    DefaultTexture = file,
                    Hidden = true
                };
                Properties.Add(hidden);
                own = new ShaderCode(Shader.Swizzle(hidden), ShaderType.Texture);
                _ownTextures[(file, fallback)] = own;
                return own;
            }

            public void Line(string code) => _lines.Append("    ").Append(code).Append('\n');

            private int _locals;

            public string Local(GraphNode node, ShaderType type, string code)
            {
                string local = $"n{node.Id}_{_locals++}";
                Line($"{GlslType(type)} {local} = {code};");
                return local;
            }

            public void Function(string function, string code)
            {
                if (_functions.Any(f => f.Name == function)) return;
                _functions.Add((function, code));
            }

            public void Fail(GraphNode node, ShaderNode type, string message)
            {
                graph.Failed(node.Id, message);
                Problems.Add($"{name}: {type.Title}: {message}");
            }
        }
    }
}
