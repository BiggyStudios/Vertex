using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Vertex.Graphs;

namespace Vertex.Rendering
{
    /// <summary>What a <see cref="Shader"/> is for.</summary>
    public enum ShaderPurpose
    {
        /// <summary>Changes the finished picture (see <see cref="PostEffect"/>): it defines <c>vec4 effect(vec2 uv)</c>.</summary>
        PostEffect,

        /// <summary>
        /// Works out how a material's surface looks at each point (see <see cref="Material.Shader"/>): it defines
        /// <c>void surface(inout Surface s)</c>.
        /// </summary>
        Surface
    }

    /// <summary>
    /// A shader written in GLSL, from a .glsl file in the project or made in code, or made with nodes in a shader graph
    /// (a .shadergraph file). It's one of two kinds (see <see cref="Purpose"/>):
    /// <list type="bullet">
    /// <item>A post effect (see <see cref="PostEffect"/>) defines <c>vec4 effect(vec2 uv)</c>, which returns the colour at
    /// <c>uv</c> (0 to 1 across the picture, from its top-left corner). It can read <c>picture(uv)</c> (the picture so far,
    /// in screen colours from 0 to 1), <c>distanceAt(uv)</c> (how far from the camera what's drawn there is),
    /// <c>screenSize</c> (in pixels) and <c>time</c> (in seconds).</item>
    /// <item>A surface shader (see <see cref="Material.Shader"/>) defines <c>void surface(inout Surface s)</c>, which is
    /// given the material's own look in <c>s</c> (its colour, roughness and so on) and changes what it likes. It can read
    /// <c>uv</c>, <c>worldPosition</c>, <c>worldNormal</c>, <c>viewDirection</c>, <c>objectPosition</c>,
    /// <c>screenPosition</c> and <c>time</c>, and sample textures with <c>sampleColor</c> (colours, made linear) and
    /// <c>sampleNormal</c> (normal maps). Colours in it are linear, as light is.</item>
    /// </list>
    /// <para>
    /// Settings shown in the editor, and set from code with <see cref="PostEffect.Set(string, float)"/> or
    /// <see cref="Material.Set(string, float)"/>, are lines like
    /// <c>property float Contrast = 1 [0, 4];  // How far apart dark and light are</c>: a type (float, int, bool, vec2,
    /// vec3, vec4, color or texture), a name, a starting value, an optional range for a slider, and the comment after as
    /// its tooltip. The shader then reads the setting by its name. A texture starts as white, black, normal (a flat
    /// normal map) or a file in the project: <c>property texture Bricks = "Textures/Bricks.png";</c>.
    /// </para>
    /// </summary>
    public sealed class Shader
    {
        /// <summary>How many settings a shader can have, besides textures.</summary>
        public const int MaxProperties = 16;

        /// <summary>How many texture settings a shader can have.</summary>
        public const int MaxTextures = 4;

        private static readonly Regex _property = new(
            @"^\s*property\s+(?<type>\w+)\s+(?<name>[A-Za-z_]\w*)\s*(=\s*(?<value>[^;\[]*?))?\s*(\[\s*(?<min>[^,\]]+?)\s*,\s*(?<max>[^\]]+?)\s*\])?\s*;\s*(//\s*(?<tip>.*?))?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex _effect = new(@"\bvec4\s+effect\s*\(", RegexOptions.Compiled);
        private static readonly Regex _surface = new(@"\bvoid\s+surface\s*\(\s*inout\s+Surface\b", RegexOptions.Compiled);
        private static readonly Regex _vertex = new(@"\bvoid\s+vertex\s*\(", RegexOptions.Compiled);

        private List<ShaderProperty> _properties = new();
        private List<string> _problems = new();
        private readonly Graph? _graph;
        private int _graphVersion = -1;

        private Shader(string code, string name)
        {
            Name = name;
            SetCode(code);
        }

        private Shader(Graph graph, string name)
        {
            Name = name;
            _graph = graph;
        }

        /// <summary>A shader from GLSL written in code. <paramref name="name"/> is what its errors are reported as.</summary>
        public static Shader FromCode(string code, string name = "shader") => new(code, name);

        /// <summary>
        /// The shader a shader graph makes. It follows the graph: change the graph and the shader changes with it. Most
        /// games use <see cref="Assets.LoadShader"/> with the .shadergraph file instead.
        /// </summary>
        public static Shader FromGraph(Graph graph) => new(graph, graph.Source != null ? Path.GetFileName(graph.Source) : graph.Name) { Source = graph.Source };

        /// <summary>The shader in a .glsl or .shadergraph file. Most games use <see cref="Assets.LoadShader"/> instead, which loads each file once.</summary>
        public static Shader Load(string path)
        {
            if (Path.GetExtension(path).Equals(ShaderGraphs.ShaderNodes.Extension, StringComparison.OrdinalIgnoreCase)) return FromGraph(Graph.Load(path));
            return new Shader(File.ReadAllText(path), Path.GetFileName(path)) { Source = Path.GetFullPath(path) };
        }

        /// <summary>The file it was loaded from; null if it was made in code.</summary>
        public string? Source { get; internal set; }

        /// <summary>What its errors are reported as: the file's name.</summary>
        public string Name { get; private set; }

        /// <summary>The shader graph it's made from, if it is: it's worked out again whenever the graph changes.</summary>
        public Graph? Graph => _graph;

        /// <summary>The GLSL as written, or as a shader graph wrote it.</summary>
        public string Code
        {
            get
            {
                Follow();
                return _code;
            }
        }

        private string _code = "";

        /// <summary>Its settings, in the order they're written.</summary>
        public IReadOnlyList<ShaderProperty> Properties
        {
            get
            {
                Follow();
                return _properties;
            }
        }

        /// <summary>
        /// What's wrong with it, if anything: a setting it couldn't read, or (once the renderer has tried) what the GLSL
        /// compiler said. It isn't drawn while there's a problem.
        /// </summary>
        public string? Problem
        {
            get
            {
                Follow();
                if (_problems.Count > 0) return string.Join("\n", _problems);
                return CompileProblem;
            }
        }

        /// <summary>What the GLSL compiler said, the last time a renderer built it and couldn't.</summary>
        internal string? CompileProblem { get; set; }

        /// <summary>The version whose problem has been written to the log, so it's said once.</summary>
        internal int ReportedVersion { get; set; }

        /// <summary>Goes up each time the code changes, so renderers know to build it again.</summary>
        public int Version
        {
            get
            {
                Follow();
                return _version;
            }
        }

        private int _version;

        /// <summary>What it's for: a post effect, or a material's surface. Null if it defines neither function.</summary>
        public ShaderPurpose? Purpose
        {
            get
            {
                Follow();
                return _purpose;
            }
        }

        private ShaderPurpose? _purpose;

        /// <summary>Whether it's a post effect: it defines <c>vec4 effect(vec2 uv)</c>.</summary>
        public bool IsPostEffect => Purpose == ShaderPurpose.PostEffect;

        /// <summary>Whether it's a surface shader, for a material: it defines <c>void surface(inout Surface s)</c>.</summary>
        public bool IsSurface => Purpose == ShaderPurpose.Surface;

        /// <summary>
        /// Whether it moves the mesh's corners as well: a surface shader that also defines
        /// <c>void vertex(inout vec3 position, vec3 normal, vec2 uv)</c>, given each corner's place in the world (to move),
        /// its normal and its texture coordinates. It can read <c>time</c>, <c>objectPosition</c> and its settings, and
        /// must stand alone: other functions of the shader aren't there to call. Grass and leaves in the wind, water's
        /// waves. Shadows are still cast from where the mesh is, unmoved.
        /// </summary>
        public bool MovesVertices => IsSurface && VertexCode != null;

        /// <summary>
        /// The vertex function, with the settings' names before it, for the mesh's vertex shader (numbered as in the
        /// file); null if there's none.
        /// </summary>
        internal string? VertexCode
        {
            get
            {
                string body = Body;
                Match match = _vertex.Match(body);
                if (!match.Success) return null;
                int open = body.IndexOf('{', match.Index);
                if (open < 0) return null;
                int depth = 0, end = -1;
                for (int i = open; i < body.Length; i++)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}' && --depth == 0)
                    {
                        end = i;
                        break;
                    }
                }

                if (end < 0) return null;
                var code = new StringBuilder();
                foreach (string line in body.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("#define", StringComparison.Ordinal)) code.Append(line).Append('\n');
                }

                int firstLine = body.AsSpan(0, match.Index).Count('\n') + 1;
                code.Append("#line ").Append(firstLine).Append('\n').Append(body, match.Index, end + 1 - match.Index).Append('\n');
                return code.ToString();
            }
        }

        /// <summary>The setting called <paramref name="name"/>, or null.</summary>
        public ShaderProperty? Property(string name) => Properties.FirstOrDefault(p => p.Name == name);

        /// <summary>
        /// Changes the code (an edited file read again): renderers build it again, and its settings are read again. A
        /// shader made from a graph is changed by changing the graph instead.
        /// </summary>
        public void SetCode(string code)
        {
            if (_graph != null) throw new InvalidOperationException($"{Name} is made from a shader graph: change the graph instead.");

            var problems = new List<string>();
            var properties = new List<ShaderProperty>();
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
                string? problem = ReadProperty(line, properties, out ShaderProperty? property);
                if (problem != null)
                {
                    problems.Add($"{Name}:{i + 1}: {problem}");
                    body.Append('\n');
                    continue;
                }

                properties.Add(property!);
                body.Append($"#define {property!.Name} {Swizzle(property)}\n");
            }

            ShaderPurpose? purpose = PurposeOf(body.ToString());
            if (purpose == null) problems.Add($"{Name}: a shader needs a function vec4 effect(vec2 uv) (a post effect) or void surface(inout Surface s) (a material's surface)");
            Apply(code, body.ToString(), purpose, properties, problems, always: true);
        }

        /// <summary>Which purpose GLSL is for, by the function it defines.</summary>
        internal static ShaderPurpose? PurposeOf(string body) =>
            _effect.IsMatch(body) ? ShaderPurpose.PostEffect : _surface.IsMatch(body) ? ShaderPurpose.Surface : null;

        /// <summary>
        /// Takes in a new build of it. Code set by hand (<paramref name="always"/>) always counts as changed, so a file
        /// read again is tried again; a shader graph's counts only if the code the renderers are given changed, so one
        /// whose nodes were only moved, or whose properties' starting values changed, isn't built again.
        /// </summary>
        internal void Apply(string code, string body, ShaderPurpose? purpose, List<ShaderProperty> properties, List<string> problems, bool always = false)
        {
            bool changed = always || body != Body || purpose != _purpose || _version == 0 || !problems.SequenceEqual(_problems) ||
                           !properties.Select(p => (p.Type, p.Slot)).SequenceEqual(_properties.Select(p => (p.Type, p.Slot)));
            _code = code;
            Body = body;
            _purpose = purpose;
            _properties = properties;
            _problems = problems;
            if (!changed) return;

            _version++;
            CompileProblem = null;
        }

        /// <summary>For a shader made from a graph: works it out again if the graph has changed since.</summary>
        private void Follow()
        {
            if (_graph == null || _graph.Version == _graphVersion) return;
            _graphVersion = _graph.Version;
            ShaderGraphs.ShaderGraphCompiler.Compile(_graph, this);
        }

        /// <summary>The code with its settings turned into names for the values handed in (see ShaderLibrary.EffectHeader).</summary>
        internal string Body
        {
            get
            {
                Follow();
                return _body;
            }
            private set => _body = value;
        }

        private string _body = "";

        /// <summary>What a setting's name stands for in GLSL: its place among the values (or textures) handed to the shader.</summary>
        internal static string Swizzle(ShaderProperty property)
        {
            if (property.Type == ShaderPropertyType.Texture) return $"shaderTexture{property.Slot}";

            string value = $"shaderValues[{property.Slot}]";
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

        private string? ReadProperty(string line, List<ShaderProperty> properties, out ShaderProperty? property)
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
                "texture" => ShaderPropertyType.Texture,
                _ => null
            };
            if (type == null) return $"a setting's type is float, int, bool, vec2, vec3, vec4, color or texture, not {match.Groups["type"].Value}";

            string name = match.Groups["name"].Value;
            if (properties.Any(p => p.Name == name)) return $"there's already a setting called {name}";
            string? tip = match.Groups["tip"].Success && match.Groups["tip"].Value.Length > 0 ? match.Groups["tip"].Value : null;
            string written = match.Groups["value"].Success ? match.Groups["value"].Value.Trim() : "";

            if (type == ShaderPropertyType.Texture)
            {
                int textures = properties.Count(p => p.Type == ShaderPropertyType.Texture);
                if (textures == MaxTextures) return $"a shader can have up to {MaxTextures} textures";
                if (ReadTexture(written) is not { } texture) return $"a texture starts as white, black, normal or a file in quotes, not {written}";
                property = new ShaderProperty(name, ShaderPropertyType.Texture, Vector4.Zero, null, null, tip, textures)
                {
                    Fallback = texture.Fallback,
                    DefaultTexture = texture.File
                };
                return null;
            }

            int values = properties.Count(p => p.Type != ShaderPropertyType.Texture);
            if (values == MaxProperties) return $"a shader can have up to {MaxProperties} settings";

            Vector4 value = type == ShaderPropertyType.Color ? Vector4.One : Vector4.Zero;
            if (written.Length > 0)
            {
                if (ParseValue(written, type.Value) is not { } parsed) return $"can't read {written} as a {match.Groups["type"].Value}";
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

            property = new ShaderProperty(name, type.Value, value, min, max, tip, values);
            return null;
        }

        /// <summary>A texture setting's starting value: white, black, normal or "a/file.png" (white until it's found).</summary>
        private static (TextureFallback Fallback, string? File)? ReadTexture(string text)
        {
            if (text.Length == 0 || text == "white") return (TextureFallback.White, null);
            if (text == "black") return (TextureFallback.Black, null);
            if (text == "normal") return (TextureFallback.Normal, null);
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') return (TextureFallback.White, text[1..^1]);
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

        // ---------------------------------------------------------------- Packing settings for the renderer

        /// <summary>
        /// The values to hand the shader, in its settings' order: as set in <paramref name="values"/>, or the shader's
        /// starting values. A surface shader's colours are made linear, as light is worked out in.
        /// </summary>
        internal Vector4[] PackValues(List<ShaderValue> values)
        {
            var packed = new Vector4[MaxProperties];
            bool linear = IsSurface;
            foreach (ShaderProperty property in Properties)
            {
                if (property.Type == ShaderPropertyType.Texture) continue;
                Vector4 value = ShaderValue.Get(values, this, property.Name);
                if (linear && property.Type == ShaderPropertyType.Color) value = Shaders.FrameUniforms.Linear(new Color(value.X, value.Y, value.Z), value.W);
                packed[property.Slot] = value;
            }

            return packed;
        }

        /// <summary>The textures to hand the shader, in its texture settings' order: as set, its starting file, or a plain one.</summary>
        internal Texture[] PackTextures(List<ShaderValue> values)
        {
            var packed = new Texture[MaxTextures];
            for (int i = 0; i < packed.Length; i++) packed[i] = ShaderTextures.White;
            foreach (ShaderProperty property in Properties)
            {
                if (property.Type == ShaderPropertyType.Texture) packed[property.Slot] = ShaderValue.GetTexture(values, this, property.Name) ?? ShaderTextures.For(property.Fallback);
            }

            return packed;
        }
    }

    /// <summary>
    /// What a <see cref="Shader"/> property takes: surface shaders (a material's) or post effects (a camera's), so an
    /// editor offers only those.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ShaderForAttribute(ShaderPurpose purpose) : Attribute
    {
        public ShaderPurpose Purpose { get; } = purpose;
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
        Color,

        /// <summary>A texture, sampled by the shader.</summary>
        Texture
    }

    /// <summary>What a texture setting is when no texture's been given it.</summary>
    public enum TextureFallback
    {
        White,
        Black,

        /// <summary>A flat normal map: bends nothing.</summary>
        Normal
    }

    /// <summary>
    /// One of a <see cref="Shader"/>'s settings: a <c>property</c> line in its code, or a property of its shader graph.
    /// Values are kept as a <see cref="Vector4"/>, whatever the type: a float in X, a bool as 0 or 1.
    /// </summary>
    /// <param name="Slot">Where among the values (or for a texture, the textures) handed to the shader this one goes.</param>
    public sealed record ShaderProperty(string Name, ShaderPropertyType Type, Vector4 Default, float? Min, float? Max, string? Tooltip, int Slot)
    {
        /// <summary>For a texture: what it is with none given and no <see cref="DefaultTexture"/>.</summary>
        public TextureFallback Fallback { get; init; }

        /// <summary>For a texture: the file in the project it starts as ("Textures/Bricks.png"), if any.</summary>
        public string? DefaultTexture { get; init; }

        /// <summary>
        /// One a shader graph made for a texture chosen on one of its nodes: kept with the shader, not shown on the
        /// materials and effects that use it.
        /// </summary>
        public bool Hidden { get; init; }
    }

    /// <summary>
    /// One of a <see cref="PostEffect"/>'s or <see cref="Material"/>'s shader settings, kept by name: a float in X, a bool
    /// as 0 or 1, or a texture.
    /// </summary>
    public sealed class ShaderValue
    {
        public string Name { get; set; } = "";
        public Vector4 Value { get; set; }

        /// <summary>For a texture setting: the texture.</summary>
        public Texture? Texture { get; set; }

        // Shared by post effects and materials, which keep their settings the same way.

        internal static void Set(List<ShaderValue> values, string name, Vector4 value)
        {
            foreach (ShaderValue saved in values)
            {
                if (saved.Name != name) continue;
                saved.Value = value;
                return;
            }

            values.Add(new ShaderValue { Name = name, Value = value });
        }

        internal static void SetTexture(List<ShaderValue> values, string name, Texture? texture)
        {
            foreach (ShaderValue saved in values)
            {
                if (saved.Name != name) continue;
                saved.Texture = texture;
                return;
            }

            values.Add(new ShaderValue { Name = name, Texture = texture });
        }

        internal static Vector4 Get(List<ShaderValue> values, Shader? shader, string name)
        {
            foreach (ShaderValue saved in values)
            {
                if (saved.Name == name) return saved.Value;
            }

            return shader?.Property(name)?.Default ?? Vector4.Zero;
        }

        /// <summary>A texture setting: as set, or the file the shader starts it as (null if neither).</summary>
        internal static Texture? GetTexture(List<ShaderValue> values, Shader? shader, string name)
        {
            foreach (ShaderValue saved in values)
            {
                if (saved.Name == name && saved.Texture != null) return saved.Texture;
            }

            return shader?.Property(name) is { DefaultTexture: { } file } && Find(file) is { } path ? Assets.LoadTexture(path) : null;
        }

        // Where each default texture's file was found: looked for once (and, while it's missing, again every couple of
        // seconds, in case it's added), not on every draw of every frame.
        // Kept with the project's folders it was looked for in, so another project's look again.
        private static readonly Dictionary<string, (string? Path, long CheckedAt, string Root)> _found = new();

        private static string? Find(string file)
        {
            long now = Environment.TickCount64;
            string[] folders = Application.AssetFolders;
            string root = folders.Length > 0 ? folders[0] : "";
            if (_found.TryGetValue(file, out var known) && known.Root == root && (known.Path != null || now - known.CheckedAt < 2000)) return known.Path;
            string? path = Assets.FindFile(file);
            _found[file] = (path, now, root);
            return path;
        }
    }

    /// <summary>The plain textures a texture setting is when it's given none.</summary>
    internal static class ShaderTextures
    {
        public static readonly Texture White = new(1, 1, [255, 255, 255, 255]);
        public static readonly Texture Black = new(1, 1, [0, 0, 0, 255]);

        /// <summary>Straight out of the surface, as normal maps have it.</summary>
        public static readonly Texture FlatNormal = new(1, 1, [128, 128, 255, 255]);

        public static Texture For(TextureFallback fallback) => fallback switch
        {
            TextureFallback.Black => Black,
            TextureFallback.Normal => FlatNormal,
            _ => White
        };
    }
}
