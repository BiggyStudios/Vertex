using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using StbImageSharp;

using Vertex.Editor.Editing;
using Vertex.Rendering;

namespace Vertex.Editor.Gui
{
    /// <summary>What's to be drawn by <see cref="ThumbnailStage"/>: a material or a model, and the file as it was when asked for.</summary>
    internal sealed record ThumbnailRequest(string Path, AssetKind Kind, string Key, bool Save);

    /// <summary>
    /// Small pictures of assets for the asset browser, pickers and the inspector: images shrunk, and materials (on a ball)
    /// and models drawn by the renderer in a scene of their own (see <see cref="ThumbnailStage"/>), one a frame. Each is
    /// made in the background and kept on disk too, so next time they're there at once. Ask with <see cref="Get"/>, which
    /// gives null until one's ready.
    /// </summary>
    public static class Thumbnails
    {
        public const int Size = 128;

        // Bumped when thumbnails are made differently, so the ones kept on disk are made again.
        private const int _format = 1;
        private const uint _magic = 0x42485456; // "VTHB"

        /// <summary>A thumbnail made, and the file as it was when it was (see <see cref="KeyOf"/>). Null if it can't be made.</summary>
        private sealed class Picture(Texture? texture, string key, int settings = 0)
        {
            public Texture? Texture = texture;
            public readonly string Key = key;

            // For a material, its settings as drawn, so changes made in the inspector show as they're made; 0 until known
            // (one read from disk is as the file is).
            public int Settings = settings;
        }

        private static readonly ConcurrentDictionary<string, Picture> _made = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> _making = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _stale = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> _asked = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentQueue<ThumbnailRequest> _toDraw = new();
        private static readonly SemaphoreSlim _workers = new(2);
        private static int _pruned;

        /// <summary>Where thumbnails are kept between runs: the user's own cache, not the project, so they're never committed.</summary>
        public static string CacheFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vertex", "Thumbnails");

        /// <summary>
        /// The thumbnail of the image, material or model at <paramref name="path"/>, or null while it's being made (or if it
        /// can't be, or it's some other kind of file). Once there's one, it stays shown while a newer one's made.
        /// </summary>
        public static Texture? Get(string path)
        {
            AssetKind kind = AssetDatabase.KindOf(path);
            if (kind is not (AssetKind.Texture or AssetKind.Material or AssetKind.Model)) return null;

            _asked[path] = Environment.TickCount64;
            _made.TryGetValue(path, out Picture? made);
            if (_making.ContainsKey(path)) return made?.Texture;

            bool stale = _stale.TryGetValue(path, out bool redraw);
            bool changed = false;
            if (made?.Texture != null && kind == AssetKind.Material && Assets.LoadedMaterial(path) is { } material)
            {
                int settings = SettingsOf(material);
                if (made.Settings == 0) made.Settings = settings;
                changed = settings != made.Settings;
            }

            if (made != null && !stale && !changed) return made.Texture;
            if (!_making.TryAdd(path, 0)) return made?.Texture;
            _stale.TryRemove(path, out _);

            // Being changed in the inspector: drawn again as it is, but not kept on disk until it's saved.
            if (changed && !stale)
            {
                _toDraw.Enqueue(new ThumbnailRequest(path, kind, made!.Key, Save: false));
                return made.Texture;
            }

            Task.Run(async () =>
            {
                await _workers.WaitAsync();
                try
                {
                    Make(path, kind, made, redraw);
                }
                catch (Exception)
                {
                    _made[path] = new Picture(null, "");
                    _making.TryRemove(path, out _);
                }
                finally
                {
                    _workers.Release();
                }
            });

            return made?.Texture;
        }

        /// <summary>
        /// Marks a thumbnail out of date, so it's made again next time it's asked for: the file changed. The old one's shown
        /// until then. A changed image redraws the materials too, as they may show it.
        /// </summary>
        public static void Forget(string path)
        {
            _stale[path] = _stale.TryGetValue(path, out bool redraw) && redraw;
            if (AssetDatabase.KindOf(path) != AssetKind.Texture) return;
            foreach (string other in _made.Keys)
            {
                if (AssetDatabase.KindOf(other) == AssetKind.Material) _stale[other] = true;
            }
        }

        /// <summary>Forgets every thumbnail made so far (not those on disk): for tests.</summary>
        internal static void Reset()
        {
            _made.Clear();
            _making.Clear();
            _stale.Clear();
            _asked.Clear();
            _toDraw.Clear();
        }

        // ---------------------------------------------------------------- Making them

        /// <summary>In the background: from disk if it's there, an image shrunk, or a material or model sent to be drawn.</summary>
        private static void Make(string path, AssetKind kind, Picture? made, bool redraw)
        {
            // Scrolled past before its turn came: left until it's asked for again.
            if (!StillWanted(path))
            {
                _making.TryRemove(path, out _);
                return;
            }

            string key = KeyOf(path);
            if (!redraw && made?.Key == key)
            {
                _making.TryRemove(path, out _);
                return;
            }

            if (!redraw && ReadCache(key) is { } cached)
            {
                Show(path, new Picture(cached, key));
                return;
            }

            if (kind == AssetKind.Texture)
            {
                Texture texture = Shrink(path);
                Show(path, new Picture(texture, key));
                SaveLater(key, texture);
                return;
            }

            _toDraw.Enqueue(new ThumbnailRequest(path, kind, key, Save: true));
        }

        /// <summary>The next material or model for <see cref="ThumbnailStage"/> to draw, of those still being shown; null if none.</summary>
        internal static ThumbnailRequest? NextToDraw()
        {
            while (_toDraw.TryDequeue(out ThumbnailRequest? request))
            {
                if (StillWanted(request.Path)) return request;
                _making.TryRemove(request.Path, out _);
            }

            return null;
        }

        /// <summary>A material or model drawn, at twice the size: shrunk, shown, and kept on disk.</summary>
        internal static void Drawn(ThumbnailRequest request, byte[] pixels, int width, int height, int settings)
        {
            int smallWidth = Math.Max(width / 2, 1), smallHeight = Math.Max(height / 2, 1);
            byte[] small = new byte[smallWidth * smallHeight * 4];
            for (int y = 0; y < smallHeight; y++)
            {
                for (int x = 0; x < smallWidth; x++)
                {
                    int a = (y * 2 * width + x * 2) * 4, b = a + width * 4, o = (y * smallWidth + x) * 4;
                    for (int c = 0; c < 4; c++) small[o + c] = (byte)((pixels[a + c] + pixels[a + 4 + c] + pixels[b + c] + pixels[b + 4 + c] + 2) / 4);
                }
            }

            var texture = new Texture(smallWidth, smallHeight, small);
            Show(request.Path, new Picture(texture, request.Key, settings));
            if (request.Save) SaveLater(request.Key, texture);
        }

        /// <summary>A material or model that couldn't be drawn (it can't be read): shown by its icon until it changes.</summary>
        internal static void Failed(ThumbnailRequest request)
        {
            _made[request.Path] = new Picture(null, request.Key);
            _making.TryRemove(request.Path, out _);
        }

        /// <summary>
        /// Puts a new thumbnail in place of the old. One the same size is copied into the old one's texture, so nothing
        /// drawing it this frame is left with a texture that's gone.
        /// </summary>
        private static void Show(string path, Picture picture)
        {
            if (_made.TryGetValue(path, out Picture? old) && old.Texture is { } shown && picture.Texture is { } made &&
                shown.Width == made.Width && shown.Height == made.Height)
            {
                lock (shown) shown.SetPixels(0, 0, made.Width, made.Height, made.Pixels);
                picture.Texture = shown;
            }

            _made[path] = picture;
            _making.TryRemove(path, out _);
        }

        private static bool StillWanted(string path) => _asked.TryGetValue(path, out long at) && Environment.TickCount64 - at < 2000;

        /// <summary>A material's settings, as a number that changes when any of them do.</summary>
        internal static int SettingsOf(Material material)
        {
            var hash = new HashCode();
            hash.Add(material.Shading);
            hash.Add(material.Color.R);
            hash.Add(material.Color.G);
            hash.Add(material.Color.B);
            hash.Add(material.Color.A);
            hash.Add(material.Texture);
            hash.Add(material.Texture?.Version);
            hash.Add(material.NormalMap);
            hash.Add(material.NormalStrength);
            hash.Add(material.TextureScale);
            hash.Add(material.TextureOffset);
            hash.Add(material.Roughness);
            hash.Add(material.Metallic);
            hash.Add(material.Specular);
            hash.Add(material.Emission.R);
            hash.Add(material.Emission.G);
            hash.Add(material.Emission.B);
            hash.Add(material.EmissionStrength);
            hash.Add(material.Shader);
            hash.Add(material.Shader?.Version);
            foreach (ShaderValue value in material.Values)
            {
                hash.Add(value.Name);
                hash.Add(value.Value);
                hash.Add(value.Texture);
            }

            // Never 0, which means "not a material".
            return hash.ToHashCode() | 1;
        }

        /// <summary>An image shrunk to fit <see cref="Size"/>, averaging boxes of pixels so detail doesn't shimmer away.</summary>
        private static Texture Shrink(string path)
        {
            using FileStream stream = File.OpenRead(path);
            ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            float scale = MathF.Min(1f, (float)Size / MathF.Max(image.Width, image.Height));
            int width = Math.Max((int)(image.Width * scale), 1), height = Math.Max((int)(image.Height * scale), 1);
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                int y0 = y * image.Height / height, y1 = Math.Max((y + 1) * image.Height / height, y0 + 1);
                for (int x = 0; x < width; x++)
                {
                    int x0 = x * image.Width / width, x1 = Math.Max((x + 1) * image.Width / width, x0 + 1);
                    float r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = (sy * image.Width + sx) * 4;
                            float alpha = image.Data[i + 3] / 255f;
                            r += image.Data[i] * alpha;
                            g += image.Data[i + 1] * alpha;
                            b += image.Data[i + 2] * alpha;
                            a += image.Data[i + 3];
                            count++;
                        }
                    }

                    // Colours multiplied by their alpha, as the 2D layer draws them.
                    int o = (y * width + x) * 4;
                    pixels[o] = (byte)(r / count);
                    pixels[o + 1] = (byte)(g / count);
                    pixels[o + 2] = (byte)(b / count);
                    pixels[o + 3] = (byte)(a / count);
                }
            }

            return new Texture(width, height, pixels);
        }

        // ---------------------------------------------------------------- Kept on disk

        /// <summary>The file as it is now: where it is, how big, and when it was last written. A thumbnail's kept by this.</summary>
        private static string KeyOf(string path)
        {
            var file = new FileInfo(path);
            string text = $"{_format}|{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
        }

        private static string CacheFile(string key) => Path.Combine(CacheFolder, key + ".thumb");

        private static Texture? ReadCache(string key)
        {
            string file = CacheFile(key);
            if (!File.Exists(file)) return null;

            try
            {
                using FileStream stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);
                if (reader.ReadUInt32() != _magic) return null;
                int width = reader.ReadUInt16(), height = reader.ReadUInt16();
                byte[] pixels = new byte[width * height * 4];
                using var unzip = new ZLibStream(stream, CompressionMode.Decompress);
                unzip.ReadExactly(pixels);
                return new Texture(width, height, pixels);
            }
            catch (Exception)
            {
                // Half-written, or from something else: made again.
                return null;
            }
        }

        /// <summary>How many thumbnails are being written to disk: for tests, to wait until they're done.</summary>
        internal static int Writing => _writing;
        private static int _writing;

        /// <summary>Keeps a thumbnail on disk, written beside and then moved into place, so it's never read half-written.</summary>
        private static void WriteCache(string key, Texture texture)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                string file = CacheFile(key), part = file + "." + Environment.ProcessId + ".part";
                using (FileStream stream = File.Create(part))
                {
                    using var writer = new BinaryWriter(stream);
                    writer.Write(_magic);
                    writer.Write((ushort)texture.Width);
                    writer.Write((ushort)texture.Height);
                    writer.Flush();
                    using var zip = new ZLibStream(stream, CompressionLevel.Fastest);
                    lock (texture) zip.Write(texture.Pixels);
                }

                File.Move(part, file, overwrite: true);
                if (Interlocked.Exchange(ref _pruned, 1) == 0) Prune();
            }
            catch (Exception)
            {
                // Without the cache, it's just made again next time.
            }
            finally
            {
                Interlocked.Decrement(ref _writing);
            }
        }

        /// <summary>Writes a thumbnail to disk in the background (counted from now, not from when it starts).</summary>
        private static void SaveLater(string key, Texture texture)
        {
            Interlocked.Increment(ref _writing);
            Task.Run(() => WriteCache(key, texture));
        }

        /// <summary>Once a run: throws away thumbnails not made for three months, of files long changed or gone.</summary>
        private static void Prune()
        {
            DateTime before = DateTime.UtcNow.AddDays(-90);
            foreach (string file in Directory.EnumerateFiles(CacheFolder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < before) File.Delete(file);
                }
                catch (IOException)
                {
                    // In use by another editor: left for next time.
                }
            }
        }
    }
}
