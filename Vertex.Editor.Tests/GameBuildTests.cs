using System.Text;

using Vertex.Editor.Editing;

namespace Vertex.Editor.Tests
{
    /// <summary>Building a game to give to players: its settings, its icons, and the program that starts it.</summary>
    public class GameBuildTests : EditorTest
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "VertexBuildTests", Guid.NewGuid().ToString("N"));

        public GameBuildTests()
        {
            Directory.CreateDirectory(_folder);
        }

        public override void Dispose()
        {
            base.Dispose();
            Directory.Delete(_folder, recursive: true);
        }

        /// <summary>A picture <paramref name="width"/> by <paramref name="height"/>, solid red, as a PNG file.</summary>
        private string Picture(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 3] = 255;
            }

            // Saved square, as the icons are made from.
            string path = Path.Combine(_folder, $"Picture{width}x{height}.png");
            byte[] square = GameIcons.Square(pixels, width, height, Math.Max(width, height));
            File.WriteAllBytes(path, GameIcons.Png(square, Math.Max(width, height)));
            return path;
        }

        [Fact]
        public void A_windows_icon_holds_the_picture_at_each_size_from_16_to_256()
        {
            string ico = Path.Combine(_folder, "game.ico");
            GameIcons.WriteIco(ico, Picture(300, 300));

            using var reader = new BinaryReader(File.OpenRead(ico));
            Assert.Equal(0, reader.ReadUInt16());
            Assert.Equal(1, reader.ReadUInt16());
            int count = reader.ReadUInt16();
            Assert.Equal(7, count);

            var sizes = new List<int>();
            for (int i = 0; i < count; i++)
            {
                byte width = reader.ReadByte();
                reader.ReadBytes(7);
                int length = reader.ReadInt32();
                int offset = reader.ReadInt32();
                sizes.Add(width == 0 ? 256 : width);

                // Each is a PNG of that size.
                long back = reader.BaseStream.Position;
                reader.BaseStream.Position = offset;
                byte[] png = reader.ReadBytes(length);
                Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
                (byte[] _, int w, int h) = LoadPng(png);
                Assert.Equal((sizes[^1], sizes[^1]), (w, h));
                reader.BaseStream.Position = back;
            }

            Assert.Equal([16, 24, 32, 48, 64, 128, 256], sizes);
        }

        private (byte[] Pixels, int Width, int Height) LoadPng(byte[] png)
        {
            string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, png);
            return GameIcons.Load(path);
        }

        [Fact]
        public void A_mac_icon_holds_the_picture_up_to_1024_and_says_how_long_it_is()
        {
            string icns = Path.Combine(_folder, "icon.icns");
            GameIcons.WriteIcns(icns, Picture(64, 64));
            byte[] file = File.ReadAllBytes(icns);
            Assert.Equal("icns", Encoding.ASCII.GetString(file, 0, 4));
            int length = file[4] << 24 | file[5] << 16 | file[6] << 8 | file[7];
            Assert.Equal(file.Length, length);
            Assert.Equal("icp4", Encoding.ASCII.GetString(file, 8, 4));
            Assert.Contains("ic10", Encoding.ASCII.GetString(file));
        }

        [Fact]
        public void A_picture_that_isnt_square_is_fitted_into_the_icon_with_the_rest_left_clear()
        {
            var pixels = new byte[200 * 100 * 4];
            for (int i = 0; i < pixels.Length; i += 4) pixels[i + 3] = 255;
            byte[] square = GameIcons.Square(pixels, 200, 100, 64);

            // Solid across the middle, clear above and below.
            Assert.Equal(255, square[(32 * 64 + 32) * 4 + 3]);
            Assert.Equal(0, square[(2 * 64 + 32) * 4 + 3]);
            Assert.Equal(0, square[(61 * 64 + 32) * 4 + 3]);
        }

        [Fact]
        public void The_starting_program_is_built_self_contained_for_the_system_with_the_engines_packages_the_games_code_and_its_icon()
        {
            var settings = new ProjectSettings { Name = "Coin & Grab", Company = "Biggy", Version = "1.2 beta" };
            string project = GameBuilder.PlayerProject(settings, "linux-x64", @"C:\Games\Coin\Coin.csproj", @"C:\Temp\game.ico");

            Assert.Contains("<RuntimeIdentifier>linux-x64</RuntimeIdentifier>", project);
            Assert.Contains("<SelfContained>true</SelfContained>", project);

            // One program file, with the runtime and the native libraries in it, not a folder full of them.
            Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project);
            Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", project);
            Assert.Contains("<Product>Coin &amp; Grab</Product>", project);
            Assert.Contains("<Version>1.2</Version>", project);
            Assert.Contains("<InformationalVersion>1.2 beta</InformationalVersion>", project);
            Assert.Contains("<ApplicationIcon>C:\\Temp\\game.ico</ApplicationIcon>", project);
            Assert.Contains("""<ProjectReference Include="C:\Games\Coin\Coin.csproj" />""", project);
            Assert.Contains("""<PackageReference Include="Silk.NET.Windowing" """, project);
            Assert.Contains(typeof(GameObject).Assembly.Location, project);

            Assert.Contains("Vertex.BuiltGame.Run(\"Coin\");", GameBuilder.PlayerProgram("Coin"));
            Assert.Contains("Vertex.BuiltGame.Run(null);", GameBuilder.PlayerProgram(null));
        }

        [Theory]
        [InlineData("0.1", "0.1")]
        [InlineData("1.2 beta", "1.2")]
        [InlineData("2.0.3.4.5", "2.0.3.4")]
        [InlineData("v3", "1.0.0")]
        [InlineData("7", "7.0")]
        public void A_version_is_made_one_a_programs_details_can_take(string version, string expected)
        {
            Assert.Equal(expected, GameBuilder.NumericVersion(version));
        }

        [Fact]
        public void The_assets_go_with_the_game_but_not_its_code()
        {
            string assets = Path.Combine(_folder, "Assets");
            Directory.CreateDirectory(Path.Combine(assets, "Scripts"));
            Directory.CreateDirectory(Path.Combine(assets, "Scenes"));
            File.WriteAllText(Path.Combine(assets, "Scenes", "Main.scene"), "{}");
            File.WriteAllText(Path.Combine(assets, "Scripts", "Player.cs"), "class Player {}");
            File.WriteAllText(Path.Combine(assets, "Scripts", "Player.vscript"), "{}");

            string to = Path.Combine(_folder, "Out");
            GameBuilder.CopyAssets(assets, to);
            Assert.True(File.Exists(Path.Combine(to, "Scenes", "Main.scene")));
            Assert.True(File.Exists(Path.Combine(to, "Scripts", "Player.vscript")));
            Assert.False(File.Exists(Path.Combine(to, "Scripts", "Player.cs")));
        }

        [Fact]
        public void A_builds_assets_are_packed_into_one_file_unless_theyre_to_be_a_folder()
        {
            string assets = Path.Combine(_folder, "Assets");
            Directory.CreateDirectory(Path.Combine(assets, "Scripts"));
            Directory.CreateDirectory(Path.Combine(assets, "Textures"));
            File.WriteAllText(Path.Combine(assets, "Textures", "Brick.png"), "pretend picture");
            File.WriteAllText(Path.Combine(assets, "Scripts", "Player.cs"), "class Player {}");
            Assert.False(new ProjectSettings().AssetsAsFolder);

            // Packed (as builds are, unless told otherwise): no Assets folder, and the game's code left out of the pack too.
            string packed = Path.Combine(_folder, "Packed");
            Directory.CreateDirectory(Path.Combine(packed, "Assets", "Textures"));
            File.WriteAllText(Path.Combine(packed, "Assets", "Textures", "Brick.png"), "copied there by the game's code project");
            GameBuilder.PutAssets(assets, packed, asFolder: false);
            Assert.False(Directory.Exists(Path.Combine(packed, "Assets")));
            using (AssetPack pack = AssetPack.Open(Path.Combine(packed, AssetPack.FileName)))
            {
                Assert.Equal(["Textures/Brick.png"], pack.Files);
                Assert.Equal("pretend picture", System.Text.Encoding.UTF8.GetString(pack.Read("Textures/Brick.png")));
            }

            // As a folder: the files as they are.
            string loose = Path.Combine(_folder, "Loose");
            Directory.CreateDirectory(loose);
            GameBuilder.PutAssets(assets, loose, asFolder: true);
            Assert.False(File.Exists(Path.Combine(loose, AssetPack.FileName)));
            Assert.Equal("pretend picture", File.ReadAllText(Path.Combine(loose, "Assets", "Textures", "Brick.png")));
        }

        [Fact]
        public void A_project_needs_somewhere_to_start_before_it_can_be_built()
        {
            Project project = Project.Create(Path.Combine(_folder, "Game.vertexproject"), "Game");
            Assert.Contains("Startup Scene", GameBuilder.Problem(project));

            project.Settings.StartupScene = "Scenes/Main.scene";
            Assert.Contains("isn't in the Assets folder", GameBuilder.Problem(project));

            File.WriteAllText(Path.Combine(project.AssetsDirectory, "Scenes", "Main.scene"), "{}");
            Assert.Null(GameBuilder.Problem(project));
            Assert.EndsWith(Path.Combine("Builds", "macOS"), GameBuilder.FolderFor(project, BuildPlatform.Mac));
        }

        [Fact]
        public void Project_settings_keep_the_games_window_and_icon_and_hand_them_to_the_built_game()
        {
            string path = Path.Combine(_folder, "Game.vertexproject");
            Project project = Project.Create(path, "Game");
            project.Settings.Icon = "Icon.png";
            project.Settings.WindowMode = WindowMode.Maximized;
            project.Settings.Renderer = RendererChoice.OpenGL;
            project.Settings.BuildLinux = true;
            project.Save();
            Assert.Contains("\"Maximized\"", File.ReadAllText(path));

            Project loaded = Project.Load(path);
            Assert.Equal(WindowMode.Maximized, loaded.Settings.WindowMode);
            Assert.True(loaded.Settings.BuildLinux);
            PlayerSettings player = loaded.Settings.ToPlayerSettings();
            Assert.Equal(("Game", "Icon.png", RendererChoice.OpenGL), (player.Name, player.Icon, player.Renderer));

            // A debug build says so to the game, and builds its code for debugging.
            Assert.Equal("Release", GameBuilder.Configuration(loaded.Settings));
            loaded.Settings.DebugBuild = true;
            Assert.True(loaded.Settings.ToPlayerSettings().DebugBuild);
            Assert.Equal("Debug", GameBuilder.Configuration(loaded.Settings));

            // A project from before these settings still opens, with their defaults.
            File.WriteAllText(path, """{ "Name": "Old", "Version": "0.1", "StartupScene": "", "GameProject": "", "PhysicsRate": 60, "ShadowResolution": 2048 }""");
            Assert.Equal(WindowMode.Windowed, Project.Load(path).Settings.WindowMode);
        }
    }
}
