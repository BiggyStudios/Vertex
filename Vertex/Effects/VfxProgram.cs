using System.Numerics;

using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.Effects
{
    /// <summary>Something in a VFX graph that can't be worked out: which node, and why.</summary>
    public sealed class VfxException(GraphNode node, string message) : Exception(message)
    {
        public GraphNode Node { get; } = node;
    }

    /// <summary>One particle system in a VFX graph, worked out ready to run: its settings, and how to work out each input.</summary>
    internal sealed class VfxSystem
    {
        public required GraphNode Spawn;
        public required VfxValue Rate;
        public required VfxValue Burst;
        public float Duration;
        public bool Loop = true;
        public int MaxParticles = 1000;
        public bool LocalSpace;

        // Initialize (what each starts as); null where there's no Initialize step, for the defaults.
        public VfxValue? Lifetime, Position, Velocity, Size, Color, Rotation;

        // Update (every frame), if there's an Update step.
        public bool HasUpdate;
        public VfxValue? Force, Drag, Turbulence, TurbulenceScale, Spin, Kill;

        // Output (drawing), if there's an Output step: without one, nothing's drawn.
        public bool HasOutput;
        public VfxValue? Tint, SizeScale, Glow, Stretch;
        public ParticleLook Look;
        public ParticleBlend Blend = ParticleBlend.Additive;
    }

    /// <summary>A VFX graph worked out into the particle systems it describes.</summary>
    internal sealed class VfxProgram
    {
        public List<VfxSystem> Systems { get; } = new();

        private readonly Graph _graph;
        private readonly NodeLibrary _library = VfxNodes.Library;
        private readonly Dictionary<(int Node, string Port), VfxValue> _outputs = new();
        private readonly HashSet<int> _working = new();

        private VfxProgram(Graph graph)
        {
            _graph = graph;
        }

        /// <summary>Works out <paramref name="graph"/>. Throws a <see cref="VfxException"/> saying which node can't be.</summary>
        public static VfxProgram Compile(Graph graph)
        {
            var program = new VfxProgram(graph);
            foreach (GraphNode node in graph.Nodes)
            {
                if (program.TypeOf(node) is { Role: VfxRole.Spawn }) program.Systems.Add(program.System(node));
            }

            return program;
        }

        private VfxNode? TypeOf(GraphNode node) => _library.Find(node.Type) as VfxNode;

        private VfxSystem System(GraphNode spawn)
        {
            var system = new VfxSystem
            {
                Spawn = spawn,
                Rate = Input(spawn, "Rate"),
                Burst = Input(spawn, "Burst"),
                Duration = Constant(spawn, "Duration").X,
                Loop = Constant(spawn, "Loop").X != 0f,
                MaxParticles = Math.Clamp((int)Constant(spawn, "Max Particles").X, 1, 100_000),
                LocalSpace = Constant(spawn, "Local Space").X != 0f
            };

            // Each step after the one before, along their Particles links.
            var seen = new HashSet<int> { spawn.Id };
            GraphNode? step = Next(spawn);
            while (step != null && seen.Add(step.Id))
            {
                switch (TypeOf(step)?.Role)
                {
                    case VfxRole.Initialize:
                        system.Lifetime = Input(step, "Lifetime");
                        system.Position = Input(step, "Position");
                        system.Velocity = Input(step, "Velocity");
                        system.Size = Input(step, "Size");
                        system.Color = Input(step, "Color");
                        system.Rotation = Input(step, "Rotation");
                        break;
                    case VfxRole.Update:
                        system.HasUpdate = true;
                        system.Force = Input(step, "Force");
                        system.Drag = Input(step, "Drag");
                        system.Turbulence = Input(step, "Turbulence");
                        system.TurbulenceScale = Input(step, "Turbulence Scale");
                        system.Spin = Input(step, "Spin");
                        system.Kill = Input(step, "Kill");
                        break;
                    case VfxRole.Output:
                        system.HasOutput = true;
                        system.Tint = Input(step, "Tint");
                        system.SizeScale = Input(step, "Size Scale");
                        system.Glow = Input(step, "Glow");
                        system.Stretch = Input(step, "Stretch");
                        system.Look = (ParticleLook)(int)Constant(step, "Look").X;
                        system.Blend = (ParticleBlend)(int)Constant(step, "Blend").X;
                        break;
                }

                step = Next(step);
            }

            return system;
        }

        private GraphNode? Next(GraphNode step) =>
            _graph.LinksFrom(step.Id, "Particles").Select(l => _graph.Node(l.ToNode)).FirstOrDefault(n => n != null);

        /// <summary>How to work out input <paramref name="name"/> of <paramref name="node"/>: from what's linked to it, or the value set on it.</summary>
        private VfxValue Input(GraphNode node, string name)
        {
            Port port = TypeOf(node)?.Input(name) ?? throw new VfxException(node, $"{node.Type} has no input {name}");
            PortType type = _graph.TypeOf(node, port);

            if (port.Linkable && _graph.LinksTo(node.Id, name).FirstOrDefault() is { } link && _graph.Node(link.FromNode) is { } from)
            {
                VfxNode source = TypeOf(from) ?? throw new VfxException(from, $"There's no node called {from.Type}");
                Port output = source.Output(link.FromPort) ?? throw new VfxException(from, $"{source.Title} has no output {link.FromPort}");
                return Converted(Output(from, link.FromPort), _graph.TypeOf(from, output), type);
            }

            Vector4 constant = Constant(node, name);
            return (ref VfxParticle _, VfxFrame _) => constant;
        }

        /// <summary>The value set on <paramref name="node"/> for input <paramref name="name"/> (or its default), as a value.</summary>
        private Vector4 Constant(GraphNode node, string name)
        {
            Port port = TypeOf(node)?.Input(name) ?? throw new VfxException(node, $"{node.Type} has no input {name}");
            return ToValue(_graph.TypeOf(node, port), node.Values.TryGetValue(name, out object? set) ? set : port.DefaultValue);
        }

        /// <summary>A value of any type in the one shape the graph works in.</summary>
        internal static Vector4 ToValue(PortType type, object? value) => type.Convert(value) switch
        {
            float f => new Vector4(f, 0f, 0f, 0f),
            bool b => new Vector4(b ? 1f : 0f, 0f, 0f, 0f),
            Vector3 v => new Vector4(v, 0f),
            Color c => new Vector4(c.R, c.G, c.B, c.A),
            Enum e => new Vector4(Convert.ToInt32(e), 0f, 0f, 0f),
            _ => Vector4.Zero
        };

        /// <summary>A value of type <paramref name="from"/> going into an input of <paramref name="to"/>: a number into a vector fills all three parts.</summary>
        private static VfxValue Converted(VfxValue value, PortType from, PortType to)
        {
            if (from == PortType.Number && to == PortType.Vector) return (ref VfxParticle p, VfxFrame f) => new Vector4(value(ref p, f).X, value(ref p, f).X, value(ref p, f).X, 0f);
            if (from == PortType.Vector && to == PortType.Color) return (ref VfxParticle p, VfxFrame f) => value(ref p, f) with { W = 1f };
            if (from == PortType.Color && to == PortType.Vector) return (ref VfxParticle p, VfxFrame f) => value(ref p, f) with { W = 0f };
            return value;
        }

        /// <summary>How to work out output <paramref name="port"/> of value node <paramref name="node"/>.</summary>
        private VfxValue Output(GraphNode node, string port)
        {
            if (_outputs.TryGetValue((node.Id, port), out VfxValue? done)) return done;

            VfxNode type = TypeOf(node) ?? throw new VfxException(node, $"There's no node called {node.Type}");
            if (type.Role != VfxRole.Value) throw new VfxException(node, $"{type.Title}'s {port} only links to the next step");

            // A property: what the game's set it to on this object, or the variable's own value.
            if (type.Inputs.FirstOrDefault(p => p.IsVariableName) is { } setting)
            {
                string name = node.Values.TryGetValue(setting.Name, out object? chosen) ? chosen as string ?? "" : "";
                GraphVariable variable = _graph.Variable(name) ?? throw new VfxException(node, name.Length == 0 ? "No property chosen" : $"There's no property called {name}");
                Vector4 fallback = ToValue(variable.Type, variable.Value);
                VfxValue property = (ref VfxParticle _, VfxFrame f) => f.Properties.TryGetValue(name, out Vector4 value) ? value : fallback;
                _outputs[(node.Id, port)] = property;
                return property;
            }

            if (!_working.Add(node.Id)) throw new VfxException(node, $"{type.Title} is worked out from itself: its links go round in a circle");
            VfxValue[] inputs = type.Inputs.Select(p => Input(node, p.Name)).ToArray();
            VfxValue[] outputs = type.Compile!(inputs);
            _working.Remove(node.Id);

            for (int i = 0; i < outputs.Length && i < type.Outputs.Count; i++) _outputs[(node.Id, type.Outputs[i].Name)] = outputs[i];
            return _outputs.TryGetValue((node.Id, port), out VfxValue? made) ? made : throw new VfxException(node, $"{type.Title} has no output {port}");
        }
    }
}
