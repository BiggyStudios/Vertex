using System.Numerics;

using Vertex.Audio;
using Vertex.Editor.Gui;

namespace Vertex.Editor.Inspection
{
    /// <summary>
    /// Listening to sound files in the editor: one plays at a time, from the inspector or by double-clicking it in the
    /// asset browser. Files are read once and kept, with their waveform worked out for drawing.
    /// </summary>
    public static class SoundPreview
    {
        private sealed record Loaded(AudioClip? Clip, float[] Peaks, string? Problem);

        private static readonly Dictionary<string, Loaded> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static string? _playingPath;
        private static PlayingSound _playing;

        private const int _peakCount = 200;

        public static bool IsPlaying(string path) => _playingPath == path && _playing.IsPlaying;

        public static void Toggle(string path)
        {
            if (IsPlaying(path)) Stop();
            else Play(path);
        }

        public static void Play(string path, float from = 0f)
        {
            Stop();
            if (Load(path).Clip is not { } clip) return;

            _playing = Sound.Play(clip);
            if (from > 0f) _playing.Seek(from);
            _playingPath = path;
        }

        public static void Stop()
        {
            _playing.Stop();
            _playingPath = null;
        }

        /// <summary>Forgets a file read before, so it's read again: after it changes on disk.</summary>
        public static void Forget(string path)
        {
            if (_playingPath == path) Stop();
            if (_loaded.Remove(path, out Loaded? loaded)) loaded.Clip?.Dispose();
        }

        private static Loaded Load(string path)
        {
            if (_loaded.TryGetValue(path, out Loaded? loaded)) return loaded;

            try
            {
                if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    loaded = new Loaded(null, [], "Only WAV files can be played: the engine doesn't read this kind yet.");
                }
                else
                {
                    AudioClip clip = AudioClip.Load(path);
                    loaded = new Loaded(clip, Peaks(clip), null);
                }
            }
            catch (Exception exception)
            {
                loaded = new Loaded(null, [], exception.Message);
            }

            // A few kept: they're whole files of samples.
            if (_loaded.Count > 24)
            {
                foreach (string old in _loaded.Keys.Where(key => key != _playingPath).Take(8).ToList()) Forget(old);
            }

            _loaded[path] = loaded;
            return loaded;
        }

        /// <summary>The loudest sample in each of a few hundred stretches of the clip, 0 to 1: its waveform's outline.</summary>
        private static float[] Peaks(AudioClip clip)
        {
            var peaks = new float[_peakCount];
            short[] samples = clip.Samples;
            if (samples.Length == 0) return peaks;

            for (int i = 0; i < _peakCount; i++)
            {
                int start = (int)((long)samples.Length * i / _peakCount), end = (int)((long)samples.Length * (i + 1) / _peakCount);
                int loudest = 0;
                for (int s = start; s < end; s++) loudest = Math.Max(loudest, Math.Abs((int)samples[s]));
                peaks[i] = loudest / 32768f;
            }

            // Scaled so the loudest part fills the height: quiet sounds still show their shape.
            float top = peaks.Max();
            if (top > 0.001f) for (int i = 0; i < peaks.Length; i++) peaks[i] /= top;

            return peaks;
        }

        /// <summary>The inspector's player: play and stop, the waveform (click it to play from there), and what the file is.</summary>
        public static float Draw(string path, float x, float y, float width)
        {
            Loaded loaded = Load(path);
            if (loaded.Clip is not { } clip)
            {
                Ui.Text(new Rect(x, y, width, 20f), loaded.Problem ?? "Can't be played", Theme.TextDim, size: Theme.SmallTextSize);
                return y + 26f;
            }

            bool playing = IsPlaying(path);
            float time = playing ? _playing.Time : 0f;

            // The waveform, mirrored about its middle, lit up to where it's played.
            var wave = new Rect(x, y, width, 84f);
            Ui.Rect(wave, Theme.Field, 6f);
            int id = Ui.Id("sound.wave:" + path);
            Interaction interaction = Ui.Interact(id, wave);
            if (interaction.Hovered) Ui.Cursor = Vertex.Input.CursorShape.Hand;
            if (interaction.Pressed) Play(path, Math.Clamp((Ui.Mouse.X - wave.X) / wave.Width, 0f, 1f) * clip.Duration);

            float played = clip.Duration > 0f ? time / clip.Duration : 0f;
            Rect inner = wave.Shrink(8f, 8f);
            float bar = inner.Width / loaded.Peaks.Length;
            for (int i = 0; i < loaded.Peaks.Length; i++)
            {
                float height = MathF.Max(loaded.Peaks[i] * inner.Height, 1.5f);
                var rect = new Rect(inner.X + i * bar, inner.Centre.Y - height * 0.5f, MathF.Max(bar - 1f, 1f), height);
                bool done = playing && (i + 0.5f) / loaded.Peaks.Length <= played;
                Ui.Rect(rect, done ? Theme.AccentText : Theme.TextFaint.WithAlpha(interaction.Hovered ? 1f : 0.8f), 0.5f);
            }

            if (playing)
            {
                float lineX = inner.X + inner.Width * played;
                Ui.Rect(new Rect(lineX - 1f, wave.Y + 4f, 2f, wave.Height - 8f), Color.White, 1f);
            }
            else if (interaction.Hovered)
            {
                Ui.Rect(new Rect(Ui.Mouse.X - 0.5f, wave.Y + 4f, 1f, wave.Height - 8f), Color.White.WithAlpha(0.5f));
            }

            y = wave.Bottom + 10f;

            // Play or stop, and the time.
            var button = new Rect(x, y, 34f, 34f);
            int playId = Ui.Id("sound.play:" + path);
            Interaction press = Ui.Interact(playId, button);
            float hover = Ui.Smooth(Ui.Id(playId, "hover"), press.Hovered ? 1f : 0f, 20f);
            Ui.Circle(button.Centre, 17f, Color.Lerp(Theme.Accent, Theme.AccentHover, hover));
            if (playing) Icons.Stop(button.Centre, 13f, Color.White);
            else Icons.Play(button.Centre + new Vector2(1.5f, 0f), 14f, Color.White);
            Ui.Tooltip(playId, playing ? "Stop" : "Play");
            if (press.Clicked) Toggle(path);

            Ui.Text(new Rect(button.Right + 12f, y, width - 46f, 34f), $"{Format(time)} / {Format(clip.Duration)}", Theme.Text);
            y += 44f;

            string channels = clip.Channels == 1 ? "Mono" : clip.Channels == 2 ? "Stereo" : $"{clip.Channels} channels";
            Ui.Text(new Rect(x, y, width, 18f), $"{channels}  ·  {clip.SampleRate:N0} Hz  ·  16-bit", Theme.TextDim, size: Theme.SmallTextSize);
            y += 22f;
            if (clip.Channels > 1)
            {
                Ui.Text(new Rect(x, y, width, 18f), "Stereo sounds play the same wherever they are: use mono for sounds in the world.", Theme.TextFaint,
                    size: Theme.SmallTextSize);
                y += 22f;
            }

            return y;
        }

        private static string Format(float seconds) => $"{(int)(seconds / 60f)}:{seconds % 60f:00.0}";
    }
}
