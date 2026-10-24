using Vertex.Audio;

namespace SampleGame
{
    /// <summary>
    /// The game's sounds. There are no sound files yet, so they're made in code; to use a recording instead,
    /// replace one with <c>AudioClip.Load(path)</c> on a mono WAV file.
    /// </summary>
    public static class Sounds
    {
        private const int _sampleRate = 44100;

        public static AudioClip Gunshot { get; } = CreateGunshot();

        /// <summary>
        /// A rifle shot built from four layers: a sharp crack, a burst of muffled noise for the bang, a falling low
        /// thump for weight, and a dull tail like the echo off the surroundings.
        /// </summary>
        private static AudioClip CreateGunshot()
        {
            const float duration = 0.7f;
            var samples = new float[(int)(_sampleRate * duration)];

            // A fixed seed, so the sound is the same every run.
            var random = new Random(1234);
            float muffled = 0f;
            float dull = 0f;
            float phase = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                float crack = noise * MathF.Exp(-t / 0.004f);

                // Smoothing noise (a simple low-pass filter) takes the hiss out, leaving a boom.
                muffled += (noise - muffled) * 0.12f;
                float bang = muffled * 2.5f * MathF.Exp(-t / 0.06f);

                float pitch = 50f + 90f * MathF.Exp(-t / 0.03f);
                phase += 2f * MathF.PI * pitch / _sampleRate;
                float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.09f);

                dull += (noise - dull) * 0.03f;
                float tail = dull * 2.5f * MathF.Exp(-t / 0.25f);

                float sample = crack * 0.9f + bang * 0.8f + thump * 0.9f + tail * 0.5f;

                // Soft clipping squashes the loudest moments, which makes it punchier without distorting harshly.
                samples[i] = MathF.Tanh(sample * 1.5f);
            }

            // Fade the very end out, so it doesn't stop with a click.
            int fade = _sampleRate / 50;
            for (int i = 0; i < fade; i++) samples[^(i + 1)] *= i / (float)fade;

            Normalize(samples, peak: 0.9f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        private static void Normalize(float[] samples, float peak)
        {
            float loudest = 0f;
            foreach (float sample in samples) loudest = MathF.Max(loudest, MathF.Abs(sample));
            if (loudest <= 0f) return;

            float scale = peak / loudest;
            for (int i = 0; i < samples.Length; i++) samples[i] *= scale;
        }
    }
}
