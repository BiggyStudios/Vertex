namespace Vertex
{
    /// <summary>The running program as a whole.</summary>
    public static class Application
    {
        /// <summary>Raised by <see cref="Quit"/>. Whatever hosts the game (its window, an editor) closes it.</summary>
        internal static event Action? QuitRequested;

        /// <summary>
        /// The folder the game's files are in: its Assets folder is here. Normally where the game was started from; an
        /// editor hosting the game points it at the game's own build folder. Load assets from here rather than
        /// <see cref="AppContext.BaseDirectory"/>, so the game finds them however it's run.
        /// </summary>
        public static string BaseDirectory { get; set; } = AppContext.BaseDirectory;

        /// <summary>A path inside the game's Assets folder: <c>Application.AssetPath("Fonts", "Main.ttf")</c>.</summary>
        public static string AssetPath(params string[] parts) => Path.Combine([BaseDirectory, "Assets", .. parts]);

        private static string[]? _assetFolders;

        /// <summary>
        /// Where files that saved scenes, materials and prefabs refer to are looked for, in order. Normally just the game's
        /// Assets folder; an editor adds the project's own, which has files the game's last build hasn't copied yet.
        /// </summary>
        public static string[] AssetFolders
        {
            get => _assetFolders is { Length: > 0 } folders ? folders : [AssetPath()];
            set => _assetFolders = value;
        }

        /// <summary>Closes the game at the end of this frame. In an editor's play mode, it stops playing instead.</summary>
        public static void Quit() => QuitRequested?.Invoke();
    }
}
