using System.IO.Compression;
using System.Text;

namespace NeonCity.Builder
{
    /// <summary>Writes RGBA pixels as a PNG file, so the builder's textures are ordinary image files the editor can show.</summary>
    internal static class Png
    {
        public static void Save(string path, int width, int height, byte[] rgba)
        {
            // Each row starts with a byte saying how it's filtered: 0, not at all.
            var raw = new byte[height * (width * 4 + 1)];
            for (int y = 0; y < height; y++) Array.Copy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

            using var file = File.Create(path);
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;   // bits per channel
            header[9] = 6;   // red, green, blue and alpha
            Chunk(file, "IHDR", header);
            Chunk(file, "IDAT", compressed.ToArray());
            Chunk(file, "IEND", []);
        }

        private static void Chunk(Stream file, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            file.Write(length);

            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            file.Write(typed);

            var crc = new byte[4];
            WriteBigEndian(crc, 0, Crc(typed));
            file.Write(crc);
        }

        private static void WriteBigEndian(byte[] into, int at, uint value)
        {
            into[at] = (byte)(value >> 24);
            into[at + 1] = (byte)(value >> 16);
            into[at + 2] = (byte)(value >> 8);
            into[at + 3] = (byte)value;
        }

        private static uint Crc(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }

            return crc ^ 0xFFFFFFFF;
        }
    }
}
