using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Vertex
{
    /// <summary>
    /// A built game's assets packed into one file (<see cref="FileName"/>, beside its program), so players can't open a
    /// folder and copy its textures, models and sounds out. Each file is squeezed and scrambled, and so is the list of
    /// what's in it, so neither the names nor the contents can be read from the pack as it is. It stops assets being taken
    /// out casually, not by someone set on it: the game has to be able to read them back, so its program knows how.
    /// <para>
    /// The game reads it through <see cref="AssetFiles"/>, which finds its files where they'd have been in the Assets
    /// folder. The editor writes one when it builds a game (unless it's told to keep the assets as a folder).
    /// </para>
    /// </summary>
    public sealed class AssetPack : IDisposable
    {
        /// <summary>The pack beside a built game's program.</summary>
        public const string FileName = "Assets.pack";

        private const ushort FormatVersion = 1;
        private const int HeaderSize = 28;
        private static ReadOnlySpan<byte> Magic => "VXPACK"u8;

        private readonly record struct Entry(long Offset, int Stored, int Length, bool Squeezed);

        private readonly SafeFileHandle _file;
        private readonly Dictionary<string, Entry> _entries;
        private readonly uint _salt;

        /// <summary>The pack's file.</summary>
        public string Source { get; }

        /// <summary>The files in it, as paths inside the Assets folder ("Textures/Brick.png").</summary>
        public IReadOnlyCollection<string> Files => _entries.Keys;

        private AssetPack(string source, SafeFileHandle file, Dictionary<string, Entry> entries, uint salt)
        {
            Source = source;
            _file = file;
            _entries = entries;
            _salt = salt;
        }

        /// <summary>Opens a pack to read from. It stays open (shared for reading) until it's disposed.</summary>
        /// <exception cref="InvalidDataException">The file isn't a pack, or is one from a newer engine.</exception>
        public static AssetPack Open(string path)
        {
            string full = Path.GetFullPath(path);
            SafeFileHandle file = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            try
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                if (RandomAccess.Read(file, header, 0) != HeaderSize || !header[..6].SequenceEqual(Magic)) throw new InvalidDataException($"{Path.GetFileName(full)} isn't an asset pack.");
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
                if (version > FormatVersion) throw new InvalidDataException($"{Path.GetFileName(full)} was packed by a newer engine.");

                uint salt = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                long indexAt = BinaryPrimitives.ReadInt64LittleEndian(header[12..]);
                int indexStored = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
                int indexLength = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
                byte[] index = ReadBlock(file, salt, new Entry(indexAt, indexStored, indexLength, Squeezed: true));

                var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                using var reader = new BinaryReader(new MemoryStream(index), Encoding.UTF8);
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    entries[name] = new Entry(reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean());
                }

                return new AssetPack(full, file, entries, salt);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>Whether the pack has a file, by its path inside the Assets folder.</summary>
        public bool Contains(string relative) => _entries.ContainsKey(Normalise(relative));

        /// <summary>A file's bytes, by its path inside the Assets folder.</summary>
        /// <exception cref="FileNotFoundException">It isn't in the pack.</exception>
        public byte[] Read(string relative)
        {
            if (!_entries.TryGetValue(Normalise(relative), out Entry entry)) throw new FileNotFoundException($"{relative} isn't in {Path.GetFileName(Source)}.", relative);
            return ReadBlock(_file, _salt, entry);
        }

        /// <summary>
        /// Packs <paramref name="files"/> (each a path inside the Assets folder and the file on disk it comes from) into a
        /// new pack at <paramref name="path"/>, in place of one already there.
        /// </summary>
        public static void Write(string path, IEnumerable<(string Relative, string File)> files)
        {
            uint salt = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            var entries = new List<(string Name, Entry Entry)>();
            string temporary = path + ".tmp";
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                output.Write(new byte[HeaderSize]);
                foreach ((string relative, string file) in files)
                {
                    byte[] data = File.ReadAllBytes(file);
                    entries.Add((Normalise(relative), WriteBlock(output, salt, data)));
                }

                var index = new MemoryStream();
                using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(entries.Count);
                    foreach ((string name, Entry entry) in entries)
                    {
                        writer.Write(name);
                        writer.Write(entry.Offset);
                        writer.Write(entry.Stored);
                        writer.Write(entry.Length);
                        writer.Write(entry.Squeezed);
                    }
                }

                Entry block = WriteBlock(output, salt, index.ToArray(), alwaysSqueeze: true);
                Span<byte> header = stackalloc byte[HeaderSize];
                Magic.CopyTo(header);
                BinaryPrimitives.WriteUInt16LittleEndian(header[6..], FormatVersion);
                BinaryPrimitives.WriteUInt32LittleEndian(header[8..], salt);
                BinaryPrimitives.WriteInt64LittleEndian(header[12..], block.Offset);
                BinaryPrimitives.WriteInt32LittleEndian(header[20..], block.Stored);
                BinaryPrimitives.WriteInt32LittleEndian(header[24..], block.Length);
                output.Position = 0;
                output.Write(header);
            }

            File.Move(temporary, path, overwrite: true);
        }

        public void Dispose() => _file.Dispose();

        /// <summary>A path inside the Assets folder as the pack keeps it: forward slashes, nothing in front.</summary>
        internal static string Normalise(string relative)
        {
            string name = relative.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            return name.TrimStart('/');
        }

        /// <summary>Writes a file's bytes, squeezed if that makes them much smaller (pictures and sounds that are already squeezed stay as they are), and scrambled.</summary>
        private static Entry WriteBlock(Stream output, uint salt, byte[] data, bool alwaysSqueeze = false)
        {
            long offset = output.Position;
            var squeezed = new MemoryStream();
            using (var deflate = new DeflateStream(squeezed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);

            bool squeeze = alwaysSqueeze || squeezed.Length < data.Length * 0.95;
            byte[] stored = squeeze ? squeezed.ToArray() : data;
            Scramble(stored, KeyFor(salt, offset));
            output.Write(stored);
            return new Entry(offset, stored.Length, data.Length, squeeze);
        }

        private static byte[] ReadBlock(SafeFileHandle file, uint salt, Entry entry)
        {
            var stored = new byte[entry.Stored];
            int done = 0;
            while (done < stored.Length)
            {
                int read = RandomAccess.Read(file, stored.AsSpan(done), entry.Offset + done);
                if (read <= 0) throw new EndOfStreamException("The asset pack ends early: it may be damaged.");
                done += read;
            }

            Scramble(stored, KeyFor(salt, entry.Offset));
            if (!entry.Squeezed) return stored;

            var data = new byte[entry.Length];
            using var inflate = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
            inflate.ReadExactly(data);
            return data;
        }

        private static ulong KeyFor(uint salt, long offset) => (salt * 0x9E3779B97F4A7C15UL) ^ ((ulong)offset * 0xD6E8FEB86659FD93UL) ^ 0x5658504B5345454DUL;

        /// <summary>Scrambles bytes with a stream made from <paramref name="key"/> (splitmix64); scrambling them again with the same key puts them back.</summary>
        private static void Scramble(Span<byte> data, ulong key)
        {
            ulong state = key;
            Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(data);
            for (int i = 0; i < words.Length; i++) words[i] ^= Next(ref state);

            ulong last = Next(ref state);
            for (int i = words.Length * 8; i < data.Length; i++)
            {
                data[i] ^= (byte)last;
                last >>= 8;
            }
        }

        private static ulong Next(ref ulong state)
        {
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
