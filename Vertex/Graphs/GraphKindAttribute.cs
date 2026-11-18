namespace Vertex.Graphs
{
    /// <summary>
    /// The kind of graph a <see cref="Graph"/> property takes (<see cref="Effects.VfxNodes.Kind"/>, say), so the editor
    /// offers only files of that kind. Without it, a graph property takes visual scripts.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class GraphKindAttribute(string kind) : Attribute
    {
        public string Kind { get; } = kind;
    }
}
