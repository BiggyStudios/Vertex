using System.Numerics;

using Vertex.Graphs;

namespace Vertex.Audio
{
    /// <summary>How a Random node in an audio graph chooses which of its sounds to play.</summary>
    public enum AudioChoice
    {
        /// <summary>Any of them, each time: the same one can come twice in a row.</summary>
        Random,

        /// <summary>Any of them, but never the one it played last time.</summary>
        NoRepeats,

        /// <summary>Each of them once, in a random order, before any plays again.</summary>
        Shuffle,

        /// <summary>One after another, in order: 1, 2, 3, then 1 again.</summary>
        InTurn
    }

    /// <summary>
    /// The nodes an audio graph can have. An audio graph (a .audiograph file) describes a sound made of sounds: Sound nodes
    /// play files, and what they're linked into decides which play, when and how. A Random node plays one of any number of
    /// sounds, Random Pitch changes the pitch a little each time, Sequence plays one after another, Layer plays them
    /// together, Loop plays again and again. What reaches the Output is heard.
    /// <para>
    /// Each time the graph's played, it's worked out again from the Output back: a different random sound, a different
    /// pitch. An <see cref="AudioSource"/> plays one on an object; <see cref="Sound.Play(Graph, float, float)"/> plays one
    /// from code.
    /// </para>
    /// </summary>
    public static class AudioNodes
    {
        public const string Kind = "audio";

        /// <summary>The extension of audio graph files.</summary>
        public const string Extension = ".audiograph";

        /// <summary>What links sounds together: what's played, from a Sound node towards the Output.</summary>
        public static readonly PortType Audio = new("Audio", null, new Color(0.34f, 0.8f, 0.86f));

        private sealed class AudioLibrary() : NodeLibrary(AudioNodes.Kind, "Audio Graph")
        {
            public override Color CategoryColor(string category) => category switch
            {
                "Output" => new Color(0.86f, 0.42f, 0.2f),
                "Sounds" => new Color(0.28f, 0.66f, 0.74f),
                "Choose" => new Color(0.82f, 0.62f, 0.22f),
                "Combine" or "Timing" => new Color(0.3f, 0.58f, 0.86f),
                "Change" => new Color(0.75f, 0.42f, 0.82f),
                "Properties" => new Color(0.7f, 0.45f, 0.85f),
                _ => new Color(0.33f, 0.62f, 0.4f)
            };

            public override (string One, string Several, string Note) VariableWords => ("Property", "Properties", "an Audio Source can set them");
        }

        /// <summary>Every kind of node an audio graph can have.</summary>
        public static NodeLibrary Library { get; } = Build();

        // The nodes' ids, for what plays them (AudioPlayback).
        internal const string Output = "Output/Output";
        internal const string SoundFile = "Sounds/Sound";
        internal const string Random = "Choose/Random";
        internal const string Switch = "Choose/Switch";
        internal const string Chance = "Choose/Chance";
        internal const string Sequence = "Combine/Sequence";
        internal const string Layer = "Combine/Layer";
        internal const string Delay = "Timing/Delay";
        internal const string Loop = "Timing/Loop";
        internal const string Volume = "Change/Volume";
        internal const string Pitch = "Change/Pitch";
        internal const string RandomPitch = "Change/Random Pitch";
        internal const string RandomVolume = "Change/Random Volume";
        internal const string FadeIn = "Change/Fade In";
        internal const string RandomNumber = "Math/Random Number";
        internal const string Add = "Math/Add";
        internal const string Multiply = "Math/Multiply";
        internal const string GetProperty = "Properties/Get Property";

        private static Port In(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Tooltip: tooltip);
        private static Port Setting(string name, PortType type, object? value = null, string? tooltip = null) => new(name, type, value, Linkable: false, Tooltip: tooltip);
        private static Port SoundIn(string tooltip = "The sound it changes") => new("Sound", Audio, Tooltip: tooltip);
        private static Port SoundOut => new("Sound", Audio);
        private static Port Sounds(string tooltip) => new("Sound", Audio, Tooltip: tooltip);

        private static NodeLibrary Build()
        {
            var library = new AudioLibrary();
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

            Node(Output, [In("Sound", Audio, tooltip: "What's heard")], [],
                "What's heard when the graph's played. Each time, everything linked into it is worked out again: a different random sound, a different pitch.",
                "end result final play out");

            // ---------------------------------------------------------------- Sounds

            Node(SoundFile, [Setting("Sound", PortType.Sound, tooltip: "A sound file of the project's (a .wav)"), In("Volume", PortType.Number, 1f, "How loud, 1 as it was recorded")], [SoundOut],
                "Plays a sound file. Drag sounds from the Assets panel onto the graph to make these.", "clip wav file sample audio");

            // ---------------------------------------------------------------- Choose

            Node(Random, [Setting("Mode", PortType.Enum<AudioChoice>(), AudioChoice.NoRepeats,
                    "Random: any. No Repeats: never the last one again. Shuffle: each once before any again. In Turn: one after another")], [SoundOut],
                "Plays one of the sounds linked to it, a different one each time: footsteps, gunshots, voices. Link as many as you like.",
                "choose pick variation shuffle cycle round robin no repeat", Sounds("One of the sounds to choose from"));

            Node(Switch, [In("Which", PortType.Number, 1f, "Which sound plays: 1 for Sound 1. Link a property, so the game can choose")], [SoundOut],
                "Plays the sound whose number it's given: a footstep for each kind of ground, chosen by a property the game sets.",
                "select index choose surface", Sounds("One of the sounds to choose from"));

            Node(Chance, [SoundIn("The sound that might play"), In("Chance", PortType.Number, 0.5f, "How likely it is to play, from 0 (never) to 1 (always)")], [SoundOut],
                "Plays the sound only some of the time: a rare extra rattle, a bird that doesn't always sing.", "probability sometimes maybe percent odds");

            // ---------------------------------------------------------------- Combine

            Node(Sequence, [In("Gap", PortType.Number, 0f, "Seconds between one ending and the next starting")], [SoundOut],
                "Plays the sounds linked to it one after another, each starting when the one before has finished.",
                "concatenate then after order chain", Sounds("Played in turn, from 1"));

            Node(Layer, [], [SoundOut],
                "Plays all the sounds linked to it at once: a gunshot's crack, body and echo, mixed.", "mix together combine stack all", Sounds("Played with the others"));

            // ---------------------------------------------------------------- Timing

            Node(Delay, [SoundIn("The sound to play later"), In("Seconds", PortType.Number, 0.25f, "How long to wait first")], [SoundOut],
                "Waits a while, then plays the sound.", "wait later after pause");

            Node(Loop, [SoundIn("The sound to play again and again"), In("Times", PortType.Number, 0f, "How many times it plays: 0 for until it's stopped"),
                    In("Gap", PortType.Number, 0f, "Seconds between one time ending and the next starting")], [SoundOut],
                "Plays the sound again each time it finishes, choosing again each time (a Random linked into it picks a new one).",
                "repeat again forever ambience ambient");

            // ---------------------------------------------------------------- Change

            Node(Volume, [SoundIn(), In("Volume", PortType.Number, 1f, "1 leaves it as it is, 0.5 is half as loud")], [SoundOut],
                "Makes the sound louder or quieter.", "gain loudness quieter louder level");

            Node(Pitch, [SoundIn(), In("Pitch", PortType.Number, 1f, "1 leaves it as it is, 2 is an octave higher (and twice as fast)")], [SoundOut],
                "Makes the sound higher or lower (and faster or slower with it).", "speed higher lower tone");

            Node(RandomPitch, [SoundIn(), In("Min", PortType.Number, 0.9f, "The lowest it can be"), In("Max", PortType.Number, 1.1f, "The highest it can be")], [SoundOut],
                "A different pitch each time it's played, between Min and Max: so a sound played over and over doesn't sound the same each time.",
                "variation vary random pitch shift");

            Node(RandomVolume, [SoundIn(), In("Min", PortType.Number, 0.8f, "The quietest it can be"), In("Max", PortType.Number, 1f, "The loudest it can be")], [SoundOut],
                "A different volume each time it's played, between Min and Max.", "variation vary random gain loudness");

            Node(FadeIn, [SoundIn(), In("Seconds", PortType.Number, 1f, "How long it takes to reach full volume")], [SoundOut],
                "Starts the sound silent and brings it up to full volume: for ambience and music.", "fade ramp swell");

            // ---------------------------------------------------------------- Numbers

            Node(RandomNumber, [In("Min", PortType.Number, 0f), In("Max", PortType.Number, 1f)], [new Port("Value", PortType.Number)],
                "A random number between Min and Max, a new one each time the graph's played.", "random range between");

            Node(Add, [In("A", PortType.Number), In("B", PortType.Number)], [new Port("Result", PortType.Number)], "A + B.", "plus sum");
            Node(Multiply, [In("A", PortType.Number, 1f), In("B", PortType.Number, 1f)], [new Port("Result", PortType.Number)], "A × B.", "times product scale");

            Node(GetProperty, [new Port("Variable", PortType.Text, "", Linkable: false) { IsVariableName = true }], [new Port("Value", PortType.Any) { TypeFromVariable = true }],
                "One of the graph's properties (its variables): an Audio Source can set them for itself, and the game can change them with SetProperty.",
                "variable parameter exposed");

            return library;
        }

        /// <summary>
        /// A new audio graph to start from: a Random node, ready for sounds to be dropped onto it, played at a slightly
        /// different pitch each time.
        /// </summary>
        public static Graph Starter()
        {
            var graph = new Graph(Kind);
            GraphNode random = graph.AddNode(Random, new Vector2(0f, 0f));
            GraphNode pitch = graph.AddNode(RandomPitch, new Vector2(300f, 0f));
            GraphNode output = graph.AddNode(Output, new Vector2(580f, 0f));
            graph.Link(random.Id, "Sound", pitch.Id, "Sound");
            graph.Link(pitch.Id, "Sound", output.Id, "Sound");
            return graph;
        }
    }
}
