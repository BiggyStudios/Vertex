using Vertex.Graphs;

namespace Vertex.Scripting
{
    /// <summary>
    /// A kind of node in a visual script, and what it does when it runs:
    /// <list type="bullet">
    /// <item>an event (<see cref="Event"/> set) starts the flow when that happens to its object: it starts, a frame
    /// passes, it's hit;</item>
    /// <item>a node with a flow input does something when the flow reaches it, then carries the flow on
    /// (<see cref="ScriptRun.Continue"/>);</item>
    /// <item>a node without one works out its outputs from its inputs whenever something reads them.</item>
    /// </list>
    /// </summary>
    public sealed class ScriptNode : NodeType
    {
        /// <summary>What it does: reads inputs with <see cref="ScriptRun.In{T}"/>, sets outputs with <see cref="ScriptRun.Out"/>, and carries the flow on.</summary>
        public Action<ScriptRun> Run { get; }

        /// <summary>The event that starts it ("Start", "Update", "Collision"); null if it isn't an event.</summary>
        public string? Event { get; init; }

        /// <summary>For an event: whether it goes this time (a key event, whether its key was pressed). Null: always.</summary>
        public Func<ScriptRun, bool>? When { get; init; }

        public ScriptNode(string id, string title, string category, IReadOnlyList<Port> inputs, IReadOnlyList<Port> outputs, Action<ScriptRun> run)
            : base(id, title, category, inputs, outputs)
        {
            Run = run;
        }
    }
}
