using System.Numerics;

using Vertex.Graphs;
using Vertex.Input;

namespace Vertex.Scripting
{
    /// <summary>How a Compare node compares its two numbers.</summary>
    public enum Comparison
    {
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
        Equal,
        NotEqual
    }

    /// <summary>The nodes a visual script can have.</summary>
    public static partial class ScriptNodes
    {
        public const string Kind = "script";

        private sealed class ScriptLibrary() : NodeLibrary(ScriptNodes.Kind, "Visual Script")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Events" => new Color(0.82f, 0.3f, 0.3f),
                "Flow" => new Color(0.55f, 0.57f, 0.63f),
                "Actions" => new Color(0.3f, 0.52f, 0.86f),
                "Variables" => new Color(0.7f, 0.45f, 0.85f),
                "Objects" => new Color(0.25f, 0.62f, 0.75f),
                "Input" => new Color(0.85f, 0.55f, 0.25f),
                "Math" or "Logic" or "Vectors" or "Values" => new Color(0.33f, 0.62f, 0.4f),
                "Physics" => new Color(0.28f, 0.66f, 0.6f),
                "Networking" => new Color(0.86f, 0.42f, 0.2f),
                "Effects" or "Sound" or "UI" => new Color(0.75f, 0.42f, 0.82f),
                _ => base.CategoryColor(category)
            };
        }

        /// <summary>Whether two values are the same: numbers nearly enough (so 0.1 + 0.2 is 0.3), text and objects exactly.</summary>
        private static bool Same(object? a, object? b)
        {
            if (a is float or double or int && b is float or double or int) return Math.Abs(Convert.ToDouble(a) - Convert.ToDouble(b)) < 1e-5;
            if (a is string || b is string) return PortType.Text.Convert(a) as string == PortType.Text.Convert(b) as string;
            return Equals(a, b);
        }

        /// <summary>Every kind of node a visual script can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        // ---------------------------------------------------------------- Making them

        private static Port FlowIn(string name = "In") => new(name, PortType.Flow);
        private static Port FlowOut(string name = "Then") => new(name, PortType.Flow);
        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Setting(string name, PortType type, object? value = null) => new(name, type, value, Linkable: false);
        private static Port Out(string name, PortType type) => new(name, type);

        private static Port Target => In("Target", PortType.Object, tooltip: "The object to change: this one if nothing's linked");
        private static Port VariableSetting => new("Variable", PortType.Text, "", Linkable: false) { IsVariableName = true };

        private static NodeLibrary Build()
        {
            var library = new ScriptLibrary();
            NodeLibrary.Register(library);

            void Node(string category, string title, Port[] inputs, Port[] outputs, Action<ScriptRun> run, string description, string keywords = "",
                string? @event = null, Func<ScriptRun, bool>? when = null)
            {
                library.Add(new ScriptNode($"{category}/{title}", title, category, inputs, outputs, run)
                {
                    Description = description,
                    Keywords = keywords,
                    Event = @event,
                    When = when
                });
            }

            // Values worked out from two numbers.
            void Math2(string title, Func<float, float, float> math, string description, string keywords = "", float b = 0f) =>
                Node("Math", title, [In("A", PortType.Number), In("B", PortType.Number, b)], [Out("Result", PortType.Number)],
                    run => run.Out("Result", math(run.In<float>("A"), run.In<float>("B"))), description, keywords);

            // ---------------------------------------------------------------- Events

            Node("Events", "On Start", [], [FlowOut()], run => run.Continue("Then"),
                "When the object first runs: once, before its first frame.", "begin awake", @event: "Start");
            Node("Events", "On Update", [], [FlowOut(), Out("Delta Time", PortType.Number)], run =>
                {
                    run.Out("Delta Time", Time.DeltaTime);
                    run.Continue("Then");
                },
                "Every frame. Delta Time is how long the last frame took, in seconds: multiply speeds by it.", "tick frame loop", @event: "Update");
            Node("Events", "On Key Pressed", [Setting("Key", PortType.Enum<Key>(), Key.Space)], [FlowOut()], run => run.Continue("Then"),
                "The frame a key goes down.", "keyboard input press", @event: "Update", when: run => InputSystem.GetKeyDown(run.In<Key>("Key")));
            Node("Events", "On Key Released", [Setting("Key", PortType.Enum<Key>(), Key.Space)], [FlowOut()], run => run.Continue("Then"),
                "The frame a key comes back up.", "keyboard input release", @event: "Update", when: run => InputSystem.GetKeyUp(run.In<Key>("Key")));
            Node("Events", "On Collision", [], [FlowOut(), Out("Other", PortType.Object)], run => run.Continue("Then"),
                "When the object bumps into another (it needs a collider, and one of them a rigidbody). Other is what it hit.", "hit touch physics",
                @event: "Collision");
            Node("Events", "On Trigger", [], [FlowOut(), Out("Other", PortType.Object)], run => run.Continue("Then"),
                "When something goes into the object's trigger collider. Other is what went in.", "enter overlap physics", @event: "Trigger");

            // ---------------------------------------------------------------- Flow

            Node("Flow", "Branch", [FlowIn(), In("Condition", PortType.Boolean)], [FlowOut("True"), FlowOut("False")],
                run => run.Continue(run.In<bool>("Condition") ? "True" : "False"), "Goes one way if the condition's true, the other if not.", "if else");
            Node("Flow", "Sequence", [FlowIn()], [FlowOut("Then 1"), FlowOut("Then 2"), FlowOut("Then 3")], run =>
                {
                    run.Continue("Then 1");
                    run.Continue("Then 2");
                    run.Continue("Then 3");
                },
                "Does several things in order: all of Then 1, then all of Then 2, then Then 3.", "order steps");
            Node("Flow", "For Loop", [FlowIn(), In("From", PortType.Number, 0f), In("To", PortType.Number, 10f)],
                [FlowOut("Body"), Out("Index", PortType.Number), FlowOut("Done")], run =>
                {
                    int from = (int)MathF.Round(run.In<float>("From")), to = (int)MathF.Round(run.In<float>("To"));
                    for (int i = from; i < to; i++)
                    {
                        run.Out("Index", (float)i);
                        run.Continue("Body");
                    }

                    run.Continue("Done");
                },
                "Does Body once for each whole number from From up to (not including) To, with it as Index, then Done.", "repeat times count");

            // ---------------------------------------------------------------- Actions

            Node("Actions", "Print", [FlowIn(), In("Value", PortType.Any, "Hello")], [FlowOut()], run =>
                {
                    Log.Info(PortType.Describe(run.In("Value")));
                    run.Continue("Then");
                },
                "Writes a value to the console.", "log debug write console");
            Node("Actions", "Set Position", [FlowIn(), Target, In("Position", PortType.Vector)], [FlowOut()], run =>
                {
                    run.In<GameObject>("Target").Transform.Position = run.In<Vector3>("Position");
                    run.Continue("Then");
                },
                "Puts an object somewhere (relative to its parent).", "move place teleport");
            Node("Actions", "Move By", [FlowIn(), Target, In("Offset", PortType.Vector), In("Local", PortType.Boolean, false, "Along the object's own axes, rather than the world's")],
                [FlowOut()], run =>
                {
                    Transform transform = run.In<GameObject>("Target").Transform;
                    Vector3 offset = run.In<Vector3>("Offset");
                    if (run.In<bool>("Local")) offset = Vector3.Transform(offset, transform.Rotation);
                    transform.Position += offset;
                    run.Continue("Then");
                },
                "Moves an object by an offset. For steady movement, scale the offset by Delta Time.", "translate move");
            Node("Actions", "Rotate By", [FlowIn(), Target, In("Degrees", PortType.Vector)], [FlowOut()], run =>
                {
                    Transform transform = run.In<GameObject>("Target").Transform;
                    Vector3 degrees = run.In<Vector3>("Degrees") * (MathF.PI / 180f);
                    transform.Rotation = Quaternion.Normalize(transform.Rotation * Quaternion.CreateFromYawPitchRoll(degrees.Y, degrees.X, degrees.Z));
                    run.Continue("Then");
                },
                "Turns an object about its own X, Y and Z axes by some degrees.", "turn spin rotation");
            Node("Actions", "Set Scale", [FlowIn(), Target, In("Scale", PortType.Vector, Vector3.One)], [FlowOut()], run =>
                {
                    run.In<GameObject>("Target").Transform.Scale = run.In<Vector3>("Scale");
                    run.Continue("Then");
                },
                "Sets how big an object is along each axis.", "size grow shrink");
            Node("Actions", "Set Active", [FlowIn(), Target, In("Active", PortType.Boolean, true)], [FlowOut()], run =>
                {
                    run.In<GameObject>("Target").Active = run.In<bool>("Active");
                    run.Continue("Then");
                },
                "Switches an object on or off: off, it's hidden and its scripts stop.", "enable disable show hide");
            Node("Actions", "Destroy", [FlowIn(), Target], [FlowOut()], run =>
                {
                    run.In<GameObject>("Target").Destroy();
                    run.Continue("Then");
                },
                "Removes an object from the scene, with everything under it.", "delete remove kill");

            // ---------------------------------------------------------------- Variables

            Node("Variables", "Get Variable", [VariableSetting], [new Port("Value", PortType.Any) { TypeFromVariable = true }],
                run => run.Out("Value", run.GetVariable(run.In<string>("Variable"))), "A variable's value now.", "read");
            Node("Variables", "Set Variable", [FlowIn(), VariableSetting, new Port("Value", PortType.Any) { TypeFromVariable = true }], [FlowOut()], run =>
                {
                    run.SetVariable(run.In<string>("Variable"), run.In("Value"));
                    run.Continue("Then");
                },
                "Changes a variable's value.", "write store assign");

            // ---------------------------------------------------------------- Values

            Node("Values", "Number", [Setting("Value", PortType.Number)], [Out("Value", PortType.Number)], run => run.Out("Value", run.In<float>("Value")),
                "A number, to use in several places.", "float constant");
            Node("Values", "Text", [Setting("Value", PortType.Text, "")], [Out("Value", PortType.Text)], run => run.Out("Value", run.In<string>("Value")),
                "Some text, to use in several places.", "string constant");
            Node("Values", "Join Text", [In("A", PortType.Text), In("B", PortType.Text)], [Out("Result", PortType.Text)],
                run => run.Out("Result", run.In<string>("A") + run.In<string>("B")), "Two pieces of text one after the other.", "append concat string");

            // ---------------------------------------------------------------- Objects

            Node("Objects", "Self", [], [Out("Object", PortType.Object)], run => run.Out("Object", run.Self), "The object the script is on.", "this me");
            Node("Objects", "Find Object", [In("Name", PortType.Text, "")], [Out("Object", PortType.Object)],
                run => run.Out("Object", run.Self.Scene.Find(run.In<string>("Name"))), "The first object in the scene with this name, or none.", "search get");
            Node("Objects", "Get Position", [Target], [Out("Position", PortType.Vector)],
                run => run.Out("Position", run.In<GameObject>("Target").Transform.Position), "Where an object is (relative to its parent).", "location");
            Node("Objects", "Get Name", [Target], [Out("Name", PortType.Text)], run => run.Out("Name", run.In<GameObject>("Target").Name),
                "An object's name.", "");

            // ---------------------------------------------------------------- Input and time

            Node("Input", "Key Held", [Setting("Key", PortType.Enum<Key>(), Key.Space)], [Out("Held", PortType.Boolean)],
                run => run.Out("Held", InputSystem.GetKey(run.In<Key>("Key"))), "Whether a key is down right now.", "keyboard pressed down");
            Node("Input", "Axis", [Setting("Negative", PortType.Enum<Key>(), Key.A), Setting("Positive", PortType.Enum<Key>(), Key.D)], [Out("Value", PortType.Number)],
                run => run.Out("Value", (InputSystem.GetKey(run.In<Key>("Positive")) ? 1f : 0f) - (InputSystem.GetKey(run.In<Key>("Negative")) ? 1f : 0f)),
                "1 while the positive key's held, -1 while the negative one is, 0 for neither (or both).", "keyboard horizontal vertical wasd");
            Node("Input", "Time", [], [Out("Time", PortType.Number), Out("Delta Time", PortType.Number)], run =>
                {
                    run.Out("Time", Time.TotalTime);
                    run.Out("Delta Time", Time.DeltaTime);
                },
                "Seconds since the game started, and how long the last frame took.", "seconds clock");

            // ---------------------------------------------------------------- Math

            Math2("Add", (a, b) => a + b, "A plus B.", "plus sum +");
            Math2("Subtract", (a, b) => a - b, "A minus B.", "minus difference -");
            Math2("Multiply", (a, b) => a * b, "A times B.", "times product *", b: 1f);
            Math2("Divide", (a, b) => b == 0f ? 0f : a / b, "A divided by B (0 if B is 0).", "over quotient /", b: 1f);
            Math2("Min", MathF.Min, "The smaller of A and B.", "smallest least");
            Math2("Max", MathF.Max, "The larger of A and B.", "largest most");
            Node("Math", "Sine", [In("Value", PortType.Number)], [Out("Result", PortType.Number)], run => run.Out("Result", MathF.Sin(run.In<float>("Value"))),
                "The sine of a number of radians: goes smoothly between -1 and 1 as the number grows.", "sin wave oscillate");
            Node("Math", "Random Range", [In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], [Out("Value", PortType.Number)],
                run => run.Out("Value", run.In<float>("Min") + Random.Shared.NextSingle() * (run.In<float>("Max") - run.In<float>("Min"))),
                "A random number between Min and Max: a new one each time it's read.", "rand");
            Node("Math", "Lerp", [In("A", PortType.Number), In("B", PortType.Number, 1f), In("T", PortType.Number, 0.5f)], [Out("Result", PortType.Number)],
                run => run.Out("Result", run.In<float>("A") + (run.In<float>("B") - run.In<float>("A")) * run.In<float>("T")),
                "Part of the way from A to B: T of 0 is A, 1 is B.", "interpolate blend mix");

            // ---------------------------------------------------------------- Logic

            Node("Logic", "Compare", [In("A", PortType.Number), In("B", PortType.Number), Setting("Is", PortType.Enum<Comparison>(), Comparison.Greater)],
                [Out("Result", PortType.Boolean)], run =>
                {
                    float a = run.In<float>("A"), b = run.In<float>("B");
                    run.Out("Result", run.In<Comparison>("Is") switch
                    {
                        Comparison.Greater => a > b,
                        Comparison.GreaterOrEqual => a >= b,
                        Comparison.Less => a < b,
                        Comparison.LessOrEqual => a <= b,
                        Comparison.Equal => MathF.Abs(a - b) < 1e-5f,
                        _ => MathF.Abs(a - b) >= 1e-5f
                    });
                },
                "Whether A is greater than, less than or equal to B.", "greater less equal > < ==");
            Node("Logic", "And", [In("A", PortType.Boolean), In("B", PortType.Boolean)], [Out("Result", PortType.Boolean)],
                run => run.Out("Result", run.In<bool>("A") && run.In<bool>("B")), "True if both are.", "&& both");
            Node("Logic", "Or", [In("A", PortType.Boolean), In("B", PortType.Boolean)], [Out("Result", PortType.Boolean)],
                run => run.Out("Result", run.In<bool>("A") || run.In<bool>("B")), "True if either is.", "|| either");
            Node("Logic", "Not", [In("Value", PortType.Boolean)], [Out("Result", PortType.Boolean)],
                run => run.Out("Result", !run.In<bool>("Value")), "True if it isn't, false if it is.", "! invert opposite");
            Node("Logic", "Equal", [In("A", PortType.Any), In("B", PortType.Any)], [Out("Result", PortType.Boolean)], run => run.Out("Result", Same(run.In("A"), run.In("B"))),
                "Whether A and B are the same: two pieces of text, two numbers, two objects.", "equals == same text compare is");

            // ---------------------------------------------------------------- Vectors

            Node("Vectors", "Make Vector", [In("X", PortType.Number), In("Y", PortType.Number), In("Z", PortType.Number)], [Out("Vector", PortType.Vector)],
                run => run.Out("Vector", new Vector3(run.In<float>("X"), run.In<float>("Y"), run.In<float>("Z"))), "A vector from three numbers.", "combine xyz vector3");
            Node("Vectors", "Split Vector", [In("Vector", PortType.Vector)], [Out("X", PortType.Number), Out("Y", PortType.Number), Out("Z", PortType.Number)], run =>
                {
                    Vector3 v = run.In<Vector3>("Vector");
                    run.Out("X", v.X);
                    run.Out("Y", v.Y);
                    run.Out("Z", v.Z);
                },
                "A vector's three numbers.", "break xyz components");
            Node("Vectors", "Add Vectors", [In("A", PortType.Vector), In("B", PortType.Vector)], [Out("Result", PortType.Vector)],
                run => run.Out("Result", run.In<Vector3>("A") + run.In<Vector3>("B")), "A plus B.", "plus sum");
            Node("Vectors", "Scale Vector", [In("Vector", PortType.Vector), In("By", PortType.Number, 1f)], [Out("Result", PortType.Vector)],
                run => run.Out("Result", run.In<Vector3>("Vector") * run.In<float>("By")), "A vector times a number.", "multiply times");
            Node("Vectors", "Distance", [In("A", PortType.Vector), In("B", PortType.Vector)], [Out("Distance", PortType.Number)],
                run => run.Out("Distance", Vector3.Distance(run.In<Vector3>("A"), run.In<Vector3>("B"))), "How far apart two points are.", "length between");

            AddGameNodes(library);
            return library;
        }

        /// <summary>A new visual script to start from: something said on start, and an empty update.</summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            GraphNode start = graph.AddNode("Events/On Start", new Vector2(0f, 0f));
            GraphNode print = graph.AddNode("Actions/Print", new Vector2(260f, 0f));
            print.Values["Value"] = "Hello from a visual script!";
            graph.Link(start.Id, "Then", print.Id, "In");
            graph.AddNode("Events/On Update", new Vector2(0f, 180f));
            return graph;
        }
    }
}
