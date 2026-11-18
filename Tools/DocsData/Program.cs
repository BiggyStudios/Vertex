using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Vertex;
using Vertex.Effects;
using Vertex.Graphs;
using Vertex.Scripting;

// Writes docs/data/*.js: the reference pages' data, taken from the engine so it's never out of date. Each file sets a
// global (window.VertexNodes...) rather than being JSON, so the site works opened straight from disk too.

string repo = FindRepo();
string output = Path.Combine(repo, "docs", "data");
Directory.CreateDirectory(output);

Write("nodes.js", "VertexNodes", Nodes());
Write("api.js", "VertexApi", Api());
Write("graphs.js", "VertexGraphs", Graphs());
Write("samples.js", "VertexSamples", Samples());
Console.WriteLine($"Wrote the documentation's data to {output}");

void Write(string file, string name, JsonNode data)
{
    string json = data.ToJsonString(new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    File.WriteAllText(Path.Combine(output, file), $"// Made by Tools/DocsData from the engine: don't edit by hand.\nwindow.{name} = {json};\n");
    Console.WriteLine($"  {file}: {json.Length / 1024} KB");
}

static string FindRepo()
{
    for (DirectoryInfo? folder = new(Directory.GetCurrentDirectory()); folder != null; folder = folder.Parent)
    {
        if (File.Exists(Path.Combine(folder.FullName, "Vertex.slnx"))) return folder.FullName;
    }

    throw new DirectoryNotFoundException("Run this from inside the Vertex repository.");
}

// ---------------------------------------------------------------- Nodes

JsonNode Nodes()
{
    var types = new JsonObject();
    var libraries = new JsonObject();
    foreach (NodeLibrary library in new[] { ScriptNodes.Library, VfxNodes.Library })
    {
        var categories = new JsonObject();
        var nodes = new JsonArray();
        foreach (NodeType node in library.All)
        {
            if (!categories.ContainsKey(node.Category)) categories[node.Category] = Hex(library.CategoryColor(node.Category));
            foreach (Port port in node.Inputs.Concat(node.Outputs)) types[port.Type.Name] = Hex(port.Type.WireColor);

            var entry = new JsonObject
            {
                ["id"] = node.Id,
                ["title"] = node.Title,
                ["category"] = node.Category,
                ["description"] = node.Description ?? "",
                ["keywords"] = node.Keywords,
                ["inputs"] = new JsonArray(node.Inputs.Select(PortJson).ToArray()),
                ["outputs"] = new JsonArray(node.Outputs.Select(PortJson).ToArray())
            };
            if (node is ScriptNode { Event: { } @event }) entry["event"] = @event;
            if (node is VfxNode vfx) entry["role"] = vfx.Role.ToString();
            nodes.Add(entry);
        }

        libraries[library.Kind] = new JsonObject { ["name"] = library.DisplayName, ["categories"] = categories, ["nodes"] = nodes };
    }

    return new JsonObject { ["libraries"] = libraries, ["types"] = types };
}

JsonNode PortJson(Port port)
{
    var json = new JsonObject { ["name"] = port.Name, ["type"] = port.Type.Name };
    if (!port.Linkable) json["setting"] = true;
    if (port.IsVariableName) json["variable"] = true;
    if (port.Tooltip != null) json["tooltip"] = port.Tooltip;
    if (port.Type.ValueType is { IsEnum: true } enumType) json["choices"] = new JsonArray(Enum.GetNames(enumType).Select(n => (JsonNode)n).ToArray());
    if (!port.Type.IsFlow && Describe(port.DefaultValue) is { } value) json["default"] = value;
    return json;
}

static string? Describe(object? value) => value switch
{
    null => null,
    float f => f.ToString("0.###", CultureInfo.InvariantCulture),
    bool b => b ? "true" : "false",
    string s => s,
    Vector3 v => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##}, {v.Y:0.##}, {v.Z:0.##}"),
    Vector4 v => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##}, {v.Y:0.##}, {v.Z:0.##}, {v.W:0.##}"),
    Color c => Hex(c),
    Enum e => e.ToString(),
    _ => value.ToString()
};

static string Hex(Color color) =>
    $"#{(int)(Math.Clamp(color.R, 0f, 1f) * 255f):x2}{(int)(Math.Clamp(color.G, 0f, 1f) * 255f):x2}{(int)(Math.Clamp(color.B, 0f, 1f) * 255f):x2}";

// ---------------------------------------------------------------- Graphs and samples

JsonNode Graphs()
{
    var graphs = new JsonObject();
    foreach (string file in Directory.GetFiles(Path.Combine(repo, "Samples", "Duel", "Assets", "Visual Scripts"), "*.vscript").Order())
    {
        graphs[Path.GetFileNameWithoutExtension(file)] = JsonNode.Parse(File.ReadAllText(file));
    }

    // A VFX graph to show: the one a new VFX graph starts as.
    graphs["Embers (VFX)"] = JsonNode.Parse(VfxNodes.Starter().ToJson());
    return graphs;
}

JsonNode Samples()
{
    var samples = new JsonObject();
    foreach (string file in Directory.GetFiles(Path.Combine(repo, "Samples"), "*.cs", SearchOption.AllDirectories).Order())
    {
        string relative = Path.GetRelativePath(Path.Combine(repo, "Samples"), file).Replace('\\', '/');
        if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
        samples[relative] = File.ReadAllText(file).Replace("\r\n", "\n");
    }

    return samples;
}

// ---------------------------------------------------------------- The C# API

JsonNode Api()
{
    Assembly engine = typeof(GameObject).Assembly;
    string xmlPath = Path.ChangeExtension(engine.Location, ".xml");
    var docs = File.Exists(xmlPath)
        ? XDocument.Load(xmlPath).Descendants("member").ToDictionary(m => (string)m.Attribute("name")!, m => m)
        : throw new FileNotFoundException("The engine's doc comments (Vertex.xml) weren't built.", xmlPath);

    var namespaces = new SortedDictionary<string, JsonArray>(StringComparer.Ordinal);
    foreach (Type type in engine.GetExportedTypes().OrderBy(t => t.FullName))
    {
        if (type.IsDefined(typeof(CompilerGeneratedAttribute)) || type.Name.StartsWith('<')) continue;
        string ns = type.Namespace ?? "Vertex";
        if (!namespaces.TryGetValue(ns, out JsonArray? list)) namespaces[ns] = list = new JsonArray();
        list.Add(TypeJson(type, docs));
    }

    var result = new JsonArray();
    foreach ((string ns, JsonArray types) in namespaces) result.Add(new JsonObject { ["name"] = ns, ["types"] = types });
    return new JsonObject { ["namespaces"] = result };
}

static JsonObject TypeJson(Type type, Dictionary<string, XElement> docs)
{
    var nullability = new NullabilityInfoContext();
    string kind = type.IsEnum ? "enum"
        : type.IsInterface ? "interface"
        : typeof(Delegate).IsAssignableFrom(type) ? "delegate"
        : type.IsValueType ? (type.GetMethod("<Clone>$") != null ? "record struct" : "struct")
        : type.IsAbstract && type.IsSealed ? "static class"
        : type.GetMethod("<Clone>$") != null ? "record"
        : "class";

    var json = new JsonObject
    {
        ["name"] = Name(type, full: false),
        ["fullName"] = type.FullName!.Replace('+', '.'),
        ["kind"] = kind
    };
    if (type.IsAbstract && !type.IsSealed && !type.IsInterface) json["abstract"] = true;
    if (type.IsClass && type.BaseType is { } baseType && baseType != typeof(object) && !typeof(Delegate).IsAssignableFrom(type)) json["base"] = Name(baseType, full: false);
    if (typeof(Component).IsAssignableFrom(type) && !type.IsAbstract) json["component"] = true;
    if (Doc(docs, "T:" + DocName(type)) is { } doc) Fill(json, doc);

    var members = new JsonArray();
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    if (type.IsEnum)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var entry = new JsonObject { ["kind"] = "value", ["name"] = field.Name, ["signature"] = field.Name };
            if (Doc(docs, $"F:{DocName(type)}.{field.Name}") is { } d) Fill(entry, d);
            members.Add(entry);
        }

        json["members"] = members;
        return json;
    }

    if (typeof(Delegate).IsAssignableFrom(type))
    {
        MethodInfo invoke = type.GetMethod("Invoke")!;
        json["signature"] = $"delegate {Name(invoke.ReturnType, false)} {Name(type, false)}({Parameters(invoke)})";
        json["members"] = members;
        return json;
    }

    bool Shown(MethodBase? method) => method != null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    foreach (ConstructorInfo constructor in type.GetConstructors(flags).Where(c => Shown(c) && !c.IsStatic))
    {
        if (type.IsValueType && constructor.GetParameters().Length == 0) continue;
        var entry = new JsonObject { ["kind"] = "constructor", ["name"] = Name(type, false), ["signature"] = $"{Access(constructor)}{Name(type, false)}({Parameters(constructor)})" };
        if (Doc(docs, $"M:{DocName(type)}.#ctor{DocParameters(constructor)}") is { } d) Fill(entry, d);
        members.Add(entry);
    }


    foreach (PropertyInfo property in type.GetProperties(flags).Where(p => Shown(p.GetMethod) || Shown(p.SetMethod)).OrderBy(p => p.MetadataToken))
    {
        if (property.GetIndexParameters().Length > 0 || property.IsDefined(typeof(ObsoleteAttribute))) continue;
        MethodInfo accessor = Shown(property.GetMethod) ? property.GetMethod! : property.SetMethod!;
        string accessors = (Shown(property.GetMethod) ? "get; " : "") + (Shown(property.SetMethod) ? (property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init; " : "set; ") : "");
        string typeName = Name(property.PropertyType, false) + Nullable(nullability.Create(property).ReadState, property.PropertyType);
        var entry = new JsonObject
        {
            ["kind"] = "property",
            ["name"] = property.Name,
            ["signature"] = $"{Access(accessor)}{(accessor.IsStatic ? "static " : "")}{typeName} {property.Name} {{ {accessors}}}"
        };
        if (Doc(docs, $"P:{DocName(type)}.{property.Name}") is { } d) Fill(entry, d);
        members.Add(entry);
    }

    foreach (FieldInfo field in type.GetFields(flags).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly).OrderBy(f => f.MetadataToken))
    {
        if (field.IsDefined(typeof(CompilerGeneratedAttribute))) continue;
        string modifiers = (field.IsFamily || field.IsFamilyOrAssembly ? "protected " : "public ") + (field.IsLiteral ? "const " : field.IsStatic ? "static " : "") + (field.IsInitOnly ? "readonly " : "");
        string typeName = Name(field.FieldType, false) + Nullable(nullability.Create(field).ReadState, field.FieldType);
        var entry = new JsonObject { ["kind"] = field.IsLiteral ? "constant" : "field", ["name"] = field.Name, ["signature"] = $"{modifiers}{typeName} {field.Name}" };
        if (field.IsLiteral && Describe(field.GetRawConstantValue()) is { } value) entry["signature"] += field.FieldType == typeof(string) ? $" = \"{value}\"" : $" = {value}";
        if (Doc(docs, $"F:{DocName(type)}.{field.Name}") is { } d) Fill(entry, d);
        members.Add(entry);
    }

    foreach (EventInfo @event in type.GetEvents(flags).Where(e => Shown(e.AddMethod)))
    {
        var entry = new JsonObject
        {
            ["kind"] = "event",
            ["name"] = @event.Name,
            ["signature"] = $"{Access(@event.AddMethod!)}{(@event.AddMethod!.IsStatic ? "static " : "")}event {Name(@event.EventHandlerType!, false)} {@event.Name}"
        };
        if (Doc(docs, $"E:{DocName(type)}.{@event.Name}") is { } d) Fill(entry, d);
        members.Add(entry);
    }

    foreach (MethodInfo method in type.GetMethods(flags).Where(m => Shown(m) && !m.IsSpecialName).OrderBy(m => m.MetadataToken))
    {
        if (method.IsDefined(typeof(CompilerGeneratedAttribute)) || method.Name.Contains('<') || method.Name is "Deconstruct" or "PrintMembers" or "<Clone>$") continue;
        if (method.GetBaseDefinition().DeclaringType == typeof(object) && method.Name is "Equals" or "GetHashCode" or "ToString") continue;

        string modifiers = Access(method) + (method.IsStatic ? "static " : "") +
                           (method.IsAbstract && !type.IsInterface ? "abstract " : method.IsVirtual && !method.IsFinal && method.GetBaseDefinition() == method && !type.IsInterface ? "virtual " : "") +
                           (method.GetBaseDefinition() != method ? "override " : "");
        string generic = method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : "";
        string returns = Name(method.ReturnType, false) + Nullable(nullability.Create(method.ReturnParameter).ReadState, method.ReturnType);
        var entry = new JsonObject
        {
            ["kind"] = "method",
            ["name"] = method.Name,
            ["signature"] = $"{modifiers}{returns} {method.Name}{generic}({Parameters(method)})"
        };
        string docId = $"M:{DocName(type)}.{method.Name}{(method.IsGenericMethodDefinition ? "``" + method.GetGenericArguments().Length : "")}{DocParameters(method)}";
        if (Doc(docs, docId) is { } d) Fill(entry, d);
        members.Add(entry);
    }

    json["members"] = members;
    return json;

    string Parameters(MethodBase method) => string.Join(", ", method.GetParameters().Select(p =>
    {
        string prefix = p.IsDefined(typeof(ParamArrayAttribute)) ? "params " : p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
        Type parameterType = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        string text = $"{prefix}{Name(parameterType, false)}{Nullable(nullability.Create(p).ReadState, parameterType)} {p.Name ?? "value"}";
        if (p.HasDefaultValue) text += " = " + (p.DefaultValue switch { null => "null", string s => $"\"{s}\"", bool b => b ? "true" : "false", float f => f.ToString("0.###", CultureInfo.InvariantCulture) + "f", _ => Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) });
        return text;
    }));
}

static string Access(MethodBase method) => method.IsPublic ? "public " : "protected ";

static string Nullable(NullabilityState state, Type type) =>
    state == NullabilityState.Nullable && !type.IsValueType ? "?" : "";

static XElement? Doc(Dictionary<string, XElement> docs, string id) => docs.GetValueOrDefault(id);

/// <summary>A type's name as written in C#: "List&lt;GameObject&gt;", "float", "Vector3?".</summary>
static string Name(Type type, bool full)
{
    if (type.IsByRef) return Name(type.GetElementType()!, full);
    if (type.IsArray) return Name(type.GetElementType()!, full) + "[]";
    if (type.IsGenericParameter) return type.Name;
    if (System.Nullable.GetUnderlyingType(type) is { } underlying) return Name(underlying, full) + "?";

    string? keyword = type == typeof(void) ? "void" : type == typeof(int) ? "int" : type == typeof(float) ? "float" : type == typeof(double) ? "double"
        : type == typeof(bool) ? "bool" : type == typeof(string) ? "string" : type == typeof(object) ? "object" : type == typeof(byte) ? "byte"
        : type == typeof(uint) ? "uint" : type == typeof(long) ? "long" : type == typeof(ulong) ? "ulong" : type == typeof(short) ? "short" : type == typeof(char) ? "char" : null;
    if (keyword != null) return keyword;

    string name = type.Name;
    int tick = name.IndexOf('`');
    if (tick >= 0) name = name[..tick];
    if (type.IsNested && !type.IsGenericParameter) name = Name(type.DeclaringType!, full) + "." + name;
    else if (full && type.Namespace != null) name = type.Namespace + "." + name;

    if (type.IsGenericType)
    {
        Type[] arguments = type.GetGenericArguments();
        if (type.IsNested) arguments = arguments.Skip(type.DeclaringType!.GetGenericArguments().Length).ToArray();
        if (arguments.Length > 0) name += "<" + string.Join(", ", arguments.Select(a => Name(a, full))) + ">";
    }

    return name;
}

/// <summary>A type's name as the doc comments file has it ("Vertex.Physics.Rigidbody", nested types with dots).</summary>
static string DocName(Type type) => type.FullName!.Replace('+', '.');

static string DocParameters(MethodBase method)
{
    ParameterInfo[] parameters = method.GetParameters();
    if (parameters.Length == 0) return "";
    return "(" + string.Join(",", parameters.Select(p => DocType(p.ParameterType, method))) + ")";
}

static string DocType(Type type, MethodBase method)
{
    if (type.IsByRef) return DocType(type.GetElementType()!, method) + "@";
    if (type.IsArray) return DocType(type.GetElementType()!, method) + "[]";
    if (type.IsGenericParameter) return type.DeclaringMethod != null ? "``" + type.GenericParameterPosition : "`" + type.GenericParameterPosition;
    if (type.IsGenericType)
    {
        string name = type.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')].Replace('+', '.');
        return name + "{" + string.Join(",", type.GetGenericArguments().Select(a => DocType(a, method))) + "}";
    }

    return type.FullName!.Replace('+', '.');
}

/// <summary>A member's summary and the rest, as small pieces of HTML: cref links as data-ref anchors the site turns into links.</summary>
static void Fill(JsonObject json, XElement doc)
{
    if (doc.Element("summary") is { } summary) json["summary"] = Html(summary);
    var parameters = doc.Elements("param").Where(p => p.Attribute("name") != null).ToList();
    if (parameters.Count > 0) json["params"] = new JsonObject(parameters.Select(p => KeyValuePair.Create((string)p.Attribute("name")!, (JsonNode?)Html(p))));
    if (doc.Element("returns") is { } returns) json["returns"] = Html(returns);
    if (doc.Element("exception") is { } exception) json["throws"] = Html(exception);
}

static string Html(XElement element)
{
    var text = new StringBuilder();
    foreach (XNode node in element.Nodes()) Append(text, node);
    return Regex.Replace(text.ToString().Trim(), @"\s+", " ").Replace(" <br><br> ", "<br><br>");
}

static void Append(StringBuilder text, XNode node)
{
    switch (node)
    {
        case XText plain:
            text.Append(System.Net.WebUtility.HtmlEncode(plain.Value));
            break;
        case XElement { Name.LocalName: "see" or "seealso" } see:
        {
            string? cref = (string?)see.Attribute("cref");
            string? langword = (string?)see.Attribute("langword");
            if (langword != null)
            {
                text.Append($"<code>{langword}</code>");
                break;
            }

            string shown = see.Value.Length > 0 ? see.Value : ShortName(cref ?? "");
            text.Append($"<a data-ref=\"{System.Net.WebUtility.HtmlEncode(cref ?? "")}\">{System.Net.WebUtility.HtmlEncode(shown)}</a>");
            break;
        }
        case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
            text.Append($"<code>{System.Net.WebUtility.HtmlEncode((string?)reference.Attribute("name") ?? "")}</code>");
            break;
        case XElement { Name.LocalName: "c" } code:
            text.Append($"<code>{System.Net.WebUtility.HtmlEncode(code.Value)}</code>");
            break;
        case XElement { Name.LocalName: "para" } para:
            text.Append(" <br><br> ");
            foreach (XNode inner in para.Nodes()) Append(text, inner);
            break;
        case XElement { Name.LocalName: "list" } list:
            text.Append("<ul>");
            foreach (XElement item in list.Elements("item"))
            {
                text.Append("<li>");
                foreach (XNode inner in (item.Element("description") ?? item).Nodes()) Append(text, inner);
                text.Append("</li>");
            }

            text.Append("</ul>");
            break;
        case XElement other:
            foreach (XNode inner in other.Nodes()) Append(text, inner);
            break;
    }
}

/// <summary>"M:Vertex.Scene.Load(System.String)" to "Scene.Load".</summary>
static string ShortName(string cref)
{
    string name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
    int paren = name.IndexOf('(');
    if (paren >= 0) name = name[..paren];
    string[] parts = name.Split('.');
    if (parts[^1] == "#ctor" && parts.Length >= 2) return "new " + parts[^2];
    return cref.StartsWith("T:") ? parts[^1] : parts.Length >= 2 ? parts[^2] + "." + parts[^1] : name;
}
