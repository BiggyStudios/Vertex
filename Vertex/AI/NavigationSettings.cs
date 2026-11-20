namespace Vertex.AI
{
    /// <summary>
    /// How characters find their way round a scene (see <see cref="Scene.Navigation"/>): the size of the characters its
    /// navmesh is made for, how steep and high a step they can walk up, and the navmesh baked for it. In the editor, under
    /// View › Navigation.
    /// </summary>
    public class NavigationSettings
    {
        /// <summary>How wide the characters are, from their middle: the navmesh keeps this far from walls and drops.</summary>
        [Tooltip("How far from their middle the characters reach: the navmesh keeps this far from walls and edges")]
        public float AgentRadius { get; set; } = 0.4f;

        /// <summary>How tall the characters are: they don't go under anything lower.</summary>
        [Tooltip("How tall the characters are: they don't go under anything lower")]
        public float AgentHeight { get; set; } = 1.8f;

        /// <summary>The highest step they walk up (or down) without a slope: kerbs and stairs.</summary>
        [Tooltip("The highest step they walk up or down: kerbs and stairs")]
        public float StepHeight { get; set; } = 0.45f;

        /// <summary>The steepest slope they walk on, in degrees.</summary>
        [Range(0f, 80f)]
        [Tooltip("The steepest slope they walk on, in degrees")]
        public float MaxSlope { get; set; } = 45f;

        /// <summary>
        /// How fine the navmesh is: the size of the squares it's made of, in units. Smaller follows the ground and fits
        /// through narrower gaps, and takes longer to make.
        /// </summary>
        [Tooltip("The size of the squares the navmesh is made of: smaller fits through narrower gaps and takes longer to make")]
        public float CellSize { get; set; } = 0.2f;

        /// <summary>
        /// Patches smaller than this many square units are left out: the tops of crates and tables, which nothing could
        /// walk onto anyway.
        /// </summary>
        [Tooltip("Patches smaller than this (in square units) are left out: the tops of crates and tables")]
        public float MinArea { get; set; } = 1f;

        /// <summary>The scene's navmesh (a .navmesh file, from the Assets folder), made by baking; empty if it hasn't been.</summary>
        [HideInInspector]
        public string NavMeshFile { get; set; } = "";

        private NavMesh? _built;

        /// <summary>
        /// The scene's navmesh: one given it (<see cref="NavMesh.Build"/> in code, for a level made as the game runs), or
        /// the baked one in <see cref="NavMeshFile"/>, read the first time it's asked for; null if there's neither.
        /// </summary>
        [HideInInspector]
        [Serialization.DontSave]
        public NavMesh? NavMesh
        {
            get
            {
                if (_built != null) return _built;
                if (NavMeshFile.Length == 0 || Assets.FindFile(NavMeshFile) is not { } file) return null;
                return Assets.LoadNavMesh(file);
            }
            set => _built = value;
        }
    }
}
