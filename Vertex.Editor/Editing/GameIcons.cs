using System.IO.Compression;
using System.Text;

using StbImageSharp;

namespace Vertex.Editor.Editing
{
    /// <summary>
    /// A game's icon, from the picture its project settings name, in each system's own form: a .ico for Windows, a
    /// .icns for macOS, a PNG for Linux. Each holds the picture at several sizes, made square (a picture that isn't is
    /// fitted in, with the rest left clear).
    /// </summary>
    public static class GameIcons
    {
        /// <summary>The sizes a Windows icon holds: the small ones for lists and the taskbar, the big one for Explorer's large icons.</summary>
        private static readonly int[] _windowsSizes = [16, 24, 32, 48, 64, 128, 256];

        /// <summary>A macOS icon's sizes, with the type each is kept as.</summary>
        private static readonly (string Type, int Size)[] _macSizes = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];

        /// <summary>A picture's pixels, red, green, blue and alpha, from a file.</summary>
        public static (byte[] Pixels, int Width, int Height) Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return (image.Data, image.Width, image.Height);
        }

        /// <summary>The picture made <paramref name="size"/> pixels square: fitted in (keeping its shape), each pixel the average of those it covers.</summary>
        public static byte[] Square(byte[] pixels, int width, int height, int size)
        {
            var result = new byte[size * size * 4];
            float scale = MathF.Max(width, height) / (float)size;
            float offsetX = (size - width / scale) * 0.5f, offsetY = (size - height / scale) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // The part of the picture this pixel covers.
                    float left = (x - offsetX) * scale, top = (y - offsetY) * scale;
                    float right = left + scale, bottom = top + scale;
                    if (right <= 0f || bottom <= 0f || left >= width || top >= height) continue;

                    int x0 = Math.Max(0, (int)MathF.Floor(left)), x1 = Math.Min(width, Math.Max(x0 + 1, (int)MathF.Ceiling(right)));
                    int y0 = Math.Max(0, (int)MathF.Floor(top)), y1 = Math.Min(height, Math.Max(y0 + 1, (int)MathF.Ceiling(bottom)));
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    int count = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sy * width + sx) * 4;
                            float alpha = pixels[i + 3] / 255f;
                            // Weighted by how solid each is, so clear pixels' colour doesn't bleed into the edges.
                            r += pixels[i] * alpha;
                            g += pixels[i + 1] * alpha;
                            b += pixels[i + 2] * alpha;
                            a += alpha;
                            count++;
                        }
                    }

                    int o = (y * size + x) * 4;
                    if (a > 0f)
                    {
                        result[o] = (byte)Math.Clamp(r / a, 0f, 255f);
                        result[o + 1] = (byte)Math.Clamp(g / a, 0f, 255f);
                        result[o + 2] = (byte)Math.Clamp(b / a, 0f, 255f);
                    }

                    result[o + 3] = (byte)Math.Clamp(a / count * 255f, 0f, 255f);
                }
            }

            return result;
        }

        /// <summary>A square picture as a PNG file's bytes.</summary>
        public static byte[] Png(byte[] pixels, int size)
        {
            // Each row starts with a byte saying how it's filtered: 0, not at all.
            var raw = new byte[size * (size * 4 + 1)];
            for (int y = 0; y < size; y++) Array.Copy(pixels, y * size * 4, raw, y * (size * 4 + 1) + 1, size * 4);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            BigEndian(header, 0, (uint)size);
            BigEndian(header, 4, (uint)size);
            header[8] = 8;
            header[9] = 6;
            Chunk(png, "IHDR", header);
            Chunk(png, "IDAT", compressed.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        /// <summary>A Windows icon (.ico) of the picture at <paramref name="path"/>: PNGs at each size, as Windows reads them.</summary>
        public static void WriteIco(string path, string picture)
        {
            (byte[] pixels, int width, int height) = Load(picture);
            List<byte[]> images = _windowsSizes.Select(size => Png(Square(pixels, width, height, size), size)).ToList();

            using var file = new BinaryWriter(File.Create(path));
            file.Write((ushort)0);
            file.Write((ushort)1);
            file.Write((ushort)images.Count);

            // A directory entry for each, then the images after them all.
            int offset = 6 + 16 * images.Count;
            for (int i = 0; i < images.Count; i++)
            {
                int size = _windowsSizes[i];
                file.Write((byte)(size >= 256 ? 0 : size));
                file.Write((byte)(size >= 256 ? 0 : size));
                file.Write((byte)0);
                file.Write((byte)0);
                file.Write((ushort)1);
                file.Write((ushort)32);
                file.Write(images[i].Length);
                file.Write(offset);
                offset += images[i].Length;
            }

            foreach (byte[] image in images) file.Write(image);
        }

        /// <summary>A macOS icon (.icns) of the picture: PNGs at each size, up to 1024 for Retina screens.</summary>
        public static void WriteIcns(string path, string picture)
        {
            (byte[] pixels, int width, int height) = Load(picture);
            var entries = new List<(string Type, byte[] Data)>();
            foreach ((string type, int size) in _macSizes) entries.Add((type, Png(Square(pixels, width, height, size), size)));

            using var file = File.Create(path);
            var number = new byte[4];
            file.Write(Encoding.ASCII.GetBytes("icns"));
            BigEndian(number, 0, (uint)(8 + entries.Sum(e => 8 + e.Data.Length)));
            file.Write(number);
            foreach ((string type, byte[] data) in entries)
            {
                file.Write(Encoding.ASCII.GetBytes(type));
                BigEndian(number, 0, (uint)(8 + data.Length));
                file.Write(number);
                file.Write(data);
            }
        }

        /// <summary>The picture as a 256-pixel square PNG: a Linux game's icon, for its .desktop file to point at.</summary>
        public static void WritePng(string path, string picture)
        {
            (byte[] pixels, int width, int height) = Load(picture);
            File.WriteAllBytes(path, Png(Square(pixels, width, height, 256), 256));
        }

        private static void Chunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            BigEndian(length, 0, (uint)data.Length);
            stream.Write(length);
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            stream.Write(typed);
            var crc = new byte[4];
            BigEndian(crc, 0, Crc(typed));
            stream.Write(crc);
        }

        private static void BigEndian(byte[] into, int at, uint value)
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
