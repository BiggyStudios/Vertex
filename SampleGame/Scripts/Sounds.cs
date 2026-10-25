using Vertex.Audio;

namespace SampleGame
{
    /// <summary>
    /// The game's sounds. Most are made in code; the rest are recordings in Assets/Sounds, loaded with
    /// <c>AudioClip.Load(path)</c>. Recordings should be mono WAV files, so they can be placed in the world.
    /// </summary>
    public static class Sounds
    {
        private const int _sampleRate = 44100;

        public static AudioClip Gunshot { get; } = CreateGunshot();
        public static AudioClip SniperShot { get; } = CreateSniperShot();
        public static AudioClip Bolt { get; } = CreateBolt();
        public static AudioClip ShotgunShot { get; } = CreateShotgunShot();
        public static AudioClip Pump { get; } = CreatePump();
        public static AudioClip HitMarker { get; } = CreateHitMarker();
        public static AudioClip Headshot { get; } = CreateHeadshot();
        public static AudioClip Kill { get; } = CreateKill();
        public static AudioClip Hurt { get; } = CreateHurt();
        public static AudioClip KnifeSwing { get; } = CreateKnifeSwing();
        public static AudioClip KnifeHitWall { get; } = CreateKnifeHitWall();
        public static AudioClip KnifeDraw { get; } = CreateKnifeDraw();

        /// <summary>Bounce pads. Recorded rather than made here: Assets/Sounds/Bouncepad.wav.</summary>
        public static AudioClip Bounce { get; } = AudioClip.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "Bouncepad.wav"));

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

            FadeOut(samples);
            Normalize(samples, peak: 0.9f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// The sniper rifle: the same layers as the rifle, but a deeper, longer boom and a rolling echo that lasts over a
        /// second, so it's heard (and recognised) right across the map.
        /// </summary>
        private static AudioClip CreateSniperShot()
        {
            const float duration = 1.6f;
            var samples = new float[(int)(_sampleRate * duration)];

            var random = new Random(4321);
            float muffled = 0f;
            float dull = 0f;
            float phase = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                float crack = noise * MathF.Exp(-t / 0.006f);

                muffled += (noise - muffled) * 0.08f;
                float bang = muffled * 3f * MathF.Exp(-t / 0.1f);

                float pitch = 38f + 80f * MathF.Exp(-t / 0.05f);
                phase += 2f * MathF.PI * pitch / _sampleRate;
                float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.16f);

                // The echo swells slightly after the shot, as it comes back off things further away, then rolls off.
                dull += (noise - dull) * 0.02f;
                float tail = dull * 3f * MathF.Exp(-t / 0.5f) * MathF.Min(t / 0.08f, 1f);

                float sample = crack * 1f + bang * 0.9f + thump * 1.1f + tail * 0.6f;
                samples[i] = MathF.Tanh(sample * 1.8f);
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.95f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>Working the sniper rifle's bolt: a click as it unlocks, and a heavier clack as it slams home.</summary>
        private static AudioClip CreateBolt()
        {
            const float duration = 0.45f;
            var samples = new float[(int)(_sampleRate * duration)];
            var random = new Random(99);

            foreach ((float start, float loudness, float ring) in (ReadOnlySpan<(float, float, float)>)[(0.0f, 0.6f, 2400f), (0.08f, 0.35f, 1700f), (0.3f, 1f, 1300f)])
            {
                int first = (int)(start * _sampleRate);
                float phase = 0f;
                for (int i = first; i < samples.Length; i++)
                {
                    float t = (i - first) / (float)_sampleRate;
                    float envelope = MathF.Exp(-t / 0.012f);
                    if (envelope < 0.001f) break;

                    // A short burst of noise for the impact, plus a metallic ring.
                    phase += 2f * MathF.PI * ring / _sampleRate;
                    samples[i] += loudness * envelope * ((random.NextSingle() * 2f - 1f) * 0.7f + MathF.Sin(phase) * 0.5f);
                }
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.8f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// The shotgun: a wide, heavy blast. More low-middle boom than the rifle and a short, thick tail, so it sounds
        /// big up close rather than carrying like the sniper.
        /// </summary>
        private static AudioClip CreateShotgunShot()
        {
            const float duration = 1f;
            var samples = new float[(int)(_sampleRate * duration)];

            var random = new Random(5150);
            float muffled = 0f;
            float dull = 0f;
            float phase = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                float crack = noise * MathF.Exp(-t / 0.008f);

                muffled += (noise - muffled) * 0.1f;
                float bang = muffled * 3.2f * MathF.Exp(-t / 0.09f);

                float pitch = 45f + 70f * MathF.Exp(-t / 0.04f);
                phase += 2f * MathF.PI * pitch / _sampleRate;
                float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.12f);

                dull += (noise - dull) * 0.03f;
                float tail = dull * 2.5f * MathF.Exp(-t / 0.3f);

                float sample = crack * 0.8f + bang * 1.1f + thump * 1.1f + tail * 0.5f;
                samples[i] = MathF.Tanh(sample * 2f);
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.95f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>Working the shotgun's pump: "chk-chk", the slide racking back, then slamming forward.</summary>
        private static AudioClip CreatePump()
        {
            var samples = new float[(int)(_sampleRate * 0.4f)];
            var random = new Random(77);
            float low = 0f;

            foreach ((float start, float loudness) in (ReadOnlySpan<(float, float)>)[(0f, 0.8f), (0.15f, 1f)])
            {
                int first = (int)(start * _sampleRate);
                float phase = 0f;
                for (int i = first; i < samples.Length; i++)
                {
                    float t = (i - first) / (float)_sampleRate;
                    float envelope = MathF.Exp(-t / 0.02f);
                    if (envelope < 0.001f) break;

                    // Gritty noise for the slide, a little body underneath, and a dull metal ring.
                    float noise = random.NextSingle() * 2f - 1f;
                    low += (noise - low) * 0.3f;
                    phase += 2f * MathF.PI * 900f / _sampleRate;
                    samples[i] += loudness * envelope * (noise * 0.5f + low * 0.8f + MathF.Sin(phase) * 0.3f);
                }
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.8f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>Hitting another player: a deep metal "ding", with a low thud at the start for weight.</summary>
        private static AudioClip CreateHitMarker()
        {
            var samples = new float[(int)(_sampleRate * 0.7f)];
            AddDing(samples, start: 0f, pitch: 280f, ring: 0.26f, loudness: 1f);

            FadeOut(samples);
            Normalize(samples, peak: 0.75f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// A headshot: CS's helmet "dink". Higher and harsher than the body hit, with a sharp tick at the front and
        /// out-of-tune partials that make it sound like thin metal.
        /// </summary>
        private static AudioClip CreateHeadshot()
        {
            var samples = new float[(int)(_sampleRate * 0.4f)];
            var random = new Random(11);
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float ring = MathF.Sin(2f * MathF.PI * 2650f * t) * MathF.Exp(-t / 0.11f)
                    + 0.7f * MathF.Sin(2f * MathF.PI * 6140f * t) * MathF.Exp(-t / 0.05f)
                    + 0.4f * MathF.Sin(2f * MathF.PI * 9870f * t) * MathF.Exp(-t / 0.02f);
                float tick = (random.NextSingle() * 2f - 1f) * MathF.Exp(-t / 0.0015f);
                samples[i] = ring * MathF.Min(t / 0.0005f, 1f) + tick * 0.8f;
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.75f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>Killing a player: "ding-ding", two deep metal dings a fifth apart, rising.</summary>
        private static AudioClip CreateKill()
        {
            var samples = new float[(int)(_sampleRate * 1f)];
            AddDing(samples, start: 0f, pitch: 220f, ring: 0.22f, loudness: 0.85f);
            AddDing(samples, start: 0.11f, pitch: 330f, ring: 0.32f, loudness: 1f);

            FadeOut(samples);
            Normalize(samples, peak: 0.75f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// Getting hit: a short, punchy flesh "thwack". A smack of noise filtered to the middle frequencies, over a
        /// quick low thump, with no boomy tail.
        /// </summary>
        private static AudioClip CreateHurt()
        {
            var samples = new float[(int)(_sampleRate * 0.2f)];
            var random = new Random(23);
            float low = 0f, lower = 0f, phase = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                // Two low-pass filters a step apart: the difference keeps the middle band, which sounds like a slap.
                low += (noise - low) * 0.35f;
                lower += (noise - lower) * 0.06f;
                float smack = (low - lower) * 3f * MathF.Exp(-t / 0.018f);

                phase += 2f * MathF.PI * (60f + 60f * MathF.Exp(-t / 0.015f)) / _sampleRate;
                float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.045f);

                samples[i] = MathF.Tanh((smack + thump * 0.9f) * 1.6f);
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.85f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// A knife cutting the air: a "whoosh" of noise that swells and fades, its pitch sweeping up then down as the blade
        /// passes. Played lower for a stab.
        /// </summary>
        private static AudioClip CreateKnifeSwing()
        {
            const float duration = 0.22f;
            var samples = new float[(int)(_sampleRate * duration)];
            var random = new Random(31);
            float low = 0f, lower = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                // The band kept between the two filters moves up and back down over the swing.
                float sweep = MathF.Sin(MathF.PI * t / duration);
                low += (noise - low) * (0.08f + 0.3f * sweep);
                lower += (noise - lower) * 0.03f;
                float swell = sweep * sweep;
                samples[i] = (low - lower) * swell;
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.7f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>The knife hitting a wall: a short, bright clink of steel on something hard.</summary>
        private static AudioClip CreateKnifeHitWall()
        {
            var samples = new float[(int)(_sampleRate * 0.25f)];
            var random = new Random(47);
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float ring = MathF.Sin(2f * MathF.PI * 1900f * t) * MathF.Exp(-t / 0.05f)
                    + 0.6f * MathF.Sin(2f * MathF.PI * 4370f * t) * MathF.Exp(-t / 0.025f);
                float tick = (random.NextSingle() * 2f - 1f) * MathF.Exp(-t / 0.003f);
                samples[i] = ring * 0.7f + tick;
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.7f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// Drawing the knife: steel scraping out of a sheath. A short, gritty scrape of hissy noise, roughened so it drags
        /// rather than whooshes, then a brief, faint ring of the blade at fixed pitches as it comes free.
        /// </summary>
        private static AudioClip CreateKnifeDraw()
        {
            const float scrapeLength = 0.22f;
            var samples = new float[(int)(_sampleRate * 0.45f)];
            var random = new Random(53);
            float low = 0f, grain = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float t = i / (float)_sampleRate;
                float noise = random.NextSingle() * 2f - 1f;

                // The noise with its low end taken out is thin and hissy, like metal on metal. Slowly wandering
                // loudness (grain) makes it catch and drag unevenly, as a blade does along a sheath.
                low += (noise - low) * 0.25f;
                grain += ((random.NextSingle() * 0.8f + 0.2f) - grain) * 0.004f;
                float swell = t < scrapeLength ? MathF.Sin(MathF.PI * MathF.Min(t / scrapeLength * 1.6f, 1f) * 0.5f) : MathF.Exp(-(t - scrapeLength) / 0.01f);
                float scrape = (noise - low) * grain * swell;

                // The blade clearing the sheath: a few out-of-tune steel partials, quiet and quick.
                float free = t - scrapeLength;
                float ring = free < 0f ? 0f
                    : (MathF.Sin(2f * MathF.PI * 2350f * free) + 0.6f * MathF.Sin(2f * MathF.PI * 3910f * free) + 0.4f * MathF.Sin(2f * MathF.PI * 6280f * free))
                        * MathF.Exp(-free / 0.06f) * MathF.Min(free / 0.002f, 1f);

                samples[i] = scrape + ring * 0.12f;
            }

            FadeOut(samples);
            Normalize(samples, peak: 0.5f);
            return AudioClip.FromSamples(samples, _sampleRate);
        }

        /// <summary>
        /// Adds one deep metal "ding": a low thud for the strike, the ringing tone, and one out-of-tune overtone (2.76
        /// times the pitch, as on struck metal) that fades quickly and makes it sound metallic.
        /// <paramref name="ring"/> is how long it rings, in seconds, until it's down to about a third.
        /// </summary>
        private static void AddDing(float[] samples, float start, float pitch, float ring, float loudness)
        {
            int first = (int)(start * _sampleRate);
            for (int i = first; i < samples.Length; i++)
            {
                float t = (i - first) / (float)_sampleRate;
                float tone = MathF.Sin(2f * MathF.PI * pitch * t) * MathF.Exp(-t / ring);
                float metal = 0.35f * MathF.Sin(2f * MathF.PI * pitch * 2.76f * t) * MathF.Exp(-t / (ring * 0.25f));
                float thud = 0.8f * MathF.Sin(2f * MathF.PI * pitch * 0.4f * t) * MathF.Exp(-t / 0.04f);
                samples[i] += loudness * (tone + metal + thud) * MathF.Min(t / 0.002f, 1f);
            }
        }

        /// <summary>Fades the very end out, so a sound doesn't stop with a click.</summary>
        private static void FadeOut(float[] samples)
        {
            int fade = Math.Min(_sampleRate / 50, samples.Length);
            for (int i = 0; i < fade; i++) samples[^(i + 1)] *= i / (float)fade;
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
