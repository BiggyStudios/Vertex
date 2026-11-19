using System.Numerics;

using Vertex.Graphs;

namespace Vertex.Scripting
{
    /// <summary>
    /// One object's own starting value for a graph's variable, in place of the one the graph gives it: a gun's own fire
    /// rate, or which object a door opens (a visual script's), or which footsteps to play (an audio graph's, on an
    /// <see cref="Audio.AudioSource"/>). Set in the inspector, under the component. Only the value of the variable's type
    /// is used.
    /// </summary>
    public sealed class ScriptVariableValue
    {
        /// <summary>The variable's name in the script.</summary>
        public string Name { get; set; } = "";

        public float Number { get; set; }
        public bool Boolean { get; set; }
        public string Text { get; set; } = "";
        public Vector3 Vector { get; set; }
        public Color Color { get; set; } = Color.White;
        public GameObject? Object { get; set; }

        public ScriptVariableValue() {}

        public ScriptVariableValue(string name, PortType type, object? value)
        {
            Name = name;
            Set(type, value);
        }

        /// <summary>The value, for a variable of <paramref name="type"/>.</summary>
        public object? Get(PortType type)
        {
            if (type == PortType.Number) return Number;
            if (type == PortType.Boolean) return Boolean;
            if (type == PortType.Text) return Text;
            if (type == PortType.Vector) return Vector;
            if (type == PortType.Color) return Color;
            if (type == PortType.Object) return Object;
            return null;
        }

        /// <summary>Sets the value of <paramref name="type"/>'s kind, made into that type as near as it goes.</summary>
        public void Set(PortType type, object? value)
        {
            value = type.Convert(value);
            if (type == PortType.Number) Number = (float)value!;
            else if (type == PortType.Boolean) Boolean = (bool)value!;
            else if (type == PortType.Text) Text = (string?)value ?? "";
            else if (type == PortType.Vector) Vector = (Vector3)value!;
            else if (type == PortType.Color) Color = (Color)value!;
            else if (type == PortType.Object) Object = value as GameObject;
        }

        public ScriptVariableValue Copy() => (ScriptVariableValue)MemberwiseClone();
    }
}
