using System.Buffers.Binary;
using System.Text;

namespace Vertex.Audio
{
    /// <summary>
    /// A sound: 16-bit samples, mono or stereo. Pure data: the audio system makes its own copy the first time it's played.
    /// Only mono clips can be placed in 3D with <see cref="Sound.PlayAt"/>; stereo clips sound the same from anywhere.
    /// </summary>
    public class AudioClip : IDisposable
    {
        /// <summary>For stereo, left and right samples alternate.</summary>
        public short[] Samples { get; }
        public int Channels { get; }
        public int SampleRate { get; }

        /// <summary>Length in seconds.</summary>
        public float Duration => Samples.Length / (float)(Channels * SampleRate);

        /// <summary>Raised by <see cref="Dispose"/> so the audio system can free its copy.</summary>
        public event Action<AudioClip>? Disposed;

        public AudioClip(short[] samples, int channels, int sampleRate)
        {
            if (channels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channels), "Clips are mono (1) or stereo (2).");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            Samples = samples;
            Channels = channels;
            SampleRate = sampleRate;
        }

        /// <summary>A mono clip from samples between -1 and 1, for sounds made in code.</summary>
        public static AudioClip FromSamples(ReadOnlySpan<float> samples, int sampleRate)
        {
            var converted = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                converted[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            }

            return new AudioClip(converted, 1, sampleRate);
        }

        /// <summary>Loads an uncompressed WAV file (8 or 16-bit PCM, mono or stereo). Use mono for sounds placed in the world.</summary>
        public static AudioClip Load(string path)
        {
            ReadOnlySpan<byte> file = File.ReadAllBytes(path);
            if (file.Length < 12 || Encoding.ASCII.GetString(file[..4]) != "RIFF" || Encoding.ASCII.GetString(file[8..12]) != "WAVE")
                throw new InvalidDataException($"'{path}' isn't a WAV file.");

            int channels = 0, sampleRate = 0, bitsPerSample = 0;
            ReadOnlySpan<byte> data = default;

            // A WAV file is a list of chunks: "fmt " describes the samples, "data" holds them. Others are skipped.
            for (int offset = 12; offset + 8 <= file.Length;)
            {
                string id = Encoding.ASCII.GetString(file.Slice(offset, 4));
                int size = BinaryPrimitives.ReadInt32LittleEndian(file[(offset + 4)..]);
                ReadOnlySpan<byte> chunk = file.Slice(offset + 8, Math.Min(size, file.Length - offset - 8));

                if (id == "fmt ")
                {
                    if (BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1)
                        throw new NotSupportedException($"'{path}' is compressed; only uncompressed (PCM) WAV files are supported.");

                    channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                }
                else if (id == "data")
                {
                    data = chunk;
                }

                // Chunks are padded to an even length.
                offset += 8 + size + (size & 1);
            }

            if (channels == 0 || data.IsEmpty) throw new InvalidDataException($"'{path}' has no sound data.");

            short[] samples = bitsPerSample switch
            {
                16 => ReadSamples16(data),
                // 8-bit samples are unsigned, centred on 128.
                8 => data.ToArray().Select(b => (short)((b - 128) << 8)).ToArray(),
                _ => throw new NotSupportedException($"'{path}' has {bitsPerSample}-bit samples; only 8 and 16-bit are supported.")
            };

            return new AudioClip(samples, channels, sampleRate);
        }

        private static short[] ReadSamples16(ReadOnlySpan<byte> data)
        {
            var samples = new short[data.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
            }

            return samples;
        }

        /// <summary>Frees the audio system's copy. The clip is copied again if it's played afterwards.</summary>
        public void Dispose()
        {
            Disposed?.Invoke(this);
            Disposed = null;
        }
    }
}
