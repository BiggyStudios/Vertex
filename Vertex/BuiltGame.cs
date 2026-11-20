using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Vertex.Rendering;

namespace Vertex
{
    /// <summary>How a game's window opens.</summary>
    public enum WindowMode
    {
        /// <summary>A window of its own size, on the desktop.</summary>
        Windowed,

        /// <summary>A window filling the screen but for the taskbar, with its frame.</summary>
        Maximized,

        /// <summary>The whole screen, without a frame (borderless, so Alt+Tab is instant).</summary>
        Fullscreen
    }

    /// <summary>Which graphics API a game prefers. Players can still choose by starting it with <c>-vulkan</c> or <c>-opengl</c>.</summary>
    public enum RendererChoice
    {
        /// <summary>Vulkan wherever the GPU supports it, otherwise OpenGL.</summary>
        Automatic,

        /// <summary>OpenGL, for a game that wants to run the same on every computer it can.</summary>
        OpenGL
    }

    /// <summary>
    /// What a built game is (its name and version) and how it opens: its window, its icon, the scene it starts in. The
    /// editor writes it beside the game's program when it builds it (game.json), from the project's settings; see
    /// <see cref="BuiltGame"/>.
    /// </summary>
    public sealed class PlayerSettings
    {
        public string Name { get; set; } = "Game";
        public string Company { get; set; } = "";
        public string Version { get; set; } = "0.1";

        /// <summary>The scene it starts in, inside the Assets folder ("Scenes/Main.scene"), for a game with no <see cref="Game"/> class of its own.</summary>
        public string StartScene { get; set; } = "";

        /// <summary>A picture in the Assets folder ("Icon.png") for the window's icon; empty for the engine's.</summary>
        public string Icon { get; set; } = "";

        public WindowMode WindowMode { get; set; } = WindowMode.Windowed;
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 720;
        public bool Resizable { get; set; } = true;
        public RendererChoice Renderer { get; set; } = RendererChoice.Automatic;
        public int PhysicsRate { get; set; } = 60;
        public int ShadowResolution { get; set; } = 2048;

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public string ToJson() => JsonSerializer.Serialize(this, _json);

        public static PlayerSettings FromJson(string json) => JsonSerializer.Deserialize<PlayerSettings>(json, _json) ?? new PlayerSettings();
    }

    /// <summary>
    /// Starts a game the editor has built (File › Build Game): its program does nothing but call <see cref="Run(string?)"/>. The
    /// game's settings come from game.json beside it, and its code (if it has any) from the game's own assembly.
    /// </summary>
    public static class BuiltGame
    {
        /// <summary>The settings file beside a built game's program.</summary>
        public const string SettingsFile = "game.json";

        /// <summary>
        /// Opens the game's window and runs it until it's closed. <paramref name="gameAssembly"/> names the game's code: its
        /// <see cref="Game"/> class, if it has one, runs; otherwise it opens in its start scene.
        /// </summary>
        public static void Run(string? gameAssembly)
        {
            Application.BaseDirectory = AppContext.BaseDirectory;
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            string file = Path.Combine(AppContext.BaseDirectory, SettingsFile);
            PlayerSettings settings = File.Exists(file) ? PlayerSettings.FromJson(File.ReadAllText(file)) : new PlayerSettings();
            Run(settings, gameAssembly);
        }

        /// <summary>Runs a game with <paramref name="settings"/>, as <see cref="Run(string?)"/> does with its settings file.</summary>
        public static void Run(PlayerSettings settings, string? gameAssembly)
        {
            Application.Name = settings.Name;
            Application.Company = settings.Company;
            Application.Version = settings.Version;
            Time.FixedDeltaTime = 1f / Math.Clamp(settings.PhysicsRate, 10, 240);

            Game game = CreateGame(settings, gameAssembly);
            var window = new VertexWindow(game, settings);
            window.Renderer.ShadowResolution = Math.Clamp(settings.ShadowResolution, 256, 4096);
            window.Run();
        }

        /// <summary>The game's own <see cref="Game"/> class, or one that opens the start scene.</summary>
        internal static Game CreateGame(PlayerSettings settings, string? gameAssembly)
        {
            if (!string.IsNullOrEmpty(gameAssembly) && GameType(gameAssembly) is { } type) return (Game)Activator.CreateInstance(type)!;
            return new Game { StartScene = settings.StartScene };
        }

        private static Type? GameType(string gameAssembly)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(gameAssembly);
            }
            catch (Exception exception)
            {
                Log.Error($"Couldn't load the game's code ({gameAssembly}): {exception.Message}");
                return null;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(t => t != null).ToArray()!;
            }

            return types.FirstOrDefault(t => t.IsSubclassOf(typeof(Game)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null);
        }
    }
}
