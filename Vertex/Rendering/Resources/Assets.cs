using Vertex.Rendering;

namespace Vertex
{
    /// <summary>
    /// Files loaded once and shared: asking for the same texture again gives the one already loaded, so a hundred
    /// objects using it share one copy on the GPU.
    /// </summary>
    public static class Assets
    {
        private static readonly Dictionary<string, Texture> _textures = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The texture in <paramref name="path"/>, loaded the first time it's asked for.</summary>
        public static Texture LoadTexture(string path)
        {
            string full = Path.GetFullPath(path);
            if (_textures.TryGetValue(full, out Texture? texture)) return texture;

            texture = Texture.Load(full);
            _textures[full] = texture;
            return texture;
        }

        /// <summary>Forgets a loaded file, so it's read again next time: after it's been changed on disk.</summary>
        public static void Forget(string path) => _textures.Remove(Path.GetFullPath(path));
    }
}
