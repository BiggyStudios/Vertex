using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Vertex.Rendering
{
    /// <summary>
    /// A shader written in GLSL, from a .glsl file in the project or made in code. For now that's a post effect (see
    /// <see cref="PostEffect"/>): a function that's given the finished picture and changes it.
    /// <para>
    /// A post effect's file defines <c>vec4 effect(vec2 uv)</c>, which returns the colour at <c>uv</c> (0 to
    /// 1 across the picture, from its top-left corner). It can read <c>picture(uv)</c> (the picture so far, in screen
    /// colours from 0 to 1), <c>distanceAt(uv)</c> (how far from the camera what's drawn there is), <c>screenSize</c> (in
    /// pixels) and <c>time</c> (in seconds).
    /// </para>
    /// <para>
    /// Settings shown in the editor, and set from code with <see cref="PostEffect.Set(string, float)"/>, are lines like
    /// <c>property float Contrast = 1 [0, 4];  // How far apart dark and light are</c>: a type (float, int, bool, vec2,
    /// vec3, vec4 or color), a name, a starting value, an optional range for a slider, and the comment after as its tooltip.
    /// The shader then reads the setting by its name.
    /// </para>
    /// </summary>
    public sealed class Shader
    {
        /// <summary>How many settings a shader can have.</summary>
        public const int MaxProperties = 16;

        private static readonly Regex _property = new(
            @"^\s*property\s+(?<type>\w+)\s+(?<name>[A-Za-z_]\w*)\s*(=\s*(?<value>[^;\[]*?))?\s*(\[\s*(?<min>[^,\]]+?)\s*,\s*(?<max>[^\]]+?)\s*\])?\s*;\s*(//\s*(?<tip>.*?))?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex _effect = new(@"\bvec4\s+effect\s*\(", RegexOptions.Compiled);

        private readonly List<ShaderProperty> _properties = new();
        private readonly List<string> _problems = new();

        private Shader(string code, string name)
        {
            Name = name;
            SetCode(code);
        }

        /// <summary>A shader from GLSL written in code. <paramref name="name"/> is what its errors are reported as.</summary>
        public static Shader FromCode(string code, string name = "shader") => new(code, name);

        /// <summary>The shader in a .glsl file. Most games use <see cref="Assets.LoadShader"/> instead, which loads each file once.</summary>
        public static Shader Load(string path) => new(File.ReadAllText(path), Path.GetFileName(path)) { Source = Path.GetFullPath(path) };

        /// <summary>The file it was loaded from; null if it was made in code.</summary>
        public string? Source { get; internal set; }

        /// <summary>What its errors are reported as: the file's name.</summary>
        public string Name { get; private set; }

        /// <summary>The GLSL as written.</summary>
        public string Code { get; private set; } = "";

        /// <summary>Its settings, in the order they're written.</summary>
        public IReadOnlyList<ShaderProperty> Properties => _properties;

        /// <summary>
        /// What's wrong with it, if anything: a setting it couldn't read, or (once the renderer has tried) what the GLSL
        /// compiler said. It isn't drawn while there's a problem.
        /// </summary>
        public string? Problem
        {
            get
            {
                if (_problems.Count > 0) return string.Join("\n", _problems);
                return CompileProblem;
            }
        }

        /// <summary>What the GLSL compiler said, the last time a renderer built it and couldn't.</summary>
        internal string? CompileProblem { get; set; }

        /// <summary>The version whose problem has been written to the log, so it's said once.</summary>
        internal int ReportedVersion { get; set; }

        /// <summary>Goes up each time the code changes, so renderers know to build it again.</summary>
        public int Version { get; private set; }

        /// <summary>Whether it's a post effect: it defines <c>vec4 effect(vec2 uv)</c>.</summary>
        public bool IsPostEffect { get; private set; }

        /// <summary>The setting called <paramref name="name"/>, or null.</summary>
        public ShaderProperty? Property(string name) => _properties.FirstOrDefault(p => p.Name == name);

        /// <summary>Changes the code (an edited file read again): renderers build it again, and its settings are read again.</summary>
        public void SetCode(string code)
        {
            Code = code;
            Version++;
            CompileProblem = null;
            _problems.Clear();
            _properties.Clear();

            var body = new StringBuilder();
            string[] lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                bool isProperty = trimmed.StartsWith("property", StringComparison.Ordinal) && (trimmed.Length == 8 || char.IsWhiteSpace(trimmed[8]));
                if (!isProperty)
                {
                    body.Append(line).Append('\n');
                    continue;
                }

                // Each setting becomes a name for its place among the values handed in, so the line count stays the
                // same and the compiler's line numbers match the file's.
                string? problem = ReadProperty(line, out ShaderProperty? property);
                if (problem != null)
                {
                    _problems.Add($"{Name}:{i + 1}: {problem}");
                    body.Append('\n');
                    continue;
                }

                _properties.Add(property!);
                body.Append($"#define {property!.Name} {Swizzle(property)}\n");
            }

            Body = body.ToString();
            IsPostEffect = _effect.IsMatch(Body);
            if (!IsPostEffect) _problems.Add($"{Name}: a post effect needs a function vec4 effect(vec2 uv)");
        }

        /// <summary>The code with its settings turned into names for the values handed in (see ShaderLibrary.EffectHeader).</summary>
        internal string Body { get; private set; } = "";

        private static string Swizzle(ShaderProperty property)
        {
            string value = $"effectValues[{property.Slot}]";
            return property.Type switch
            {
                ShaderPropertyType.Float => $"({value}.x)",
                ShaderPropertyType.Int => $"int({value}.x)",
                ShaderPropertyType.Bool => $"({value}.x > 0.5)",
                ShaderPropertyType.Vector2 => $"({value}.xy)",
                ShaderPropertyType.Vector3 => $"({value}.xyz)",
                _ => $"({value})"
            };
        }

        private string? ReadProperty(string line, out ShaderProperty? property)
        {
            property = null;
            Match match = _property.Match(line);
            if (!match.Success) return "a setting is written: property float Name = 1;";

            ShaderPropertyType? type = match.Groups["type"].Value switch
            {
                "float" => ShaderPropertyType.Float,
                "int" => ShaderPropertyType.Int,
                "bool" => ShaderPropertyType.Bool,
                "vec2" => ShaderPropertyType.Vector2,
                "vec3" => ShaderPropertyType.Vector3,
                "vec4" => ShaderPropertyType.Vector4,
                "color" => ShaderPropertyType.Color,
                _ => null
            };
            if (type == null) return $"a setting's type is float, int, bool, vec2, vec3, vec4 or color, not {match.Groups["type"].Value}";

            string name = match.Groups["name"].Value;
            if (_properties.Any(p => p.Name == name)) return $"there's already a setting called {name}";
            if (_properties.Count == MaxProperties) return $"a shader can have up to {MaxProperties} settings";

            Vector4 value = type == ShaderPropertyType.Color ? Vector4.One : Vector4.Zero;
            if (match.Groups["value"].Success && match.Groups["value"].Value.Length > 0)
            {
                if (ParseValue(match.Groups["value"].Value, type.Value) is not { } parsed) return $"can't read {match.Groups["value"].Value.Trim()} as a {match.Groups["type"].Value}";
                value = parsed;
            }

            float? min = null, max = null;
            if (match.Groups["min"].Success)
            {
                if (!TryNumber(match.Groups["min"].Value, out float low) || !TryNumber(match.Groups["max"].Value, out float high))
                    return "a range is written [lowest, highest]";
                min = low;
                max = high;
            }

            string? tip = match.Groups["tip"].Success && match.Groups["tip"].Value.Length > 0 ? match.Groups["tip"].Value : null;
            property = new ShaderProperty(name, type.Value, value, min, max, tip, _properties.Count);
            return null;
        }

        /// <summary>
        /// A setting's value as it's written in GLSL: a number, true or false, a vector as vec3(1, 0, 0) or (1, 0, 0), or a
        /// colour as #RRGGBB, #RRGGBBAA or (r, g, b, a) from 0 to 1. Null if it can't be read.
        /// </summary>
        internal static Vector4? ParseValue(string text, ShaderPropertyType type)
        {
            text = text.Trim();
            if (type == ShaderPropertyType.Bool)
            {
                if (text is "true" or "1") return new Vector4(1f, 0f, 0f, 0f);
                if (text is "false" or "0") return Vector4.Zero;
                return null;
            }

            if (type == ShaderPropertyType.Color && text.StartsWith('#'))
            {
                string hex = text[1..];
                if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits)) return null;
                if (hex.Length == 6) bits = (bits << 8) | 0xFF;
                return new Vector4((bits >> 24) / 255f, ((bits >> 16) & 0xFF) / 255f, ((bits >> 8) & 0xFF) / 255f, (bits & 0xFF) / 255f);
            }

            // vec3(1, 2, 3), color(1, 0, 0) or just (1, 2, 3).
            int open = text.IndexOf('(');
            if (open >= 0)
            {
                if (!text.EndsWith(')')) return null;
                text = text[(open + 1)..^1];
            }

            string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
            var numbers = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryNumber(parts[i], out numbers[i])) return null;
            }

            int size = type switch
            {
                ShaderPropertyType.Vector2 => 2,
                ShaderPropertyType.Vector3 => 3,
                ShaderPropertyType.Vector4 or ShaderPropertyType.Color => 4,
                _ => 1
            };
            if (numbers.Length > size) return null;
            if (type == ShaderPropertyType.Color && numbers.Length == 3) return new Vector4(numbers[0], numbers[1], numbers[2], 1f);

            // One number fills them all, as in GLSL's vec3(1).
            if (numbers.Length == 1) return size == 1 ? new Vector4(numbers[0], 0f, 0f, 0f) : new Vector4(numbers[0]);
            if (numbers.Length != size) return null;

            var result = Vector4.Zero;
            for (int i = 0; i < size; i++) result[i] = numbers[i];
            return result;
        }

        private static bool TryNumber(string text, out float value) =>
            float.TryParse(text.Trim().TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>What kind of value a <see cref="ShaderProperty"/> holds.</summary>
    public enum ShaderPropertyType
    {
        Float,
        Int,
        Bool,
        Vector2,
        Vector3,
        Vector4,

        /// <summary>A colour, red, green, blue and alpha from 0 to 1.</summary>
        Color
    }

    /// <summary>
    /// One of a <see cref="Shader"/>'s settings: a <c>property</c> line in its code. Values are kept as a
    /// <see cref="Vector4"/>, whatever the type: a float in X, a bool as 0 or 1.
    /// </summary>
    /// <param name="Slot">Where among the values handed to the shader this one goes.</param>
    public sealed record ShaderProperty(string Name, ShaderPropertyType Type, Vector4 Default, float? Min, float? Max, string? Tooltip, int Slot);
}
