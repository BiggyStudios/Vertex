using System.Numerics;

using Vertex.Audio.OpenAL;
using Vertex.Graphs;
using Vertex.Rendering;

namespace Vertex.Audio
{
    /// <summary>
    /// Plays sounds. You hear from the scene's <see cref="AudioListener"/>, or <see cref="Camera.Main"/> if there's none:
    /// sounds placed with <see cref="PlayAt(AudioClip, Vector3, float, float, float)"/> get quieter with distance and come
    /// from their direction as the listener turns. If the computer has no audio device, nothing plays.
    /// <para>
    /// Besides single clips, it plays audio graphs (.audiograph files, made in the editor): a random one of several sounds
    /// at a random pitch, layers, sequences, loops. An <see cref="AudioSource"/> plays either on an object.
    /// </para>
    /// </summary>
    public static class Sound
    {
        private static OpenALDevice? _device;

        /// <summary>
        /// What actually makes the sound: the audio device, or a stand-in in tests (which play without one, and check
        /// what would have been heard).
        /// </summary>
        internal static IVoices Voices { get; set; } = new DeviceVoices();

        private static readonly List<AudioPlayback> _playbacks = new();

        /// <summary>Overall loudness, 0 to 1.</summary>
        public static float Volume { get; set; } = 1f;

        public static bool IsAvailable => _device != null;

        /// <summary>
        /// Where sound is heard from: the first switched-on <see cref="AudioListener"/> in the scene, or the main camera
        /// if there's none. Null if there's neither.
        /// </summary>
        public static Transform? Listener =>
            Scene.Active.FindComponent<AudioListener>()?.Transform ?? Camera.Main?.Transform;

        // Where the listener was last frame, for how fast it's moving (Doppler shifts are worked out from that).
        private static Vector3? _lastListenerPosition;
        private static Vector3 _listenerVelocity;

        /// <summary>Plays a clip once, the same in both ears: for menus, or the player's own gun.</summary>
        public static PlayingSound Play(AudioClip clip, float volume = 1f, float pitch = 1f) => Voices.Start(clip, SoundPlacement.Everywhere, volume, pitch, loop: false);

        /// <summary>
        /// Plays a mono clip once at a point in the world. Within <paramref name="fullVolumeDistance"/> it plays at full
        /// volume; beyond that it halves in loudness each time the distance doubles, like sound in open air.
        /// </summary>
        public static PlayingSound PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float fullVolumeDistance = 3f) =>
            PlayAt(clip, position, SpatialSettings.Within(fullVolumeDistance), volume, pitch);

        /// <summary>
        /// Plays a mono clip once at a point in the world, heard as <paramref name="spatial"/> says: how it fades with distance,
        /// how much it Doppler-shifts.
        /// </summary>
        public static PlayingSound PlayAt(AudioClip clip, Vector3 position, SpatialSettings spatial, float volume = 1f, float pitch = 1f) =>
            Voices.Start(clip, SoundPlacement.At(position, spatial), volume, pitch, loop: false);

        /// <summary>
        /// Plays an audio graph (a .audiograph file, loaded with <see cref="Assets.LoadGraph"/>), the same in both ears.
        /// Each time it's played its nodes choose again: a different random sound, a different pitch.
        /// </summary>
        public static AudioPlayback Play(Graph audioGraph, float volume = 1f, float pitch = 1f) => Start(new AudioPlayback(audioGraph) { Volume = volume, Pitch = pitch });

        /// <summary>Plays an audio graph at a point in the world, as <see cref="PlayAt(AudioClip, Vector3, float, float, float)"/> plays a clip.</summary>
        public static AudioPlayback PlayAt(Graph audioGraph, Vector3 position, float volume = 1f, float pitch = 1f, float fullVolumeDistance = 3f) =>
            PlayAt(audioGraph, position, SpatialSettings.Within(fullVolumeDistance), volume, pitch);

        /// <summary>Plays an audio graph at a point in the world, heard as <paramref name="spatial"/> says.</summary>
        public static AudioPlayback PlayAt(Graph audioGraph, Vector3 position, SpatialSettings spatial, float volume = 1f, float pitch = 1f) =>
            Start(new AudioPlayback(audioGraph) { Position = position, Volume = volume, Pitch = pitch, Spatial = spatial });

        /// <summary>Starts a playback and keeps it going (it's moved on each frame) until it's done or stopped.</summary>
        internal static AudioPlayback Start(AudioPlayback playback)
        {
            playback.Begin();
            if (playback.IsPlaying) _playbacks.Add(playback);
            return playback;
        }

        /// <summary>The audio graphs playing now.</summary>
        public static IReadOnlyList<AudioPlayback> Playbacks => _playbacks;

        /// <summary>Stops everything playing: every clip and audio graph.</summary>
        public static void StopAll()
        {
            foreach (AudioPlayback playback in _playbacks.ToList()) playback.Stop();
            _playbacks.Clear();
            Voices.StopAll();
        }

        internal static void Initialise()
        {
            try
            {
                _device = new OpenALDevice();
            }
            catch (Exception exception)
            {
                Log.Warning($"No audio: {exception.Message}");
            }
        }

        /// <summary>Moves the listener to the camera, and audio graphs on. Called once per frame.</summary>
        internal static void Update()
        {
            UpdatePlaybacks(Time.DeltaTime);
            if (_device == null) return;

            _device.SetVolume(Volume);

            if (Listener is not { } listener)
            {
                _lastListenerPosition = null;
                return;
            }

            Vector3 position = listener.WorldPosition;
            float deltaTime = Time.DeltaTime;
            _listenerVelocity = _lastListenerPosition is { } last && deltaTime > 0f ? (position - last) / deltaTime : Vector3.Zero;
            _lastListenerPosition = position;
            _device.SetListener(position, listener.Forward, listener.Up, _listenerVelocity);
        }

        /// <summary>
        /// Moves the audio graphs playing on by <paramref name="deltaTime"/> seconds: the next sound in a sequence, a
        /// delay ending. The editor calls it while the game isn't running, so graphs can be heard as they're made.
        /// </summary>
        internal static void UpdatePlaybacks(float deltaTime)
        {
            for (int i = _playbacks.Count - 1; i >= 0; i--)
            {
                AudioPlayback playback = _playbacks[i];
                if (!playback.Update(deltaTime)) _playbacks.RemoveAt(i);
            }
        }

        internal static void Shutdown()
        {
            StopAll();
            _device?.Dispose();
            _device = null;
        }

        /// <summary>Sounds through the audio device; when there isn't one, sounds start and are over at once.</summary>
        private sealed class DeviceVoices : IVoices
        {
            public PlayingSound Start(AudioClip clip, in SoundPlacement placement, float volume, float pitch, bool loop)
            {
                if (_device == null) return default;
                (int voice, int generation) = _device.Play(clip, placement, volume, pitch, loop);
                return new PlayingSound(voice, generation, clip);
            }

            public bool IsPlaying(int voice, int generation) => _device?.IsPlaying(voice, generation) ?? false;
            public float Offset(int voice, int generation) => _device?.Offset(voice, generation) ?? 0f;
            public void Seek(int voice, int generation, float seconds) => _device?.Seek(voice, generation, seconds);
            public void Set(int voice, int generation, in SoundPlacement placement, float volume, float pitch) => _device?.Set(voice, generation, placement, volume, pitch);
            public void Stop(int voice, int generation) => _device?.Stop(voice, generation);

            public void StopAll() => _device?.StopAll();
        }
    }

    /// <summary>
    /// Where sounds are played: the audio device, or a stand-in for tests. A sound is a voice and which of the sounds it
    /// has played this is (its generation), so a handle to one that's over does nothing.
    /// </summary>
    internal interface IVoices
    {
        PlayingSound Start(AudioClip clip, in SoundPlacement placement, float volume, float pitch, bool loop);
        bool IsPlaying(int voice, int generation);
        float Offset(int voice, int generation);
        void Seek(int voice, int generation, float seconds);
        void Set(int voice, int generation, in SoundPlacement placement, float volume, float pitch);
        void Stop(int voice, int generation);
        void StopAll();
    }
}
