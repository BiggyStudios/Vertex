using System.Numerics;
using System.Reflection;

using Vertex.Rendering;

namespace Vertex.Editor.Inspection
{
    /// <summary>How a value is shown and edited in the inspector.</summary>
    public enum MemberKind
    {
        Float,
        Int,
        Bool,
        Text,
        Vector2,
        Vector3,

        /// <summary>A range two numbers wide (<see cref="Rendering.FloatRange"/>): the smallest and largest.</summary>
        FloatRange,
        Rotation,
        Color,
        Enum,
        Mesh,

        /// <summary>A texture: a slot showing it, to pick one of the project's or drop one in.</summary>
        Texture,

        /// <summary>Another file of the project's (a prefab, a font, a sound): a slot like a texture's.</summary>
        Asset,

        /// <summary>A settings object of its own (a material): its values are shown under it.</summary>
        Nested,

        /// <summary>Another object or component: shown by name, not yet pickable.</summary>
        Reference
    }

    /// <summary>
    /// A public property or field the inspector shows: its name as words, how to read and write it, and how it's edited.
    /// </summary>
    public sealed class InspectedMember
    {
        public required MemberInfo Member { get; init; }
        public required string Name { get; init; }
        public required Type Type { get; init; }
        public required MemberKind Kind { get; init; }
        public bool CanWrite { get; init; }
        public RangeAttribute? Range { get; init; }
        public string? Tooltip { get; init; }

        /// <summary>When it's shown at all (see <see cref="ShowWhenAttribute"/>); null for always.</summary>
        public ShowWhenAttribute? ShowWhen { get; init; }

        /// <summary>Whether it's shown for <paramref name="target"/> as it is now.</summary>
        public bool IsShown(object target)
        {
            if (ShowWhen == null) return true;

            const BindingFlags instance = BindingFlags.Public | BindingFlags.Instance;
            Type type = target.GetType();
            MemberInfo? other = (MemberInfo?)type.GetProperty(ShowWhen.Member, instance) ?? type.GetField(ShowWhen.Member, instance);
            if (other == null) return true;

            object? value = Editing.SetValue.Read(target, other);
            return ShowWhen.Values.Any(shown => Equals(shown, value));
        }

        /// <summary>The section of the inspector it's in (see <see cref="HeaderAttribute"/>); null if it's in none.</summary>
        public string? Section { get; private set; }

        public object? Read(object target) => Editing.SetValue.Read(target, Member);

        /// <summary>The value, or null and false if reading it threw (some only work once the game is running).</summary>
        public bool TryRead(object target, out object? value)
        {
            try
            {
                value = Read(target);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        private static readonly Dictionary<Type, IReadOnlyList<InspectedMember>> _cache = new();

        // Shown elsewhere: the component's own header has its on/off switch.
        private static readonly HashSet<string> _skipped = [nameof(Component.Enabled)];

        /// <summary>
        /// What the inspector shows for <paramref name="type"/>: public properties with a public getter and setter, and
        /// public fields, of types it knows how to edit, base class first and then in the order they're written.
        /// </summary>
        public static IReadOnlyList<InspectedMember> Of(Type type)
        {
            if (_cache.TryGetValue(type, out IReadOnlyList<InspectedMember>? cached)) return cached;

            var members = new List<(int Depth, int Order, InspectedMember Member)>();
            int Depth(Type? declaring)
            {
                int depth = 0;
                for (Type? t = declaring; t != null; t = t.BaseType) depth++;
                return depth;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true }) continue;
                bool canWrite = property.SetMethod is { IsPublic: true };
                MemberKind? kind = KindOf(property.PropertyType);

                // Read-only values only show if they're something to look into (a material made in code, say).
                if (kind == null || !canWrite && kind != MemberKind.Nested) continue;
                if (Skip(property)) continue;

                members.Add((Depth(property.DeclaringType), property.MetadataToken, Make(property, property.PropertyType, kind.Value, canWrite)));
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                MemberKind? kind = KindOf(field.FieldType);
                if (kind == null || field.IsInitOnly && kind != MemberKind.Nested || Skip(field)) continue;
                members.Add((Depth(field.DeclaringType), field.MetadataToken, Make(field, field.FieldType, kind.Value, !field.IsInitOnly)));
            }

            IReadOnlyList<InspectedMember> result = Sections(members.OrderBy(m => m.Depth).ThenBy(m => m.Order).Select(m => m.Member));
            _cache[type] = result;
            return result;
        }

        /// <summary>
        /// The members gathered into their sections: one with a <see cref="HeaderAttribute"/> starts its section (or joins
        /// it, if a base class started it), and those after it follow it into the same one.
        /// </summary>
        private static List<InspectedMember> Sections(IEnumerable<InspectedMember> ordered)
        {
            var sections = new List<(string? Title, List<InspectedMember> Members)>();
            string? current = null;
            foreach (InspectedMember member in ordered)
            {
                if (member.Member.GetCustomAttribute<HeaderAttribute>() is { } header) current = header.Text;
                member.Section = current;

                int index = sections.FindIndex(s => s.Title == current);
                if (index < 0)
                {
                    sections.Add((current, new List<InspectedMember>()));
                    index = sections.Count - 1;
                }

                sections[index].Members.Add(member);
            }

            return sections.SelectMany(s => s.Members).ToList();
        }

        private static bool Skip(MemberInfo member) =>
            _skipped.Contains(member.Name) && member.DeclaringType == typeof(Component) ||
            member.IsDefined(typeof(HideInInspectorAttribute), inherit: true) ||
            member.IsDefined(typeof(ObsoleteAttribute), inherit: true);

        private static InspectedMember Make(MemberInfo member, Type type, MemberKind kind, bool canWrite) => new()
        {
            Member = member,
            Name = ComponentCatalog.SplitWords(member.Name),
            Type = type,
            Kind = kind,
            CanWrite = canWrite,
            Range = member.GetCustomAttribute<RangeAttribute>(),
            Tooltip = member.GetCustomAttribute<TooltipAttribute>()?.Text,
            ShowWhen = member.GetCustomAttribute<ShowWhenAttribute>()
        };

        /// <summary>How a value of <paramref name="type"/> is edited, or null if the inspector leaves it out.</summary>
        public static MemberKind? KindOf(Type type)
        {
            if (type == typeof(float) || type == typeof(double)) return MemberKind.Float;
            if (type == typeof(int)) return MemberKind.Int;
            if (type == typeof(bool)) return MemberKind.Bool;
            if (type == typeof(string)) return MemberKind.Text;
            if (type == typeof(Vector2)) return MemberKind.Vector2;
            if (type == typeof(Vector3)) return MemberKind.Vector3;
            if (type == typeof(Rendering.FloatRange)) return MemberKind.FloatRange;
            if (type == typeof(Quaternion)) return MemberKind.Rotation;
            if (type == typeof(Color)) return MemberKind.Color;
            if (type.IsEnum) return MemberKind.Enum;
            if (type == typeof(Mesh)) return MemberKind.Mesh;
            if (typeof(Texture).IsAssignableFrom(type)) return MemberKind.Texture;
            if (type == typeof(Prefab) || type == typeof(Vertex.UI.Font) || type == typeof(Vertex.Audio.AudioClip) || type == typeof(Vertex.Graphs.Graph)) return MemberKind.Asset;
            if (typeof(GameObject).IsAssignableFrom(type) || typeof(Component).IsAssignableFrom(type)) return MemberKind.Reference;

            // A settings object: a class of the engine's or the game's with values of its own to show.
            if (type.IsClass && !type.IsAbstract && type != typeof(object) && !typeof(Delegate).IsAssignableFrom(type) &&
                !typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.Namespace?.StartsWith("System") != true &&
                type.GetConstructor(Type.EmptyTypes) != null)
            {
                return MemberKind.Nested;
            }

            return null;
        }
    }
}
