using System.Text.Json;

namespace Vertex.Editor.Editing
{
    /// <summary>How the scene view snaps things moved with its handles. Ctrl does the opposite while held.</summary>
    public sealed class SnapSettings
    {
        [Tooltip("Snap while moving, turning and scaling with the handles. Hold Ctrl for the opposite.")]
        public bool Enabled { get; set; }

        [Tooltip("Positions snap to multiples of this, in metres.")]
        [Range(0.01f, 10f)]
        public float Move { get; set; } = 0.5f;

        [Tooltip("Turns snap to this many degrees.")]
        [Range(1f, 90f)]
        public float Rotate { get; set; } = 15f;

        [Tooltip("Scaling snaps to steps this big.")]
        [Range(0.01f, 2f)]
        public float Scale { get; set; } = 0.1f;
    }

    /// <summary>
    /// The editor's own settings, the same for every project: kept in the user's settings folder between runs.
    /// </summary>
    public sealed class Preferences
    {
        [Tooltip("How big the editor's text and controls are, on top of the screen's own scaling. Ctrl+= and Ctrl+- change it too.")]
        [Range(0.6f, 2f)]
        public float UiScale { get; set; } = 1f;

        [Tooltip("Makes everything bigger on big, sharp screens the system doesn't scale up itself (a 1440p, ultrawide or 4K monitor at 100%)")]
        public bool AutoSize { get; set; } = true;

        [Tooltip("How quickly the scene view's camera eases to where it's going: higher is snappier.")]
        [Range(4f, 40f)]
        public float CameraSmoothing { get; set; } = 16f;

        [Tooltip("How fast the scene view's camera flies with WASD, in metres a second.")]
        [Range(0.5f, 100f)]
        public float FlySpeed { get; set; } = 8f;

        [Tooltip("Open the last project when the editor starts.")]
        public bool OpenLastProject { get; set; } = true;

        public SnapSettings Snapping { get; set; } = new();

        [HideInInspector] public bool ShowGrid { get; set; } = true;

        /// <summary>Whether the scene view shows the game camera's post effects, so it looks as the game will.</summary>
        [HideInInspector] public bool ShowEffects { get; set; } = true;
        [HideInInspector] public float OutlinerWidth { get; set; } = 270f;
        [HideInInspector] public float InspectorWidth { get; set; } = 340f;
        [HideInInspector] public float AssetsHeight { get; set; } = 230f;
        [HideInInspector] public float AssetTileSize { get; set; } = 88f;
        [HideInInspector] public List<string> RecentProjects { get; set; } = new();

        // Which panels are docked where, as tabs, and which tab each place shows. A panel in none of them is closed.
        [HideInInspector] public List<string> LeftTabs { get; set; } = ["Outliner"];
        [HideInInspector] public List<string> RightTabs { get; set; } = ["Inspector"];
        [HideInInspector] public List<string> BottomTabs { get; set; } = ["Assets", "Console"];
        [HideInInspector] public Dictionary<string, string> ActiveTabs { get; set; } = new();

        [Tooltip("Empties the Console each time play starts, so it only has what this run of the game wrote")]
        public bool ClearConsoleOnPlay { get; set; } = true;

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex", "editor.json");

        private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

        // Only the editor's own preferences (from Load) are written back to the file: ones made in code, as tests do,
        // never overwrite what someone has set.
        private bool _fromFile;

        public static Preferences Load()
        {
            Preferences preferences = new();
            try
            {
                if (File.Exists(FilePath)) preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), _options) ?? new Preferences();
            }
            catch (Exception)
            {
                // Unreadable: start again from the defaults.
            }

            preferences._fromFile = true;
            return preferences;
        }

        public void Save()
        {
            if (!_fromFile) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _options));
            }
            catch (Exception)
            {
                // Not being able to save preferences isn't worth interrupting anyone for.
            }
        }

        /// <summary>Puts a project at the top of the recent list.</summary>
        public void Remember(string projectFile)
        {
            RecentProjects.RemoveAll(path => string.Equals(path, projectFile, StringComparison.OrdinalIgnoreCase));
            RecentProjects.Insert(0, projectFile);
            if (RecentProjects.Count > 8) RecentProjects.RemoveRange(8, RecentProjects.Count - 8);
            Save();
        }
    }
}
