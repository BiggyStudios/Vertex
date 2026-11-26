using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Vertex
{
    /// <summary>
    /// A game's save kept so players can't read or change it by opening it: what's in it is squeezed, scrambled and signed
    /// with a secret of the game's, so a file that's been changed (or damaged) is refused rather than believed. The last
    /// few saves are kept beside it as backups, and reading falls back to the newest of them that's whole.
    /// <para>
    /// Like <see cref="AssetPack"/>, it stops casual cheating, not someone set on it: the game has to know its secret to
    /// read its own saves, so it's somewhere in the game's program.
    /// </para>
    /// </summary>
    public static class SaveFile
    {
        /// <summary>How many earlier saves are kept, unless a game asks for another number.</summary>
        public const int DefaultBackups = 3;

        private const ushort FormatVersion = 1;
        private const int HeaderSize = 12;
        private const int SignatureSize = 32;
        private static ReadOnlySpan<byte> Magic => "VXSAVE"u8;

        /// <summary>
        /// Saves <paramref name="text"/> to <paramref name="path"/>, signed with <paramref name="secret"/>. The save that was
        /// there becomes the first backup (the first becomes the second, and so on, the oldest dropped), unless it was
        /// damaged or changed, when it's let go so it doesn't push a good backup out.
        /// </summary>
        public static void Write(string path, string text, string secret, int backups = DefaultBackups)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder != null) Directory.CreateDirectory(folder);

            // Written in full beside it first, so a save cut short (the game closing, the power going) leaves the old one.
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Seal(text, secret));

            if (backups > 0 && File.Exists(path) && Unseal(File.ReadAllBytes(path), secret) != null)
            {
                for (int i = backups - 1; i >= 1; i--)
                {
                    if (File.Exists(Backup(path, i))) File.Move(Backup(path, i), Backup(path, i + 1), overwrite: true);
                }

                File.Move(path, Backup(path, 1), overwrite: true);
            }

            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>
        /// What was saved at <paramref name="path"/>: the save itself if it's whole and signed with
        /// <paramref name="secret"/>, else the newest backup that is, else null (nothing saved yet, or nothing that can be
        /// trusted).
        /// </summary>
        public static string? Read(string path, string secret, int backups = DefaultBackups)
        {
            for (int i = 0; i <= backups; i++)
            {
                string file = i == 0 ? path : Backup(path, i);
                if (!File.Exists(file)) continue;

                string? text = null;
                try
                {
                    text = Unseal(File.ReadAllBytes(file), secret);
                }
                catch (IOException exception)
                {
                    Log.Warning($"Couldn't read {Path.GetFileName(file)}: {exception.Message}");
                }

                if (text == null)
                {
                    Log.Warning($"{Path.GetFileName(file)} has been changed or damaged, so it isn't used.");
                    continue;
                }

                if (i > 0) Log.Warning($"Using the backup {Path.GetFileName(file)} instead.");
                return text;
            }

            return null;
        }

        /// <summary>Whether there's a save at <paramref name="path"/> or any backup of it, whole or not.</summary>
        public static bool Exists(string path, int backups = DefaultBackups)
        {
            for (int i = 0; i <= backups; i++)
            {
                if (File.Exists(i == 0 ? path : Backup(path, i))) return true;
            }

            return false;
        }

        /// <summary>Deletes the save at <paramref name="path"/> and its backups.</summary>
        public static void Delete(string path, int backups = DefaultBackups)
        {
            foreach (string file in Enumerable.Range(0, backups + 1).Select(i => i == 0 ? path : Backup(path, i)).Append(path + ".tmp"))
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        /// <summary>Where the <paramref name="number"/>th backup of the save at <paramref name="path"/> is kept (1 the newest).</summary>
        public static string Backup(string path, int number) => $"{path}.bak{number}";

        /// <summary>The bytes kept for <paramref name="text"/>: a header, the text squeezed and scrambled, and a signature of both.</summary>
        internal static byte[] Seal(string text, string secret)
        {
            var squeezed = new MemoryStream();
            using (var deflate = new DeflateStream(squeezed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(Encoding.UTF8.GetBytes(text));

            uint salt = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
            var file = new byte[HeaderSize + squeezed.Length + SignatureSize];
            Span<byte> span = file;
            Magic.CopyTo(span);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], FormatVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], salt);

            Span<byte> body = span[HeaderSize..^SignatureSize];
            squeezed.ToArray().CopyTo(body);
            AssetPack.Scramble(body, KeyFor(secret, salt));
            HMACSHA256.HashData(SigningKey(secret), span[..^SignatureSize], span[^SignatureSize..]);
            return file;
        }

        /// <summary>The text kept in <paramref name="file"/>, or null if it isn't a save, isn't signed with <paramref name="secret"/>, or has been changed since.</summary>
        internal static string? Unseal(byte[] file, string secret)
        {
            if (file.Length < HeaderSize + SignatureSize || !file.AsSpan(0, 6).SequenceEqual(Magic)) return null;
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)) > FormatVersion) return null;

            Span<byte> signature = stackalloc byte[SignatureSize];
            HMACSHA256.HashData(SigningKey(secret), file.AsSpan(0, file.Length - SignatureSize), signature);
            if (!CryptographicOperations.FixedTimeEquals(signature, file.AsSpan(file.Length - SignatureSize))) return null;

            uint salt = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
            byte[] body = file[HeaderSize..^SignatureSize];
            AssetPack.Scramble(body, KeyFor(secret, salt));
            try
            {
                using var inflate = new DeflateStream(new MemoryStream(body), CompressionMode.Decompress);
                using var reader = new StreamReader(inflate, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static byte[] SigningKey(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes("Vertex save signature\n" + secret));

        private static ulong KeyFor(string secret, uint salt)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("Vertex save scramble\n" + secret));
            return BinaryPrimitives.ReadUInt64LittleEndian(hash) ^ (salt * 0x9E3779B97F4A7C15UL);
        }
    }
}
