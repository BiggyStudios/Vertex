using System.Reflection;
using System.Text;

namespace Vertex.Editor.Inspection
{
    /// <summary>A kind of component that can be added to an object, as listed by Add Component.</summary>
    public sealed record ComponentType(Type Type, string Name, string Category);

    /// <summary>
    /// Every kind of component there is to add: the engine's own and the game's scripts, found by looking through the
    /// loaded code for public, non-abstract <see cref="Component"/> classes that can be made without arguments. Ones
    /// marked <see cref="HideInInspectorAttribute"/> are left out.
    /// </summary>
    public static class ComponentCatalog
    {
        private static List<ComponentType>? _all;

        static ComponentCatalog()
        {
            // Game code loaded later (a plugin, a mod, a rebuilt game) shows up too, and code let go doesn't.
            LoadedCode.Changed += () => _all = null;
        }

        public static IReadOnlyList<ComponentType> All => _all ??= Find();

        private static List<ComponentType> Find()
        {
            var found = new List<ComponentType>();
            foreach (Assembly assembly in LoadedCode.Assemblies)
            {
                if (assembly.IsDynamic) continue;

                Type[] types;
                try
                {
                    types = assembly.GetExportedTypes();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (!type.IsSubclassOf(typeof(Component)) || type.IsAbstract || type.IsGenericTypeDefinition) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                    if (type.IsDefined(typeof(HideInInspectorAttribute), inherit: false)) continue;

                    found.Add(new ComponentType(type, DisplayName(type), Category(type)));
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Category + "/" + a.Name, b.Category + "/" + b.Name));
            return found;
        }

        /// <summary>The engine's components by the part of the engine they're from; anything else is a script.</summary>
        public static string Category(Type type) => type.Namespace switch
        {
            "Vertex.Rendering" => "Rendering",
            "Vertex.Physics" => "Physics",
            "Vertex.Audio" => "Audio",
            "Vertex.Networking" => "Networking",
            "Vertex.UI" => "UI",
            _ => "Scripts"
        };

        public static string DisplayName(Type type) => SplitWords(type.Name);

        /// <summary>"CastShadows" as "Cast Shadows", "UIScale" as "UI Scale": names from code as words.</summary>
        public static string SplitWords(string name)
        {
            var words = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_')
                {
                    if (words.Length > 0 && words[^1] != ' ') words.Append(' ');
                    continue;
                }

                bool startsWord = i > 0 && words.Length > 0 && words[^1] != ' ' && (
                    char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])) ||
                    char.IsDigit(c) && !char.IsDigit(name[i - 1]));
                if (startsWord) words.Append(' ');
                words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : c);
            }

            return words.ToString();
        }
    }
}
