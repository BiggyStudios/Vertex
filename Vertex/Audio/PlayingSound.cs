namespace Vertex.Audio
{
    /// <summary>
    /// A sound that was started with <see cref="Sound.Play(AudioClip, float, float)"/> or
    /// <see cref="Sound.PlayAt(AudioClip, System.Numerics.Vector3, float, float, float)"/>: to stop it early, or see how far
    /// it's got. Once it's finished (or its voice has been taken for another sound) it does nothing and reads as stopped.
    /// The default one is no sound at all.
    /// </summary>
    public readonly struct PlayingSound
    {
        private readonly int _voice;
        private readonly int _generation;

        /// <summary>The clip being played; null for the default (no sound).</summary>
        public AudioClip? Clip { get; }

        internal PlayingSound(int voice, int generation, AudioClip clip)
        {
            _voice = voice;
            _generation = generation;
            Clip = clip;
        }

        public bool IsPlaying => Clip != null && Sound.Voices.IsPlaying(_voice, _generation);

        /// <summary>How far into the clip it is, in seconds. 0 once it's stopped.</summary>
        public float Time => Clip != null ? Sound.Voices.Offset(_voice, _generation) : 0f;

        /// <summary>Jumps to <paramref name="seconds"/> into the clip, if it's still playing.</summary>
        public void Seek(float seconds)
        {
            if (Clip != null) Sound.Voices.Seek(_voice, _generation, seconds);
        }

        /// <summary>Changes its volume and pitch while it plays, and moves it if it's in the world.</summary>
        internal void Set(System.Numerics.Vector3? position, float volume, float pitch)
        {
            if (Clip != null) Sound.Voices.Set(_voice, _generation, position, volume, pitch);
        }

        internal int Voice => _voice;
        internal int Generation => _generation;

        public void Stop()
        {
            if (Clip != null) Sound.Voices.Stop(_voice, _generation);
        }
    }
}
