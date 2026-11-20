using System.Numerics;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.AI
{
    /// <summary>How a task in a behaviour tree is doing when it's run: still going, done, or it couldn't.</summary>
    public enum BehaviorStatus
    {
        Running,
        Success,
        Failure
    }

    /// <summary>
    /// When a Sequence (or an If) checks its conditions again, rather than only when they're reached: so a guard stops
    /// chasing when it loses sight of the player, and stops patrolling when it sees them.
    /// </summary>
    public enum Recheck
    {
        /// <summary>Only when they're reached.</summary>
        Never,

        /// <summary>Every frame while it runs: if one stops being true, it stops, and fails.</summary>
        WhileRunning,

        /// <summary>
        /// Every frame while something after it (under the same Selector) runs: when they're all true, that's stopped
        /// and this runs instead.
        /// </summary>
        ToInterrupt,

        /// <summary>Both: while it runs, and while what's after it runs.</summary>
        Both
    }

    /// <summary>When a Parallel is done.</summary>
    public enum ParallelPolicy
    {
        /// <summary>When every task under it has succeeded (and it fails as soon as one fails).</summary>
        AllSucceed,

        /// <summary>When any task under it succeeds (and it fails once all have failed).</summary>
        AnySucceeds
    }

    /// <summary>
    /// The nodes a behaviour tree can have. A behaviour tree (a .btree file) is how a character decides what to do: from
    /// the Root, each frame, tasks are run one under another. A Selector tries what's under it in turn until one works
    /// (chase the player if they're seen, otherwise patrol); a Sequence does what's under it in order until one fails
    /// (can see the player, then move to them, then attack). Conditions check something; actions do something and take
    /// as long as it takes (walking somewhere, waiting); decorators change what's under them (Invert, Repeat, Cooldown).
    /// <para>
    /// The tasks under a node run from the top down, in the order they're placed in the graph. The tree's variables are
    /// what the character knows (its target, where it last saw them): set in the inspector, by Set Variable, or by code
    /// (<see cref="BehaviorTree.SetVariable"/>). Movement needs a <see cref="NavMeshAgent"/> on the same object.
    /// </para>
    /// </summary>
    public static class BehaviorNodes
    {
        public const string Kind = "behavior";

        /// <summary>The extension of behaviour tree files.</summary>
        public const string Extension = ".btree";

        /// <summary>What joins a task to the tasks under it: from a node's Children (or Child) to each one's Parent.</summary>
        public static readonly PortType Task = new("Task", null, new Color(0.96f, 0.72f, 0.32f));

        private sealed class BehaviorLibrary() : NodeLibrary(BehaviorNodes.Kind, "Behavior Tree")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Root" => new Color(0.86f, 0.42f, 0.2f),
                "Composites" => new Color(0.3f, 0.52f, 0.86f),
                "Decorators" => new Color(0.6f, 0.44f, 0.86f),
                "Conditions" => new Color(0.78f, 0.36f, 0.4f),
                "Movement" => new Color(0.22f, 0.62f, 0.6f),
                "Actions" => new Color(0.33f, 0.62f, 0.4f),
                "Variables" => new Color(0.7f, 0.45f, 0.85f),
                _ => new Color(0.42f, 0.46f, 0.54f)
            };

            public override IReadOnlyList<PortType> VariableTypes { get; } = [PortType.Object, PortType.Vector, PortType.Number, PortType.Boolean, PortType.Text];

            public override (string One, string Several, string Note) VariableWords => ("Variable", "Variables", "what the character knows, per object");
        }

        /// <summary>Every kind of node a behaviour tree can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        // The nodes' ids, for what runs them (BehaviorTreeInstance).
        internal const string Root = "Root/Root";
        internal const string Sequence = "Composites/Sequence";
        internal const string Selector = "Composites/Selector";
        internal const string Parallel = "Composites/Parallel";
        internal const string RandomChild = "Composites/Random";
        internal const string Invert = "Decorators/Invert";
        internal const string AlwaysSucceed = "Decorators/Always Succeed";
        internal const string Repeat = "Decorators/Repeat";
        internal const string Retry = "Decorators/Retry";
        internal const string Cooldown = "Decorators/Cooldown";
        internal const string TimeLimit = "Decorators/Time Limit";
        internal const string If = "Decorators/If";
        internal const string Check = "Conditions/Check";
        internal const string CanSee = "Conditions/Can See";
        internal const string IsNear = "Conditions/Is Near";
        internal const string IsSet = "Conditions/Is Variable Set";
        internal const string Chance = "Conditions/Chance";
        internal const string MoveTo = "Movement/Move To";
        internal const string Wander = "Movement/Wander";
        internal const string Patrol = "Movement/Patrol";
        internal const string Flee = "Movement/Flee";
        internal const string LookAt = "Movement/Look At";
        internal const string StopMoving = "Movement/Stop";
        internal const string Wait = "Actions/Wait";
        internal const string SetVariable = "Actions/Set Variable";
        internal const string PlayAnimation = "Actions/Play Animation";
        internal const string SetAnimationParameter = "Actions/Set Animation Parameter";
        internal const string PlaySound = "Actions/Play Sound";
        internal const string SendEvent = "Actions/Send Event";
        internal const string CallMethod = "Actions/Call Method";
        internal const string LogText = "Actions/Log";
        internal const string GetVariable = "Variables/Get Variable";
        internal const string Self = "Values/Self";
        internal const string PositionOf = "Values/Position Of";
        internal const string DistanceTo = "Values/Distance To";
        internal const string RandomNumber = "Values/Random Number";
        internal const string Add = "Math/Add";
        internal const string Subtract = "Math/Subtract";
        internal const string Multiply = "Math/Multiply";
        internal const string Compare = "Logic/Compare";
        internal const string And = "Logic/And";
        internal const string Or = "Logic/Or";
        internal const string Not = "Logic/Not";

        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Setting(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Linkable: false, Tooltip: tooltip);
        private static Port Variable(string name, string tooltip) => new(name, PortType.Text, "", Linkable: false, Tooltip: tooltip) { IsVariableName = true };
        private static Port Parent => new("Parent", Task, Tooltip: "What runs this: a Root, a composite or a decorator, to its left");
        private static Port Children => new("Children", Task, Tooltip: "The tasks it runs, from the top down");
        private static Port Child => new("Child", Task, Tooltip: "The task it runs") { OneLink = true };
        private static Port Speed => In("Speed", PortType.Number, 0f, "How fast it goes; 0 for its Nav Mesh Agent's own speed");

        private static NodeLibrary Build()
        {
            var library = new BehaviorLibrary();
            NodeLibrary.Register(library);

            void Node(string id, Port[] inputs, Port[] outputs, string description, string keywords = "")
            {
                int slash = id.IndexOf('/');
                library.Add(new NodeType(id, id[(slash + 1)..], id[..slash], inputs, outputs) { Description = description, Keywords = keywords });
            }

            Node(Root, [], [new Port("Start", Task, Tooltip: "The task the tree starts from: usually a Selector") { OneLink = true }],
                "Where the tree starts, each frame. When what's under it finishes, it starts again from the top (unless the Behavior Tree's Loop is off).",
                "start begin entry top");

            // ---------------------------------------------------------------- Composites

            Node(Sequence, [Parent, Setting("Recheck", PortType.Enum<Recheck>(), Recheck.Never,
                    "When its conditions are checked again. While Running: it stops when one's no longer true. To Interrupt: it takes over from what runs after it when they're all true. Both: both")],
                [Children],
                "Does what's under it in order, from the top down: succeeds when they all have, and fails as soon as one fails. Put conditions first to say when it should happen: Can See, then Move To.",
                "and then steps in order all list");

            Node(Selector, [Parent], [Children],
                "Tries what's under it in order, from the top down, until one doesn't fail: the first that works is what happens. Put what matters most at the top: chase the player, otherwise patrol.",
                "or fallback priority choose first try");

            Node(Parallel, [Parent, Setting("Succeeds When", PortType.Enum<ParallelPolicy>(), ParallelPolicy.AllSucceed,
                    "All Succeed: when every one has succeeded (it fails when one fails). Any Succeeds: when one has (it fails when all have failed)")],
                [Children], "Runs everything under it at once, each frame: walk somewhere while playing a sound.", "together same time simultaneous concurrent");

            Node(RandomChild, [Parent], [Children], "Does one of what's under it, picked at random each time it starts.", "choose pick random any shuffle");

            // ---------------------------------------------------------------- Decorators

            Node(Invert, [Parent], [Child], "Succeeds when what's under it fails, and fails when it succeeds: Can See, inverted, is \"can't see\".", "not opposite negate");
            Node(AlwaysSucceed, [Parent], [Child], "Succeeds whatever what's under it does: for something that may not work but shouldn't stop a Sequence.",
                "succeeder ignore optional force success");
            Node(Repeat, [Parent, In("Times", PortType.Number, 0f, "How many times; 0 for ever")], [Child],
                "Runs what's under it again each time it succeeds, that many times (or for ever). It fails if what's under it fails.", "loop again times forever");
            Node(Retry, [Parent, In("Times", PortType.Number, 3f, "How many tries at most")], [Child],
                "Runs what's under it again each time it fails, up to that many tries. It succeeds as soon as what's under it does.", "try again until success attempts");
            Node(Cooldown, [Parent, In("Seconds", PortType.Number, 2f, "How long before it can run again")], [Child],
                "After what's under it has run, it fails straight away for a while instead of running it again: an attack every two seconds at most.",
                "delay rate limit throttle once every");
            Node(TimeLimit, [Parent, In("Seconds", PortType.Number, 5f, "The longest it can take")], [Child],
                "Stops what's under it, and fails, if it takes longer than that: give up looking after a while.", "timeout give up max duration");
            Node(If, [Parent, In("Condition", PortType.Boolean, false, "Whether it runs: link a value (Compare, Get Variable...)"),
                    Setting("Recheck", PortType.Enum<Recheck>(), Recheck.WhileRunning,
                        "When it checks again. While Running: it stops what's under it when the condition stops being true. To Interrupt: under a Selector, it takes over from what's after it when it becomes true")],
                [Child], "Runs what's under it only while the condition's true; fails when it isn't.", "condition guard when only branch blackboard");

            // ---------------------------------------------------------------- Conditions

            Node(Check, [Parent, In("Condition", PortType.Boolean, false, "What's checked: link a value (Compare, Get Variable...)")], [],
                "Succeeds if the condition's true, fails if it isn't.", "condition test if bool compare");
            Node(CanSee, [Parent, Variable("Target", "The variable that holds what it looks for: an object (the player), or a place"),
                    In("Distance", PortType.Number, 15f, "How far it sees"), In("Angle", PortType.Number, 120f, "How wide it sees, in degrees: 360 all round")],
                [], "Succeeds if the target's near enough, in front, and not hidden behind anything with a collider.",
                "sight vision see spot detect line of sight perception");
            Node(IsNear, [Parent, Variable("Target", "The variable that holds what it's near: an object or a place"), In("Distance", PortType.Number, 2f, "How near")], [],
                "Succeeds if the target's within that distance.", "close distance range within reach");
            Node(IsSet, [Parent, Variable("Variable", "The variable checked")], [],
                "Succeeds if the variable has something in it: an object that's still there, a true, a number that isn't 0, text that isn't empty.",
                "has known null exists empty");
            Node(Chance, [Parent, In("Chance", PortType.Number, 0.5f, "How likely, from 0 (never) to 1 (always)")], [],
                "Succeeds now and then, at random: for variety.", "random probability maybe percent");

            // ---------------------------------------------------------------- Movement

            Node(MoveTo, [Parent, Variable("Target", "The variable that holds where to go: an object (followed as it moves), or a place"),
                    In("Stop Within", PortType.Number, 1f, "How near it needs to get"), Speed],
                [], "Walks to the target, the shortest way, round what's in the way. Succeeds when it's there; fails if it can't get there.",
                "go walk run chase follow path navigate pathfind");
            Node(Wander, [Parent, In("Radius", PortType.Number, 8f, "How far from where it started it wanders"), Speed], [],
                "Walks to a random place it can get to, near where it started. Succeeds when it's there.", "roam random idle walk around");
            Node(Patrol, [Parent, Variable("Waypoints", "The variable that holds an object whose children are the places to go, in order"), Speed], [],
                "Walks to the next of the waypoints (the children of an object), one each time it runs, round and round. Succeeds at each.",
                "waypoints route path guard round circuit");
            Node(Flee, [Parent, Variable("From", "The variable that holds what it runs from"), In("Distance", PortType.Number, 10f, "How far it runs"), Speed], [],
                "Runs away from something, as far as that. Succeeds when it's got there.", "run away escape avoid retreat");
            Node(LookAt, [Parent, Variable("Target", "The variable that holds what to look at"), In("Turn Speed", PortType.Number, 360f, "How fast it turns, in degrees a second")], [],
                "Turns to face the target. Succeeds once it does.", "face turn rotate aim towards");
            Node(StopMoving, [Parent], [], "Stops walking.", "halt stand still");

            // ---------------------------------------------------------------- Actions

            Node(Wait, [Parent, In("Seconds", PortType.Number, 1f, "How long"), In("Random", PortType.Number, 0f, "Up to this many seconds more, at random")], [],
                "Waits, then succeeds.", "delay pause sleep idle timer");
            Node(SetVariable, [Parent, Variable("Variable", "The variable to set"), new Port("Value", PortType.Any) { TypeFromVariable = true }], [],
                "Sets one of the tree's variables: remember where the player was seen, with Position Of.", "assign store remember blackboard");
            Node(PlayAnimation, [Parent, Setting("Animation", PortType.Animation, tooltip: "An animation of the project's"),
                    In("Fade", PortType.Number, 0.2f, "Seconds to blend into it"), In("Wait Until Done", PortType.Boolean, false, "Succeed when it's finished, not straight away")],
                [], "Plays an animation on its Animator: an attack, a wave.", "animator clip anim attack emote");
            Node(SetAnimationParameter, [Parent, Setting("Parameter", PortType.Text, "Speed", "The animation graph's parameter"), In("Value", PortType.Number, 0f)], [],
                "Sets a parameter of its Animator's animation graph.", "animator blend speed state");
            Node(PlaySound, [Parent, Setting("Sound", PortType.Sound, tooltip: "A sound of the project's"), In("Volume", PortType.Number, 1f)], [],
                "Plays a sound where it is.", "audio sfx bark voice");
            Node(SendEvent, [Parent, Setting("Event", PortType.Text, "Spotted", "Its name: visual scripts' On Event nodes with this name start"),
                    In("Value", PortType.Any, tooltip: "Sent with it")], [],
                "Tells the scene's visual scripts something happened (their On Event nodes start), with its object as well as the value.",
                "message signal notify broadcast trigger");
            Node(CallMethod, [Parent, Setting("Method", PortType.Text, "", "The name of a method of one of its components (C# scripts), taking nothing")], [],
                "Runs a method of one of its own components, written in C#: it succeeds after one that returns nothing or true, fails after one that returns false, and keeps going while one that returns BehaviorStatus.Running does.",
                "code script csharp function custom action");
            Node(LogText, [Parent, In("Text", PortType.Text, "Hello", "What's written")], [], "Writes in the console, for seeing what the tree's doing.", "print debug console write");

            // ---------------------------------------------------------------- Values

            Node(GetVariable, [Variable("Variable", "The variable read")], [new Port("Value", PortType.Any) { TypeFromVariable = true }],
                "One of the tree's variables.", "variable blackboard read value");
            Node(Self, [], [new Port("Object", PortType.Object)], "The object the tree's running on.", "me this own");
            Node(PositionOf, [Variable("Target", "The variable that holds an object or a place")], [new Port("Position", PortType.Vector)],
                "Where the target is: an object's position, or the place itself.", "location where transform");
            Node(DistanceTo, [Variable("Target", "The variable that holds an object or a place")], [new Port("Distance", PortType.Number)],
                "How far away the target is.", "range how far near");
            Node(RandomNumber, [In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], [new Port("Result", PortType.Number)],
                "A random number between Min and Max, each time it's read.", "random");
            Node(Add, [In("A", PortType.Number), In("B", PortType.Number)], [new Port("Result", PortType.Number)], "A + B.", "plus sum");
            Node(Subtract, [In("A", PortType.Number), In("B", PortType.Number)], [new Port("Result", PortType.Number)], "A − B.", "minus difference");
            Node(Multiply, [In("A", PortType.Number, 1f), In("B", PortType.Number, 1f)], [new Port("Result", PortType.Number)], "A × B.", "times product scale");
            Node(Compare, [In("A", PortType.Number), In("B", PortType.Number), Setting("Is", PortType.Enum<Comparison>(), Comparison.Greater)], [new Port("Result", PortType.Boolean)],
                "Whether A is greater than B (or less, or equal...): health under 20, say.", "greater less equal than");
            Node(And, [In("A", PortType.Boolean), In("B", PortType.Boolean)], [new Port("Result", PortType.Boolean)], "True when both are.", "both all");
            Node(Or, [In("A", PortType.Boolean), In("B", PortType.Boolean)], [new Port("Result", PortType.Boolean)], "True when either is.", "either any");
            Node(Not, [In("Value", PortType.Boolean)], [new Port("Result", PortType.Boolean)], "True when it isn't, false when it is.", "invert opposite");

            return library;
        }

        /// <summary>
        /// The tasks under <paramref name="node"/> (linked from its Children, Child or Start), in the order they run: from
        /// the top of the graph down, then from the left.
        /// </summary>
        public static List<GraphNode> ChildrenOf(Graph graph, GraphNode node)
        {
            var children = new List<GraphNode>();
            foreach (GraphLink link in graph.Links)
            {
                if (link.FromNode != node.Id || link.ToPort != "Parent" || graph.Node(link.ToNode) is not { } child) continue;
                if (!children.Contains(child)) children.Add(child);
            }

            children.Sort((a, b) => a.Position.Y != b.Position.Y ? a.Position.Y.CompareTo(b.Position.Y) : a.Position.X != b.Position.X ? a.Position.X.CompareTo(b.Position.X) : a.Id.CompareTo(b.Id));
            return children;
        }

        /// <summary>
        /// A new behaviour tree to start from: a guard that chases its Target when it sees them, and otherwise wanders
        /// about, stopping now and then. Choose the Target (the player) on its Behavior Tree in the inspector.
        /// </summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            graph.Variables.Add(new GraphVariable("Target", PortType.Object));

            GraphNode root = graph.AddNode(Root, new Vector2(0f, 120f));
            GraphNode choose = graph.AddNode(Selector, new Vector2(220f, 120f));

            GraphNode chase = graph.AddNode(Sequence, new Vector2(460f, 0f));
            chase.Values["Recheck"] = nameof(Recheck.Both);
            GraphNode see = graph.AddNode(CanSee, new Vector2(760f, -60f));
            see.Values["Target"] = "Target";
            GraphNode go = graph.AddNode(MoveTo, new Vector2(760f, 90f));
            go.Values["Target"] = "Target";
            go.Values["Stop Within"] = 1.5f;

            GraphNode idle = graph.AddNode(Sequence, new Vector2(460f, 260f));
            GraphNode wander = graph.AddNode(Wander, new Vector2(760f, 260f));
            GraphNode wait = graph.AddNode(Wait, new Vector2(760f, 380f));
            wait.Values["Seconds"] = 1.5f;
            wait.Values["Random"] = 2f;

            graph.Link(root.Id, "Start", choose.Id, "Parent");
            graph.Link(choose.Id, "Children", chase.Id, "Parent");
            graph.Link(choose.Id, "Children", idle.Id, "Parent");
            graph.Link(chase.Id, "Children", see.Id, "Parent");
            graph.Link(chase.Id, "Children", go.Id, "Parent");
            graph.Link(idle.Id, "Children", wander.Id, "Parent");
            graph.Link(idle.Id, "Children", wait.Id, "Parent");
            return graph;
        }
    }
}
