using System.Numerics;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Animation
{
    /// <summary>
    /// The nodes an animation graph can have. An animation graph (a .animgraph file) works out an object's pose each frame
    /// from its animations: Clip nodes play them, and what they're linked into blends them by the graph's parameters,
    /// which the game sets (<see cref="Animator.Set"/>): Blend by Value turns a speed into idle, walk or run; Blend by
    /// Bool fades between crouching and standing; Switch picks one of several, fading; Layer puts an upper body's aim over
    /// the legs' walk; Play Once plays an attack over the top when a trigger's set. What reaches the Output is the pose.
    /// </summary>
    public static class AnimationNodes
    {
        public const string Kind = "animation";

        /// <summary>The extension of animation graph files.</summary>
        public const string Extension = ".animgraph";

        /// <summary>What links animations together: a pose, from a Clip node towards the Output.</summary>
        public static readonly PortType Pose = new("Pose", null, new Color(0.36f, 0.78f, 0.62f));

        /// <summary>A parameter that's true for one frame when the game sets it (<see cref="Animator.SetTrigger"/>): a jump, an attack.</summary>
        public static readonly PortType Trigger = new("Trigger", typeof(bool), new Color(0.95f, 0.55f, 0.3f), false);

        private sealed class AnimationLibrary() : NodeLibrary(AnimationNodes.Kind, "Animation Graph")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Output" => new Color(0.86f, 0.42f, 0.2f),
                "Animations" => new Color(0.26f, 0.64f, 0.52f),
                "Blend" => new Color(0.3f, 0.55f, 0.86f),
                "Parameters" => new Color(0.7f, 0.45f, 0.85f),
                "Logic" => new Color(0.8f, 0.36f, 0.36f),
                _ => new Color(0.33f, 0.62f, 0.4f)
            };

            public override bool CanLink(PortType output, PortType input) =>
                output.CanLinkTo(input) || (output == Trigger && (input == PortType.Boolean || input == PortType.Number));

            public override IReadOnlyList<PortType> VariableTypes { get; } = [PortType.Number, PortType.Boolean, Trigger];

            public override (string One, string Several, string Note) VariableWords => ("Parameter", "Parameters", "the game sets them, or an Animator for itself");
        }

        /// <summary>Every kind of node an animation graph can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        // The nodes' ids, for what plays them (AnimationGraphPlayer).
        internal const string Output = "Output/Output";
        internal const string Clip = "Animations/Clip";
        internal const string RestPose = "Animations/Rest Pose";
        internal const string Blend = "Blend/Blend";
        internal const string BlendByValue = "Blend/Blend by Value";
        internal const string BlendByBool = "Blend/Blend by Bool";
        internal const string Switch = "Blend/Switch";
        internal const string Layer = "Blend/Layer";
        internal const string PlayOnce = "Blend/Play Once";
        internal const string GetParameter = "Parameters/Get Parameter";
        internal const string Add = "Math/Add";
        internal const string Subtract = "Math/Subtract";
        internal const string Multiply = "Math/Multiply";
        internal const string Divide = "Math/Divide";
        internal const string Clamp = "Math/Clamp";
        internal const string Compare = "Logic/Compare";
        internal const string And = "Logic/And";
        internal const string Or = "Logic/Or";
        internal const string Not = "Logic/Not";

        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Setting(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Linkable: false, Tooltip: tooltip);
        private static Port PoseIn(string name, string tooltip) => new(name, Pose, Tooltip: tooltip) { WhenUnlinked = "Rest pose" };
        private static Port PoseOut => new("Pose", Pose);
        private static Port NumberOut => new("Result", PortType.Number);
        private static Port BooleanOut => new("Result", PortType.Boolean);

        private static NodeLibrary Build()
        {
            var library = new AnimationLibrary();
            NodeLibrary.Register(library);

            void Node(string id, Port[] inputs, Port[] outputs, string description, string keywords = "", Port? repeated = null)
            {
                int slash = id.IndexOf('/');
                library.Add(new NodeType(id, id[(slash + 1)..], id[..slash], inputs, outputs)
                {
                    Description = description,
                    Keywords = keywords,
                    Repeated = repeated
                });
            }

            Node(Output, [PoseIn("Pose", "The pose the objects are put in")], [],
                "The pose the animator puts its object and the objects under it in, each frame.", "end result final");

            // ---------------------------------------------------------------- Animations

            Node(Clip, [Setting("Animation", PortType.Animation, tooltip: "An animation of the project's: a .anim file, or one of a model's own"),
                    In("Speed", PortType.Number, 1f, "How fast it plays: 1 as made, 2 twice as fast. Link a parameter to match a walk to how fast the game moves")],
                [PoseOut],
                "Plays an animation, round and round if it loops. It starts from the beginning each time it comes back into play (a blend moves to it, say). Drag animations from the Assets panel onto the graph to make these.",
                "animation play clip motion walk run idle");

            Node(RestPose, [], [PoseOut],
                "Everything as it was before any animation moved it: to blend towards, or to play an action over.", "bind default t-pose still none");

            // ---------------------------------------------------------------- Blend

            Node(Blend, [PoseIn("A", "The pose at a Weight of 0"), PoseIn("B", "The pose at a Weight of 1"), In("Weight", PortType.Number, 0.5f, "How far from A to B, from 0 to 1")],
                [PoseOut], "A mix of two poses: from all A at a Weight of 0 to all B at 1.", "mix lerp crossfade");

            Node(BlendByValue, [In("Value", PortType.Number, 0f, "What chooses the mix: link a parameter, such as a speed"),
                    Setting("At", PortType.Text, "0, 1, 2", "The value each pose is at, in order, with commas: \"0, 1.5, 4\" for idle at 0, walk at 1.5 and run at 4")],
                [PoseOut],
                "Blends between any number of poses by a value: idle, walk and run by how fast the character goes. Each pose is at a value (At); in between, it's a mix of the two either side.",
                "blend space 1d locomotion speed idle walk run", new Port("Pose", Pose, Tooltip: "A pose, at its value in At") { WhenUnlinked = "Rest pose" });

            Node(BlendByBool, [In("Condition", PortType.Boolean, false, "Which pose: link a parameter"), PoseIn("True", "The pose while Condition is true"),
                    PoseIn("False", "The pose while Condition is false"), In("Fade", PortType.Number, 0.25f, "Seconds to blend from one to the other when Condition changes")],
                [PoseOut], "One of two poses, fading from one to the other when its Condition changes: crouching or standing, aiming or not.",
                "if branch condition crouch aim toggle");

            Node(Switch, [In("Which", PortType.Number, 1f, "Which pose: 1 for Pose 1. Link a parameter, so the game can choose"),
                    In("Fade", PortType.Number, 0.25f, "Seconds to blend to a new pose when Which changes")],
                [PoseOut], "One of any number of poses, by number, fading to the new one when it changes: a character's states (idle, walking, climbing, dead).",
                "state machine select choose index states", new Port("Pose", Pose, Tooltip: "One of the poses to choose from") { WhenUnlinked = "Rest pose" });

            Node(Layer, [PoseIn("Base", "The pose underneath: the legs' walk"), PoseIn("Layer", "The pose put over it: the arms' aim"),
                    In("Weight", PortType.Number, 1f, "How much of the layer, from 0 to 1"),
                    Setting("From", PortType.Text, "", "The object it starts from, by name: the layer moves it and everything under it (\"Spine\" for the upper body). Empty for everything")],
                [PoseOut], "Puts one pose over another from an object down: an upper body aiming or waving while the legs walk.",
                "mask upper body override additive bone");

            Node(PlayOnce, [PoseIn("Base", "The pose the action plays over"), PoseIn("Action", "What plays once: an attack, a jump, a flinch"),
                    In("Play", PortType.Boolean, false, "Starts the action when it turns true: link a Trigger parameter"),
                    In("Fade In", PortType.Number, 0.15f, "Seconds to blend into the action"), In("Fade Out", PortType.Number, 0.25f, "Seconds to blend back out at its end")],
                [PoseOut], "Plays an action once over a pose when Play turns true (a trigger the game sets), then blends back: attacks, jumps, reloads.",
                "one shot action trigger attack jump montage");

            // ---------------------------------------------------------------- Parameters

            Node(GetParameter, [new Port("Variable", PortType.Text, "", Linkable: false) { IsVariableName = true }], [new Port("Value", PortType.Any) { TypeFromVariable = true }],
                "One of the graph's parameters: the game sets them (animator.Set(\"Speed\", 4)), visual scripts with Set Animation Parameter, and an Animator can start them at its own values.",
                "variable parameter input exposed speed");

            // ---------------------------------------------------------------- Maths and logic

            Node(Add, [In("A", PortType.Number), In("B", PortType.Number)], [NumberOut], "A + B.", "plus sum");
            Node(Subtract, [In("A", PortType.Number), In("B", PortType.Number)], [NumberOut], "A − B.", "minus difference");
            Node(Multiply, [In("A", PortType.Number, 1f), In("B", PortType.Number, 1f)], [NumberOut], "A × B.", "times product scale");
            Node(Divide, [In("A", PortType.Number, 1f), In("B", PortType.Number, 1f)], [NumberOut], "A ÷ B (0 when B is 0).", "over ratio");
            Node(Clamp, [In("Value", PortType.Number), In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], [NumberOut],
                "The value, kept between Min and Max.", "limit saturate range");
            Node(Compare, [In("A", PortType.Number), In("B", PortType.Number), Setting("Is", PortType.Enum<Comparison>(), Comparison.Greater)], [BooleanOut],
                "Whether A is greater than B (or less, or equal...): a speed over a walk's, say.", "greater less equal than");
            Node(And, [In("A", PortType.Boolean), In("B", PortType.Boolean)], [BooleanOut], "True when both are.", "both all");
            Node(Or, [In("A", PortType.Boolean), In("B", PortType.Boolean)], [BooleanOut], "True when either is.", "either any");
            Node(Not, [In("Value", PortType.Boolean)], [BooleanOut], "True when it isn't, false when it is.", "invert opposite");

            return library;
        }

        /// <summary>
        /// A new animation graph to start from: idle, walk and run blended by a Speed parameter, ready for animations to be
        /// chosen on its Clip nodes.
        /// </summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
            GraphNode speed = graph.AddNode(GetParameter, new Vector2(0f, -120f));
            speed.Values["Variable"] = "Speed";
            GraphNode idle = graph.AddNode(Clip, new Vector2(0f, 0f));
            GraphNode walk = graph.AddNode(Clip, new Vector2(0f, 140f));
            GraphNode run = graph.AddNode(Clip, new Vector2(0f, 280f));
            GraphNode blend = graph.AddNode(BlendByValue, new Vector2(320f, 60f));
            blend.Values["At"] = "0, 1.5, 4";
            GraphNode output = graph.AddNode(Output, new Vector2(620f, 60f));
            graph.Link(speed.Id, "Value", blend.Id, "Value");
            graph.Link(idle.Id, "Pose", blend.Id, "Pose 1");
            graph.Link(walk.Id, "Pose", blend.Id, "Pose 2");
            graph.Link(run.Id, "Pose", blend.Id, "Pose 3");
            graph.Link(blend.Id, "Pose", output.Id, "Pose");
            return graph;
        }
    }
}
