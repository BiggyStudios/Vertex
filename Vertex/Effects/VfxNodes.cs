using System.Numerics;

using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.Effects
{
    /// <summary>
    /// The nodes a VFX graph can have. A graph is one or more particle systems, each a chain of steps linked by their
    /// Particles ports: Spawn (how many, when) → Initialize (what each starts as) → Update (what happens to it every
    /// frame, optional) → Output (how it's drawn). Values for their inputs are worked out for each particle, from value
    /// nodes: its age, random points in shapes, maths, colours, noise.
    /// </summary>
    public static class VfxNodes
    {
        public const string Kind = "vfx";

        /// <summary>What links one step of a particle system to the next.</summary>
        public static readonly PortType Particles = new("Particles", null, new Color(0.95f, 0.93f, 0.85f));

        private sealed class VfxLibrary() : NodeLibrary(VfxNodes.Kind, "VFX Graph")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Systems" => new Color(0.86f, 0.42f, 0.2f),
                "Particle" => new Color(0.3f, 0.58f, 0.86f),
                "Properties" => new Color(0.7f, 0.45f, 0.85f),
                "Shapes" or "Random" or "Noise" => new Color(0.82f, 0.62f, 0.22f),
                "Colors" => new Color(0.75f, 0.42f, 0.82f),
                _ => new Color(0.33f, 0.62f, 0.4f)
            };

            public override (string One, string Several, string Note) VariableWords => ("Property", "Properties", "the game can set them on each object");
        }

        /// <summary>Every kind of node a VFX graph can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        // ---------------------------------------------------------------- Making them

        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Setting(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Linkable: false, Tooltip: tooltip);
        private static Port Out(string name, PortType type) => new(name, type);
        private static Port ParticlesIn => new("Particles", Particles);
        private static Port ParticlesOut => new("Particles", Particles);

        private static Vector4 N(float x) => new(x, 0f, 0f, 0f);
        private static Vector4 V(Vector3 v) => new(v, 0f);
        private static Vector3 V3(Vector4 v) => new(v.X, v.Y, v.Z);

        /// <summary>A unit direction from two random numbers, evenly over a sphere.</summary>
        private static Vector3 RandomDirection() =>
            ParticleEmitter.SpreadDirection(Vector3.UnitY, 180f, System.Random.Shared.NextSingle(), System.Random.Shared.NextSingle());

        private static NodeLibrary Build()
        {
            var library = new VfxLibrary();
            NodeLibrary.Register(library);

            void Step(VfxRole role, string title, Port[] inputs, Port[] outputs, string description, string keywords = "") =>
                library.Add(new VfxNode($"Systems/{title}", title, "Systems", role, inputs, outputs) { Description = description, Keywords = keywords });

            void Node(string category, string title, Port[] inputs, Port[] outputs, Func<VfxValue[], VfxValue[]> compile, string description, string keywords = "") =>
                library.Add(new VfxNode($"{category}/{title}", title, category, VfxRole.Value, inputs, outputs)
                {
                    Compile = compile,
                    Description = description,
                    Keywords = keywords
                });

            // One output, worked out from the inputs.
            void One(string category, string title, Port[] inputs, Port output, Func<VfxValue[], VfxValue> compile, string description, string keywords = "") =>
                Node(category, title, inputs, [output], i => [compile(i)], description, keywords);

            // Numbers from two numbers.
            void Math2(string title, Func<float, float, float> math, string description, string keywords = "", float b = 0f) =>
                One("Math", title, [In("A", PortType.Number), In("B", PortType.Number, b)], Out("Result", PortType.Number),
                    i => (ref VfxParticle p, VfxFrame f) => N(math(i[0](ref p, f).X, i[1](ref p, f).X)), description, keywords);

            // A number from one.
            void Math1(string title, Func<float, float> math, string description, string keywords = "") =>
                One("Math", title, [In("Value", PortType.Number)], Out("Result", PortType.Number),
                    i => (ref VfxParticle p, VfxFrame f) => N(math(i[0](ref p, f).X)), description, keywords);

            // ---------------------------------------------------------------- The steps

            Step(VfxRole.Spawn, "Spawn",
            [
                In("Rate", PortType.Number, 20f, "Particles made each second"),
                In("Burst", PortType.Number, 0f, "Particles made at once when it starts, and each time it loops"),
                Setting("Duration", PortType.Number, 0f, "Seconds it plays for each time: 0 is for as long as it's on"),
                Setting("Loop", PortType.Boolean, true, "Starts again (with its burst) when its duration's up"),
                Setting("Max Particles", PortType.Number, 1000f, "The most alive at once"),
                Setting("Local Space", PortType.Boolean, false, "Particles move with the object, rather than staying where they were let go")
            ], [ParticlesOut], "The start of a particle system: how many particles are made, and when. Link it to Initialize.", "emit rate burst start system");

            Step(VfxRole.Initialize, "Initialize",
            [
                ParticlesIn,
                In("Lifetime", PortType.Number, 1f, "Seconds it lives"),
                In("Position", PortType.Vector, Vector3.Zero, "Where it starts, from the object (link a shape for a random place in it)"),
                In("Velocity", PortType.Vector, new Vector3(0f, 1f, 0f), "How fast it starts off, and which way, in metres a second"),
                In("Size", PortType.Number, 0.1f, "How wide it is, in metres"),
                In("Color", PortType.Color, Color.White, "Its colour, and how see-through"),
                In("Rotation", PortType.Number, 0f, "Degrees it starts turned by")
            ], [ParticlesOut], "What each particle starts as, worked out once as it's made. Random nodes give each its own.", "init start new birth");

            Step(VfxRole.Update, "Update",
            [
                ParticlesIn,
                In("Force", PortType.Vector, Vector3.Zero, "Pushes it, in metres a second squared: (0, -9.8, 0) falls"),
                In("Drag", PortType.Number, 0f, "How quickly it slows: the fraction of its speed lost each second, more or less"),
                In("Turbulence", PortType.Number, 0f, "How strongly it swirls about"),
                In("Turbulence Scale", PortType.Number, 1f, "How big the swirls are, in metres"),
                In("Spin", PortType.Number, 0f, "Degrees a second it turns by"),
                In("Kill", PortType.Boolean, false, "Ends it now, when true: one that's gone below the floor, say")
            ], [ParticlesOut], "What happens to each particle every frame. Optional: Initialize can link straight to Output.", "simulate forces gravity move");

            Step(VfxRole.Output, "Output",
            [
                ParticlesIn,
                In("Tint", PortType.Color, Color.White, "Multiplies its colour: link a gradient by its life to change colour as it ages"),
                In("Size Scale", PortType.Number, 1f, "Multiplies its size: link a curve by its life to grow or shrink it"),
                In("Glow", PortType.Number, 0f, "How much it glows, adding to bloom"),
                In("Stretch", PortType.Number, 0f, "Stretches it along its movement by how far it goes in this many seconds"),
                Setting("Look", PortType.Enum<ParticleLook>(), ParticleLook.Soft, "The shape drawn"),
                Setting("Blend", PortType.Enum<ParticleBlend>(), ParticleBlend.Additive, "Adds light, or covers what's behind")
            ], [], "How each particle is drawn, every frame: its colour and size can change as it ages.", "render draw look");

            // ---------------------------------------------------------------- The particle, the effect, properties

            Node("Particle", "Particle", [],
            [
                Out("Age", PortType.Number), Out("Life", PortType.Number), Out("Lifetime", PortType.Number), Out("Position", PortType.Vector),
                Out("Velocity", PortType.Vector), Out("Color", PortType.Color), Out("Size", PortType.Number), Out("Random", PortType.Number)
            ], _ =>
            [
                (ref VfxParticle p, VfxFrame f) => N(p.Age),
                (ref VfxParticle p, VfxFrame f) => N(p.Life),
                (ref VfxParticle p, VfxFrame f) => N(p.Lifetime),
                (ref VfxParticle p, VfxFrame f) => V(p.Position),
                (ref VfxParticle p, VfxFrame f) => V(p.Velocity),
                (ref VfxParticle p, VfxFrame f) => p.Color,
                (ref VfxParticle p, VfxFrame f) => N(p.Size),
                (ref VfxParticle p, VfxFrame f) => N(p.Random)
            ], "The particle being worked out: its age in seconds, Life from 0 (new) to 1 (about to go), where it is, and its own random number.",
                "age life attribute");

            Node("Particle", "Effect", [], [Out("Time", PortType.Number), Out("Delta Time", PortType.Number), Out("Position", PortType.Vector)], _ =>
            [
                (ref VfxParticle p, VfxFrame f) => N(f.Time),
                (ref VfxParticle p, VfxFrame f) => N(f.DeltaTime),
                (ref VfxParticle p, VfxFrame f) => V(f.World.Translation)
            ], "The effect itself: seconds since it started, how long the last frame took, and where it is in the world.", "time clock emitter");

            Node("Properties", "Get Property", [new Port("Variable", PortType.Text, "", Linkable: false) { IsVariableName = true }],
                [new Port("Value", PortType.Any) { TypeFromVariable = true }], _ => [],
                "One of the effect's properties (the graph's variables): the game can change them on each object with SetProperty.", "variable exposed parameter");

            // ---------------------------------------------------------------- Values

            One("Values", "Number", [Setting("Value", PortType.Number)], Out("Value", PortType.Number), i => i[0], "A number, to use in several places.", "float constant");
            One("Values", "Vector", [In("Value", PortType.Vector)], Out("Value", PortType.Vector), i => i[0], "A vector, to use in several places.", "vector3 constant");
            One("Values", "Color", [In("Value", PortType.Color)], Out("Value", PortType.Color), i => i[0], "A colour, to use in several places.", "colour constant");

            // ---------------------------------------------------------------- Random

            One("Random", "Random Range", [In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], Out("Value", PortType.Number), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float min = i[0](ref p, f).X, max = i[1](ref p, f).X;
                    return N(min + System.Random.Shared.NextSingle() * (max - min));
                },
                "A random number between Min and Max, new each time: in Initialize, each particle gets its own.", "rand");
            One("Random", "Random Vector", [In("Min", PortType.Vector, new Vector3(-1f)), In("Max", PortType.Vector, new Vector3(1f))], Out("Value", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector4 min = i[0](ref p, f), max = i[1](ref p, f);
                    var t = new Vector4(System.Random.Shared.NextSingle(), System.Random.Shared.NextSingle(), System.Random.Shared.NextSingle(), 0f);
                    return min + (max - min) * t;
                },
                "A vector with each part random between Min's and Max's.", "rand xyz");
            One("Random", "Random Color", [In("A", PortType.Color, Color.White), In("B", PortType.Color, new Color(1f, 0.5f, 0.2f))], Out("Color", PortType.Color),
                i => (ref VfxParticle p, VfxFrame f) => Vector4.Lerp(i[0](ref p, f), i[1](ref p, f), System.Random.Shared.NextSingle()),
                "A colour somewhere between A and B.", "rand colour tint");
            One("Random", "Random Direction", [], Out("Direction", PortType.Vector), _ => (ref VfxParticle p, VfxFrame f) => V(RandomDirection()),
                "A direction any way at all, a metre long.", "sphere unit rand");

            // ---------------------------------------------------------------- Shapes

            One("Shapes", "Point in Sphere", [In("Radius", PortType.Number, 0.5f), Setting("On Surface", PortType.Boolean, false)], Out("Position", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float radius = i[0](ref p, f).X;
                    bool surface = i[1](ref p, f).X != 0f;
                    return V(RandomDirection() * (surface ? radius : radius * MathF.Cbrt(System.Random.Shared.NextSingle())));
                },
                "A random point in a ball (or on its surface). Link it to Initialize's Position.", "ball shape emit");
            One("Shapes", "Point in Box", [In("Size", PortType.Vector, Vector3.One)], Out("Position", PortType.Vector), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector4 size = i[0](ref p, f);
                    var t = new Vector4(System.Random.Shared.NextSingle() - 0.5f, System.Random.Shared.NextSingle() - 0.5f, System.Random.Shared.NextSingle() - 0.5f, 0f);
                    return size * t;
                },
                "A random point in a box this big, centred on the object.", "cube area shape");
            One("Shapes", "Point on Circle", [In("Radius", PortType.Number, 0.5f), Setting("On Edge", PortType.Boolean, false)], Out("Position", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float radius = i[0](ref p, f).X;
                    float distance = i[1](ref p, f).X != 0f ? radius : radius * MathF.Sqrt(System.Random.Shared.NextSingle());
                    float angle = System.Random.Shared.NextSingle() * MathF.Tau;
                    return new Vector4(MathF.Cos(angle) * distance, 0f, MathF.Sin(angle) * distance, 0f);
                },
                "A random point on a flat circle lying on the ground (or on its rim): rings, puddles, portals.", "disc ring shape");
            One("Shapes", "Direction in Cone", [In("Direction", PortType.Vector, Vector3.UnitY), In("Angle", PortType.Number, 25f)], Out("Direction", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector3 axis = V3(i[0](ref p, f));
                    axis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitY;
                    return V(ParticleEmitter.SpreadDirection(axis, i[1](ref p, f).X, System.Random.Shared.NextSingle(), System.Random.Shared.NextSingle()));
                },
                "A random direction within Angle degrees of Direction, a metre long: scale it by a speed for Velocity.", "spread fountain shape");

            // ---------------------------------------------------------------- Math

            Math2("Add", (a, b) => a + b, "A plus B.", "plus sum +");
            Math2("Subtract", (a, b) => a - b, "A minus B.", "minus -");
            Math2("Multiply", (a, b) => a * b, "A times B.", "times *", b: 1f);
            Math2("Divide", (a, b) => b == 0f ? 0f : a / b, "A divided by B (0 if B is 0).", "over /", b: 1f);
            Math2("Min", MathF.Min, "The smaller of A and B.", "smallest");
            Math2("Max", MathF.Max, "The larger of A and B.", "largest");
            Math2("Power", MathF.Pow, "A to the power of B.", "pow exponent", b: 2f);
            Math1("Sine", MathF.Sin, "The sine of a number of radians: goes smoothly between -1 and 1.", "sin wave");
            Math1("One Minus", v => 1f - v, "1 take away the value: turns a life of 0 to 1 into 1 to 0.", "invert reverse");
            Math1("Absolute", MathF.Abs, "The value without its sign.", "abs");
            One("Math", "Lerp", [In("A", PortType.Number), In("B", PortType.Number, 1f), In("T", PortType.Number, 0.5f)], Out("Result", PortType.Number),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float a = i[0](ref p, f).X;
                    return N(a + (i[1](ref p, f).X - a) * i[2](ref p, f).X);
                },
                "Part of the way from A to B: T of 0 is A, 1 is B.", "interpolate mix blend");
            One("Math", "Clamp", [In("Value", PortType.Number), In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], Out("Result", PortType.Number),
                i => (ref VfxParticle p, VfxFrame f) => N(Math.Clamp(i[0](ref p, f).X, i[1](ref p, f).X, MathF.Max(i[1](ref p, f).X, i[2](ref p, f).X))),
                "The value kept between Min and Max.", "limit");
            One("Math", "Compare", [In("A", PortType.Number), In("B", PortType.Number), Setting("Is", PortType.Enum<Scripting.Comparison>(), Scripting.Comparison.Less)],
                Out("Result", PortType.Boolean), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float a = i[0](ref p, f).X, b = i[1](ref p, f).X;
                    bool result = (Scripting.Comparison)(int)i[2](ref p, f).X switch
                    {
                        Scripting.Comparison.Greater => a > b,
                        Scripting.Comparison.GreaterOrEqual => a >= b,
                        Scripting.Comparison.Less => a < b,
                        Scripting.Comparison.LessOrEqual => a <= b,
                        Scripting.Comparison.Equal => MathF.Abs(a - b) < 1e-5f,
                        _ => MathF.Abs(a - b) >= 1e-5f
                    };
                    return N(result ? 1f : 0f);
                },
                "Whether A is less than, greater than or equal to B: link it to Update's Kill to end particles below the floor.", "less greater < > if");
            One("Math", "Curve", [In("Start", PortType.Number, 0f), In("Middle", PortType.Number, 1f), In("End", PortType.Number, 0f), In("T", PortType.Number, 0f)],
                Out("Result", PortType.Number), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float start = i[0](ref p, f).X, middle = i[1](ref p, f).X, end = i[2](ref p, f).X, t = Math.Clamp(i[3](ref p, f).X, 0f, 1f);
                    float s = t < 0.5f ? t * 2f : (t - 0.5f) * 2f;
                    s = s * s * (3f - 2f * s);
                    return N(t < 0.5f ? start + (middle - start) * s : middle + (end - middle) * s);
                },
                "Eases from Start to Middle to End as T goes 0 to 1: link a particle's Life to grow it then shrink it away.", "ease over life animate");

            // ---------------------------------------------------------------- Vectors

            One("Vectors", "Make Vector", [In("X", PortType.Number), In("Y", PortType.Number), In("Z", PortType.Number)], Out("Vector", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) => new Vector4(i[0](ref p, f).X, i[1](ref p, f).X, i[2](ref p, f).X, 0f), "A vector from three numbers.", "combine xyz");
            Node("Vectors", "Split Vector", [In("Vector", PortType.Vector)], [Out("X", PortType.Number), Out("Y", PortType.Number), Out("Z", PortType.Number)], i =>
            [
                (ref VfxParticle p, VfxFrame f) => N(i[0](ref p, f).X),
                (ref VfxParticle p, VfxFrame f) => N(i[0](ref p, f).Y),
                (ref VfxParticle p, VfxFrame f) => N(i[0](ref p, f).Z)
            ], "A vector's three numbers.", "break xyz");
            One("Vectors", "Add Vectors", [In("A", PortType.Vector), In("B", PortType.Vector)], Out("Result", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) => i[0](ref p, f) + i[1](ref p, f), "A plus B.", "plus sum");
            One("Vectors", "Subtract Vectors", [In("A", PortType.Vector), In("B", PortType.Vector)], Out("Result", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) => i[0](ref p, f) - i[1](ref p, f), "A minus B: from B to A.", "minus difference");
            One("Vectors", "Scale Vector", [In("Vector", PortType.Vector), In("By", PortType.Number, 1f)], Out("Result", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) => i[0](ref p, f) * i[1](ref p, f).X, "A vector times a number: a direction times a speed.", "multiply times");
            One("Vectors", "Normalize", [In("Vector", PortType.Vector)], Out("Result", PortType.Vector), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector4 v = i[0](ref p, f);
                    float length = V3(v).Length();
                    return length > 1e-6f ? v / length : Vector4.Zero;
                },
                "The same direction, a metre long.", "unit direction");
            One("Vectors", "Length", [In("Vector", PortType.Vector)], Out("Length", PortType.Number),
                i => (ref VfxParticle p, VfxFrame f) => N(V3(i[0](ref p, f)).Length()), "How long a vector is: a velocity's speed.", "magnitude speed");
            One("Vectors", "Lerp Vectors", [In("A", PortType.Vector), In("B", PortType.Vector), In("T", PortType.Number, 0.5f)], Out("Result", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) => Vector4.Lerp(i[0](ref p, f), i[1](ref p, f), i[2](ref p, f).X), "Part of the way from A to B.", "interpolate mix");

            // ---------------------------------------------------------------- Colors

            One("Colors", "Make Color", [In("R", PortType.Number, 1f), In("G", PortType.Number, 1f), In("B", PortType.Number, 1f), In("A", PortType.Number, 1f)],
                Out("Color", PortType.Color), i => (ref VfxParticle p, VfxFrame f) => new Vector4(i[0](ref p, f).X, i[1](ref p, f).X, i[2](ref p, f).X, i[3](ref p, f).X),
                "A colour from its red, green, blue and alpha (how solid), each 0 to 1.", "rgba colour");
            One("Colors", "Lerp Colors", [In("A", PortType.Color, Color.White), In("B", PortType.Color, Color.Black), In("T", PortType.Number, 0.5f)], Out("Color", PortType.Color),
                i => (ref VfxParticle p, VfxFrame f) => Vector4.Lerp(i[0](ref p, f), i[1](ref p, f), Math.Clamp(i[2](ref p, f).X, 0f, 1f)),
                "Part of the way from colour A to B.", "mix blend fade colour");
            One("Colors", "Gradient",
                [In("Start", PortType.Color, new Color(1f, 0.9f, 0.4f)), In("Middle", PortType.Color, new Color(1f, 0.35f, 0.1f)), In("End", PortType.Color, new Color(0.2f, 0.2f, 0.2f, 0f)), In("T", PortType.Number, 0f)],
                Out("Color", PortType.Color), i => (ref VfxParticle p, VfxFrame f) =>
                {
                    float t = Math.Clamp(i[3](ref p, f).X, 0f, 1f);
                    return t < 0.5f ? Vector4.Lerp(i[0](ref p, f), i[1](ref p, f), t * 2f) : Vector4.Lerp(i[1](ref p, f), i[2](ref p, f), t * 2f - 1f);
                },
                "From Start through Middle to End as T goes 0 to 1: link a particle's Life to Output's Tint to change colour as it ages.", "colour over life ramp fade");
            One("Colors", "Multiply Colors", [In("A", PortType.Color, Color.White), In("B", PortType.Color, Color.White)], Out("Color", PortType.Color),
                i => (ref VfxParticle p, VfxFrame f) => i[0](ref p, f) * i[1](ref p, f), "One colour tinted by another.", "tint times colour");
            One("Colors", "Set Alpha", [In("Color", PortType.Color, Color.White), In("Alpha", PortType.Number, 1f)], Out("Color", PortType.Color),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector4 c = i[0](ref p, f);
                    c.W = i[1](ref p, f).X;
                    return c;
                },
                "The colour, with how solid it is set: link One Minus of Life to fade it out.", "opacity transparency fade colour");

            // ---------------------------------------------------------------- Noise

            One("Noise", "Swirl", [In("Position", PortType.Vector), In("Scale", PortType.Number, 1f), In("Speed", PortType.Number, 1f)], Out("Push", PortType.Vector),
                i => (ref VfxParticle p, VfxFrame f) =>
                {
                    Vector3 at = V3(i[0](ref p, f)) / MathF.Max(i[1](ref p, f).X, 0.01f);
                    return V(Swirl(at, f.Time * i[2](ref p, f).X));
                },
                "A push that changes smoothly from place to place and over time: add it to a force for wandering smoke and embers.", "turbulence curl noise wind");

            return library;
        }

        /// <summary>A smoothly changing push, different from place to place: sums of waves at odd angles.</summary>
        internal static Vector3 Swirl(Vector3 p, float t) => new(
            MathF.Sin(p.Y * 1.7f + t) + MathF.Sin(p.Z * 2.3f - t * 0.7f) * 0.5f,
            MathF.Sin(p.Z * 1.9f + t * 1.3f) + MathF.Sin(p.X * 2.1f + t * 0.5f) * 0.5f,
            MathF.Sin(p.X * 1.5f - t * 0.9f) + MathF.Sin(p.Y * 2.7f + t * 1.1f) * 0.5f);

        /// <summary>A new VFX graph to start from: embers rising from a small area, glowing orange and fading to smoke.</summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            GraphNode spawn = graph.AddNode("Systems/Spawn", new Vector2(0f, 0f));
            spawn.Values["Rate"] = 40f;
            GraphNode init = graph.AddNode("Systems/Initialize", new Vector2(320f, 0f));
            GraphNode update = graph.AddNode("Systems/Update", new Vector2(640f, 0f));
            update.Values["Force"] = new Vector3(0f, 1.2f, 0f);
            update.Values["Drag"] = 0.6f;
            update.Values["Turbulence"] = 1.5f;
            GraphNode output = graph.AddNode("Systems/Output", new Vector2(960f, 0f));
            output.Values["Glow"] = 2f;
            graph.Link(spawn.Id, "Particles", init.Id, "Particles");
            graph.Link(init.Id, "Particles", update.Id, "Particles");
            graph.Link(update.Id, "Particles", output.Id, "Particles");

            GraphNode lifetime = graph.AddNode("Random/Random Range", new Vector2(40f, 220f));
            lifetime.Values["Min"] = 1f;
            lifetime.Values["Max"] = 2f;
            graph.Link(lifetime.Id, "Value", init.Id, "Lifetime");

            GraphNode area = graph.AddNode("Shapes/Point in Sphere", new Vector2(40f, 340f));
            area.Values["Radius"] = 0.3f;
            graph.Link(area.Id, "Position", init.Id, "Position");

            GraphNode cone = graph.AddNode("Shapes/Direction in Cone", new Vector2(-300f, 460f));
            GraphNode speed = graph.AddNode("Vectors/Scale Vector", new Vector2(40f, 460f));
            speed.Values["By"] = 1.5f;
            graph.Link(cone.Id, "Direction", speed.Id, "Vector");
            graph.Link(speed.Id, "Result", init.Id, "Velocity");

            GraphNode particle = graph.AddNode("Particle/Particle", new Vector2(420f, 300f));
            GraphNode gradient = graph.AddNode("Colors/Gradient", new Vector2(680f, 260f));
            graph.Link(particle.Id, "Life", gradient.Id, "T");
            graph.Link(gradient.Id, "Color", output.Id, "Tint");

            GraphNode curve = graph.AddNode("Math/Curve", new Vector2(680f, 460f));
            curve.Values["Start"] = 0.4f;
            curve.Values["Middle"] = 1.2f;
            curve.Values["End"] = 0.2f;
            graph.Link(particle.Id, "Life", curve.Id, "T");
            graph.Link(curve.Id, "Result", output.Id, "Size Scale");
            return graph;
        }
    }
}
