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
using Vertex.AI;
using Vertex.Animation;
using Vertex.Audio;
using Vertex.Effects;
using Vertex.Graphs;
using Vertex.Scripting;
using Vertex.ShaderGraphs;

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
    foreach (NodeLibrary library in new[] { ScriptNodes.Library, VfxNodes.Library, AudioNodes.Library, ShaderNodes.Library, AnimationNodes.Library, BehaviorNodes.Library })
    {
        var categories = new JsonObject();
        var nodes = new JsonArray();
        foreach (NodeType node in library.All)
        {
            if (!categories.ContainsKey(node.Category)) categories[node.Category] = Hex(library.CategoryColor(node.Category));
            foreach (Port port in node.Inputs.Concat(node.Outputs).Concat(node.Repeated is { } r ? [r] : [])) types[port.Type.Name] = Hex(port.Type.WireColor);

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
            if (node.Repeated is { } repeated)
            {
                JsonNode port = PortJson(repeated);
                port["atLeast"] = node.RepeatedAtLeast;
                entry["repeated"] = port;
            }
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
    // What an input that takes nothing typed is while unlinked ("Material's"), shown where a value would be.
    if (port.WhenUnlinked != null) json["default"] = port.WhenUnlinked;
    return json;
}

static string? Describe(object? value) => value switch
{
    null => null,
    float f => f.ToString("0.###", CultureInfo.InvariantCulture),
    bool b => b ? "true" : "false",
    string s => s,
    Vector2 v => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##}, {v.Y:0.##}"),
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

    // Audio graphs to show, made as they would be in the editor.
    graphs["Footsteps (Audio)"] = JsonNode.Parse(Footsteps().ToJson());
    graphs["Gunshot (Audio)"] = JsonNode.Parse(Gunshot().ToJson());
    graphs["Rain (Audio)"] = JsonNode.Parse(Rain().ToJson());

    // Shader graphs: the two a new one starts as, and two more.
    graphs["Rim Light (Shader)"] = JsonNode.Parse(ShaderNodes.Starter().ToJson());
    graphs["Lava (Shader)"] = JsonNode.Parse(Lava().ToJson());
    graphs["Colour Grade (Shader)"] = JsonNode.Parse(ShaderNodes.PostEffectStarter().ToJson());
    graphs["Retro Pixels (Shader)"] = JsonNode.Parse(RetroPixels().ToJson());

    // Animation graphs: walking into running by speed, and a character that crouches, aims and attacks too.
    graphs["Locomotion (Animation)"] = JsonNode.Parse(Locomotion().ToJson());
    graphs["Character (Animation)"] = JsonNode.Parse(Character().ToJson());
    graphs["States (Animation)"] = JsonNode.Parse(States().ToJson());

    // Behaviour trees: the guard a new one starts as, and a sentry that patrols, turns to what it hears and chases what it sees.
    graphs["Guard (Behavior)"] = JsonNode.Parse(BehaviorNodes.Starter().ToJson());
    graphs["Sentry (Behavior)"] = JsonNode.Parse(Sentry().ToJson());
    return graphs;
}

// A state machine: idle, moving (walk and run blended by speed, linked in), and a jump from any state that falls once
// it's played, until the character's on the ground again.
// A sentry: chases the player while it sees them; turns to look when they're close by but unseen (it heard them); and
// otherwise walks its route, stopping at each point.
static Graph Sentry()
{
    var graph = new Graph(BehaviorNodes.Kind);
    graph.Variables.Add(new GraphVariable("Player", PortType.Object));
    graph.Variables.Add(new GraphVariable("Route", PortType.Object));

    GraphNode Add(string type, float x, float y, params (string Port, object Value)[] values)
    {
        GraphNode node = graph.AddNode(type, new Vector2(x, y));
        foreach ((string port, object value) in values) node.Values[port] = value;
        return node;
    }

    void Under(GraphNode parent, string port, params GraphNode[] children)
    {
        foreach (GraphNode child in children) graph.Link(parent.Id, port, child.Id, "Parent");
    }

    GraphNode root = Add("Root/Root", 0f, 200f);
    GraphNode choose = Add("Composites/Selector", 220f, 200f);
    Under(root, "Start", choose);

    GraphNode chase = Add("Composites/Sequence", 460f, 0f, ("Recheck", "Both"));
    Under(chase, "Children", Add("Conditions/Can See", 760f, -60f, ("Target", "Player")), Add("Movement/Move To", 760f, 100f, ("Target", "Player"), ("Stop Within", 1.5f), ("Speed", 5f)));

    GraphNode listen = Add("Composites/Sequence", 460f, 270f, ("Recheck", "Both"));
    Under(listen, "Children", Add("Conditions/Is Near", 760f, 250f, ("Target", "Player"), ("Distance", 5f)), Add("Movement/Look At", 760f, 370f, ("Target", "Player")));

    GraphNode patrol = Add("Composites/Sequence", 460f, 500f);
    Under(patrol, "Children", Add("Movement/Patrol", 760f, 490f, ("Waypoints", "Route")), Add("Actions/Wait", 760f, 610f, ("Seconds", 1f), ("Random", 1f)));
    Under(choose, "Children", chase, listen, patrol);
    return graph;
}

static Graph States()
{
    var graph = new Graph(AnimationNodes.Kind);
    graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
    graph.Variables.Add(new GraphVariable("Grounded", PortType.Boolean, true));
    graph.Variables.Add(new GraphVariable("Jump", AnimationNodes.Trigger));

    var machine = new AnimationStateMachine { Entry = "Idle" };
    machine.States.Add(new AnimationState { Name = "Idle", Animation = "Animations/Idle.anim", Position = new Vector2(0f, 0f) });
    machine.States.Add(new AnimationState { Name = "Move", Position = new Vector2(250f, 0f) });
    machine.States.Add(new AnimationState { Name = "Jump", Animation = "Animations/Jump.anim", Position = new Vector2(70f, 150f) });
    machine.States.Add(new AnimationState { Name = "Fall", Animation = "Animations/Fall.anim", Position = new Vector2(300f, 190f) });

    StateTransition When(string from, string to, string parameter, Comparison comparison, float value)
    {
        var transition = new StateTransition { From = from, To = to };
        transition.Conditions.Add(new TransitionCondition { Parameter = parameter, Is = comparison, Value = value });
        return transition;
    }

    machine.Transitions.Add(When("Idle", "Move", "Speed", Comparison.Greater, 0.1f));
    machine.Transitions.Add(When("Move", "Idle", "Speed", Comparison.LessOrEqual, 0.1f));
    machine.Transitions.Add(When(AnimationStateMachine.AnyState, "Jump", "Jump", Comparison.Equal, 1f));
    machine.Transitions.Add(new StateTransition { From = "Jump", To = "Fall", AtEnd = true });
    machine.Transitions.Add(When("Fall", "Idle", "Grounded", Comparison.Equal, 1f));

    GraphNode speed = Node(graph, "Parameters/Get Parameter", -20f, -40f, ("Variable", "Speed"));
    GraphNode walk = ClipNode(graph, "Walk", -20f, 60f);
    GraphNode run = ClipNode(graph, "Run", -20f, 200f);
    GraphNode moving = Node(graph, "Blend/Blend by Value", 300f, 40f, ("At", "1.5, 4"));
    graph.Link(speed.Id, "Value", moving.Id, "Value");
    graph.Link(walk.Id, "Pose", moving.Id, "Pose 1");
    graph.Link(run.Id, "Pose", moving.Id, "Pose 2");

    GraphNode states = graph.AddNode("Blend/State Machine", new Vector2(600f, -20f));
    machine.Store(graph, states);
    graph.Link(moving.Id, "Pose", states.Id, "Move");

    GraphNode output = Node(graph, "Output/Output", 900f, -20f);
    graph.Link(states.Id, "Pose", output.Id, "Pose");
    return graph;
}

// A Clip node playing Animations/<name>.anim.
static GraphNode ClipNode(Graph graph, string name, float x, float y)
{
    GraphNode node = graph.AddNode("Animations/Clip", new Vector2(x, y));
    node.Values["Animation"] = $"Animations/{name}.anim";
    return node;
}

// What a new animation graph starts as, with its clips chosen: idle, walk and run by Speed.
static Graph Locomotion()
{
    Graph graph = AnimationNodes.Starter();
    string[] names = ["Idle", "Walk", "Run"];
    List<GraphNode> clips = graph.Nodes.Where(n => n.Type == "Animations/Clip").ToList();
    for (int i = 0; i < clips.Count; i++) clips[i].Values["Animation"] = $"Animations/{names[i]}.anim";
    return graph;
}

// A character: walks and runs by speed, crouches while Crouching, aims its upper body while Aiming, and swings when
// Attack is triggered.
static Graph Character()
{
    var graph = new Graph(AnimationNodes.Kind);
    graph.Variables.Add(new GraphVariable("Speed", PortType.Number, 0f));
    graph.Variables.Add(new GraphVariable("Crouching", PortType.Boolean, false));
    graph.Variables.Add(new GraphVariable("Aiming", PortType.Boolean, false));
    graph.Variables.Add(new GraphVariable("Attack", AnimationNodes.Trigger));

    GraphNode Parameter(string name, float x, float y) => Node(graph, "Parameters/Get Parameter", x, y, ("Variable", name));

    GraphNode speed = Parameter("Speed", -40f, -140f);
    GraphNode idle = ClipNode(graph, "Idle", -40f, -40f);
    GraphNode walk = ClipNode(graph, "Walk", -40f, 100f);
    GraphNode run = ClipNode(graph, "Run", -40f, 240f);
    GraphNode moving = Node(graph, "Blend/Blend by Value", 280f, -20f, ("At", "0, 1.5, 4"));
    graph.Link(speed.Id, "Value", moving.Id, "Value");
    graph.Link(idle.Id, "Pose", moving.Id, "Pose 1");
    graph.Link(walk.Id, "Pose", moving.Id, "Pose 2");
    graph.Link(run.Id, "Pose", moving.Id, "Pose 3");

    GraphNode crouching = Parameter("Crouching", 280f, -180f);
    GraphNode crouch = ClipNode(graph, "Crouch", 280f, 240f);
    GraphNode stance = Node(graph, "Blend/Blend by Bool", 580f, -40f, ("Fade", 0.25f));
    graph.Link(crouching.Id, "Value", stance.Id, "Condition");
    graph.Link(crouch.Id, "Pose", stance.Id, "True");
    graph.Link(moving.Id, "Pose", stance.Id, "False");

    // Aiming puts the aim over the upper body (from the spine up), fading in and out.
    GraphNode aim = ClipNode(graph, "Aim", 580f, 260f);
    GraphNode layer = Node(graph, "Blend/Layer", 880f, 120f, ("From", "Spine"));
    graph.Link(stance.Id, "Pose", layer.Id, "Base");
    graph.Link(aim.Id, "Pose", layer.Id, "Layer");
    GraphNode aiming = Parameter("Aiming", 880f, -160f);
    GraphNode upper = Node(graph, "Blend/Blend by Bool", 1180f, -20f, ("Fade", 0.2f));
    graph.Link(aiming.Id, "Value", upper.Id, "Condition");
    graph.Link(layer.Id, "Pose", upper.Id, "True");
    graph.Link(stance.Id, "Pose", upper.Id, "False");

    GraphNode attack = Parameter("Attack", 1180f, 220f);
    GraphNode swing = ClipNode(graph, "Swing", 1180f, 320f);
    GraphNode once = Node(graph, "Blend/Play Once", 1480f, 40f);
    graph.Link(upper.Id, "Pose", once.Id, "Base");
    graph.Link(swing.Id, "Pose", once.Id, "Action");
    graph.Link(attack.Id, "Value", once.Id, "Play");

    GraphNode output = Node(graph, "Output/Output", 1780f, 40f);
    graph.Link(once.Id, "Pose", output.Id, "Pose");
    return graph;
}

// A Sound node playing Sounds/<name>.wav.
static GraphNode SoundFile(Graph graph, string name, float x, float y)
{
    GraphNode node = graph.AddNode("Sounds/Sound", new Vector2(x, y));
    node.Values["Sound"] = $"Sounds/{name}.wav";
    return node;
}

static GraphNode Node(Graph graph, string type, float x, float y, params (string Port, object Value)[] values)
{
    GraphNode node = graph.AddNode(type, new Vector2(x, y));
    foreach ((string port, object value) in values) node.Values[port] = value;
    return node;
}

// One of four footsteps, never the same twice running, a little higher or lower and louder or quieter each time.
// Glowing cracks that crawl across a dark rock: noise, scrolled by time, cut into cracks, lit by a Glow property.
static Graph Lava()
{
    var graph = new Graph(ShaderNodes.Kind);
    graph.Variables.Add(new GraphVariable("Glow", PortType.Color, new Color(1f, 0.35f, 0.08f)));
    GraphNode time = graph.AddNode("Input/Time", new Vector2(0f, 0f));
    GraphNode slow = graph.AddNode("Math/Multiply", new Vector2(200f, 0f));
    slow.Values["B"] = 0.05f;
    GraphNode scroll = graph.AddNode("UV/Tiling And Offset", new Vector2(420f, -10f));
    scroll.Values["Tiling"] = new Vector2(2f, 2f);
    GraphNode noise = graph.AddNode("Noise/Noise", new Vector2(680f, 0f));
    noise.Values["Scale"] = 6f;
    noise.Values["Octaves"] = 4f;
    GraphNode cracks = graph.AddNode("Math/Smoothstep", new Vector2(920f, 0f));
    cracks.Values["Edge 1"] = 0.55f;
    cracks.Values["Edge 2"] = 0.7f;
    GraphNode glow = graph.AddNode("Properties/Get Property", new Vector2(920f, 150f));
    glow.Values["Variable"] = "Glow";
    GraphNode bright = graph.AddNode("Math/Multiply", new Vector2(1160f, 60f));
    GraphNode rock = graph.AddNode("Constant/Color", new Vector2(1160f, -110f));
    rock.Values["Color"] = new Color(0.08f, 0.06f, 0.05f);
    GraphNode surface = graph.AddNode("Output/Surface", new Vector2(1400f, -60f));

    graph.Link(time.Id, "Time", slow.Id, "A");
    graph.Link(slow.Id, "Result", scroll.Id, "Offset");
    graph.Link(scroll.Id, "UV", noise.Id, "UV");
    graph.Link(noise.Id, "Noise", cracks.Id, "Value");
    graph.Link(cracks.Id, "Result", bright.Id, "A");
    graph.Link(glow.Id, "Value", bright.Id, "B");
    graph.Link(rock.Id, "Color", surface.Id, "Color");
    graph.Link(bright.Id, "Result", surface.Id, "Emission");
    return graph;
}

// The picture in big pixels and a few shades, for a post effect.
static Graph RetroPixels()
{
    var graph = new Graph(ShaderNodes.Kind);
    graph.Variables.Add(new GraphVariable("Shades", PortType.Number, 6f));
    GraphNode pixelate = graph.AddNode("UV/Pixelate", new Vector2(0f, 0f));
    pixelate.Values["Pixels"] = new Vector2(320f, 180f);
    GraphNode picture = graph.AddNode("Input/Picture", new Vector2(240f, 0f));
    GraphNode shades = graph.AddNode("Properties/Get Property", new Vector2(240f, 110f));
    shades.Values["Variable"] = "Shades";
    GraphNode posterize = graph.AddNode("Math/Posterize", new Vector2(500f, 20f));
    GraphNode output = graph.AddNode("Output/Post Effect", new Vector2(740f, 30f));
    graph.Link(pixelate.Id, "UV", picture.Id, "UV");
    graph.Link(picture.Id, "Color", posterize.Id, "Value");
    graph.Link(shades.Id, "Value", posterize.Id, "Steps");
    graph.Link(posterize.Id, "Result", output.Id, "Color");
    return graph;
}

static Graph Footsteps()
{
    var graph = new Graph(AudioNodes.Kind);
    GraphNode random = Node(graph, "Choose/Random", 300f, 120f);
    for (int i = 1; i <= 4; i++) graph.Link(SoundFile(graph, $"Step {i}", 0f, (i - 1) * 100f).Id, "Sound", random.Id, $"Sound {i}");
    GraphNode pitch = Node(graph, "Change/Random Pitch", 560f, 120f, ("Min", 0.92f), ("Max", 1.08f));
    GraphNode volume = Node(graph, "Change/Random Volume", 800f, 120f, ("Min", 0.75f), ("Max", 1f));
    GraphNode output = Node(graph, "Output/Output", 1040f, 120f);
    graph.Link(random.Id, "Sound", pitch.Id, "Sound");
    graph.Link(pitch.Id, "Sound", volume.Id, "Sound");
    graph.Link(volume.Id, "Sound", output.Id, "Sound");
    return graph;
}

// A crack (one of two), a body, and an echo a moment later, played together at a slightly different pitch each time.
static Graph Gunshot()
{
    var graph = new Graph(AudioNodes.Kind);
    GraphNode crack = Node(graph, "Choose/Random", 300f, 0f);
    graph.Link(SoundFile(graph, "Crack 1", 0f, 0f).Id, "Sound", crack.Id, "Sound 1");
    graph.Link(SoundFile(graph, "Crack 2", 0f, 100f).Id, "Sound", crack.Id, "Sound 2");
    GraphNode body = SoundFile(graph, "Body", 300f, 200f);
    GraphNode delay = Node(graph, "Timing/Delay", 300f, 320f, ("Seconds", 0.12f));
    GraphNode echo = SoundFile(graph, "Echo", 0f, 300f);
    echo.Values["Volume"] = 0.5f;
    graph.Link(echo.Id, "Sound", delay.Id, "Sound");
    GraphNode layer = Node(graph, "Combine/Layer", 580f, 120f);
    graph.Link(crack.Id, "Sound", layer.Id, "Sound 1");
    graph.Link(body.Id, "Sound", layer.Id, "Sound 2");
    graph.Link(delay.Id, "Sound", layer.Id, "Sound 3");
    GraphNode pitch = Node(graph, "Change/Random Pitch", 820f, 120f, ("Min", 0.95f), ("Max", 1.05f));
    GraphNode output = Node(graph, "Output/Output", 1060f, 120f);
    graph.Link(layer.Id, "Sound", pitch.Id, "Sound");
    graph.Link(pitch.Id, "Sound", output.Id, "Sound");
    return graph;
}

// Rain that fades in and loops, with a roll of thunder now and then: a chance of one every 4 to 9 seconds.
static Graph Rain()
{
    var graph = new Graph(AudioNodes.Kind);
    graph.Variables.Add(new GraphVariable("Storminess", PortType.Number, 0.3f));
    GraphNode rain = SoundFile(graph, "Rain", 0f, 0f);
    GraphNode loopRain = Node(graph, "Timing/Loop", 280f, 0f);
    GraphNode fade = Node(graph, "Change/Fade In", 540f, 0f, ("Seconds", 3f));
    graph.Link(rain.Id, "Sound", loopRain.Id, "Sound");
    graph.Link(loopRain.Id, "Sound", fade.Id, "Sound");

    GraphNode thunder = Node(graph, "Choose/Random", 280f, 180f);
    graph.Link(SoundFile(graph, "Thunder 1", 0f, 140f).Id, "Sound", thunder.Id, "Sound 1");
    graph.Link(SoundFile(graph, "Thunder 2", 0f, 240f).Id, "Sound", thunder.Id, "Sound 2");
    GraphNode chance = Node(graph, "Choose/Chance", 540f, 180f);
    GraphNode storminess = Node(graph, "Properties/Get Property", 280f, 340f, ("Variable", "Storminess"));
    graph.Link(thunder.Id, "Sound", chance.Id, "Sound");
    graph.Link(storminess.Id, "Value", chance.Id, "Chance");
    GraphNode gap = Node(graph, "Math/Random Number", 540f, 340f, ("Min", 4f), ("Max", 9f));
    GraphNode loopThunder = Node(graph, "Timing/Loop", 800f, 180f);
    graph.Link(chance.Id, "Sound", loopThunder.Id, "Sound");
    graph.Link(gap.Id, "Value", loopThunder.Id, "Gap");

    GraphNode layer = Node(graph, "Combine/Layer", 1040f, 80f);
    graph.Link(fade.Id, "Sound", layer.Id, "Sound 1");
    graph.Link(loopThunder.Id, "Sound", layer.Id, "Sound 2");
    GraphNode output = Node(graph, "Output/Output", 1280f, 80f);
    graph.Link(layer.Id, "Sound", output.Id, "Sound");
    return graph;
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
