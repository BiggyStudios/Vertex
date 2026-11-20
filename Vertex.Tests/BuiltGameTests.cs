using System.Reflection;

namespace Vertex.Tests
{
    /// <summary>What a game the editor built starts with: its settings file, and which game it runs.</summary>
    public class BuiltGameTests : EngineTest
    {
        [Fact]
        public void A_built_games_settings_come_back_from_their_file_as_they_were_written()
        {
            var settings = new PlayerSettings
            {
                Name = "Coin Grab",
                Company = "Biggy",
                Version = "1.2 beta",
                StartScene = "Scenes/Main.scene",
                Icon = "Icon.png",
                WindowMode = WindowMode.Fullscreen,
                Width = 1600,
                Height = 900,
                Resizable = false,
                Renderer = RendererChoice.OpenGL,
                PhysicsRate = 120
            };

            string json = settings.ToJson();
            Assert.Contains("\"Fullscreen\"", json);
            PlayerSettings loaded = PlayerSettings.FromJson(json);
            Assert.Equivalent(settings, loaded);

            // A file from an older build, without the newer settings, gets their defaults.
            PlayerSettings old = PlayerSettings.FromJson("""{ "Name": "Old" }""");
            Assert.Equal("Old", old.Name);
            Assert.Equal(WindowMode.Windowed, old.WindowMode);
            Assert.Equal(60, old.PhysicsRate);
        }

        [Fact]
        public void A_built_game_without_code_of_its_own_opens_its_start_scene()
        {
            Game game = BuiltGame.CreateGame(new PlayerSettings { StartScene = "Scenes/Main.scene" }, gameAssembly: null);
            Assert.Equal(typeof(Game), game.GetType());
            Assert.Equal("Scenes/Main.scene", game.StartScene);

            // Code that isn't there: the start scene still.
            Assert.Equal("Scenes/Main.scene", BuiltGame.CreateGame(new PlayerSettings { StartScene = "Scenes/Main.scene" }, "NoSuchGame").StartScene);
        }

        [Fact]
        public void A_built_games_window_and_sound_libraries_are_found_where_dotnet_unpacked_them()
        {
            // A built game is one file: .NET unpacks its native libraries into a folder and says where.
            string folder = Path.Combine(Path.GetTempPath(), "VertexUnpacked", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string library = Path.Combine(folder, "glfw-unpacked-test.dll");
            File.WriteAllText(library, "");
            object? before = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES");
            try
            {
                AppContext.SetData("NATIVE_DLL_SEARCH_DIRECTORIES", folder + Path.PathSeparator);
                BuiltGame.FindPackedLibraries();
                Assert.Contains(library, Silk.NET.Core.Loader.PathResolver.Default.EnumeratePossibleLibraryLoadTargets("glfw-unpacked-test.dll"));
            }
            finally
            {
                AppContext.SetData("NATIVE_DLL_SEARCH_DIRECTORIES", before);
                Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void The_engine_records_the_packages_it_uses_for_the_editor_to_build_games_with()
        {
            Dictionary<string, string?> packages = typeof(GameObject).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key.StartsWith("Vertex.Package.", StringComparison.Ordinal))
                .ToDictionary(a => a.Key["Vertex.Package.".Length..], a => a.Value);

            Assert.Contains("Silk.NET.Windowing", packages.Keys);
            Assert.Contains("JoltPhysicsSharp", packages.Keys);
            Assert.All(packages.Values, version => Assert.False(string.IsNullOrEmpty(version)));
        }
    }
}
