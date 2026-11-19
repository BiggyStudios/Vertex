using Vertex.Graphs;
using Vertex.Scripting;

namespace Vertex.Audio
{
    /// <summary>
    /// Plays a sound from its object: a single sound file, or an audio graph (a .audiograph file, made in the editor) that
    /// picks one of several sounds at a random pitch, layers them, loops them. The sound follows the object as it moves,
    /// and gets quieter further from the camera.
    /// <para>
    /// <see cref="Play"/> plays it (again: what's still sounding carries on, so quick repeats overlap, as gunshots and
    /// footsteps should); <see cref="Stop"/> stops it.
    /// </para>
    /// </summary>
    public sealed class AudioSource : Component
    {
        /// <summary>A sound file to play. An <see cref="AudioGraph"/> plays instead, if one's chosen.</summary>
        [Tooltip("A sound file to play. An Audio Graph plays instead, if one's chosen")]
        public AudioClip? Sound { get; set; }

        /// <summary>An audio graph to play: a .audiograph file, made in the editor.</summary>
        [Tooltip("An audio graph to play (a .audiograph file, made in the editor): random sounds, random pitches, layers, loops")]
        [GraphKind(AudioNodes.Kind)]
        public Graph? AudioGraph { get; set; }

        [Tooltip("How loud, on top of the sound itself. Changes what's playing too")]
        [Range(0f, 1f)]
        public float Volume { get; set; } = 1f;

        [Tooltip("How high, on top of the sound itself: 2 is an octave up. Changes what's playing too")]
        [Range(0.1f, 3f)]
        public float Pitch { get; set; } = 1f;

        [Tooltip("Plays as soon as the game starts")]
        public bool PlayOnStart { get; set; } = true;

        [Tooltip("Plays again each time it ends, until it's stopped (an audio graph chooses again each time)")]
        public bool Loop { get; set; }

        [Tooltip("Comes from the object, quieter further away. Off, it's the same everywhere: music, the player's own sounds")]
        public bool InWorld { get; set; } = true;

        [Tooltip("Within this many metres it plays at full volume; beyond, it halves each time the distance doubles")]
        public float FullVolumeDistance { get; set; } = 3f;

        /// <summary>This object's own values for the audio graph's properties, in place of the graph's. Set in the inspector, under the source.</summary>
        [HideInInspector]
        public List<ScriptVariableValue> Properties { get; set; } = new();

        private readonly List<AudioPlayback> _playing = new();
        private readonly Dictionary<string, object?> _set = new();
        private bool _looping;

        /// <summary>Whether anything it played is still sounding (or still to come, in a graph's delay or loop).</summary>
        public bool IsPlaying => _playing.Any(p => p.IsPlaying);

        /// <summary>
        /// Plays its sound or audio graph from the start. What's still sounding from before carries on. Returns what's
        /// playing, to stop or change on its own; null if there's nothing to play.
        /// </summary>
        public AudioPlayback? Play()
        {
            AudioPlayback? playback = AudioGraph != null ? new AudioPlayback(AudioGraph)
                : Sound != null ? new AudioPlayback(Sound) { LoopClip = Loop }
                : null;
            if (playback == null) return null;

            playback.Volume = Volume;
            playback.Pitch = Pitch;
            playback.Position = InWorld ? Transform.WorldPosition : null;
            playback.FullVolumeDistance = FullVolumeDistance;
            if (AudioGraph != null)
            {
                foreach (ScriptVariableValue own in Properties)
                {
                    if (AudioGraph.Variable(own.Name) is { } variable) playback.SetProperty(own.Name, own.Get(variable.Type));
                }

                foreach ((string name, object? value) in _set) playback.SetProperty(name, value);
            }

            _looping = Loop;
            _playing.Add(Audio.Sound.Start(playback));
            return playback;
        }

        /// <summary>Stops everything it's playing, and stops looping.</summary>
        public void Stop()
        {
            _looping = false;
            foreach (AudioPlayback playback in _playing) playback.Stop();
            _playing.Clear();
        }

        /// <summary>
        /// Sets one of the audio graph's properties (its variables): a number for a Switch to choose a footstep by, say.
        /// What's playing uses it from its next choice on, and so does everything played after.
        /// </summary>
        public void SetProperty(string name, object? value)
        {
            _set[name] = value;
            foreach (AudioPlayback playback in _playing) playback.SetProperty(name, value);
        }

        protected internal override void Start()
        {
            if (PlayOnStart) Play();
        }

        protected internal override void Update()
        {
            _playing.RemoveAll(p => !p.IsPlaying);
            foreach (AudioPlayback playback in _playing)
            {
                playback.Volume = Volume;
                playback.Pitch = Pitch;
                playback.Position = InWorld ? Transform.WorldPosition : null;
            }

            // An audio graph set to loop starts again when it's over, choosing again. (A single sound loops on its own.)
            if (_looping && _playing.Count == 0 && AudioGraph != null) Play();
        }

        protected internal override void OnDestroy() => Stop();
    }
}
