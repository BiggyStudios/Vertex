using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Animation
{
    /// <summary>One of a state machine's states: what the character's doing (idle, running, jumping), and what plays while it is.</summary>
    public sealed class AnimationState
    {
        /// <summary>Its name: what transitions go from and to, and the State Machine node's input for it.</summary>
        public string Name { get; set; } = "State";

        /// <summary>Where it's drawn in the editor's state machine view.</summary>
        public Vector2 Position { get; set; }

        /// <summary>The animation it plays (as a graph's Clip node names one), when nothing's linked into its input; empty for none.</summary>
        public string Animation { get; set; } = "";

        /// <summary>How fast its animation plays.</summary>
        public float Speed { get; set; } = 1f;

        public AnimationState Copy() => (AnimationState)MemberwiseClone();
    }

    /// <summary>One thing that has to be so for a transition to happen: a parameter compared with a value.</summary>
    public sealed class TransitionCondition
    {
        public string Parameter { get; set; } = "";
        public Comparison Is { get; set; } = Comparison.Greater;

        /// <summary>What it's compared with: a number, or 1 for true and 0 for false.</summary>
        public float Value { get; set; }

        public TransitionCondition Copy() => (TransitionCondition)MemberwiseClone();

        /// <summary>Whether it holds, with the parameter at <paramref name="value"/> (a true/false or trigger as 1 or 0).</summary>
        public bool Holds(float value) => Is switch
        {
            Comparison.Greater => value > Value,
            Comparison.GreaterOrEqual => value >= Value,
            Comparison.Less => value < Value,
            Comparison.LessOrEqual => value <= Value,
            Comparison.Equal => MathF.Abs(value - Value) < 1e-4f,
            _ => MathF.Abs(value - Value) >= 1e-4f
        };
    }

    /// <summary>A way from one state to another, taken when all its conditions hold (and, if it waits, once the state's animation is done).</summary>
    public sealed class StateTransition
    {
        /// <summary>The state it goes from; <see cref="AnimationStateMachine.AnyState"/> for from any.</summary>
        public string From { get; set; } = "";
        public string To { get; set; } = "";

        /// <summary>Seconds it blends from one to the other.</summary>
        public float Fade { get; set; } = 0.25f;

        /// <summary>Waits until the state it's from has played its animation through (ending as the fade does): an attack that goes back to idle when it's done.</summary>
        public bool AtEnd { get; set; }

        public List<TransitionCondition> Conditions { get; } = new();

        public StateTransition Copy()
        {
            var copy = new StateTransition { From = From, To = To, Fade = Fade, AtEnd = AtEnd };
            foreach (TransitionCondition condition in Conditions) copy.Conditions.Add(condition.Copy());
            return copy;
        }
    }

    /// <summary>
    /// What an animation graph's State Machine node is made of: states (each playing an animation, or whatever's linked
    /// into its input on the node), and transitions between them taken when the graph's parameters say. It's in one
    /// state at a time, fading to the next when a transition's taken. Kept on the node as JSON (its "States" value), and
    /// made in the editor's state machine view.
    /// </summary>
    public sealed class AnimationStateMachine
    {
        /// <summary>A transition's <see cref="StateTransition.From"/> for one that can be taken from any state.</summary>
        public const string AnyState = "*";

        /// <summary>The node value it's kept in.</summary>
        public const string ValueName = "States";

        public List<AnimationState> States { get; } = new();
        public List<StateTransition> Transitions { get; } = new();

        /// <summary>The state it starts in.</summary>
        public string Entry { get; set; } = "";

        /// <summary>Where the editor draws Entry and Any State.</summary>
        public Vector2 EntryPosition { get; set; } = new(-260f, 0f);
        public Vector2 AnyPosition { get; set; } = new(-260f, 120f);

        public AnimationState? State(string name)
        {
            foreach (AnimationState state in States)
            {
                if (state.Name == name) return state;
            }

            return null;
        }

        public int IndexOf(string name) => States.FindIndex(s => s.Name == name);

        /// <summary>The state it starts in: <see cref="Entry"/>, or the first if that's gone.</summary>
        public int EntryIndex
        {
            get
            {
                int entry = IndexOf(Entry);
                return entry >= 0 ? entry : States.Count > 0 ? 0 : -1;
            }
        }

        /// <summary>A name for a new state that no state has yet: "Walk", or "Walk 2".</summary>
        public string UniqueName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "State" : name.Trim();
            if (State(name) == null && name != AnyState) return name;
            for (int i = 2; ; i++)
            {
                if (State($"{name} {i}") == null) return $"{name} {i}";
            }
        }

        public AnimationStateMachine Copy() => Parse(ToJson());

        /// <summary>A new one: an Idle state to start from.</summary>
        public static AnimationStateMachine Starter()
        {
            var machine = new AnimationStateMachine { Entry = "Idle" };
            machine.States.Add(new AnimationState { Name = "Idle" });
            return machine;
        }

        // ---------------------------------------------------------------- On a node

        private static readonly ConditionalWeakTable<string, AnimationStateMachine> _parsed = new();
        private static readonly AnimationStateMachine _starter = Starter();

        /// <summary>
        /// The state machine kept on <paramref name="node"/>, read once for each change. It's shared: change a
        /// <see cref="Copy"/>, and put that back with <see cref="Store"/>.
        /// </summary>
        public static AnimationStateMachine Of(GraphNode node)
        {
            if (node.Values.GetValueOrDefault(ValueName) is not string json || json.Length == 0) return _starter;
            lock (_parsed)
            {
                if (_parsed.TryGetValue(json, out AnimationStateMachine? known)) return known;
                AnimationStateMachine machine;
                try
                {
                    machine = Parse(json);
                }
                catch (Exception)
                {
                    machine = Starter();
                }

                _parsed.AddOrUpdate(json, machine);
                return machine;
            }
        }

        /// <summary>Keeps this on <paramref name="node"/> (in place of what it had), and marks the graph changed.</summary>
        public void Store(Graph graph, GraphNode node)
        {
            node.Values[ValueName] = ToJson();
            graph.Changed();
        }

        private readonly Dictionary<string, Port> _ports = new();

        /// <summary>The node's inputs for its states: one each, a pose, by the state's name.</summary>
        internal IReadOnlyList<Port> Ports()
        {
            var ports = new List<Port>(States.Count);
            foreach (AnimationState state in States)
            {
                if (!_ports.TryGetValue(state.Name, out Port? port))
                {
                    string unlinked = state.Animation.Length > 0 ? AnimationName(state.Animation) : "Rest pose";
                    port = new Port(state.Name, AnimationNodes.Pose, Tooltip: $"What plays in the {state.Name} state: link a blend here, or it plays its own animation")
                    {
                        WhenUnlinked = unlinked
                    };
                    _ports[state.Name] = port;
                }

                ports.Add(port);
            }

            return ports;
        }

        /// <summary>An animation's name from how a graph keeps it: "Walk" for "Animations/Walk.anim", and for "Robot.glb#Run", "Run".</summary>
        public static string AnimationName(string path)
        {
            int hash = path.LastIndexOf('#');
            if (hash >= 0) return path[(hash + 1)..];
            return Path.GetFileNameWithoutExtension(path.StartsWith("file:", StringComparison.Ordinal) ? path[5..] : path);
        }

        // ---------------------------------------------------------------- While it plays

        private static readonly ConditionalWeakTable<Graph, Dictionary<int, (string State, double At)>> _playing = new();

        /// <summary>A state machine node of <paramref name="graph"/> is in <paramref name="state"/> this frame: for the editor to light it up.</summary>
        internal static void Playing(Graph graph, int node, string state)
        {
            Dictionary<int, (string, double)> playing = _playing.GetOrCreateValue(graph);
            lock (playing) playing[node] = (state, Graph.Clock);
        }

        /// <summary>The state node <paramref name="node"/> of <paramref name="graph"/> is in, if it played in the last moment (by <see cref="Graph.Clock"/>).</summary>
        public static string? PlayingState(Graph graph, int node, double within = 0.25)
        {
            if (!_playing.TryGetValue(graph, out Dictionary<int, (string State, double At)>? playing)) return null;
            lock (playing) return playing.TryGetValue(node, out (string State, double At) last) && Graph.Clock - last.At <= within ? last.State : null;
        }

        // ---------------------------------------------------------------- JSON

        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("entry", Entry);
                Point("entryAt", EntryPosition);
                Point("anyAt", AnyPosition);

                json.WriteStartArray("states");
                foreach (AnimationState state in States)
                {
                    json.WriteStartObject();
                    json.WriteString("name", state.Name);
                    Point("at", state.Position);
                    if (state.Animation.Length > 0) json.WriteString("animation", state.Animation);
                    if (state.Speed != 1f) json.WriteNumber("speed", state.Speed);
                    json.WriteEndObject();
                }

                json.WriteEndArray();

                json.WriteStartArray("transitions");
                foreach (StateTransition transition in Transitions)
                {
                    json.WriteStartObject();
                    json.WriteString("from", transition.From);
                    json.WriteString("to", transition.To);
                    json.WriteNumber("fade", transition.Fade);
                    if (transition.AtEnd) json.WriteBoolean("atEnd", true);
                    json.WriteStartArray("when");
                    foreach (TransitionCondition condition in transition.Conditions)
                    {
                        json.WriteStartObject();
                        json.WriteString("parameter", condition.Parameter);
                        json.WriteString("is", condition.Is.ToString());
                        json.WriteNumber("value", condition.Value);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();

                void Point(string name, Vector2 at)
                {
                    json.WriteStartArray(name);
                    json.WriteNumberValue(MathF.Round(at.X, 1));
                    json.WriteNumberValue(MathF.Round(at.Y, 1));
                    json.WriteEndArray();
                }
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static AnimationStateMachine Parse(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            var machine = new AnimationStateMachine
            {
                Entry = Text(root, "entry"),
                EntryPosition = Point(root, "entryAt") ?? new Vector2(-260f, 0f),
                AnyPosition = Point(root, "anyAt") ?? new Vector2(-260f, 120f)
            };

            if (root.TryGetProperty("states", out JsonElement states))
            {
                foreach (JsonElement saved in states.EnumerateArray())
                {
                    string name = Text(saved, "name");
                    if (name.Length == 0 || machine.State(name) != null) continue;
                    machine.States.Add(new AnimationState
                    {
                        Name = name,
                        Position = Point(saved, "at") ?? Vector2.Zero,
                        Animation = Text(saved, "animation"),
                        Speed = saved.TryGetProperty("speed", out JsonElement speed) ? speed.GetSingle() : 1f
                    });
                }
            }

            if (root.TryGetProperty("transitions", out JsonElement transitions))
            {
                foreach (JsonElement saved in transitions.EnumerateArray())
                {
                    var transition = new StateTransition
                    {
                        From = Text(saved, "from"),
                        To = Text(saved, "to"),
                        Fade = saved.TryGetProperty("fade", out JsonElement fade) ? fade.GetSingle() : 0.25f,
                        AtEnd = saved.TryGetProperty("atEnd", out JsonElement atEnd) && atEnd.GetBoolean()
                    };

                    if (saved.TryGetProperty("when", out JsonElement when))
                    {
                        foreach (JsonElement condition in when.EnumerateArray())
                        {
                            transition.Conditions.Add(new TransitionCondition
                            {
                                Parameter = Text(condition, "parameter"),
                                Is = Enum.TryParse(Text(condition, "is"), out Comparison comparison) ? comparison : Comparison.Greater,
                                Value = condition.TryGetProperty("value", out JsonElement value) ? value.GetSingle() : 0f
                            });
                        }
                    }

                    machine.Transitions.Add(transition);
                }
            }

            return machine;

            static string Text(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

            static Vector2? Point(JsonElement element, string name)
            {
                if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2) return null;
                return new Vector2(value[0].GetSingle(), value[1].GetSingle());
            }
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{States.Count} states, {Transitions.Count} transitions");
    }
}
