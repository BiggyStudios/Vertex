namespace Vertex
{
    /// <summary>
    /// Keeps a number between <see cref="Min"/> and <see cref="Max"/> in the editor's inspector, which shows it as a
    /// slider. The game can still set it to anything.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class RangeAttribute(float min, float max) : Attribute
    {
        public float Min { get; } = min;
        public float Max { get; } = max;
    }

    /// <summary>
    /// Leaves a public property or field out of the editor's inspector, for values only code should change. On a component
    /// class, leaves it out of the editor's Add Component list.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class)]
    public sealed class HideInInspectorAttribute : Attribute;

    /// <summary>Shown when the mouse rests on the property's name in the editor's inspector: what it does, in a sentence.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class TooltipAttribute(string text) : Attribute
    {
        public string Text { get; } = text;
    }

    /// <summary>
    /// Shows the property in the editor's inspector only while another of the same object's, <see cref="Member"/>, is one
    /// of <see cref="Values"/>: a spot light's angle only on spot lights, say. It's still saved and can be set either way.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ShowWhenAttribute(string member, params object[] values) : Attribute
    {
        public string Member { get; } = member;
        public object[] Values { get; } = values;
    }

    /// <summary>
    /// Starts a section of the inspector, titled <see cref="Text"/>, at this property: it and those after it are shown
    /// together under the title. A subclass's property with the same title joins that section, wherever it's written.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class HeaderAttribute(string text) : Attribute
    {
        public string Text { get; } = text;
    }
}
