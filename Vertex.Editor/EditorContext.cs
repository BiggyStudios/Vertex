using Vertex.Editor.Editing;

namespace Vertex.Editor
{
    /// <summary>What the move, rotate and scale handles in the scene view do.</summary>
    public enum TransformTool
    {
        Move,
        Rotate,
        Scale
    }

    /// <summary>What's selected: an object, the scene itself (for its lighting and sky), or nothing.</summary>
    public sealed class Selection
    {
        public GameObject? Object { get; private set; }

        /// <summary>The scene itself is selected, so the inspector shows its lighting and sky.</summary>
        public bool Scene { get; private set; }

        /// <summary>Counts every change of selection, so panels can tell.</summary>
        public int Version { get; private set; }

        public void Select(GameObject? gameObject)
        {
            if (Object == gameObject && !Scene) return;
            Object = gameObject;
            Scene = false;
            Version++;
        }

        public void SelectScene()
        {
            if (Scene) return;
            Object = null;
            Scene = true;
            Version++;
        }

        public void Clear() => Select(null);
    }

    /// <summary>
    /// What all the editor's panels share: the scene being edited, what's selected in it, the history of changes, and
    /// the tool in hand.
    /// </summary>
    public sealed class EditorContext
    {
        public Scene Scene => Vertex.Scene.Active;

        public Selection Selection { get; } = new();
        public UndoHistory History { get; } = new();
        public TransformTool Tool { get; set; } = TransformTool.Move;

        /// <summary>Where new objects go: the point the scene view's camera is looking at, set by the scene view.</summary>
        public System.Numerics.Vector3 SpawnPoint { get; set; }

        /// <summary>The name shown for the scene, until scenes can be saved under one.</summary>
        public string SceneName { get; set; } = "Untitled";

        /// <summary>Raised to show a short note at the bottom of the window ("Undo Move Cube").</summary>
        public event Action<string>? Notified;

        public void Notify(string message) => Notified?.Invoke(message);

        /// <summary>Whether <paramref name="gameObject"/> is in the scene: not deleted (deleting can be undone, so deleted objects live on).</summary>
        public bool InScene(GameObject gameObject)
        {
            if (gameObject.IsDestroyed || gameObject.Scene != Scene) return false;
            foreach (GameObject member in Scene.GameObjects)
            {
                if (member == gameObject) return true;
            }

            return false;
        }

        /// <summary>Makes a new object, records it, and selects it.</summary>
        public GameObject Create(string name, Action<GameObject>? build = null, Transform? parent = null)
        {
            var gameObject = new GameObject(name, Scene);
            build?.Invoke(gameObject);
            gameObject.Transform.Parent = parent;
            Scene.ApplyChanges();
            History.Record(new CreateObject(gameObject));
            Selection.Select(gameObject);
            return gameObject;
        }

        public void Delete(GameObject gameObject)
        {
            History.Do(new DeleteObject(gameObject));
            if (Selection.Object == gameObject) Selection.Clear();
            Notify($"Deleted {gameObject.Name}");
        }

        public void Undo()
        {
            IUndoable? change = History.Undo();
            if (change != null) Notify($"Undo {change.Name}");
        }

        public void Redo()
        {
            IUndoable? change = History.Redo();
            if (change != null) Notify($"Redo {change.Name}");
        }
    }
}
