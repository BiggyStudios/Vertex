using System.Text.Json;

using Vertex.Editor.Inspection;

namespace Vertex.Editor.Editing
{
    /// <summary>A project's settings, kept in its .vertexproject file and changed in File > Project Settings.</summary>
    public sealed class ProjectSettings
    {
        [Header("Game")]
        [Tooltip("The game's name: its window's title, and its program's name when it's built.")]
        public string Name { get; set; } = "New Game";

        [Tooltip("Who's making it: in the built game's details.")]
        public string Company { get; set; } = "";

        [Tooltip("This build's version (0.1, 1.2.3): in the built game's details, and Application.Version for a title screen.")]
        public string Version { get; set; } = "0.1";

        [Tooltip("A square picture (a PNG, 256 × 256 or bigger) for the game's program, its window and the taskbar.")]
        [AssetPath(AssetKind.Texture, "The engine's")]
        public string Icon { get; set; } = "";

        [Header("Starting")]
        [Tooltip("The scene opened first. A game whose code builds its world (a Game class with no StartScene) opens with what it builds instead.")]
        [AssetPath(AssetKind.Scene)]
        public string StartupScene { get; set; } = "";

        [Tooltip("The game's code: a .csproj file, next to this project's file. Its components show in Add Component, and Play runs it.")]
        public string GameProject { get; set; } = "";

        [Header("Window")]
        [Tooltip("How the built game's window opens: a window, filling the screen with its frame, or the whole screen")]
        public WindowMode WindowMode { get; set; } = WindowMode.Windowed;

        [Tooltip("The window's width and height when it's a window, in pixels.")]
        [Range(320f, 3840f)]
        public int Width { get; set; } = 1280;

        [Range(240f, 2160f)]
        public int Height { get; set; } = 720;

        [Tooltip("Whether players can resize the window by dragging its edges.")]
        public bool Resizable { get; set; } = true;

        [Tooltip("Which graphics API it uses: Vulkan wherever it can (and OpenGL otherwise), or always OpenGL. Players can still choose, starting it with -vulkan or -opengl.")]
        public RendererChoice Renderer { get; set; } = RendererChoice.Automatic;

        [Header("Physics and graphics")]
        [Tooltip("Physics steps a second. FixedUpdate runs this often.")]
        [Range(10f, 240f)]
        public int PhysicsRate { get; set; } = 60;

        [Tooltip("How detailed the sun's shadows are, in texels across each shadow map.")]
        [Range(256f, 4096f)]
        public int ShadowResolution { get; set; } = 2048;

        // Building (File › Build Game): where to, and for which systems. Set in the Build Game window.
        [HideInInspector] public string BuildFolder { get; set; } = "Builds";
        [HideInInspector] public bool BuildWindows { get; set; } = true;
        [HideInInspector] public bool BuildLinux { get; set; }
        [HideInInspector] public bool BuildMac { get; set; }

        /// <summary>Builds made for testing: the frame rate and times on F3, the console, and the game's code built for debugging.</summary>
        [HideInInspector] public bool DebugBuild { get; set; }

        /// <summary>What the built game reads when it starts (its game.json).</summary>
        public PlayerSettings ToPlayerSettings() => new()
        {
            Name = Name.Trim().Length > 0 ? Name.Trim() : "Game",
            Company = Company.Trim(),
            Version = Version.Trim(),
            StartScene = StartupScene,
            Icon = Icon,
            WindowMode = WindowMode,
            Width = Width,
            Height = Height,
            Resizable = Resizable,
            Renderer = Renderer,
            PhysicsRate = PhysicsRate,
            ShadowResolution = ShadowResolution,
            DebugBuild = DebugBuild
        };
    }

    /// <summary>
    /// A game being made in the editor: a folder with a .vertexproject file, an Assets folder for textures, models,
    /// sounds and scenes, and optionally the game's code (see <see cref="ProjectSettings.GameProject"/>).
    /// </summary>
    public sealed class Project
    {
        public string FilePath { get; }
        public string Directory => Path.GetDirectoryName(FilePath)!;
        public string AssetsDirectory => Path.Combine(Directory, "Assets");
        public ProjectSettings Settings { get; private set; } = new();

        public string Name => Settings.Name.Length > 0 ? Settings.Name : Path.GetFileNameWithoutExtension(FilePath);

        /// <summary>The game's .csproj, if it has code.</summary>
        public string? GameProjectFile =>
            Settings.GameProject.Length > 0 ? Path.GetFullPath(Path.Combine(Directory, Settings.GameProject)) : null;

        public const string Extension = ".vertexproject";

        private static readonly JsonSerializerOptions _options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        private Project(string filePath)
        {
            FilePath = Path.GetFullPath(filePath);
        }

        public static Project Load(string filePath)
        {
            var project = new Project(filePath);
            project.Settings = JsonSerializer.Deserialize<ProjectSettings>(File.ReadAllText(filePath), _options) ?? new ProjectSettings();
            return project;
        }

        /// <summary>Makes a new project file with an Assets folder beside it (and a Scenes folder in that).</summary>
        public static Project Create(string filePath, string name, string gameProject = "")
        {
            var project = new Project(filePath) { Settings = new ProjectSettings { Name = name, GameProject = gameProject } };
            System.IO.Directory.CreateDirectory(Path.Combine(project.AssetsDirectory, "Scenes"));
            project.Save();
            return project;
        }

        /// <summary>
        /// The project for whatever was picked: a .vertexproject file; a game's .csproj (whose project file is made beside
        /// it, if there isn't one, with the game's code set up); or a folder with either in it.
        /// </summary>
        public static Project Open(string path)
        {
            if (System.IO.Directory.Exists(path))
            {
                string? file = System.IO.Directory.GetFiles(path, "*" + Extension).FirstOrDefault()
                               ?? System.IO.Directory.GetFiles(path, "*.csproj").FirstOrDefault()
                               ?? throw new FileNotFoundException("There's no project or game in that folder.");
                return Open(file);
            }

            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
                string? existing = System.IO.Directory.GetFiles(folder, "*" + Extension).FirstOrDefault();
                if (existing != null) return Load(existing);

                string name = Path.GetFileNameWithoutExtension(path);
                return Create(Path.Combine(folder, name + Extension), name, Path.GetFileName(path));
            }

            return Load(path);
        }

        public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, _options));

        /// <summary>
        /// Gives a project without code a .csproj of its own, beside its project file, for scripts to go in, and returns
        /// where it is. A project that has code keeps what it has.
        /// </summary>
        public string AddCode()
        {
            if (GameProjectFile is { } existing && File.Exists(existing)) return existing;

            string name = new string(Name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
            if (name.Length == 0) name = "Game";
            string file = Path.Combine(Directory, name + ".csproj");
            if (!File.Exists(file)) File.WriteAllText(file, GameCode.NewProjectFile(name));

            Settings.GameProject = Path.GetFileName(file);
            Save();
            return file;
        }

        /// <summary>Where to look for a scene's textures and models: the project's Assets, then the game's built copy of them.</summary>
        public string[] AssetFolders(string? gameOutput) =>
            gameOutput != null ? [AssetsDirectory, Path.Combine(gameOutput, "Assets")] : [AssetsDirectory];
    }
}
