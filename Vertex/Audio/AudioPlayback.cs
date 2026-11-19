using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Graphs;

namespace Vertex.Audio
{
    /// <summary>
    /// An audio graph (or a single clip) being played: started by an <see cref="AudioSource"/> or by
    /// <see cref="Sound.Play(Graph, float, float)"/>. It works the graph out from its Output back as it plays: a Random
    /// node chooses as it starts, a Sequence starts its next sound when the one before finishes, a Loop chooses again
    /// each time round. Its <see cref="Volume"/>, <see cref="Pitch"/> and <see cref="Position"/> can change while it plays.
    /// </summary>
    public sealed class AudioPlayback
    {
        /// <summary>The graph it plays; null when it plays a single clip.</summary>
        public Graph? Graph { get; }

        /// <summary>The clip it plays, when it isn't playing a graph.</summary>
        public AudioClip? Clip { get; }

        /// <summary>How loud, on top of what the graph says: 1 as the graph has it. Can change while it plays.</summary>
        public float Volume { get; set; } = 1f;

        /// <summary>How high, on top of what the graph says: 1 as the graph has it. Can change while it plays.</summary>
        public float Pitch { get; set; } = 1f;

        /// <summary>Where it is in the world (it gets quieter further away); null to play the same everywhere.</summary>
        public Vector3? Position { get; set; }

        /// <summary>Within this distance it plays at full volume; beyond, it halves each time the distance doubles.</summary>
        public float FullVolumeDistance { get; set; } = 3f;

        /// <summary>Plays a single clip over and over (for an <see cref="AudioSource"/> set to loop with no graph).</summary>
        internal bool LoopClip { get; init; }

        private readonly Dictionary<string, object?> _properties = new();
        private Part? _root;
        private bool _stopped;

        public AudioPlayback(Graph audioGraph)
        {
            Graph = audioGraph;
        }

        public AudioPlayback(AudioClip clip)
        {
            Clip = clip;
        }

        /// <summary>Whether anything's still playing, or still to come (a delay that hasn't ended, a loop).</summary>
        public bool IsPlaying => !_stopped && _root != null && !_root.Done;

        /// <summary>
        /// Sets one of the graph's properties (its variables) for this playback: Get Property nodes read it from then on,
        /// as they're next worked out (the next sound in a sequence, the next time round a loop).
        /// </summary>
        public void SetProperty(string name, object? value)
        {
            _properties[name] = Graph?.Variable(name) is { } variable ? variable.Type.Convert(value) : value;
        }

        /// <summary>Stops everything it's playing, and what was still to come.</summary>
        public void Stop()
        {
            _stopped = true;
            _root?.Stop();
        }

        /// <summary>Works out what plays first, and starts it.</summary>
        internal void Begin()
        {
            if (Clip != null)
            {
                _root = new ClipPart(this, Clip, 1f, 1f, LoopClip);
            }
            else if (Graph != null && Graph.Nodes.FirstOrDefault(n => n.Type == AudioNodes.Output) is { } output)
            {
                Graph.Ran(output.Id);
                _root = BuildInput(output, "Sound", 1f, 1f);
            }
            else
            {
                _root = new Silence();
            }

            _root.Start(1f);
        }

        /// <summary>Moves it on by <paramref name="deltaTime"/> seconds. False once it's over.</summary>
        internal bool Update(float deltaTime)
        {
            if (_stopped || _root == null) return false;
            _root.Update(deltaTime, 1f);
            return !_root.Done;
        }

        // ---------------------------------------------------------------- Working out the graph

        /// <summary>What's linked into input <paramref name="port"/> of <paramref name="node"/>, made into something to play.</summary>
        private Part BuildInput(GraphNode node, string port, float volume, float pitch)
        {
            Graph graph = Graph!;
            GraphLink? link = graph.LinksTo(node.Id, port).FirstOrDefault();
            return link != null && graph.Node(link.FromNode) is { } from ? Build(from, volume, pitch) : new Silence();
        }

        /// <summary>
        /// What node <paramref name="node"/> plays, worked out now: a random choice made, a random pitch picked. Volume and
        /// pitch from the nodes it's linked through are passed in.
        /// </summary>
        private Part Build(GraphNode node, float volume, float pitch)
        {
            Graph graph = Graph!;
            try
            {
                graph.Ran(node.Id);
                switch (node.Type)
                {
                    case AudioNodes.SoundFile:
                        AudioClip clip = PortType.Sound.Convert(node.Values.GetValueOrDefault("Sound")) as AudioClip ?? throw new InvalidOperationException("No sound chosen");
                        return new ClipPart(this, clip, volume * Number(node, "Volume"), pitch, loop: false);

                    case AudioNodes.Random:
                        return Repeated(node, Choose(node), volume, pitch);

                    case AudioNodes.Switch:
                    {
                        List<int> linked = Linked(node);
                        if (linked.Count == 0) return new Silence();
                        int which = (int)MathF.Round(Number(node, "Which"));
                        int highest = linked.Max();
                        return Repeated(node, Math.Clamp(which, 1, highest), volume, pitch);
                    }

                    case AudioNodes.Chance:
                        return System.Random.Shared.NextSingle() < Number(node, "Chance") ? BuildInput(node, "Sound", volume, pitch) : new Silence();

                    case AudioNodes.Sequence:
                    {
                        float gap = MathF.Max(Number(node, "Gap"), 0f);
                        List<Func<Part>> steps = Linked(node).Select(index => (Func<Part>)(() => Repeated(node, index, volume, pitch))).ToList();
                        return new SequencePart(steps, gap);
                    }

                    case AudioNodes.Layer:
                        return new LayerPart(Linked(node).Select(index => Repeated(node, index, volume, pitch)).ToList());

                    case AudioNodes.Delay:
                        return new SequencePart([() => new Silence(), () => BuildInput(node, "Sound", volume, pitch)], MathF.Max(Number(node, "Seconds"), 0f));

                    case AudioNodes.Loop:
                    {
                        int times = Math.Max((int)MathF.Round(Number(node, "Times")), 0);
                        // Worked out again each time round: a gap linked to a Random Number is different each time.
                        float Gap() => MathF.Max(Number(node, "Gap"), 0f);

                        // A single sound file looped forever with no gap loops seamlessly, on the voice itself.
                        if (times == 0 && !graph.LinksTo(node.Id, "Gap").Any() && Gap() == 0f && graph.LinksTo(node.Id, "Sound").FirstOrDefault() is { } link &&
                            graph.Node(link.FromNode) is { Type: AudioNodes.SoundFile } file)
                        {
                            graph.Ran(file.Id);
                            AudioClip looped = PortType.Sound.Convert(file.Values.GetValueOrDefault("Sound")) as AudioClip ?? throw new InvalidOperationException("No sound chosen");
                            return new ClipPart(this, looped, volume * Number(file, "Volume"), pitch, loop: true);
                        }

                        return new LoopPart(() => BuildInput(node, "Sound", volume, pitch), times, Gap);
                    }

                    case AudioNodes.Volume:
                        return BuildInput(node, "Sound", volume * MathF.Max(Number(node, "Volume"), 0f), pitch);

                    case AudioNodes.Pitch:
                        return BuildInput(node, "Sound", volume, pitch * Number(node, "Pitch"));

                    case AudioNodes.RandomPitch:
                        return BuildInput(node, "Sound", volume, pitch * Between(Number(node, "Min"), Number(node, "Max")));

                    case AudioNodes.RandomVolume:
                        return BuildInput(node, "Sound", volume * MathF.Max(Between(Number(node, "Min"), Number(node, "Max")), 0f), pitch);

                    case AudioNodes.FadeIn:
                        return new FadePart(BuildInput(node, "Sound", volume, pitch), MathF.Max(Number(node, "Seconds"), 0f));

                    default:
                        return new Silence();
                }
            }
            catch (Exception exception)
            {
                // Marked on the node for the editor, and nothing plays from it: one missing sound doesn't stop the rest.
                graph.Failed(node.Id, exception.Message);
                return new Silence();
            }
        }

        /// <summary>The repeated input <paramref name="index"/> of a node ("Sound 3"), made into something to play.</summary>
        private Part Repeated(GraphNode node, int index, float volume, float pitch) => index > 0 ? BuildInput(node, $"Sound {index}", volume, pitch) : new Silence();

        /// <summary>Which of a node's repeated inputs have something linked, by number, in order.</summary>
        private List<int> Linked(GraphNode node)
        {
            NodeType? type = Graph!.Library?.Find(node.Type);
            var linked = new SortedSet<int>();
            foreach (GraphLink link in Graph.Links)
            {
                if (link.ToNode == node.Id && type?.RepeatIndex(link.ToPort) is int index) linked.Add(index);
            }

            return linked.ToList();
        }

        /// <summary>
        /// Which of a Random node's sounds plays this time, by its mode. What it played before is kept with the graph, so
        /// every object playing it takes turns together (footsteps don't repeat even across two players).
        /// </summary>
        private int Choose(GraphNode node)
        {
            List<int> linked = Linked(node);
            if (linked.Count == 0) return 0;
            if (linked.Count == 1) return linked[0];

            var mode = (AudioChoice)PortType.Enum<AudioChoice>().Convert(node.Values.GetValueOrDefault("Mode") ?? AudioChoice.NoRepeats)!;
            ChoiceState state = _choices.GetOrCreateValue(Graph!).For(node.Id);
            int chosen;
            switch (mode)
            {
                case AudioChoice.Random:
                    chosen = linked[System.Random.Shared.Next(linked.Count)];
                    break;

                case AudioChoice.NoRepeats:
                    do chosen = linked[System.Random.Shared.Next(linked.Count)];
                    while (chosen == state.Last);
                    break;

                case AudioChoice.Shuffle:
                    // A fresh shuffle once each has had its turn (or the sounds linked have changed), not starting with
                    // the one that ended the last.
                    state.Bag.RemoveAll(i => !linked.Contains(i));
                    if (state.Bag.Count == 0)
                    {
                        state.Bag.AddRange(linked.OrderBy(_ => System.Random.Shared.Next()));
                        if (state.Bag[0] == state.Last) (state.Bag[0], state.Bag[^1]) = (state.Bag[^1], state.Bag[0]);
                    }

                    chosen = state.Bag[0];
                    state.Bag.RemoveAt(0);
                    break;

                default:
                    chosen = linked.FirstOrDefault(i => i > state.Last, linked[0]);
                    break;
            }

            state.Last = chosen;
            return chosen;
        }

        private sealed class ChoiceState
        {
            public int Last;
            public readonly List<int> Bag = new();
        }

        private sealed class GraphChoices
        {
            private readonly Dictionary<int, ChoiceState> _nodes = new();
            public ChoiceState For(int node) => _nodes.TryGetValue(node, out ChoiceState? state) ? state : _nodes[node] = new ChoiceState();
        }

        // Kept per graph, and let go of with it.
        private static readonly ConditionalWeakTable<Graph, GraphChoices> _choices = new();

        private static float Between(float min, float max) => min + System.Random.Shared.NextSingle() * (max - min);

        /// <summary>A number input: what's linked into it worked out now, or the value set on the node.</summary>
        private float Number(GraphNode node, string port) => (float)PortType.Number.Convert(Value(node, port))!;

        private object? Value(GraphNode node, string port)
        {
            Graph graph = Graph!;
            if (graph.LinksTo(node.Id, port).FirstOrDefault() is { } link && graph.Node(link.FromNode) is { } from) return Output(from);
            if (node.Values.TryGetValue(port, out object? value)) return value;
            return graph.Library?.Find(node.Type)?.Input(port)?.DefaultValue;
        }

        /// <summary>What a number node gives: worked out each time it's asked for (a random number is new each time).</summary>
        private object? Output(GraphNode node)
        {
            Graph!.Ran(node.Id);
            switch (node.Type)
            {
                case AudioNodes.RandomNumber: return Between(Number(node, "Min"), Number(node, "Max"));
                case AudioNodes.Add: return Number(node, "A") + Number(node, "B");
                case AudioNodes.Multiply: return Number(node, "A") * Number(node, "B");
                case AudioNodes.GetProperty:
                    string name = node.Values.GetValueOrDefault("Variable") as string ?? "";
                    return _properties.TryGetValue(name, out object? set) ? set : Graph!.Variable(name)?.Value;
                default: return null;
            }
        }

        // ---------------------------------------------------------------- What's playing

        /// <summary>
        /// Something playing, or waiting to: a clip, or a step of the graph made of others. Moved on each frame with the
        /// volume the steps around it give it now (a fade).
        /// </summary>
        private abstract class Part
        {
            public bool Done { get; protected set; }
            public abstract void Start(float volume);
            public abstract void Update(float deltaTime, float volume);
            public abstract void Stop();
        }

        /// <summary>Nothing: a Random with no sounds, a Chance that didn't come up.</summary>
        private sealed class Silence : Part
        {
            public override void Start(float volume) => Done = true;
            public override void Update(float deltaTime, float volume) { }
            public override void Stop() => Done = true;
        }

        /// <summary>One clip on a voice, following the playback's volume, pitch and position.</summary>
        private sealed class ClipPart(AudioPlayback playback, AudioClip clip, float volume, float pitch, bool loop) : Part
        {
            private PlayingSound _sound;
            private float _fade = 1f;

            private float Gain => volume * _fade * MathF.Max(playback.Volume, 0f);
            private float Speed => pitch * playback.Pitch;

            public override void Start(float fade)
            {
                _fade = fade;
                _sound = Sound.Voices.Start(clip, playback.Position, Gain, Speed, playback.FullVolumeDistance, loop);
                Done = !_sound.IsPlaying;
            }

            public override void Update(float deltaTime, float volume)
            {
                if (Done) return;
                if (!_sound.IsPlaying)
                {
                    Done = true;
                    return;
                }

                _fade = volume;
                _sound.Set(playback.Position, Gain, Speed);
            }

            public override void Stop()
            {
                _sound.Stop();
                Done = true;
            }
        }

        /// <summary>One thing after another, with a gap between: a Sequence, and a Delay (silence, then the sound).</summary>
        private sealed class SequencePart(List<Func<Part>> steps, float gap) : Part
        {
            private int _next;
            private Part? _current;
            private float _waiting;

            public override void Start(float volume) => Advance(volume);

            public override void Update(float deltaTime, float volume)
            {
                if (Done) return;
                _current?.Update(deltaTime, volume);
                if (_current is { Done: false }) return;

                // The last one's over: so is the sequence, without waiting a gap after it.
                if (_next >= steps.Count)
                {
                    Done = true;
                    return;
                }

                // Between steps: the gap, then the next.
                _waiting += deltaTime;
                if (_waiting >= gap) Advance(volume);
            }

            private void Advance(float volume)
            {
                // Steps that are over at once (silence) don't wait a frame each, unless there's a gap to wait.
                while (_next < steps.Count)
                {
                    _current = steps[_next++]();
                    _current.Start(volume);
                    _waiting = 0f;
                    if (!_current.Done || gap > 0f && _next < steps.Count) return;
                }

                Done = _current?.Done ?? true;
            }

            public override void Stop()
            {
                _current?.Stop();
                _next = steps.Count;
                Done = true;
            }
        }

        /// <summary>Several at once: done when all are.</summary>
        private sealed class LayerPart(List<Part> parts) : Part
        {
            public override void Start(float volume)
            {
                foreach (Part part in parts) part.Start(volume);
                Done = parts.All(p => p.Done);
            }

            public override void Update(float deltaTime, float volume)
            {
                foreach (Part part in parts) part.Update(deltaTime, volume);
                Done = parts.All(p => p.Done);
            }

            public override void Stop()
            {
                foreach (Part part in parts) part.Stop();
                Done = true;
            }
        }

        /// <summary>The same step again each time it ends, worked out afresh each time; forever when <paramref name="times"/> is 0.</summary>
        private sealed class LoopPart(Func<Part> make, int times, Func<float> gap) : Part
        {
            private Part? _current;
            private int _played;
            private float _waiting;
            private float _gap;

            public override void Start(float volume) => Next(volume);

            // Goes round at most once a frame, so a loop of nothing (a Chance that didn't come up) waits a frame rather
            // than going round forever at once.
            private void Next(float volume)
            {
                if (times > 0 && _played >= times)
                {
                    Done = true;
                    return;
                }

                _played++;
                _waiting = 0f;
                _gap = gap();
                _current = make();
                _current.Start(volume);
            }

            public override void Update(float deltaTime, float volume)
            {
                if (Done) return;
                _current?.Update(deltaTime, volume);
                if (_current is { Done: false }) return;

                _waiting += deltaTime;
                if (_waiting >= _gap) Next(volume);
            }

            public override void Stop()
            {
                _current?.Stop();
                Done = true;
            }
        }

        /// <summary>Brings what it plays up from silence over some seconds.</summary>
        private sealed class FadePart(Part inner, float seconds) : Part
        {
            private float _time;

            private float Faded => seconds > 0f ? Math.Clamp(_time / seconds, 0f, 1f) : 1f;

            public override void Start(float volume)
            {
                inner.Start(volume * Faded);
                Done = inner.Done;
            }

            public override void Update(float deltaTime, float volume)
            {
                _time += deltaTime;
                inner.Update(deltaTime, volume * Faded);
                Done = inner.Done;
            }

            public override void Stop()
            {
                inner.Stop();
                Done = true;
            }
        }
    }
}
