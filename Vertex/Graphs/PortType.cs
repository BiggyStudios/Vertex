using System.Globalization;
using System.Numerics;

namespace Vertex.Graphs
{
    /// <summary>
    /// What flows along a link between two nodes: <see cref="Flow"/> (the order things happen in, in a visual script),
    /// or a kind of value (a number, a vector, an object). Each has a colour, so links and ports of the same type match.
    /// Graph kinds (scripts, and later shaders, audio and animation) share the common ones and can add their own.
    /// </summary>
    public sealed class PortType
    {
        /// <summary>The value type it carries (float for <see cref="Number"/>); null for <see cref="Flow"/> and <see cref="Any"/>.</summary>
        public Type? ValueType { get; }

        public string Name { get; }
        /// <summary>The colour its ports and links are drawn in.</summary>
        public Vertex.Color WireColor { get; }

        /// <summary>What an input of this type is when nothing's linked to it and no value's been typed.</summary>
        public object? Default { get; }

        public bool IsFlow => this == Flow;

        public PortType(string name, Type? valueType, Vertex.Color color, object? defaultValue = null)
        {
            Name = name;
            ValueType = valueType;
            WireColor = color;
            Default = defaultValue;
            _byName[name] = this;
        }

        public override string ToString() => Name;

        private static readonly Dictionary<string, PortType> _byName = new();

        /// <summary>The order things happen in: a link from one node's flow out to another's flow in runs that one next.</summary>
        public static readonly PortType Flow = new("Flow", null, new Vertex.Color(0.92f, 0.92f, 0.94f));

        /// <summary>Takes any value: for printing, and variables of any type.</summary>
        public static readonly PortType Any = new("Any", null, new Vertex.Color(0.62f, 0.64f, 0.7f));

        public static readonly PortType Number = new("Number", typeof(float), new Vertex.Color(0.45f, 0.83f, 0.52f), 0f);
        public static readonly PortType Boolean = new("Boolean", typeof(bool), new Vertex.Color(0.9f, 0.36f, 0.36f), false);
        public static readonly PortType Text = new("Text", typeof(string), new Vertex.Color(0.93f, 0.48f, 0.82f), "");
        public static readonly PortType Vector = new("Vector", typeof(Vector3), new Vertex.Color(0.98f, 0.78f, 0.3f), Vector3.Zero);

        /// <summary>A colour, and how see-through it is (its alpha). Made from a vector, it's solid.</summary>
        public static readonly PortType Color = new("Color", typeof(Vertex.Color), new Vertex.Color(0.8f, 0.5f, 0.98f), Vertex.Color.White);

        /// <summary>An object in the scene. Unlinked, an input of this type means the object the graph belongs to.</summary>
        public static readonly PortType Object = new("Object", typeof(GameObject), new Vertex.Color(0.38f, 0.66f, 0.98f));

        /// <summary>A prefab of the project's, to make copies of. Kept in a graph as where its file is.</summary>
        public static readonly PortType Prefab = new("Prefab", typeof(Vertex.Prefab), new Vertex.Color(0.44f, 0.66f, 1f));

        /// <summary>A sound of the project's. Kept in a graph as where its file is.</summary>
        public static readonly PortType Sound = new("Sound", typeof(Audio.AudioClip), new Vertex.Color(0.79f, 0.55f, 0.96f));

        /// <summary>A texture of the project's. Kept in a graph as where its file is.</summary>
        public static readonly PortType Texture = new("Texture", typeof(Rendering.Texture), new Vertex.Color(0.95f, 0.6f, 0.35f));

        /// <summary>
        /// An animation of the project's: a .anim file, or one of a model's own. Kept in a graph as where it is
        /// ("Models/Robot.glb#Walk" for a model's).
        /// </summary>
        public static readonly PortType Animation = new("Animation", typeof(Vertex.Animation.AnimationClip), new Vertex.Color(0.36f, 0.78f, 0.62f));

        private static readonly Dictionary<Type, PortType> _enums = new();

        /// <summary>A choice from an enum (a key on the keyboard): set on the node, not linked.</summary>
        public static PortType Enum(Type type)
        {
            if (!type.IsEnum) throw new ArgumentException($"{type.Name} isn't an enum.", nameof(type));
            lock (_enums)
            {
                if (!_enums.TryGetValue(type, out PortType? port)) _enums[type] = port = new PortType(type.Name, type, new Vertex.Color(0.55f, 0.6f, 0.7f), System.Enum.GetValues(type).GetValue(0));
                return port;
            }
        }

        public static PortType Enum<T>() where T : struct, System.Enum => Enum(typeof(T));

        /// <summary>The port type called <paramref name="name"/>, or <see cref="Any"/> if there isn't one.</summary>
        public static PortType Named(string name) => _byName.GetValueOrDefault(name, Any);

        /// <summary>The types a graph variable can be.</summary>
        public static IReadOnlyList<PortType> VariableTypes { get; } = [Number, Boolean, Text, Vector, Color, Object];

        /// <summary>
        /// Whether an output of this type can be linked to an input of <paramref name="input"/>'s: the same type, flow to
        /// flow only, anything to <see cref="Any"/> or <see cref="Text"/> (written out), <see cref="Any"/> to anything
        /// (converted when it runs), a number to a vector (all three parts) or a true/false, and a vector to a colour and back.
        /// </summary>
        public bool CanLinkTo(PortType input)
        {
            if (IsFlow || input.IsFlow) return IsFlow && input.IsFlow;
            if (this == input || input == Any || this == Any || input == Text) return true;
            return (this == Number && (input == Vector || input == Boolean)) || (this == Boolean && input == Number) ||
                   (this == Vector && input == Color) || (this == Color && input == Vector);
        }

        /// <summary>
        /// <paramref name="value"/> as a value of this type, as near as it goes: numbers to text, text to numbers, a number
        /// to a vector... What it can't be made into becomes this type's <see cref="Default"/>.
        /// </summary>
        public object? Convert(object? value)
        {
            if (this == Any || IsFlow) return value;
            if (value != null && ValueType != null && ValueType.IsInstanceOfType(value)) return value;

            try
            {
                if (this == Number) return ToNumber(value);
                if (this == Boolean) return value switch { float f => f != 0f, double d => d != 0d, int i => i != 0, string s => s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase), null => false, _ => true };
                if (this == Text) return Describe(value);
                if (this == Vector) return value switch { float f => new Vector3(f), double d => new Vector3((float)d), int i => new Vector3(i), Vector2 v => new Vector3(v, 0f), Vertex.Color c => new Vector3(c.R, c.G, c.B), Vector4 v => new Vector3(v.X, v.Y, v.Z), _ => Vector3.Zero };
                if (this == Object) return value switch { GameObject gameObject => gameObject, Component component => component.GameObject, _ => null };
                if (this == Prefab) return value is string prefab && Assets.FindFile(prefab) is { } prefabFile ? Assets.LoadPrefab(prefabFile) : null;
                if (this == Sound) return value is string sound && Assets.FindFile(sound) is { } soundFile ? Assets.LoadSound(soundFile) : null;
                if (this == Texture) return value is string texture && Assets.FindFile(texture) is { } textureFile ? Assets.LoadTexture(textureFile) : null;
                if (this == Animation) return value is string animation ? Assets.FindAnimation(animation) : null;
                if (this == Color) return value switch { Vector4 v => new Vertex.Color(v.X, v.Y, v.Z, v.W), Vector3 v => new Vertex.Color(v.X, v.Y, v.Z), float f => new Vertex.Color(f, f, f), _ => Default };
                if (ValueType is { IsEnum: true } enumType && value is string name && System.Enum.TryParse(enumType, name, out object? parsed)) return parsed;
            }
            catch (Exception)
            {
                // Falls through to the default.
            }

            return Default;
        }

        private static float ToNumber(object? value) => value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            bool b => b ? 1f : 0f,
            string s when float.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) => parsed,
            Vector3 v => v.Length(),
            _ => 0f
        };

        /// <summary>A value written out for people to read: numbers without long tails, vectors as (x, y, z), objects by name.</summary>
        public static string Describe(object? value) => value switch
        {
            null => "None",
            string s => s,
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            Vector3 v => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})"),
            Vector2 v => string.Create(CultureInfo.InvariantCulture, $"({v.X:0.###}, {v.Y:0.###})"),
            Vertex.Color c => string.Create(CultureInfo.InvariantCulture, $"({c.R:0.##}, {c.G:0.##}, {c.B:0.##}, {c.A:0.##})"),
            GameObject gameObject => gameObject.Name,
            Vertex.Prefab prefab => prefab.Name,
            Audio.AudioClip => "Sound",
            Rendering.Texture => "Texture",
            Vertex.Animation.AnimationClip clip => clip.Name,
            Component component => $"{component.GameObject.Name} ({component.GetType().Name})",
            _ => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }
}
