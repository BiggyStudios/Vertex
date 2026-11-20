using System.Globalization;

using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Animation
{
    /// <summary>
    /// An animation graph playing on one <see cref="Animator"/>: each frame it works the pose out from the Output back,
    /// only through what's in play (a blend that's all one pose doesn't work out the other). Each node keeps what it needs
    /// from frame to frame: how far into its animation a Clip is, how far a fade has gone. A Clip that comes back into play
    /// starts from the beginning.
    /// </summary>
    internal sealed class AnimationGraphPlayer
    {
        private sealed class NodeState
        {
            public float Time;
            public int LastFrame = int.MinValue;
            public float Weight;
            public float[] Weights = [];
            public bool Playing, WasPlay;
            public float Elapsed, Length;
        }

        private const int _maxDepth = 64;

        private readonly Graph _graph;
        private readonly Animator _animator;
        private readonly Dictionary<int, NodeState> _states = new();
        private readonly Stack<Pose> _free = new();
        private readonly Dictionary<int, object?> _settings = new();
        private int _settingsVersion = -1;
        private int _frame;
        private float _deltaTime;
        private int _depth;

        public AnimationGraphPlayer(Graph graph, Animator animator)
        {
            _graph = graph;
            _animator = animator;
        }

        private AnimationRig Rig => _animator.Rig;

        /// <summary>The objects were looked up again: poses and masks made for the old ones go.</summary>
        public void Rebind()
        {
            _free.Clear();
            _settings.Clear();
            _settingsVersion = -1;
        }

        /// <summary>Moves the graph on by <paramref name="deltaTime"/> seconds and works out its pose into <paramref name="into"/>.</summary>
        public void Evaluate(float deltaTime, Pose into, float weight)
        {
            if (_settingsVersion != _graph.Version)
            {
                _settings.Clear();
                _settingsVersion = _graph.Version;
            }

            _frame++;
            _deltaTime = deltaTime;
            _depth = 0;
            GraphNode? output = null;
            foreach (GraphNode node in _graph.Nodes)
            {
                if (node.Type == AnimationNodes.Output)
                {
                    output = node;
                    break;
                }
            }

            into.CopyFrom(Rig.Rest);
            if (output == null) return;
            _graph.Ran(output.Id);
            Input(output, "Pose", into, weight);
        }

        private NodeState State(GraphNode node)
        {
            if (!_states.TryGetValue(node.Id, out NodeState? state)) _states[node.Id] = state = new NodeState();
            return state;
        }

        /// <summary>Whether a node was in play last frame too: if not, it starts again.</summary>
        private bool Continues(NodeState state)
        {
            bool continues = state.LastFrame == _frame - 1 || state.LastFrame == _frame;
            state.LastFrame = _frame;
            return continues;
        }

        private Pose Rent()
        {
            Pose pose = _free.Count > 0 ? _free.Pop() : Rig.NewPose();
            pose.CopyFrom(Rig.Rest);
            return pose;
        }

        private void Return(Pose pose) => _free.Push(pose);

        private GraphNode? Linked(GraphNode node, string port)
        {
            foreach (GraphLink link in _graph.Links)
            {
                if (link.ToNode == node.Id && link.ToPort == port) return _graph.Node(link.FromNode);
            }

            return null;
        }

        /// <summary>The pose linked into <paramref name="port"/> of <paramref name="node"/>, into <paramref name="into"/>; the rest pose if nothing is.</summary>
        private void Input(GraphNode node, string port, Pose into, float weight)
        {
            if (Linked(node, port) is { } from) Evaluate(from, into, weight);
            else into.CopyFrom(Rig.Rest);
        }

        private void Evaluate(GraphNode node, Pose into, float weight)
        {
            if (++_depth > _maxDepth)
            {
                _graph.Failed(node.Id, "This goes round in a loop");
                into.CopyFrom(Rig.Rest);
                _depth--;
                return;
            }

            try
            {
                _graph.Ran(node.Id);
                switch (node.Type)
                {
                    case AnimationNodes.Clip: PlayClip(node, into, weight); break;
                    case AnimationNodes.Blend: BlendTwo(node, into, weight); break;
                    case AnimationNodes.BlendByValue: BlendByValue(node, into, weight); break;
                    case AnimationNodes.BlendByBool: BlendByBool(node, into, weight); break;
                    case AnimationNodes.Switch: Switch(node, into, weight); break;
                    case AnimationNodes.Layer: Layer(node, into, weight); break;
                    case AnimationNodes.PlayOnce: PlayOnce(node, into, weight); break;
                    default: into.CopyFrom(Rig.Rest); break;
                }
            }
            finally
            {
                _depth--;
            }
        }

        // ---------------------------------------------------------------- Poses

        private void PlayClip(GraphNode node, Pose into, float weight)
        {
            NodeState state = State(node);
            if (!Continues(state)) state.Time = 0f;

            if (ClipOf(node) is not { } clip)
            {
                _graph.Failed(node.Id, "Choose an animation for it to play");
                into.CopyFrom(Rig.Rest);
                return;
            }

            float before = state.Time;
            state.Time += _deltaTime * Number(node, "Speed");
            into.CopyFrom(Rig.Rest);
            Rig.Sample(clip, clip.Wrap(state.Time), into);
            if (weight >= 0.5f) _animator.Passed(clip, before, state.Time);
        }

        private void BlendTwo(GraphNode node, Pose into, float weight)
        {
            float w = Math.Clamp(Number(node, "Weight"), 0f, 1f);
            Mix(node, "A", 1f - w, "B", w, into, weight);
        }

        /// <summary>Two inputs mixed, <paramref name="a"/> and <paramref name="b"/> of each (adding up to 1): one alone isn't worked out with the other.</summary>
        private void Mix(GraphNode node, string first, float a, string second, float b, Pose into, float weight)
        {
            if (b <= 0.001f)
            {
                Input(node, first, into, weight);
                return;
            }

            if (a <= 0.001f)
            {
                Input(node, second, into, weight);
                return;
            }

            Input(node, first, into, weight * a);
            Pose other = Rent();
            Input(node, second, other, weight * b);
            into.Blend(other, b);
            Return(other);
        }

        private void BlendByValue(GraphNode node, Pose into, float weight)
        {
            NodeType type = AnimationNodes.Library.Find(node.Type)!;
            int count = type.RepeatedCount(_graph, node) - 1;
            float[] points = Points(node, count);
            if (count <= 0)
            {
                into.CopyFrom(Rig.Rest);
                return;
            }

            float value = Number(node, "Value");
            if (count == 1 || value <= points[0])
            {
                Input(node, "Pose 1", into, weight);
                return;
            }

            if (value >= points[count - 1])
            {
                Input(node, $"Pose {count}", into, weight);
                return;
            }

            int low = 0;
            while (low < count - 2 && value > points[low + 1]) low++;
            float span = points[low + 1] - points[low];
            float t = span > 1e-6f ? Math.Clamp((value - points[low]) / span, 0f, 1f) : 1f;
            Mix(node, $"Pose {low + 1}", 1f - t, $"Pose {low + 2}", t, into, weight);
        }

        /// <summary>The values a Blend by Value's poses are at: as typed in its At, then on in steps of 1 for any it doesn't say.</summary>
        private float[] Points(GraphNode node, int count)
        {
            if (_settings.TryGetValue(node.Id, out object? cached) && cached is float[] known && known.Length == Math.Max(count, 0)) return known;

            var points = new float[Math.Max(count, 0)];
            string text = node.Values.GetValueOrDefault("At") as string ?? "0, 1, 2";
            string[] parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < points.Length; i++)
            {
                if (i < parts.Length && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float at)) points[i] = at;
                else points[i] = i == 0 ? 0f : points[i - 1] + 1f;
                if (i > 0 && points[i] < points[i - 1]) points[i] = points[i - 1];
            }

            _settings[node.Id] = points;
            return points;
        }

        private void BlendByBool(GraphNode node, Pose into, float weight)
        {
            NodeState state = State(node);
            float target = Bool(node, "Condition") ? 1f : 0f;
            if (!Continues(state)) state.Weight = target;
            else state.Weight = Toward(state.Weight, target, Number(node, "Fade"));
            Mix(node, "False", 1f - state.Weight, "True", state.Weight, into, weight);
        }

        /// <summary>A fade's weight moved on a frame towards <paramref name="target"/>, all the way in <paramref name="fade"/> seconds.</summary>
        private float Toward(float current, float target, float fade)
        {
            if (fade <= 0f) return target;
            float step = MathF.Abs(_deltaTime) / fade;
            return target > current ? MathF.Min(target, current + step) : MathF.Max(target, current - step);
        }

        private void Switch(GraphNode node, Pose into, float weight)
        {
            NodeState state = State(node);
            NodeType type = AnimationNodes.Library.Find(node.Type)!;
            int count = Math.Max(1, type.RepeatedCount(_graph, node) - 1);
            int which = Math.Clamp((int)MathF.Round(Number(node, "Which")), 1, count) - 1;

            bool continues = Continues(state);
            if (state.Weights.Length != count)
            {
                Array.Resize(ref state.Weights, count);
                continues = false;
            }

            float fade = Number(node, "Fade");
            for (int i = 0; i < count; i++)
            {
                float target = i == which ? 1f : 0f;
                state.Weights[i] = continues ? Toward(state.Weights[i], target, fade) : target;
            }

            // A running mix of each in play, by its share of the total.
            float total = 0f;
            Pose? other = null;
            for (int i = 0; i < count; i++)
            {
                float w = state.Weights[i];
                if (w <= 0.001f) continue;
                if (total == 0f)
                {
                    Input(node, $"Pose {i + 1}", into, weight * w);
                }
                else
                {
                    other ??= Rent();
                    Input(node, $"Pose {i + 1}", other, weight * w);
                    into.Blend(other, w / (total + w));
                }

                total += w;
            }

            if (other != null) Return(other);
            if (total == 0f) into.CopyFrom(Rig.Rest);
        }

        private void Layer(GraphNode node, Pose into, float weight)
        {
            Input(node, "Base", into, weight);
            float w = Math.Clamp(Number(node, "Weight"), 0f, 1f);
            if (w <= 0.001f) return;

            Pose layer = Rent();
            Input(node, "Layer", layer, weight * w);
            into.Blend(layer, w, Mask(node));
            Return(layer);
        }

        private bool[]? Mask(GraphNode node)
        {
            int key = -node.Id;
            if (_settings.TryGetValue(key, out object? cached)) return cached as bool[];
            bool[]? mask = Rig.MaskFrom(node.Values.GetValueOrDefault("From") as string ?? "");
            _settings[key] = mask;
            return mask;
        }

        private void PlayOnce(GraphNode node, Pose into, float weight)
        {
            NodeState state = State(node);
            Continues(state);
            bool play = Bool(node, "Play");
            if (play && !state.WasPlay)
            {
                state.Playing = true;
                state.Elapsed = 0f;
                state.Length = Linked(node, "Action") is { } action ? LengthOf(action, 0) : 0f;
                if (Linked(node, "Action") is { } restart) Restart(restart, 0);
            }

            state.WasPlay = play;
            Input(node, "Base", into, weight);
            if (!state.Playing) return;

            state.Elapsed += _deltaTime;
            float fadeIn = Number(node, "Fade In"), fadeOut = Number(node, "Fade Out");
            float w = 1f;
            if (fadeIn > 0f) w = MathF.Min(w, state.Elapsed / fadeIn);
            if (fadeOut > 0f) w = MathF.Min(w, (state.Length - state.Elapsed) / fadeOut);
            w = Math.Clamp(w, 0f, 1f);
            if (state.Elapsed >= state.Length)
            {
                state.Playing = false;
                return;
            }

            Pose action2 = Rent();
            // Its events are sent whatever the fade, so an attack's hit isn't missed while it blends in.
            Input(node, "Action", action2, weight);
            into.Blend(action2, w);
            Return(action2);
        }

        /// <summary>How long what a node plays lasts, in seconds: its animation's length at its speed, or the longest of what's linked into it.</summary>
        private float LengthOf(GraphNode node, int depth)
        {
            if (depth > _maxDepth) return 0f;
            if (node.Type == AnimationNodes.Clip) return ClipOf(node) is { } clip ? clip.Length / MathF.Max(MathF.Abs(Number(node, "Speed")), 0.01f) : 0f;

            float longest = 0f;
            foreach (GraphLink link in _graph.Links)
            {
                if (link.ToNode != node.Id || _graph.Node(link.FromNode) is not { } from) continue;
                if (AnimationNodes.Library.Find(from.Type)?.Output(link.FromPort)?.Type != AnimationNodes.Pose) continue;
                longest = MathF.Max(longest, LengthOf(from, depth + 1));
            }

            return longest;
        }

        /// <summary>Makes everything linked into a node start again next time it's in play.</summary>
        private void Restart(GraphNode node, int depth)
        {
            if (depth > _maxDepth) return;
            State(node).LastFrame = int.MinValue;
            foreach (GraphLink link in _graph.Links)
            {
                if (link.ToNode == node.Id && _graph.Node(link.FromNode) is { } from) Restart(from, depth + 1);
            }
        }

        // ---------------------------------------------------------------- Values

        private AnimationClip? ClipOf(GraphNode node)
        {
            if (_settings.TryGetValue(node.Id, out object? cached)) return cached as AnimationClip;
            AnimationClip? clip = PortType.Animation.Convert(node.Values.GetValueOrDefault("Animation")) as AnimationClip;
            _settings[node.Id] = clip;
            return clip;
        }

        private float Number(GraphNode node, string port) => Value(node, port, 0) switch
        {
            float f => f,
            bool b => b ? 1f : 0f,
            int i => i,
            double d => (float)d,
            _ => 0f
        };

        private bool Bool(GraphNode node, string port) => Value(node, port, 0) switch
        {
            bool b => b,
            float f => f != 0f,
            _ => false
        };

        /// <summary>What input <paramref name="port"/> of <paramref name="node"/> is: what's linked to it, worked out, or the value set on the node.</summary>
        private object? Value(GraphNode node, string port, int depth)
        {
            if (Linked(node, port) is { } from && depth < _maxDepth) return Output(from, depth + 1);
            if (node.Values.TryGetValue(port, out object? set) && set != null) return set;
            return AnimationNodes.Library.Find(node.Type)?.Input(port)?.DefaultValue;
        }

        private float NumberAt(GraphNode node, string port, int depth) => Value(node, port, depth) switch
        {
            float f => f,
            bool b => b ? 1f : 0f,
            _ => 0f
        };

        private bool BoolAt(GraphNode node, string port, int depth) => Value(node, port, depth) switch
        {
            bool b => b,
            float f => f != 0f,
            _ => false
        };

        /// <summary>What a value node gives.</summary>
        private object? Output(GraphNode node, int depth)
        {
            switch (node.Type)
            {
                case AnimationNodes.GetParameter:
                    string name = node.Values.GetValueOrDefault("Variable") as string ?? "";
                    object? value = _animator.Get(name);
                    return _graph.Variable(name)?.Type == PortType.Number ? _animator.NumberParameter(name) : value is bool ? value : value ?? 0f;
                case AnimationNodes.Add: return NumberAt(node, "A", depth) + NumberAt(node, "B", depth);
                case AnimationNodes.Subtract: return NumberAt(node, "A", depth) - NumberAt(node, "B", depth);
                case AnimationNodes.Multiply: return NumberAt(node, "A", depth) * NumberAt(node, "B", depth);
                case AnimationNodes.Divide:
                    float divisor = NumberAt(node, "B", depth);
                    return divisor == 0f ? 0f : NumberAt(node, "A", depth) / divisor;
                case AnimationNodes.Clamp:
                    float min = NumberAt(node, "Min", depth), max = NumberAt(node, "Max", depth);
                    return Math.Clamp(NumberAt(node, "Value", depth), MathF.Min(min, max), MathF.Max(min, max));
                case AnimationNodes.Compare:
                    float a = NumberAt(node, "A", depth), b = NumberAt(node, "B", depth);
                    Comparison comparison = PortType.Enum<Comparison>().Convert(node.Values.GetValueOrDefault("Is")) is Comparison chosen ? chosen : Comparison.Greater;
                    return comparison switch
                    {
                        Comparison.Greater => a > b,
                        Comparison.GreaterOrEqual => a >= b,
                        Comparison.Less => a < b,
                        Comparison.LessOrEqual => a <= b,
                        Comparison.Equal => MathF.Abs(a - b) < 1e-4f,
                        _ => MathF.Abs(a - b) >= 1e-4f
                    };
                case AnimationNodes.And: return BoolAt(node, "A", depth) && BoolAt(node, "B", depth);
                case AnimationNodes.Or: return BoolAt(node, "A", depth) || BoolAt(node, "B", depth);
                case AnimationNodes.Not: return !BoolAt(node, "Value", depth);
                default: return null;
            }
        }
    }
}
