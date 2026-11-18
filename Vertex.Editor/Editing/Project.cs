using System.Text.Json;

namespace Vertex.Editor.Editing
{
    /// <summary>A project's settings, kept in its .vertexproject file and changed in File > Project Settings.</summary>
    public sealed class ProjectSettings
    {
        [Tooltip("The game's name.")]
        public string Name { get; set; } = "New Game";

        [Tooltip("This build's version.")]
        public string Version { get; set; } = "0.1";

        [Tooltip("The scene opened first, inside the Assets folder (Scenes/Main.scene). Projects with game code open with what the game builds instead.")]
        public string StartupScene { get; set; } = "";

        [Tooltip("The game's code: a .csproj file, next to this project's file. Its components show in Add Component, and Play runs it.")]
        public string GameProject { get; set; } = "";

        [Tooltip("Physics steps a second. FixedUpdate runs this often.")]
        [Range(10f, 240f)]
        public int PhysicsRate { get; set; } = 60;

        [Tooltip("How detailed the sun's shadows are, in texels across each shadow map.")]
        [Range(256f, 4096f)]
        public int ShadowResolution { get; set; } = 2048;
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

        private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

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
