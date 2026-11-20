using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Graphs;

namespace Vertex.Editor.Graphs
{
    /// <summary>
    /// A graph open for editing in a tab: the graph itself (shared with everything using its file, so changes reach a
    /// running game), and how it's being looked at: where it's scrolled to, how far it's zoomed, and which nodes are selected.
    /// </summary>
    public sealed class GraphDocument
    {
        public Graph Graph { get; }

        /// <summary>The file, in full: where the graph is now, if it's been moved or renamed since it was opened.</summary>
        public string Path => Graph.Source ?? _path;

        private readonly string _path;

        public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>The point of the graph at the view's top-left, and how much it's magnified.</summary>
        public Vector2 Offset { get; set; }
        public float Zoom { get; set; } = 1f;

        /// <summary>Whether it's been scrolled to show its nodes yet: done the first time it's shown, when the view's size is known.</summary>
        public bool Framed { get; set; }

        public HashSet<int> Selected { get; } = new();

        /// <summary>
        /// The State Machine node whose states are shown instead of the graph (an animation graph's, opened with a
        /// double-click), with how they're being looked at and which are picked; null for the graph itself.
        /// </summary>
        public int? OpenMachine { get; set; }
        public Vector2 MachineOffset { get; set; }
        public float MachineZoom { get; set; } = 1f;
        public bool MachineFramed { get; set; }
        public HashSet<string> SelectedStates { get; } = new();
        public int? SelectedTransition { get; set; }

        public GraphDocument(Graph graph, string path)
        {
            Graph = graph;
            _path = path;
        }

        /// <summary>
        /// Changes the graph with undo: the graph as it was before <paramref name="change"/> and as it is after are kept,
        /// so undo and redo put it back either way. A change with the same <paramref name="mergeKey"/> as the last one
        /// (a value being dragged) joins it.
        /// </summary>
        public void Edit(UndoHistory history, string name, Action change, object? mergeKey = null)
        {
            string before = Graph.ToJson();
            change();
            Graph.Changed();
            string after = Graph.ToJson();
            if (before == after) return;
            history.Record(new GraphChange(this, name, before, after), mergeKey);
        }

        /// <summary>Records a change already made (nodes dragged into place), given the graph as it was before.</summary>
        public void Record(UndoHistory history, string name, string before)
        {
            Graph.Changed();
            string after = Graph.ToJson();
            if (before != after) history.Record(new GraphChange(this, name, before, after));
        }
    }

    /// <summary>A change to a graph: the whole graph before and after, put back by undo and redo.</summary>
    public sealed class GraphChange(GraphDocument document, string name, string before, string after) : IUndoable, IMergeable
    {
        private string _after = after;

        public string Name => name;
        public GraphDocument Document => document;

        public void Undo() => Put(before);
        public void Redo() => Put(_after);

        private void Put(string json)
        {
            document.Graph.ReadJson(json);

            // Nodes that are gone can't stay selected.
            document.Selected.RemoveWhere(id => document.Graph.Node(id) == null);
        }

        public bool Merge(IUndoable next)
        {
            if (next is not GraphChange other || other.Document != document) return false;
            _after = other._after;
            return true;
        }
    }
}
