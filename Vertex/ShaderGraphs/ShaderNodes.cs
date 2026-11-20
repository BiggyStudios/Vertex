using System.Numerics;

using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.ShaderGraphs
{
    /// <summary>What a Sample Texture node's texture holds: colours (made linear in a surface graph), or other values.</summary>
    public enum TextureContents
    {
        Colors,

        /// <summary>Masks, heights, noise: used as they're stored.</summary>
        Data
    }

    /// <summary>Which kind of noise a Noise node makes.</summary>
    public enum NoiseKind
    {
        /// <summary>Smooth rolling hills and valleys (Perlin's).</summary>
        Gradient,

        /// <summary>Softer blobs, a little blockier.</summary>
        Value
    }

    /// <summary>How a Blend node mixes one colour onto another, as image editors do.</summary>
    public enum ColorBlend
    {
        Multiply,
        Screen,
        Overlay,
        Add,
        Subtract,
        Lighten,
        Darken,
        Difference
    }

    /// <summary>How a Compare node compares A with B.</summary>
    public enum CompareOperation
    {
        Greater,
        Less,
        Equal,
        NotEqual,
        GreaterOrEqual,
        LessOrEqual
    }

    /// <summary>What an Expression node gives back.</summary>
    public enum ExpressionResult
    {
        Number,
        Vector2,
        Vector3,
        Color
    }

    /// <summary>
    /// A kind of node in a shader graph. It writes GLSL (<see cref="Generate"/>): given its inputs' code, the code for
    /// each of its outputs.
    /// </summary>
    public sealed class ShaderNode : NodeType
    {
        public ShaderNode(string id, string title, string category, IReadOnlyList<Port> inputs, IReadOnlyList<Port> outputs)
            : base(id, title, category, inputs, outputs) {}

        /// <summary>Writes its GLSL: the code for each of its outputs, in order.</summary>
        public Func<ShaderNodeContext, ShaderCode[]>? Generate { get; init; }

        /// <summary>The one kind of shader it works in, if it doesn't work in both (the picture so far is only a post effect's).</summary>
        public ShaderPurpose? Only { get; init; }

        /// <summary>Whether it's where the graph ends: its Surface or Post Effect node.</summary>
        public bool IsOutput { get; init; }
    }

    /// <summary>
    /// The nodes a shader graph (a .shadergraph file) can have. A graph ends in one output: a Surface node, for a material
    /// (see <see cref="Material.Shader"/>), which says how its surface looks at each point (colour, roughness, glow,
    /// bumps); or a Post Effect node, for a camera (see <see cref="PostEffect"/>), which says what colour each pixel of
    /// the finished picture becomes. What's linked into it is worked out from the point being drawn (where it is, its
    /// texture coordinates, the time), maths, textures, noise and colours, and the graph's properties, which each
    /// material or effect sets for itself. The graph becomes GLSL (see <see cref="Shader"/>), the same as one written by hand.
    /// </summary>
    public static class ShaderNodes
    {
        public const string Kind = "shader";
        public const string Extension = ".shadergraph";

        /// <summary>
        /// A number, vector or colour, whichever is linked to it: maths nodes work on all of them, and give back the
        /// biggest of what they're given (a vector times a number is a vector). Unlinked, it's a number.
        /// </summary>
        public static readonly PortType Value = new("Value", typeof(float), new Color(0.5f, 0.78f, 0.7f), 0f);

        /// <summary>Two numbers: texture coordinates, mostly.</summary>
        public static readonly PortType Vector2 = new("Vector 2", typeof(System.Numerics.Vector2), new Color(0.72f, 0.85f, 0.38f), System.Numerics.Vector2.Zero);

        private sealed class ShaderLibrary() : NodeLibrary(ShaderNodes.Kind, "Shader Graph")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Output" => new Color(0.86f, 0.42f, 0.2f),
                "Input" => new Color(0.3f, 0.58f, 0.86f),
                "Properties" => new Color(0.7f, 0.45f, 0.85f),
                "Texture" => new Color(0.82f, 0.5f, 0.25f),
                "Noise" or "Shapes" => new Color(0.82f, 0.62f, 0.22f),
                "Color" => new Color(0.75f, 0.42f, 0.82f),
                "UV" => new Color(0.55f, 0.72f, 0.3f),
                "Logic" => new Color(0.82f, 0.36f, 0.4f),
                "Constant" => new Color(0.45f, 0.5f, 0.58f),
                _ => new Color(0.33f, 0.62f, 0.4f)
            };

            public override (string One, string Several, string Note) VariableWords => ("Property", "Properties", "set on each material or effect");

            public override IReadOnlyList<PortType> VariableTypes { get; } = [PortType.Number, ShaderNodes.Vector2, PortType.Vector, PortType.Color, PortType.Boolean, PortType.Texture];

            /// <summary>Numbers, vectors and colours all link to each other (GLSL turns one into another); textures only to textures.</summary>
            public override bool CanLink(PortType output, PortType input)
            {
                if (output == PortType.Texture || input == PortType.Texture) return output == input;
                if (output == PortType.Boolean || input == PortType.Boolean)
                    return output == input || output == PortType.Number || input == PortType.Number || output == Value || input == Value;
                return IsNumeric(output) && IsNumeric(input);
            }

            private static bool IsNumeric(PortType type) =>
                type == PortType.Number || type == Value || type == ShaderNodes.Vector2 || type == PortType.Vector || type == PortType.Color;
        }

        /// <summary>Every kind of node a shader graph can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        /// <summary>
        /// What a new surface shader graph starts as, to show how one's made: the material's colour times a Tint
        /// property, and a rim of light where it's seen edge-on (a Fresnel times a Rim property).
        /// </summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            graph.Variables.Add(new GraphVariable("Tint", PortType.Color, Color.White));
            graph.Variables.Add(new GraphVariable("Rim", PortType.Color, new Color(0.25f, 0.55f, 1f)));

            GraphNode material = graph.AddNode("Input/Material", new System.Numerics.Vector2(0f, 0f));
            GraphNode tint = graph.AddNode("Properties/Get Property", new System.Numerics.Vector2(0f, 180f));
            tint.Values["Variable"] = "Tint";
            GraphNode color = graph.AddNode("Math/Multiply", new System.Numerics.Vector2(260f, 60f));
            GraphNode fresnel = graph.AddNode("Color/Fresnel", new System.Numerics.Vector2(0f, 270f));
            GraphNode rim = graph.AddNode("Properties/Get Property", new System.Numerics.Vector2(0f, 380f));
            rim.Values["Variable"] = "Rim";
            GraphNode glow = graph.AddNode("Math/Multiply", new System.Numerics.Vector2(260f, 300f));
            GraphNode surface = graph.AddNode("Output/Surface", new System.Numerics.Vector2(520f, 80f));

            graph.Link(material.Id, "Color", color.Id, "A");
            graph.Link(tint.Id, "Value", color.Id, "B");
            graph.Link(color.Id, "Result", surface.Id, "Color");
            graph.Link(fresnel.Id, "Fresnel", glow.Id, "A");
            graph.Link(rim.Id, "Value", glow.Id, "B");
            graph.Link(glow.Id, "Result", surface.Id, "Emission");
            return graph;
        }

        /// <summary>What a new post effect graph starts as: the picture, with its brightness and saturation as properties.</summary>
        public static Graph PostEffectStarter()
        {
            var graph = new Graph(Kind);
            graph.Variables.Add(new GraphVariable("Brightness", PortType.Number, 1f));
            graph.Variables.Add(new GraphVariable("Saturation", PortType.Number, 1f));

            GraphNode picture = graph.AddNode("Input/Picture", new System.Numerics.Vector2(0f, 0f));
            GraphNode brightness = graph.AddNode("Properties/Get Property", new System.Numerics.Vector2(0f, 100f));
            brightness.Values["Variable"] = "Brightness";
            GraphNode saturation = graph.AddNode("Properties/Get Property", new System.Numerics.Vector2(0f, 180f));
            saturation.Values["Variable"] = "Saturation";
            GraphNode adjust = graph.AddNode("Color/Adjust Color", new System.Numerics.Vector2(300f, 20f));
            GraphNode output = graph.AddNode("Output/Post Effect", new System.Numerics.Vector2(620f, 40f));

            graph.Link(picture.Id, "Color", adjust.Id, "Color");
            graph.Link(saturation.Id, "Value", adjust.Id, "Saturation");
            graph.Link(brightness.Id, "Value", adjust.Id, "Brightness");
            graph.Link(adjust.Id, "Color", output.Id, "Color");
            return graph;
        }

        // ---------------------------------------------------------------- Making them

        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Unlinked(string name, PortType type, string whenUnlinked, string? tooltip = null) => new(name, type, Tooltip: tooltip) { WhenUnlinked = whenUnlinked };
        private static Port Setting(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Linkable: false, Tooltip: tooltip);
        private static Port Out(string name, PortType type) => new(name, type);
        private static Port UVIn => Unlinked("UV", Vector2, "Its own", "Where to look: unlinked, the point being drawn's own texture coordinates");

        private static ShaderCode F(string code) => new(code, ShaderType.Float);
        private static ShaderCode V2(string code) => new(code, ShaderType.Vector2);
        private static ShaderCode V3(string code) => new(code, ShaderType.Vector3);
        private static ShaderCode V4(string code) => new(code, ShaderType.Vector4);
        private static ShaderCode B(string code) => new(code, ShaderType.Bool);

        private static NodeLibrary Build()
        {
            var library = new ShaderLibrary();
            NodeLibrary.Register(library);

            void Node(string category, string title, Port[] inputs, Port[] outputs, Func<ShaderNodeContext, ShaderCode[]> generate, string description,
                string keywords = "", ShaderPurpose? only = null) =>
                library.Add(new ShaderNode($"{category}/{title}", title, category, inputs, outputs)
                {
                    Generate = generate,
                    Description = description,
                    Keywords = keywords,
                    Only = only
                });

            // One output.
            void One(string category, string title, Port[] inputs, Port output, Func<ShaderNodeContext, ShaderCode> generate, string description,
                string keywords = "", ShaderPurpose? only = null) =>
                Node(category, title, inputs, [output], c => [generate(c)], description, keywords, only);

            // Maths on any mix of numbers, vectors and colours: each input made the biggest of their types first.
            void Math(string title, Port[] inputs, Func<string[], string> code, string description, string keywords = "") =>
                One("Math", title, inputs, Out("Result", Value), c =>
                {
                    var codes = new string[inputs.Length];
                    for (int i = 0; i < inputs.Length; i++) codes[i] = c.Dynamic(i).Code;
                    return new ShaderCode(code(codes), c.DynamicType);
                }, description, keywords);

            void Math1(string title, string function, string description, string keywords = "") =>
                Math(title, [In("Value", Value)], v => $"{function}({v[0]})", description, keywords);

            void Math2(string title, Func<string, string, string> code, string description, float b, string keywords = "") =>
                Math(title, [In("A", Value), In("B", Value, b)], v => code(v[0], v[1]), description, keywords);

            // ---------------------------------------------------------------- Outputs

            library.Add(new ShaderNode("Output/Surface", "Surface", "Output",
            [
                Unlinked("Color", PortType.Color, "Material's", "The surface's colour (its alpha is ignored: link Alpha for that)"),
                Unlinked("Alpha", PortType.Number, "Material's", "How solid it is, 0 to 1: with Alpha Clip, what's below the clip isn't drawn"),
                Unlinked("Alpha Clip", PortType.Number, "None", "Where Alpha is below this, nothing's drawn: leaves, fences, holes (0.5 is usual)"),
                Unlinked("Normal", PortType.Vector, "Material's", "Bumps: a direction off the surface (z straight out), as a normal map has them. Link Sample Normal Map"),
                Unlinked("Roughness", PortType.Number, "Material's", "From 0 (polished, sharp reflections) to 1 (chalky)"),
                Unlinked("Metallic", PortType.Number, "Material's", "1 for metal, 0 for anything else"),
                Unlinked("Specular", PortType.Number, "Material's", "How much a non-metal reflects: 0.5 is right for nearly everything"),
                Unlinked("Emission", PortType.Color, "Material's", "Light it gives off itself: above 1, it glows")
            ], [])
            {
                Generate = c =>
                {
                    if (c.IsLinked(0)) c.Line($"s.color = {c.As(0, ShaderType.Vector3)};");
                    if (c.IsLinked(1)) c.Line($"s.alpha = {c.As(1, ShaderType.Float)};");
                    if (c.IsLinked(2)) c.Line($"s.alphaClip = {c.As(2, ShaderType.Float)};");
                    if (c.IsLinked(3)) c.Line($"s.normal = surfaceToWorld(normalize({c.As(3, ShaderType.Vector3)}));");
                    if (c.IsLinked(4)) c.Line($"s.roughness = {c.As(4, ShaderType.Float)};");
                    if (c.IsLinked(5)) c.Line($"s.metallic = {c.As(5, ShaderType.Float)};");
                    if (c.IsLinked(6)) c.Line($"s.specular = {c.As(6, ShaderType.Float)};");
                    if (c.IsLinked(7)) c.Line($"s.emission = {c.As(7, ShaderType.Vector3)};");
                    return [];
                },
                Description = "Where a material's graph ends: how its surface looks at each point. What's left unlinked keeps the material's own setting.",
                Keywords = "output material master fragment albedo",
                Only = ShaderPurpose.Surface,
                IsOutput = true
            });

            library.Add(new ShaderNode("Output/Post Effect", "Post Effect", "Output",
            [
                Unlinked("Color", PortType.Color, "The picture", "The colour this pixel of the finished picture becomes, in screen colours from 0 to 1")
            ], [])
            {
                Generate = c =>
                {
                    c.Line(c.IsLinked(0) ? $"return {c.As(0, ShaderType.Vector4)};" : "return picture(uv);");
                    return [];
                },
                Description = "Where a post effect's graph ends: what colour each pixel of the camera's finished picture becomes.",
                Keywords = "output camera screen image filter",
                Only = ShaderPurpose.PostEffect,
                IsOutput = true
            });

            // ---------------------------------------------------------------- Inputs

            One("Input", "UV", [], Out("UV", Vector2), _ => V2("uv"),
                "The point's texture coordinates: across the mesh's texture in a surface, across the picture (from its top-left) in a post effect.",
                "texture coordinates texcoord");

            Node("Input", "Time", [], [Out("Time", PortType.Number), Out("Sine Time", PortType.Number), Out("Cosine Time", PortType.Number)],
                _ => [F("time"), F("sin(time)"), F("cos(time)")],
                "Seconds since the game started, for things that move: scrolling, pulsing, flowing.", "seconds clock animate");

            One("Input", "Screen Position", [], Out("Position", Vector2), c => V2(c.Target == ShaderPurpose.Surface ? "screenPosition" : "uv"),
                "Where the point is on the screen, 0 to 1 from its top-left corner.", "screen space viewport");

            One("Input", "Screen Size", [], Out("Size", Vector2), _ => V2("screenSize"),
                "How big the picture is, in pixels.", "resolution pixels", ShaderPurpose.PostEffect);

            Node("Input", "Position", [], [Out("World", PortType.Vector), Out("Object", PortType.Vector), Out("From Object", PortType.Vector)],
                _ => [V3("worldPosition"), V3("objectPosition"), V3("(worldPosition - objectPosition)")],
                "Where the point is in the world; where the object's origin is; and the point from the origin.", "world local object", ShaderPurpose.Surface);

            One("Input", "Normal", [], Out("Normal", PortType.Vector), _ => V3("worldNormal"),
                "Which way the mesh's surface faces here, in the world, before any bumps.", "world normal direction facing", ShaderPurpose.Surface);

            One("Input", "View Direction", [], Out("Direction", PortType.Vector), _ => V3("viewDirection"),
                "The way from the point to the camera.", "camera eye vector", ShaderPurpose.Surface);

            Node("Input", "Camera", [], [Out("Position", PortType.Vector), Out("Distance", PortType.Number)],
                _ => [V3("frame.cameraPosition.xyz"), F("length(frame.cameraPosition.xyz - worldPosition)")],
                "Where the camera is, and how far the point is from it: fade things out up close.", "eye distance", ShaderPurpose.Surface);

            Node("Input", "Material", [],
            [
                Out("Color", PortType.Color), Out("Roughness", PortType.Number), Out("Metallic", PortType.Number), Out("Specular", PortType.Number),
                Out("Emission", PortType.Color)
            ], _ =>
            [
                V4("vec4(materialSurface.color, materialSurface.alpha)"), F("materialSurface.roughness"), F("materialSurface.metallic"),
                F("materialSurface.specular"), V4("vec4(materialSurface.emission, 1.0)")
            ], "The material's own settings: its colour (times its texture), roughness and so on, to build on.", "albedo base settings", ShaderPurpose.Surface);

            One("Input", "Picture", [UVIn], Out("Color", PortType.Color), c => V4($"picture({c.UV(0)})"),
                "The camera's picture so far, in screen colours from 0 to 1: at the pixel's own place, or wherever's linked.", "screen color source image",
                ShaderPurpose.PostEffect);

            One("Input", "Distance", [UVIn], Out("Distance", PortType.Number), c => F($"distanceAt({c.UV(0)})"),
                "How far from the camera what's in the picture is, in metres along the view (very far for the sky).", "depth fog z", ShaderPurpose.PostEffect);

            // ---------------------------------------------------------------- Constants and properties

            One("Constant", "Number", [Setting("Value", PortType.Number, 1f)], Out("Value", PortType.Number),
                c => F(ShaderNodeContext.Number(c.Setting<float>("Value"))), "A number.", "float constant value");

            One("Constant", "Vector 2", [Setting("Value", Vector2, System.Numerics.Vector2.One)], Out("Value", Vector2), c =>
            {
                System.Numerics.Vector2 v = c.Setting<System.Numerics.Vector2>("Value");
                return V2($"vec2({ShaderNodeContext.Number(v.X)}, {ShaderNodeContext.Number(v.Y)})");
            }, "Two numbers.", "vec2 constant");

            One("Constant", "Vector 3", [Setting("Value", PortType.Vector, Vector3.One)], Out("Value", PortType.Vector), c =>
            {
                Vector3 v = c.Setting<Vector3>("Value");
                return V3($"vec3({ShaderNodeContext.Number(v.X)}, {ShaderNodeContext.Number(v.Y)}, {ShaderNodeContext.Number(v.Z)})");
            }, "Three numbers: a direction or a place.", "vec3 vector constant");

            One("Constant", "Color", [Setting("Color", PortType.Color, Color.White)], Out("Color", PortType.Color),
                c => V4(c.Color(c.Setting<Color>("Color"))), "A colour, and how see-through.", "rgba constant colour");

            One("Properties", "Get Property", [new Port("Variable", PortType.Text, "", Linkable: false) { IsVariableName = true }],
                new Port("Value", PortType.Any) { TypeFromVariable = true }, c =>
                {
                    string chosen = c.Setting<string>("Variable");
                    if (c.Property(chosen) is { } property) return property;
                    c.Fail(chosen.Length == 0 ? "Choose one of the graph's properties" : $"There's no property {chosen}");
                    return F("0.0");
                }, "One of the graph's properties: each material or effect using the graph sets its own.", "variable exposed parameter uniform");

            // ---------------------------------------------------------------- Maths

            Math2("Add", (a, b) => $"({a} + {b})", "A plus B.", 0f, "plus sum +");
            Math2("Subtract", (a, b) => $"({a} - {b})", "A take away B.", 0f, "minus difference -");
            Math2("Multiply", (a, b) => $"({a} * {b})", "A times B: a colour times another tints it.", 1f, "times product scale *");
            Math2("Divide", (a, b) => $"({a} / {b})", "A divided by B.", 1f, "over /");
            Math2("Power", (a, b) => $"pow({a}, {b})", "A to the power of B.", 2f, "exponent pow");
            Math2("Minimum", (a, b) => $"min({a}, {b})", "The smaller of A and B.", 0f, "min smallest");
            Math2("Maximum", (a, b) => $"max({a}, {b})", "The bigger of A and B.", 0f, "max largest");
            Math2("Modulo", (a, b) => $"mod({a}, {b})", "What's left of A after taking away as many B as fit: counts up to B and starts again.", 1f, "mod remainder repeat wrap");
            Math2("Step", (a, b) => $"step({a}, {b})", "0 where B is below A (the edge), 1 where it isn't: a hard edge.", 0.5f, "threshold edge");

            Math1("Absolute", "abs", "The value without its minus sign.", "abs");
            Math1("Floor", "floor", "Rounded down to a whole number.", "round down");
            Math1("Ceiling", "ceil", "Rounded up to a whole number.", "ceil round up");
            Math1("Round", "round", "Rounded to the nearest whole number.", "nearest");
            Math1("Fraction", "fract", "Only what's after the decimal point: counts 0 to 1 and starts again.", "fract frac repeat");
            Math1("Sign", "sign", "-1 if it's below 0, 1 above, 0 for 0.", "");
            Math1("Square Root", "sqrt", "The square root.", "sqrt");
            Math1("Sine", "sin", "Goes smoothly up and down between -1 and 1: sin of the value in radians.", "sin wave");
            Math1("Cosine", "cos", "Like Sine, a quarter of a turn on.", "cos wave");
            Math1("Exponential", "exp", "e to the power of the value.", "exp");
            Math1("Logarithm", "log", "The natural logarithm: what e is raised to for the value.", "log ln");
            Math("Negate", [In("Value", Value)], v => $"(-{v[0]})", "The value with its sign turned round.", "minus invert");
            Math("One Minus", [In("Value", Value)], v => $"(1.0 - {v[0]})", "1 take away the value: turns 0 to 1 round, and a mask inside out.", "invert reverse");
            Math("Saturate", [In("Value", Value)], v => $"clamp({v[0]}, 0.0, 1.0)", "Kept between 0 and 1.", "clamp01");
            Math("Lerp", [In("A", Value), In("B", Value, 1f), In("T", Value, 0.5f)], v => $"mix({v[0]}, {v[1]}, {v[2]})",
                "From A at T = 0 to B at T = 1: blends two colours or values by how much T is.", "mix blend interpolate");
            Math("Clamp", [In("Value", Value), In("Min", Value, 0f), In("Max", Value, 1f)], v => $"clamp({v[0]}, {v[1]}, {v[2]})",
                "The value, kept between Min and Max.", "limit");
            Math("Smoothstep", [In("Edge 1", Value, 0f), In("Edge 2", Value, 1f), In("Value", Value)], v => $"smoothstep({v[0]}, {v[1]}, {v[2]})",
                "0 below Edge 1, 1 above Edge 2, and a smooth curve between: a soft edge.", "smooth threshold fade");
            Math("Remap", [In("Value", Value), In("From Min", Value, 0f), In("From Max", Value, 1f), In("To Min", Value, 0f), In("To Max", Value, 1f)],
                v => $"({v[3]} + ({v[0]} - {v[1]}) * ({v[4]} - {v[3]}) / ({v[2]} - {v[1]}))",
                "The value from one range into another: -1 to 1 into 0 to 1, say.", "range map scale");

            // ---------------------------------------------------------------- Vectors

            One("Vector", "Dot Product", [In("A", Value), In("B", Value)], Out("Result", PortType.Number), c => F($"dot({c.Dynamic(0)}, {c.Dynamic(1)})"),
                "How much two directions point the same way: 1 together, 0 at right angles, -1 opposite (for unit lengths).", "dot");
            One("Vector", "Cross Product", [In("A", PortType.Vector), In("B", PortType.Vector)], Out("Result", PortType.Vector),
                c => V3($"cross({c.Input(0)}, {c.Input(1)})"), "The direction at right angles to both.", "cross perpendicular");
            One("Vector", "Length", [In("Value", Value)], Out("Length", PortType.Number), c => F($"length({c.Dynamic(0)})"), "How long a vector is.", "magnitude size");
            One("Vector", "Distance", [In("A", Value), In("B", Value)], Out("Distance", PortType.Number), c => F($"distance({c.Dynamic(0)}, {c.Dynamic(1)})"),
                "How far apart two points are.", "between");
            Math("Normalize", [In("Value", Value)], v => $"normalize({v[0]})", "The same direction, 1 long.", "unit direction");
            One("Vector", "Reflect", [In("Direction", PortType.Vector), In("Normal", PortType.Vector, Vector3.UnitY)], Out("Reflected", PortType.Vector),
                c => V3($"reflect({c.Input(0)}, normalize({c.Input(1)}))"), "A direction bounced off a surface facing Normal.", "bounce mirror");
            Node("Vector", "Split", [In("Value", Value)], [Out("X", PortType.Number), Out("Y", PortType.Number), Out("Z", PortType.Number), Out("W", PortType.Number)], c =>
            {
                string v = c.Local(ShaderType.Vector4, ShaderGraphCompiler.Cast(c.Input(0), ShaderType.Vector4).Code);
                return [F($"{v}.x"), F($"{v}.y"), F($"{v}.z"), F($"{v}.w")];
            }, "A vector or colour's parts: X, Y, Z and W are red, green, blue and alpha for a colour.", "separate components channels rgba xyzw");
            Node("Vector", "Combine", [In("X", PortType.Number), In("Y", PortType.Number), In("Z", PortType.Number), In("W", PortType.Number, 1f)],
                [Out("XY", Vector2), Out("XYZ", PortType.Vector), Out("Color", PortType.Color)], c =>
                [
                    V2($"vec2({c.Input(0)}, {c.Input(1)})"), V3($"vec3({c.Input(0)}, {c.Input(1)}, {c.Input(2)})"),
                    V4($"vec4({c.Input(0)}, {c.Input(1)}, {c.Input(2)}, {c.Input(3)})")
                ], "Numbers put together into a vector or a colour (X, Y, Z, W are red, green, blue, alpha).", "merge append make rgba");

            // ---------------------------------------------------------------- Texture coordinates

            One("UV", "Tiling And Offset", [UVIn, In("Tiling", Vector2, System.Numerics.Vector2.One), In("Offset", Vector2, System.Numerics.Vector2.Zero)], Out("UV", Vector2),
                c => V2($"({c.UV(0)} * {c.Input(1)} + {c.Input(2)})"),
                "Repeats a texture more (Tiling) and slides it (Offset): link Time into Offset to scroll it.", "scale scroll repeat tile pan");
            One("UV", "Rotate", [UVIn, In("Center", Vector2, new System.Numerics.Vector2(0.5f)), In("Degrees", PortType.Number, 0f)], Out("UV", Vector2), c =>
            {
                c.Function("sgRotate", """
                    vec2 sgRotate(vec2 p, vec2 centre, float degrees)
                    {
                        float a = radians(degrees);
                        float c = cos(a), s = sin(a);
                        p -= centre;
                        return vec2(c * p.x - s * p.y, s * p.x + c * p.y) + centre;
                    }
                    """);
                return V2($"sgRotate({c.UV(0)}, {c.Input(1)}, {c.Input(2)})");
            }, "Turns texture coordinates round a centre.", "spin turn");
            One("UV", "Polar Coordinates", [UVIn, In("Center", Vector2, new System.Numerics.Vector2(0.5f))], Out("UV", Vector2), c =>
                {
                    c.Function("sgPolar", """
                        vec2 sgPolar(vec2 p, vec2 centre)
                        {
                            vec2 d = p - centre;
                            return vec2(length(d) * 2.0, atan(d.y, d.x) / 6.2831853 + 0.5);
                        }
                        """);
                    return V2($"sgPolar({c.UV(0)}, {c.Input(1)})");
                },
                "Texture coordinates as how far from the centre (X) and how far round it (Y, 0 to 1): for rings, spirals and portals.", "radial circular angle");
            One("UV", "Pixelate", [UVIn, In("Pixels", Vector2, new System.Numerics.Vector2(64f))], Out("UV", Vector2),
                c => V2($"((floor({c.UV(0)} * {c.Input(1)}) + 0.5) / {c.Input(1)})"),
                "Snaps texture coordinates to a grid of that many squares across and down: blocky, like old games.", "retro blocky mosaic grid");

            // ---------------------------------------------------------------- Textures

            Node("Texture", "Sample Texture",
            [
                In("Texture", PortType.Texture, null, "The texture: chosen here, or a texture property linked in"),
                UVIn,
                Setting("Holds", PortType.Enum<TextureContents>(), TextureContents.Colors, "Colors are made linear in a surface, as light is; data is used as it's stored")
            ], [Out("Color", PortType.Color), Out("Alpha", PortType.Number)], c =>
            {
                ShaderCode texture = c.Texture(0);
                bool colors = c.Setting<TextureContents>("Holds") == TextureContents.Colors && c.Target == ShaderPurpose.Surface;
                string sample = c.Target == ShaderPurpose.Surface ? (colors ? "sampleColor" : "sampleData") : "texture";
                string texel = c.Local(ShaderType.Vector4, $"{sample}({texture}, {c.UV(1)})");
                return [V4(texel), F($"{texel}.a")];
            }, "A texture's colour at a point.", "texture2d image map read lookup");

            One("Texture", "Sample Normal Map",
            [
                In("Texture", PortType.Texture, null, "The normal map: chosen here, or a texture property linked in"),
                UVIn,
                In("Strength", PortType.Number, 1f, "How deep the bumps look: 1 as made, 0 flat")
            ], Out("Normal", PortType.Vector), c =>
            {
                ShaderCode texture = c.Texture(0, TextureFallback.Normal);
                return V3($"sampleNormal({texture}, {c.UV(1)}, {c.Input(2)})");
            }, "A normal map's bumps at a point: link it to the Surface's Normal.", "bump normalmap detail", ShaderPurpose.Surface);

            // ---------------------------------------------------------------- Noise and shapes

            const string hash = """
                float sgHash(vec2 p)
                {
                    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
                    p3 += dot(p3, p3.yzx + 33.33);
                    return fract((p3.x + p3.y) * p3.z);
                }

                vec2 sgHash2(vec2 p)
                {
                    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
                    p3 += dot(p3, p3.yzx + 33.33);
                    return fract((p3.xx + p3.yz) * p3.zy);
                }
                """;

            const string noise = """
                float sgValueNoise(vec2 p)
                {
                    vec2 i = floor(p);
                    vec2 f = fract(p);
                    vec2 u = f * f * (3.0 - 2.0 * f);
                    return mix(mix(sgHash(i), sgHash(i + vec2(1.0, 0.0)), u.x), mix(sgHash(i + vec2(0.0, 1.0)), sgHash(i + vec2(1.0, 1.0)), u.x), u.y);
                }

                float sgGradientCorner(vec2 i, vec2 f)
                {
                    float angle = sgHash(i) * 6.2831853;
                    return dot(vec2(cos(angle), sin(angle)), f);
                }

                float sgGradientNoise(vec2 p)
                {
                    vec2 i = floor(p);
                    vec2 f = fract(p);
                    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
                    float a = sgGradientCorner(i, f);
                    float b = sgGradientCorner(i + vec2(1.0, 0.0), f - vec2(1.0, 0.0));
                    float c = sgGradientCorner(i + vec2(0.0, 1.0), f - vec2(0.0, 1.0));
                    float d = sgGradientCorner(i + vec2(1.0, 1.0), f - vec2(1.0, 1.0));
                    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y) * 0.7071 + 0.5;
                }

                // Layers of noise, each half the size and half as strong as the one before: detail on detail.
                float sgNoise(vec2 p, int octaves, bool gradient)
                {
                    float sum = 0.0;
                    float strength = 1.0;
                    float total = 0.0;
                    for (int i = 0; i < octaves; i++)
                    {
                        sum += (gradient ? sgGradientNoise(p) : sgValueNoise(p)) * strength;
                        total += strength;
                        strength *= 0.5;
                        p = p * 2.0 + vec2(17.1, 31.7);
                    }
                    return sum / total;
                }
                """;

            One("Noise", "Noise",
            [
                UVIn,
                In("Scale", PortType.Number, 10f, "How many bumps across: bigger is finer"),
                Setting("Kind", PortType.Enum<NoiseKind>(), NoiseKind.Gradient),
                Setting("Octaves", PortType.Number, 1f, "Layers of finer and finer detail: 1 is smooth, 4 or 5 is rough and cloudy")
            ], Out("Noise", PortType.Number), c =>
            {
                c.Function("sgHash", hash);
                c.Function("sgNoise", noise);
                int octaves = System.Math.Clamp((int)MathF.Round(c.Setting<float>("Octaves")), 1, 8);
                bool gradient = c.Setting<NoiseKind>("Kind") == NoiseKind.Gradient;
                return F($"sgNoise({c.UV(0)} * {c.Input(1)}, {octaves}, {(gradient ? "true" : "false")})");
            }, "Smooth random hills and valleys, 0 to 1: clouds, lava, dirt, wobble.", "perlin simplex fractal fbm random cloud");

            Node("Noise", "Voronoi", [UVIn, In("Scale", PortType.Number, 5f, "How many cells across"), In("Randomness", PortType.Number, 1f, "0 lines the cells up in a grid; 1 scatters them")],
                [Out("Distance", PortType.Number), Out("Cell", PortType.Number)], c =>
                {
                    c.Function("sgHash", hash);
                    c.Function("sgVoronoi", """
                        vec2 sgVoronoi(vec2 p, float randomness)
                        {
                            vec2 cell = floor(p);
                            vec2 f = fract(p);
                            float best = 8.0;
                            float id = 0.0;
                            for (int y = -1; y <= 1; y++)
                            {
                                for (int x = -1; x <= 1; x++)
                                {
                                    vec2 offset = vec2(float(x), float(y));
                                    vec2 point = offset + mix(vec2(0.5), sgHash2(cell + offset), randomness) - f;
                                    float d = dot(point, point);
                                    if (d < best)
                                    {
                                        best = d;
                                        id = sgHash(cell + offset + 0.5);
                                    }
                                }
                            }
                            return vec2(sqrt(best), id);
                        }
                        """);
                    string cells = c.Local(ShaderType.Vector2, $"sgVoronoi({c.UV(0)} * {c.Input(1)}, {c.Input(2)})");
                    return [F($"{cells}.x"), F($"{cells}.y")];
                }, "Cells, like cracked mud or scales: how far each point is from its cell's middle, and a random number for each cell.",
                "cells worley cellular crackle");

            One("Noise", "Random", [In("Seed", Value)], Out("Random", PortType.Number), c =>
            {
                c.Function("sgHash", hash);
                return F($"sgHash({ShaderGraphCompiler.Cast(c.Input(0), ShaderType.Vector2)} + 0.37)");
            }, "A number from 0 to 1 that looks random, the same for the same seed: link UV for static, or Floor of it for blocks.", "hash white noise static");

            One("Shapes", "Checkerboard",
            [
                UVIn, In("Color A", PortType.Color, new Color(0.2f, 0.2f, 0.2f)), In("Color B", PortType.Color, new Color(0.8f, 0.8f, 0.8f)),
                In("Frequency", Vector2, new System.Numerics.Vector2(4f))
            ], Out("Color", PortType.Color), c =>
                V4($"mix({c.Input(1)}, {c.Input(2)}, mod(floor({c.UV(0)}.x * {c.Input(3)}.x) + floor({c.UV(0)}.y * {c.Input(3)}.y), 2.0))"),
                "Squares of two colours, Frequency of them across and down.", "checker grid tiles chess");
            One("Shapes", "Ellipse", [UVIn, In("Width", PortType.Number, 0.5f), In("Height", PortType.Number, 0.5f)], Out("Inside", PortType.Number), c =>
                {
                    c.Function("sgEllipse", """
                        float sgEllipse(vec2 p, vec2 size)
                        {
                            float d = length((p * 2.0 - 1.0) / max(size, vec2(1e-4)));
                            return clamp((1.0 - d) / max(fwidth(d), 1e-5), 0.0, 1.0);
                        }
                        """);
                    return F($"sgEllipse({c.UV(0)}, vec2({c.Input(1)}, {c.Input(2)}))");
                }, "1 inside an ellipse in the middle (Width and Height across, from 0 to 1), 0 outside, with a smooth edge.", "circle round disc oval");
            One("Shapes", "Rectangle", [UVIn, In("Width", PortType.Number, 0.5f), In("Height", PortType.Number, 0.5f)], Out("Inside", PortType.Number), c =>
                {
                    c.Function("sgRectangle", """
                        float sgRectangle(vec2 p, vec2 size)
                        {
                            vec2 d = abs(p * 2.0 - 1.0) - size;
                            d = 1.0 - d / max(fwidth(d), vec2(1e-5));
                            return clamp(min(d.x, d.y), 0.0, 1.0);
                        }
                        """);
                    return F($"sgRectangle({c.UV(0)}, vec2({c.Input(1)}, {c.Input(2)}))");
                }, "1 inside a rectangle in the middle (Width and Height across, from 0 to 1), 0 outside.", "square box");

            // ---------------------------------------------------------------- Colours

            One("Color", "Luminance", [In("Color", PortType.Color)], Out("Luminance", PortType.Number), c => F($"dot({c.Input(0)}.rgb, vec3(0.2126, 0.7152, 0.0722))"),
                "How bright a colour looks, as grey.", "grayscale greyscale brightness lightness");
            One("Color", "Adjust Color",
            [
                In("Color", PortType.Color), In("Hue", PortType.Number, 0f, "Degrees round the colour wheel to turn it"),
                In("Saturation", PortType.Number, 1f, "0 grey, 1 as it is, more for stronger colour"), In("Brightness", PortType.Number, 1f),
                In("Contrast", PortType.Number, 1f, "1 as it is, more to push dark and light apart")
            ], Out("Color", PortType.Color), c =>
            {
                c.Function("sgHsv", """
                    vec3 sgRgbToHsv(vec3 c)
                    {
                        vec4 k = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                        vec4 p = mix(vec4(c.bg, k.wz), vec4(c.gb, k.xy), step(c.b, c.g));
                        vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));
                        float d = q.x - min(q.w, q.y);
                        return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
                    }

                    vec3 sgHsvToRgb(vec3 c)
                    {
                        vec3 p = abs(fract(c.xxx + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
                        return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
                    }

                    vec4 sgAdjust(vec4 color, float hue, float saturation, float brightness, float contrast, float middle)
                    {
                        vec3 hsv = sgRgbToHsv(max(color.rgb, vec3(0.0)));
                        hsv.x = fract(hsv.x + hue / 360.0);
                        hsv.y = clamp(hsv.y * saturation, 0.0, 1.0);
                        vec3 rgb = sgHsvToRgb(hsv) * brightness;
                        return vec4(max((rgb - middle) * contrast + middle, vec3(0.0)), color.a);
                    }
                    """);
                // Contrast pivots round middle grey: 0.18 in linear light, 0.5 on screen.
                string middle = c.Target == ShaderPurpose.Surface ? "0.18" : "0.5";
                return V4($"sgAdjust({c.Input(0)}, {c.Input(1)}, {c.Input(2)}, {c.Input(3)}, {c.Input(4)}, {middle})");
            }, "Turns a colour's hue, and changes its saturation, brightness and contrast.", "hue saturation value hsv tint grade");
            One("Color", "Blend",
            [
                In("Base", PortType.Color), In("Blend", PortType.Color, Color.White), In("Opacity", PortType.Number, 1f),
                Setting("Mode", PortType.Enum<ColorBlend>(), ColorBlend.Multiply)
            ], Out("Color", PortType.Color), c =>
            {
                string a = c.Input(0).Code, b = c.Input(1).Code;
                string blended = c.Setting<ColorBlend>("Mode") switch
                {
                    ColorBlend.Screen => $"(1.0 - (1.0 - {a}) * (1.0 - {b}))",
                    ColorBlend.Overlay => $"mix(2.0 * {a} * {b}, 1.0 - 2.0 * (1.0 - {a}) * (1.0 - {b}), step(0.5, {a}))",
                    ColorBlend.Add => $"({a} + {b})",
                    ColorBlend.Subtract => $"max({a} - {b}, vec4(0.0))",
                    ColorBlend.Lighten => $"max({a}, {b})",
                    ColorBlend.Darken => $"min({a}, {b})",
                    ColorBlend.Difference => $"abs({a} - {b})",
                    _ => $"({a} * {b})"
                };
                return V4($"vec4(mix({a}, {blended}, {c.Input(2)}).rgb, {a}.a)");
            }, "Lays one colour over another the way image editors do: multiply, screen, overlay...", "layer mix multiply screen overlay");
            Math("Posterize", [In("Value", Value), In("Steps", Value, 4f)], v => $"(floor({v[0]} * {v[1]}) / {v[1]})",
                "Cuts smooth shades into that many flat steps.", "bands quantize steps retro");
            One("Color", "Fresnel", [Unlinked("Normal", PortType.Vector, "The mesh's"), In("Power", PortType.Number, 5f, "Bigger keeps it to the very edges")],
                Out("Fresnel", PortType.Number), c =>
                {
                    string normal = c.IsLinked(0) ? $"normalize({c.Input(0)})" : "worldNormal";
                    return F($"pow(1.0 - clamp(dot({normal}, viewDirection), 0.0, 1.0), {c.Input(1)})");
                }, "1 where the surface is seen edge-on, 0 where it faces the camera: rims of light, force fields, ghosts.", "rim edge glow", ShaderPurpose.Surface);

            // ---------------------------------------------------------------- Logic

            One("Logic", "Compare", [In("A", PortType.Number), In("B", PortType.Number), Setting("Is", PortType.Enum<CompareOperation>(), CompareOperation.Greater)],
                Out("Result", PortType.Boolean), c =>
                {
                    string a = c.Input(0).Code, b = c.Input(1).Code;
                    return B(c.Setting<CompareOperation>("Is") switch
                    {
                        CompareOperation.Less => $"({a} < {b})",
                        CompareOperation.Equal => $"(abs({a} - {b}) < 1e-4)",
                        CompareOperation.NotEqual => $"(abs({a} - {b}) >= 1e-4)",
                        CompareOperation.GreaterOrEqual => $"({a} >= {b})",
                        CompareOperation.LessOrEqual => $"({a} <= {b})",
                        _ => $"({a} > {b})"
                    });
                }, "Whether A is more than B (or less, or the same...).", "greater less equal if");
            One("Logic", "Branch", [In("Condition", PortType.Boolean), In("True", Value, 1f), In("False", Value, 0f)], Out("Result", Value), c =>
                new ShaderCode($"({c.Input(0)} ? {c.Dynamic(1)} : {c.Dynamic(2)})", c.DynamicType), "True if the condition is, False if it isn't.", "if select switch");
            One("Logic", "And", [In("A", PortType.Boolean), In("B", PortType.Boolean)], Out("Result", PortType.Boolean), c => B($"({c.Input(0)} && {c.Input(1)})"),
                "Whether both are true.", "&& both");
            One("Logic", "Or", [In("A", PortType.Boolean), In("B", PortType.Boolean)], Out("Result", PortType.Boolean), c => B($"({c.Input(0)} || {c.Input(1)})"),
                "Whether either is true.", "|| either");
            One("Logic", "Not", [In("Value", PortType.Boolean)], Out("Result", PortType.Boolean), c => B($"(!{c.Input(0)})"),
                "True if it isn't, false if it is.", "! invert");

            // ---------------------------------------------------------------- GLSL of one's own

            One("Custom", "Expression",
            [
                In("A", Value), In("B", Value), In("C", Value), In("D", Value),
                Setting("Code", PortType.Text, "a * b", "GLSL working out the result from a, b, c and d: sin(a * 3.0) * b"),
                Setting("Result", PortType.Enum<ExpressionResult>(), ExpressionResult.Number)
            ], Out("Result", Value), c =>
            {
                ShaderType result = c.Setting<ExpressionResult>("Result") switch
                {
                    ExpressionResult.Vector2 => ShaderType.Vector2,
                    ExpressionResult.Vector3 => ShaderType.Vector3,
                    ExpressionResult.Color => ShaderType.Vector4,
                    _ => ShaderType.Float
                };
                string function = $"sgExpression{c.Node.Id}";
                string parameters = string.Join(", ", "abcd".Select((letter, i) => $"{ShaderGraphCompiler.GlslType(c.Input(i).Type)} {letter}"));
                string type = ShaderGraphCompiler.GlslType(result);
                c.Function(function, $"{type} {function}({parameters})\n{{\n    return {type}({c.Setting<string>("Code")});\n}}");
                return new ShaderCode($"{function}({c.Input(0)}, {c.Input(1)}, {c.Input(2)}, {c.Input(3)})", result);
            }, "A line of GLSL of your own, using the inputs as a, b, c and d.", "code glsl custom function formula");

            return library;
        }
    }
}
