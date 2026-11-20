using Vertex.AI;
using Vertex.Editor.Gui;
using Vertex.Rendering;

namespace Vertex.Editor
{
    // The open scene's navmesh: baking it (View › Navigation) off the main thread, keeping it in a file beside the scene,
    // and showing it in the scene view.
    public sealed partial class EditorContext
    {
        private Task<NavMesh>? _navBake;
        private CancellationTokenSource? _navBakeCancel;
        private Scene? _navBakeScene;
        private string _navBakeFile = "";
        private readonly System.Diagnostics.Stopwatch _navBakeTimer = new();

        /// <summary>Whether the scene's navmesh is being baked.</summary>
        public bool BakingNavMesh => _navBake != null;

        /// <summary>Where the open scene's navmesh is kept: a .navmesh file beside it, named after it.</summary>
        public string? NavMeshPath => ScenePath != null ? Path.ChangeExtension(ScenePath, NavMesh.Extension) : null;

        /// <summary>
        /// Bakes the open scene's navmesh (see <see cref="NavMeshBuilder"/>): in the background, then saves it beside the
        /// scene, names it in the scene and saves the scene, so the two always match.
        /// </summary>
        public void BakeNavMesh()
        {
            if (_navBake != null) return;
            if (Play.Playing) Play.Stop();
            if (NavMeshPath is not { } file)
            {
                Dialog.Message("Save the scene first", "The navmesh is kept in a file beside the scene, so the scene needs saving before it can be baked.");
                return;
            }

            NavMeshBuilder builder = NavMeshBuilder.Prepare(Scene);
            if (builder.Objects == 0)
            {
                Dialog.Message("Nothing to walk on", "The navmesh is made from what never moves: give the floors and walls colliders (without a Rigidbody), or tick Static on their Mesh Renderers, then bake.");
                return;
            }

            _navBakeCancel = new CancellationTokenSource();
            CancellationToken cancel = _navBakeCancel.Token;
            _navBakeScene = Scene;
            _navBakeFile = file;
            _navBakeTimer.Restart();
            _navBake = Task.Run(() => builder.Build(cancel), cancel);
        }

        /// <summary>Stops baking the navmesh: the scene keeps the one it had.</summary>
        public void CancelNavMeshBake() => _navBakeCancel?.Cancel();

        /// <summary>Takes away the scene's navmesh and its file: agents make one as the game starts instead.</summary>
        public void ClearNavMesh()
        {
            if (_navBake != null) return;
            if (NavMeshPath is { } file && File.Exists(file))
            {
                File.Delete(file);
                Vertex.Assets.Forget(file);
            }

            Scene.Navigation.NavMeshFile = "";
            Scene.Navigation.NavMesh = null;
            if (ScenePath != null) SaveScene();
        }

        private void FinishNavMeshBake()
        {
            if (_navBake is not { IsCompleted: true } bake) return;
            _navBake = null;
            _navBakeCancel?.Dispose();
            _navBakeCancel = null;

            if (bake.IsCanceled)
            {
                Notify("Baking stopped: the scene keeps the navmesh it had");
                return;
            }

            if (bake.Exception is { } failed)
            {
                Exception problem = failed.InnerException ?? failed;
                Log.Write(LogLevel.Error, $"The navmesh didn't bake: {problem.Message}", problem.ToString());
                ShowConsole();
                return;
            }

            if (_navBakeScene != Scene)
            {
                Notify("Another scene was opened while baking, so the navmesh wasn't kept");
                return;
            }

            NavMesh navMesh = bake.Result;
            try
            {
                navMesh.Save(_navBakeFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Notify($"Couldn't save the navmesh: {exception.Message}");
                return;
            }

            Vertex.Assets.Forget(_navBakeFile);
            Scene.Navigation.NavMesh = null;
            Scene.Navigation.NavMeshFile = Vertex.Assets.RelativePath(_navBakeFile);
            if (ScenePath != null) SaveScene();
            Notify(navMesh.IsEmpty
                ? "Baked the navmesh, but there's nowhere to walk: check the Agent size fits, and that floors are flat enough"
                : $"Baked the navmesh in {_navBakeTimer.Elapsed.TotalSeconds:0.#} s: {navMesh.WalkableArea:0} square units to walk on");
        }

        // ---------------------------------------------------------------- Showing it

        /// <summary>When the Navigation panel was last drawn (by <see cref="Ui.Time"/>): the navmesh shows while it's open.</summary>
        internal float NavigationShownAt { get; set; } = float.MinValue;

        private NavMesh? _overlayOf;
        private List<WorldQuad>? _overlay;

        /// <summary>The scene's navmesh, to draw over the scene view, while the Navigation panel's showing; null otherwise.</summary>
        public IReadOnlyList<WorldQuad>? NavigationOverlay
        {
            get
            {
                if (!Preferences.ShowNavMesh || Ui.Time - NavigationShownAt > 0.5f || Scene.Navigation.NavMesh is not { IsEmpty: false } navMesh) return null;
                if (navMesh != _overlayOf)
                {
                    _overlay = navMesh.Quads(new Color(0.25f, 0.72f, 1f, 0.3f), new Color(0.15f, 0.5f, 0.95f, 0.9f));
                    _overlayOf = navMesh;
                }

                return _overlay;
            }
        }
    }
}
