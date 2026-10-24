using System.Numerics;
using System.Runtime.CompilerServices;

using Silk.NET.OpenAL;

namespace Vertex.Audio.OpenAL
{
    /// <summary>
    /// Audio through OpenAL Soft. Keeps a fixed set of voices (OpenAL sources) and reuses whichever has finished,
    /// or the one that started longest ago if all are busy. Nothing outside this class talks to OpenAL.
    /// </summary>
    internal sealed unsafe class OpenALDevice : IDisposable
    {
        private const int _voiceCount = 32;

        private readonly ALContext _alc;
        private readonly AL _al;
        private readonly Device* _device;
        private readonly Context* _context;
        private readonly uint[] _voices = new uint[_voiceCount];
        private int _nextVoice;

        // OpenAL copies of clips. Weak keys, so dropping a clip doesn't keep it alive here.
        private readonly ConditionalWeakTable<AudioClip, ClipBuffer> _buffers = new();

        public OpenALDevice()
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);

            // An empty name picks the system's default output.
            _device = _alc.OpenDevice("");
            if (_device == null) throw new InvalidOperationException("Couldn't open an audio device.");

            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            // Gain = reference distance / distance, so loudness halves each time the distance doubles.
            _al.DistanceModel(DistanceModel.InverseDistanceClamped);

            for (int i = 0; i < _voices.Length; i++) _voices[i] = _al.GenSource();
        }

        public void Play(AudioClip clip, Vector3? position, float volume, float pitch, float fullVolumeDistance)
        {
            uint buffer = GetBuffer(clip);
            uint voice = TakeVoice();

            _al.SourceStop(voice);
            _al.SetSourceProperty(voice, SourceInteger.Buffer, buffer);
            _al.SetSourceProperty(voice, SourceFloat.Gain, MathF.Max(volume, 0f));
            _al.SetSourceProperty(voice, SourceFloat.Pitch, Math.Clamp(pitch, 0.1f, 4f));

            if (position is Vector3 world)
            {
                _al.SetSourceProperty(voice, SourceBoolean.SourceRelative, false);
                _al.SetSourceProperty(voice, SourceVector3.Position, world.X, world.Y, world.Z);
                _al.SetSourceProperty(voice, SourceFloat.ReferenceDistance, MathF.Max(fullVolumeDistance, 0.01f));
                _al.SetSourceProperty(voice, SourceFloat.RolloffFactor, 1f);
            }
            else
            {
                // At the listener, with no fall-off: the same in both ears.
                _al.SetSourceProperty(voice, SourceBoolean.SourceRelative, true);
                _al.SetSourceProperty(voice, SourceVector3.Position, 0f, 0f, 0f);
                _al.SetSourceProperty(voice, SourceFloat.RolloffFactor, 0f);
            }

            _al.SourcePlay(voice);
        }

        public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
        {
            _al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);

            float* orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
            _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
        }

        public void SetVolume(float volume) => _al.SetListenerProperty(ListenerFloat.Gain, Math.Clamp(volume, 0f, 1f));

        /// <summary>A voice that's finished playing, or else the one that started longest ago.</summary>
        private uint TakeVoice()
        {
            for (int i = 0; i < _voices.Length; i++)
            {
                uint voice = _voices[(_nextVoice + i) % _voices.Length];
                _al.GetSourceProperty(voice, GetSourceInteger.SourceState, out int state);
                if (state == (int)SourceState.Playing) continue;

                _nextVoice = (_nextVoice + i + 1) % _voices.Length;
                return voice;
            }

            uint oldest = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            return oldest;
        }

        private uint GetBuffer(AudioClip clip)
        {
            if (_buffers.TryGetValue(clip, out ClipBuffer? existing)) return existing.Id;

            uint id = _al.GenBuffer();
            BufferFormat format = clip.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
            _al.BufferData(id, format, clip.Samples, clip.SampleRate);

            _buffers.Add(clip, new ClipBuffer(id));
            clip.Disposed += OnClipDisposed;
            return id;
        }

        private void OnClipDisposed(AudioClip clip)
        {
            if (!_buffers.TryGetValue(clip, out ClipBuffer? buffer)) return;

            DeleteBuffer(buffer.Id);
            _buffers.Remove(clip);
        }

        private void DeleteBuffer(uint id)
        {
            // OpenAL won't delete a buffer a voice is still using, so stop and detach those first.
            foreach (uint voice in _voices)
            {
                _al.GetSourceProperty(voice, GetSourceInteger.Buffer, out int attached);
                if ((uint)attached != id) continue;

                _al.SourceStop(voice);
                _al.SetSourceProperty(voice, SourceInteger.Buffer, 0u);
            }

            _al.DeleteBuffer(id);
        }

        public void Dispose()
        {
            foreach (uint voice in _voices)
            {
                _al.SourceStop(voice);
                _al.DeleteSource(voice);
            }

            foreach (KeyValuePair<AudioClip, ClipBuffer> entry in _buffers) _al.DeleteBuffer(entry.Value.Id);
            _buffers.Clear();

            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _al.Dispose();
            _alc.Dispose();
        }

        private sealed record ClipBuffer(uint Id);
    }
}
