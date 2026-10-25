using Vertex.UI;

namespace SampleGame
{
    /// <summary>The game's fonts: Noto Sans (SIL Open Font License, see Assets/Fonts/OFL.txt).</summary>
    public static class Fonts
    {
        private static readonly string _folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

        public static Font Regular { get; } = Font.Load(Path.Combine(_folder, "NotoSans-Regular.ttf"));
        public static Font Bold { get; } = Font.Load(Path.Combine(_folder, "NotoSans-Bold.ttf"));
    }
}
