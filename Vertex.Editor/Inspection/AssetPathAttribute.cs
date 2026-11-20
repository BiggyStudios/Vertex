using Vertex.Editor.Editing;

namespace Vertex.Editor.Inspection
{
    /// <summary>
    /// Shows a text setting that holds where one of the project's files is in its Assets folder ("Scenes/Main.scene") as
    /// a slot to pick the file in, like a texture's, rather than text to type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class AssetPathAttribute(AssetKind kind, string? noneLabel = null) : Attribute
    {
        public AssetKind Kind { get; } = kind;

        /// <summary>What it says when no file's chosen ("The engine's").</summary>
        public string? NoneLabel { get; } = noneLabel;
    }
}
