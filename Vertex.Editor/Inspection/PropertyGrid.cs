using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Rendering;

namespace Vertex.Editor.Inspection
{
    /// <summary>
    /// Shows an object's values as rows, a name on the left and the control to change it on the right, chosen by type:
    /// number boxes, sliders for <see cref="RangeAttribute"/> values, tick boxes, colour swatches, dropdowns for enums.
    /// Every change goes through the <see cref="UndoHistory"/>.
    /// </summary>
    public sealed class PropertyGrid
    {
        // Asked for each time: the editor's changes go to the scene's history, or the open prefab's.
        private readonly Func<UndoHistory> _historyNow;
        private UndoHistory _history => _historyNow();
        private readonly Action<string> _notify;
        private readonly EditorContext? _context;

        public PropertyGrid(EditorContext context) : this(() => context.History, context.Notify, context) {}

        /// <summary>A grid for settings outside the scene (project settings, preferences), with a history of its own.</summary>
        public PropertyGrid(UndoHistory history, Action<string> notify, EditorContext? context = null) : this(() => history, notify, context) {}

        private PropertyGrid(Func<UndoHistory> history, Action<string> notify, EditorContext? context)
        {
            _historyNow = history;
            _notify = notify;
            _context = context;
        }

        // Rotations are shown as angles, which a rotation alone can't give back the same as typed (90 can come back as
        // -270): so the angles last shown or typed are kept, and only worked out again if the rotation is changed some other way.
        private readonly ConditionalWeakTable<object, Dictionary<string, (Quaternion Rotation, Vector3 Angles)>> _angles = new();

        // Nested settings (a material) that have been closed.
        private readonly HashSet<int> _closed = new();

        public const float RowGap = 4f;

        private static readonly string[] _meshNames = ["None", "Cube", "Sphere", "Plane"];

        /// <summary>The width of the names column for a grid <paramref name="width"/> wide.</summary>
        public static float LabelWidth(float width) => Math.Clamp(width * 0.36f, 72f, 130f);

        /// <summary>
        /// Draws a row for each of <paramref name="target"/>'s values from <paramref name="y"/> down, and returns where they
        /// end. With <paramref name="others"/> (the same kind of thing on other selected objects), changing a value changes
        /// it on them all, and a value that differs between them shows a dash.
        /// </summary>
        public float Draw(object target, float x, float y, float width, int depth = 0, IReadOnlyList<object>? others = null)
        {
            if (others is { Count: 0 }) others = null;
            string? section = null;
            foreach (InspectedMember member in InspectedMember.Of(target.GetType()))
            {
                if (!member.IsShown(target)) continue;

                if (member.Section != null && member.Section != section)
                {
                    section = member.Section;
                    y += 6f;
                    Ui.Text(new Rect(x, y, width, 18f), section.ToUpperInvariant(), Theme.TextFaint, size: 10.5f, bold: true);
                    y += 22f;
                }

                y = DrawMember(target, member, x, y, width, depth, others);
            }

            if (target is PostEffect effect) y = ShaderSettings(effect, effect.Shader, effect.Values, x, y, width);
            if (target is Material material) y = ShaderSettings(material, material.Shader, material.Values, x, y, width);
            return y;
        }

        /// <summary>
        /// A post effect's or material's shader's own settings (its <c>property</c> lines, or its graph's properties), a
        /// row each with the control for its type, and what's wrong with the shader, if anything. Changed values are kept
        /// on <paramref name="owner"/>, by name. A shader made in a graph has a button to open the graph.
        /// </summary>
        private float ShaderSettings(object owner, Shader? shader, List<ShaderValue> values, float x, float y, float width)
        {
            if (shader is null) return y;

            if (shader.Graph is { Source: { } graphFile } && _context != null)
            {
                Row(Ui.Id(RuntimeHelpers.GetHashCode(owner), "shaderGraph"), x, y, width, "", null, out Rect button);
                if (Widgets.Button(Ui.Id(RuntimeHelpers.GetHashCode(owner), "editShaderGraph"), button, "Edit Shader Graph", ButtonStyle.Subtle, "Open the graph in a tab", Icons.Graph))
                {
                    _context.OpenGraph(graphFile);
                }

                y += Theme.RowHeight + RowGap;
            }

            if (shader.Problem is { } problem)
            {
                y += 2f;
                foreach (string line in problem.Split('\n').Where(l => l.Trim().Length > 0).Take(8).SelectMany(l => Ui.Wrap(l.Trim(), width, Theme.SmallTextSize)))
                {
                    Ui.Text(new Rect(x, y, width, 17f), line, Theme.Danger, size: Theme.SmallTextSize);
                    y += 17f;
                }

                y += 6f;
            }

            foreach (ShaderProperty property in shader.Properties)
            {
                if (property.Hidden) continue;
                int id = Ui.Id(RuntimeHelpers.GetHashCode(owner), "shader:" + property.Name);
                Row(id, x, y, width, ComponentCatalog.SplitWords(property.Name), property.Tooltip, out Rect field);
                Vector4 value = ShaderValue.Get(values, shader, property.Name);
                Vector4? changed = null;

                switch (property.Type)
                {
                    case ShaderPropertyType.Texture:
                    {
                        // A texture of the project's: as set, or what the shader starts it as.
                        Texture? texture = ShaderValue.GetTexture(values, shader, property.Name);
                        AssetField(id, field, AssetKind.Texture, texture, canWrite: true, path =>
                        {
                            try
                            {
                                SetShaderTexture(owner, values, property.Name, path != null ? Assets.LoadTexture(path) : null);
                            }
                            catch (Exception exception)
                            {
                                _notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                            }
                        }, noneLabel: property.Fallback switch { TextureFallback.Black => "Black", TextureFallback.Normal => "Flat", _ => "White" });
                        break;
                    }

                    case ShaderPropertyType.Float:
                    {
                        float number = value.X;
                        bool moved = property is { Min: { } min, Max: { } max } ? Widgets.Slider(id, field, ref number, min, max) : NumberField.Float(id, field, ref number);
                        if (moved) changed = new Vector4(number, 0f, 0f, 0f);
                        break;
                    }

                    case ShaderPropertyType.Int:
                    {
                        float number = value.X;
                        int whole = (int)MathF.Round(number);
                        bool moved = property is { Min: { } min, Max: { } max } ? Widgets.Slider(id, field, ref number, min, max, "0") : NumberField.Int(id, field, ref whole);
                        if (moved) changed = new Vector4(property.Min != null ? MathF.Round(number) : whole, 0f, 0f, 0f);
                        break;
                    }

                    case ShaderPropertyType.Bool:
                    {
                        bool on = value.X > 0.5f;
                        if (Widgets.Checkbox(id, field.TakeLeft(Theme.RowHeight), ref on)) changed = new Vector4(on ? 1f : 0f, 0f, 0f, 0f);
                        break;
                    }

                    case ShaderPropertyType.Vector2:
                    {
                        var vector = new Vector2(value.X, value.Y);
                        if (NumberField.Vector2(id, field, ref vector)) changed = new Vector4(vector, 0f, 0f);
                        break;
                    }

                    case ShaderPropertyType.Vector3:
                    {
                        var vector = new Vector3(value.X, value.Y, value.Z);
                        if (NumberField.Vector3(id, field, ref vector)) changed = new Vector4(vector, 0f);
                        break;
                    }

                    case ShaderPropertyType.Vector4:
                    {
                        // Two rows' worth wouldn't fit: X and Y, then Z and W.
                        var first = new Vector2(value.X, value.Y);
                        var second = new Vector2(value.Z, value.W);
                        if (NumberField.Vector2(Ui.Id(id, "xy"), field, ref first)) changed = new Vector4(first, value.Z, value.W);
                        y += Theme.RowHeight + RowGap;
                        Rect more = field with { Y = y };
                        if (NumberField.Vector2(Ui.Id(id, "zw"), more, ref second)) changed = new Vector4(value.X, value.Y, second.X, second.Y);
                        break;
                    }

                    case ShaderPropertyType.Color:
                        ColorField.Draw(id, field, new Color(value.X, value.Y, value.Z, value.W), (color, finished) =>
                        {
                            SetShaderValue(owner, values, property.Name, new Vector4(color.R, color.G, color.B, color.A));
                            if (finished) _history.EndMerge();
                        });
                        break;
                }

                if (changed is { } next) SetShaderValue(owner, values, property.Name, next);
                y += Theme.RowHeight + RowGap;
            }

            return y;
        }

        /// <summary>
        /// Changes one of a post effect's or material's shader settings, as an undo of its own (a drag is one). Both keep
        /// them in a list called Values, which is replaced by a new one, so undo has the one from before to put back.
        /// </summary>
        private void SetShaderValue(object owner, List<ShaderValue> values, string name, Vector4 value)
        {
            List<ShaderValue> changed = CopyValues(values, name);
            changed.Add(new ShaderValue { Name = name, Value = value });
            SetValue.Apply(_history, $"Change {ComponentCatalog.SplitWords(name)}", owner, owner.GetType().GetProperty(nameof(Material.Values))!, changed);
        }

        /// <summary>Changes one of a post effect's or material's shader textures (null puts it back to the shader's own).</summary>
        private void SetShaderTexture(object owner, List<ShaderValue> values, string name, Texture? texture)
        {
            List<ShaderValue> changed = CopyValues(values, name);
            if (texture != null) changed.Add(new ShaderValue { Name = name, Texture = texture });
            SetValue.Apply(_history, $"Change {ComponentCatalog.SplitWords(name)}", owner, owner.GetType().GetProperty(nameof(Material.Values))!, changed);
        }

        private static List<ShaderValue> CopyValues(List<ShaderValue> values, string without) =>
            values.Where(v => v.Name != without).Select(v => new ShaderValue { Name = v.Name, Value = v.Value, Texture = v.Texture }).ToList();

        private float DrawMember(object target, InspectedMember member, float x, float y, float width, int depth, IReadOnlyList<object>? others)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(target), member.Name);

            if (!member.TryRead(target, out object? value))
            {
                Row(id, x, y, width, member, out Rect broken);
                Ui.Text(broken, "—", Theme.TextFaint);
                return y + Theme.RowHeight + RowGap;
            }

            if (member.Kind == MemberKind.Nested) return Nested(id, target, member, value, x, y, width, depth, others);

            int mixed = others == null ? 0 : Mixed(target, member, value, others);
            Row(id, x, y, width, member.Name, mixed != 0 ? $"{member.Tooltip}{(member.Tooltip != null ? "\n\n" : "")}Not the same on all the selected objects" : member.Tooltip,
                out Rect field, mixed != 0);
            DrawValue(id, target, member, value, field, others, mixed);
            return y + Theme.RowHeight + RowGap;
        }

        /// <summary>
        /// Which parts of a value differ between <paramref name="value"/> and the same value on <paramref name="others"/>:
        /// a bit for each of a vector's or colour's parts (1 for X, 2 for Y...), or 1 for anything else. 0 if they're all the same.
        /// </summary>
        private int Mixed(object target, InspectedMember member, object? value, IReadOnlyList<object> others)
        {
            int mixed = 0;
            foreach (object other in others)
            {
                if (!member.TryRead(other, out object? theirs)) continue;
                mixed |= (value, theirs) switch
                {
                    (Vector2 a, Vector2 b) => (a.X != b.X ? 1 : 0) | (a.Y != b.Y ? 2 : 0),
                    (Vector3 a, Vector3 b) => (a.X != b.X ? 1 : 0) | (a.Y != b.Y ? 2 : 0) | (a.Z != b.Z ? 4 : 0),
                    (FloatRange a, FloatRange b) => (a.Min != b.Min ? 1 : 0) | (a.Max != b.Max ? 2 : 0),
                    (Quaternion a, Quaternion b) => AnglesDiffer(Angles(target, member.Member.Name, a), Angles(other, member.Member.Name, b)),
                    _ => Equals(value, theirs) ? 0 : 1
                };
            }

            return mixed;
        }

        /// <summary>Which of two sets of angles differ: 1 for X, 2 for Y, 4 for Z.</summary>
        public static int AnglesDiffer(Vector3 a, Vector3 b) =>
            (MathF.Abs(a.X - b.X) > 0.01f ? 1 : 0) | (MathF.Abs(a.Y - b.Y) > 0.01f ? 2 : 0) | (MathF.Abs(a.Z - b.Z) > 0.01f ? 4 : 0);

        /// <summary>A row's name, and the rectangle beside it for its control.</summary>
        public static void Row(int id, float x, float y, float width, InspectedMember member, out Rect field) =>
            Row(id, x, y, width, member.Name, member.Tooltip, out field);

        /// <param name="mixed">The value differs between the objects being edited together: the name is shown dimmer.</param>
        public static void Row(int id, float x, float y, float width, string name, string? tooltip, out Rect field, bool mixed = false)
        {
            float labelWidth = LabelWidth(width);
            var label = new Rect(x, y, labelWidth - 6f, Theme.RowHeight);
            Widgets.Label(Ui.Id(id, "label"), label, name, tooltip, mixed ? Theme.TextFaint : null);
            field = new Rect(x + labelWidth, y, width - labelWidth, Theme.RowHeight);
        }

        private void DrawValue(int id, object target, InspectedMember member, object? value, Rect field, IReadOnlyList<object>? others, int mixed)
        {
            switch (member.Kind)
            {
                case MemberKind.Float:
                {
                    float number = value is double d ? (float)d : (float)(value ?? 0f);
                    bool changed = member.Range is { } range
                        ? Widgets.Slider(id, field, ref number, range.Min, range.Max)
                        : NumberField.Float(id, field, ref number, mixed: mixed != 0);
                    if (changed) Set(target, member, member.Type == typeof(double) ? (double)number : (object)number, others);
                    break;
                }

                case MemberKind.Int:
                {
                    int number = (int)(value ?? 0);
                    bool changed;
                    if (member.Range is { } range)
                    {
                        float asFloat = number;
                        changed = Widgets.Slider(id, field, ref asFloat, range.Min, range.Max, "0");
                        number = (int)MathF.Round(asFloat);
                    }
                    else
                    {
                        changed = NumberField.Int(id, field, ref number, mixed: mixed != 0);
                    }

                    if (changed) Set(target, member, number, others);
                    break;
                }

                case MemberKind.Bool:
                {
                    bool on = (bool)(value ?? false);
                    if (Widgets.Checkbox(id, field.TakeLeft(Theme.RowHeight), ref on, mixed: mixed != 0)) Set(target, member, on, others);
                    break;
                }

                case MemberKind.Text:
                {
                    // Changes as it's typed, so the scene (a UI text in the game view) shows it at once; one undo for the lot.
                    string text = mixed != 0 ? "" : (string?)value ?? "";
                    if (TextInput.Draw(id, field, ref text, mixed != 0 ? "—" : "", live: true)) Set(target, member, text, others);
                    TypingSession(id);
                    break;
                }

                case MemberKind.Vector2:
                {
                    var vector = (Vector2)(value ?? Vector2.Zero);
                    if (NumberField.Vector2(id, field, ref vector, mixed: mixed)) Set(target, member, vector, others);
                    break;
                }

                case MemberKind.Vector3:
                {
                    var vector = (Vector3)(value ?? Vector3.Zero);
                    if (NumberField.Vector3(id, field, ref vector, mixed: mixed)) Set(target, member, vector, others);
                    break;
                }

                case MemberKind.FloatRange:
                {
                    // The smallest and the largest, side by side.
                    var range = (FloatRange)(value ?? default(FloatRange));
                    float low = range.Min, high = range.Max;
                    Rect left = field.Column(0, 2, 16f), right = field.Column(1, 2, 16f);
                    bool changed = NumberField.Float(Ui.Id(id, "min"), left, ref low, mixed: (mixed & 1) != 0);
                    changed |= NumberField.Float(Ui.Id(id, "max"), right, ref high, mixed: (mixed & 2) != 0);
                    Ui.Text(new Rect(left.Right, field.Y, 16f, field.Height), "–", Theme.TextFaint, align: 0.5f);
                    if (changed) Set(target, member, new FloatRange(low, high), others);
                    break;
                }

                case MemberKind.Rotation:
                {
                    var rotations = (others ?? []).Select(o => (o, member.TryRead(o, out object? r) && r is Quaternion q ? q : Quaternion.Identity)).ToList();
                    if (RotationField(id, field, target, member.Member.Name, (Quaternion)(value ?? Quaternion.Identity), rotations, mixed) is { } next)
                    {
                        var byOwner = new Dictionary<object, Quaternion>(ReferenceEqualityComparer.Instance) { [target] = next[0] };
                        for (int i = 0; i < rotations.Count; i++) byOwner[rotations[i].Item1] = next[i + 1];
                        SetEach(target, member, others, owner => byOwner[owner]);
                    }

                    break;
                }

                case MemberKind.Color:
                    ColorField.Draw(id, field, (Color)(value ?? Color.White), (color, finished) =>
                    {
                        Set(target, member, color, others);
                        if (finished) _history.EndMerge();
                    });
                    break;

                case MemberKind.Enum when member.Type.IsDefined(typeof(FlagsAttribute), false):
                {
                    if (FlagsField(id, field, member.Type, value, mixed != 0) is { } flags) Set(target, member, flags, others);
                    break;
                }

                case MemberKind.Enum:
                {
                    Array values = Enum.GetValues(member.Type);
                    var names = new string[values.Length];
                    int selected = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        object option = values.GetValue(i)!;
                        names[i] = ComponentCatalog.SplitWords(option.ToString()!);
                        if (option.Equals(value) && mixed == 0) selected = i;
                    }

                    if (Widgets.Dropdown(id, field, names, selected) is int picked) Set(target, member, values.GetValue(picked), others);
                    break;
                }

                case MemberKind.Mesh:
                    MeshField(id, field, target, member, value as Mesh, others, mixed != 0);
                    break;

                case MemberKind.Texture:
                case MemberKind.Asset:
                    AssetMember(id, field, target, member, value, others, mixed != 0);
                    break;

                case MemberKind.Reference:
                    ReferenceField(id, field, member.Type, value, member.CanWrite, picked => Set(target, member, picked, others), mixed != 0);
                    break;
            }
        }

        /// <summary>
        /// A set of switches, one per flag of a [Flags] enum (X, Y and Z, say), side by side: each lights up when it's on
        /// and is switched on or off on its own. Returns the new value the frame one is clicked.
        /// </summary>
        public static object? FlagsField(int id, Rect field, Type type, object? value, bool mixed)
        {
            long current = value != null ? Convert.ToInt64(value) : 0L;
            var flags = new List<(string Name, long Bit)>();
            foreach (object option in Enum.GetValues(type))
            {
                long bit = Convert.ToInt64(option);
                if (bit != 0 && (bit & (bit - 1)) == 0) flags.Add((ComponentCatalog.SplitWords(option.ToString()!), bit));
            }

            object? picked = null;
            for (int i = 0; i < flags.Count; i++)
            {
                (string name, long bit) = flags[i];
                Rect chip = field.Column(i, flags.Count, 4f);
                int chipId = Ui.Id(id, name);
                Interaction interaction = Ui.Interact(chipId, chip);
                bool on = !mixed && (current & bit) != 0;

                Color background = Ui.Smooth(chipId, on ? Theme.Accent : interaction.Hovered ? Theme.ControlHover : Theme.Control, 24f);
                Ui.Rect(chip, background, Theme.ControlRadius);
                Ui.Text(chip, mixed ? "—" : name, on ? Color.White : Theme.TextDim, align: 0.5f, size: Theme.SmallTextSize);

                if (interaction.Clicked) picked = Enum.ToObject(type, mixed ? bit : current ^ bit);
            }

            return picked;
        }

        private void MeshField(int id, Rect field, object target, InspectedMember member, Mesh? mesh, IReadOnlyList<object>? others, bool mixed)
        {
            Mesh?[] meshes = [null, Primitives.Cube, Primitives.Sphere, Primitives.Plane];
            int selected = Array.IndexOf(meshes, mesh);
            string[] names = _meshNames;
            if (selected < 0)
            {
                // Made in code or loaded from a file: listed as itself.
                names = [.. _meshNames, $"Custom ({mesh!.Indices.Length / 3:N0} triangles)"];
                meshes = [.. meshes, mesh];
                selected = names.Length - 1;
            }

            // Different meshes on the objects being edited together: none is ticked, and any picked goes on them all.
            if (mixed) selected = -1;
            if (Widgets.Dropdown(id, field, names, selected) is int picked && picked != selected) Set(target, member, meshes[picked], others);
        }

        /// <summary>The kind of project file a value of <paramref name="type"/> comes from, and how one's loaded; null if it isn't one.</summary>
        public static (AssetKind Kind, Func<string, object> Load)? AssetKindOf(Type type)
        {
            if (typeof(Texture).IsAssignableFrom(type)) return (AssetKind.Texture, Assets.LoadTexture);
            if (type == typeof(Material)) return (AssetKind.Material, Assets.LoadMaterial);
            if (type == typeof(Prefab)) return (AssetKind.Prefab, Assets.LoadPrefab);
            if (type == typeof(Vertex.UI.Font)) return (AssetKind.Font, Assets.LoadFont);
            if (type == typeof(Vertex.Audio.AudioClip)) return (AssetKind.Sound, Assets.LoadSound);
            if (type == typeof(Vertex.Graphs.Graph)) return (AssetKind.VisualScript, Assets.LoadGraph);
            if (type == typeof(Shader)) return (AssetKind.Shader, Assets.LoadShader);
            return null;
        }

        /// <summary>The file an asset was loaded from, or null if it wasn't (made in code).</summary>
        public static string? SourceOf(object? value) => value switch
        {
            Texture texture => texture.Source,
            Material material => material.Source,
            Prefab prefab => prefab.Source,
            Vertex.UI.Font font => font.Source,
            Vertex.Audio.AudioClip clip => clip.Source,
            Vertex.Graphs.Graph graph => graph.Source,
            Shader shader => shader.Source,
            _ => null
        };

        private void AssetMember(int id, Rect field, object target, InspectedMember member, object? value, IReadOnlyList<object>? others, bool mixed)
        {
            if (AssetKindOf(member.Type) is not { } kind) return;

            // A graph of another kind than a visual script (an effect's VFX graph, a source's audio graph) offers only files
            // of that kind.
            if (member.Member.GetCustomAttribute<Vertex.Graphs.GraphKindAttribute>() is { } graphKind)
            {
                if (graphKind.Kind == Vertex.Effects.VfxNodes.Kind) kind = (AssetKind.VisualEffect, kind.Load);
                else if (graphKind.Kind == Vertex.Audio.AudioNodes.Kind) kind = (AssetKind.AudioGraph, kind.Load);
            }

            // A shader slot offers only shaders for what it's for: a material's, surface shaders; a camera's, post effects.
            Func<AssetEntry, bool>? fits = member.Member.GetCustomAttribute<ShaderForAttribute>() is { } shaderFor ? entry => IsFor(entry.Path, shaderFor.Purpose) : null;

            AssetField(id, field, kind.Kind, value, member.CanWrite, path =>
            {
                try
                {
                    Set(target, member, path != null ? kind.Load(path) : null, others);
                }
                catch (Exception exception)
                {
                    _notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                }
            }, mixed: mixed, fits: fits);
        }

        /// <summary>Whether the shader in a .glsl or .shadergraph file is for <paramref name="purpose"/>.</summary>
        private static bool IsFor(string path, ShaderPurpose purpose)
        {
            try
            {
                return Assets.LoadShader(path).Purpose == purpose;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The files of <paramref name="kind"/>: for shaders, those written in GLSL and those made in shader graphs.</summary>
        private IEnumerable<AssetEntry> FilesOf(AssetKind kind) =>
            kind == AssetKind.Shader ? _context!.Assets!.All(AssetKind.Shader).Concat(_context.Assets.All(AssetKind.ShaderGraph)) : _context!.Assets!.All(kind);

        private static bool OfKind(AssetEntry entry, AssetKind kind) => entry.Kind == kind || kind == AssetKind.Shader && entry.Kind == AssetKind.ShaderGraph;

        /// <summary>
        /// A slot for one of the project's files: a small picture of it and its name. Click to pick one from a list, or
        /// drag one onto it from the assets. <paramref name="set"/> gets the chosen file's path, or null for none.
        /// </summary>
        /// <param name="mixed">Different files on the objects being edited together: shows a dash, and what's picked goes on them all.</param>
        /// <param name="fits">Which files of that kind it takes, if not all.</param>
        public void AssetField(int id, Rect field, AssetKind kind, object? value, bool canWrite, Action<string?> set, string? noneLabel = null, bool mixed = false,
            Func<AssetEntry, bool>? fits = null)
        {
            bool canPick = canWrite && _context?.Assets != null;
            Interaction interaction = Ui.Interact(id, field);

            // Something dragged from the assets lights the slot up, and lands in it when let go.
            bool dropping = canPick && DragDrop.Dragging && DragDrop.Payload is AssetEntry entry && OfKind(entry, kind) && (fits == null || fits(entry)) && field.Contains(Ui.Mouse);
            if (dropping && DragDrop.Dropped && DragDrop.Payload is AssetEntry dropped) set(dropped.Path);

            Color background = Ui.Smooth(id, interaction.Hovered && canPick ? Theme.Control : Theme.Field, 24f);
            Ui.Rect(field, background, Theme.ControlRadius);
            Ui.Outline(field, dropping ? Theme.Accent : Popup.IsOpen(id) ? Theme.Accent.WithAlpha(0.7f) : Theme.Border, Theme.ControlRadius, dropping ? 1.5f : 1f);

            var swatch = new Rect(field.X + 3f, field.Y + 3f, field.Height - 6f, field.Height - 6f);
            string? source = SourceOf(value);
            if (mixed)
            {
                Ui.Rect(swatch, Theme.Background, 3f);
            }
            else if (value is Texture texture)
            {
                Texture shown = source != null && Thumbnails.Get(source) is { } thumbnail ? thumbnail : texture;
                if (texture is not RenderTexture) Vertex.UI.Canvas.DrawImage(shown, swatch.Position, swatch.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White), 3f);
            }
            else if (value != null)
            {
                Panels.AssetsPanel.DrawPicture(new AssetEntry(source ?? "", source != null ? AssetDatabase.KindOf(source) : kind), swatch.Grow(2f), small: true);
            }
            else
            {
                Ui.Rect(swatch, Theme.Background, 3f);
            }

            string name = mixed ? "—" : value == null ? noneLabel ?? "None"
                : source != null ? Path.GetFileNameWithoutExtension(source)
                : value is RenderTexture render ? $"Render texture  {render.Width} × {render.Height}"
                : value is Texture made ? $"Made in code  {made.Width} × {made.Height}"
                : "Made in code";
            Ui.Text(new Rect(swatch.Right + 8f, field.Y, field.Right - swatch.Right - 16f, field.Height), name, value == null ? Theme.TextFaint : Theme.Text);

            if (!canPick) return;
            if (interaction.Clicked && !Popup.IsOpen(id))
            {
                Popup.Open(id, field);
                _pickerSearch = "";
                TextInput.Focus(Ui.Id(id, "search"), selectAll: true);
            }

            Popup.Show(id, new Vector2(MathF.Max(field.Width, 300f), 330f), inner => AssetPicker(id, inner, kind, source, value == null, set, noneLabel, fits));
        }

        private string _pickerSearch = "";

        /// <summary>
        /// A slot for another object in the scene, or one of its components (<paramref name="type"/> says which): its name,
        /// and its icon. Click to pick one from a list of those that fit, or drag one onto it from the outliner.
        /// </summary>
        /// <param name="mixed">Different objects on the objects being edited together: shows a dash, and what's picked goes on them all.</param>
        public void ReferenceField(int id, Rect field, Type type, object? value, bool canWrite, Action<object?> set, bool mixed = false)
        {
            bool canPick = canWrite && _context != null;
            Interaction interaction = Ui.Interact(id, field);

            // An object dragged from the outliner lights the slot up if it fits, and lands in it when let go.
            object? dropped = canPick && Panels.OutlinerPanel.Dragged is { } dragged && field.Contains(Ui.Mouse) ? Fitting(dragged, type) : null;
            if (dropped != null && Ui.MouseReleased()) set(dropped);

            Color background = Ui.Smooth(id, interaction.Hovered && canPick ? Theme.Control : Theme.Field, 24f);
            Ui.Rect(field, background, Theme.ControlRadius);
            Ui.Outline(field, dropped != null ? Theme.Accent : Popup.IsOpen(id) ? Theme.Accent.WithAlpha(0.7f) : Theme.Border, Theme.ControlRadius, dropped != null ? 1.5f : 1f);

            GameObject? shown = mixed ? null : value switch { GameObject gameObject => gameObject, Component component => component.GameObject, _ => null };
            if (shown is { IsDestroyed: true }) shown = null;
            Icon icon = shown != null ? (value is Component held ? ComponentIcons.For(held) : Panels.OutlinerPanel.IconFor(shown))
                : typeof(Component).IsAssignableFrom(type) ? ComponentIcons.For(type) : Icons.Empty;
            icon(new Vector2(field.X + 13f, field.Centre.Y), 14f, shown != null ? Theme.TextDim : Theme.TextFaint);

            string what = typeof(GameObject).IsAssignableFrom(type) ? "Object" : ComponentCatalog.DisplayName(type);
            string name = mixed ? "—" : shown == null ? $"None ({what})" : shown.Name;
            Ui.Text(new Rect(field.X + 26f, field.Y, field.Width - 34f, field.Height), name, shown == null ? Theme.TextFaint : Theme.Text);

            if (!canPick) return;
            if (interaction.Clicked && !Popup.IsOpen(id))
            {
                Popup.Open(id, field);
                _pickerSearch = "";
                TextInput.Focus(Ui.Id(id, "search"), selectAll: true);
            }

            Popup.Show(id, new Vector2(MathF.Max(field.Width, 280f), 320f), inner => ReferencePicker(id, inner, type, mixed ? null : value, set));
        }

        /// <summary>What a slot of <paramref name="type"/> would hold for <paramref name="gameObject"/>: it, or its component of that type, or null if it hasn't one.</summary>
        public static object? Fitting(GameObject gameObject, Type type)
        {
            if (type.IsInstanceOfType(gameObject)) return gameObject;
            if (!typeof(Component).IsAssignableFrom(type)) return null;
            return gameObject.Components.FirstOrDefault(type.IsInstanceOfType);
        }

        private void ReferencePicker(int id, Rect rect, Type type, object? current, Action<object?> set)
        {
            string what = typeof(GameObject).IsAssignableFrom(type) ? "objects" : ComponentCatalog.DisplayName(type) + "s";
            TextInput.Draw(Ui.Id(id, "search"), rect.TakeTop(28f), ref _pickerSearch, "Search " + what, live: true, icon: Icons.Search);

            string search = _pickerSearch.Trim();
            var choices = new List<(GameObject? Object, object? Value)> { (null, null) };
            foreach (GameObject gameObject in _context!.Scene.GameObjects)
            {
                if (gameObject.IsDestroyed || search.Length > 0 && !gameObject.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                if (Fitting(gameObject, type) is { } fits) choices.Add((gameObject, fits));
            }

            Rect list = rect.CutTop(34f);
            int scrollId = Ui.Id(id, "pickerScroll");
            var scroll = ScrollView.Begin(scrollId, list);
            const float rowHeight = 26f;
            for (int i = 0; i < choices.Count; i++)
            {
                (GameObject? gameObject, object? value) = choices[i];
                var row = new Rect(list.X, scroll.Top + i * rowHeight, list.Width - 6f, rowHeight - 2f);
                if (row.Bottom < list.Y || row.Y > list.Bottom) continue;

                Interaction interaction = Ui.Interact(Ui.Id(id, i), row);
                bool isCurrent = ReferenceEquals(value, current);
                if (isCurrent) Ui.Rect(row, Theme.Selection, 5f);
                else if (interaction.Hovered) Ui.Rect(row, Theme.Hover.WithAlpha(1.6f), 5f);

                if (gameObject == null)
                {
                    Ui.Text(row.Shrink(10f, 0f), "None", Theme.TextFaint);
                }
                else
                {
                    Panels.OutlinerPanel.IconFor(gameObject)(new Vector2(row.X + 14f, row.Centre.Y), 14f, Theme.TextDim);
                    Rect label = new(row.X + 28f, row.Y, row.Width - 34f, row.Height);
                    Ui.Text(label, gameObject.Name, Theme.Text);

                    // Where it is, so two of the same name can be told apart.
                    if (gameObject.Transform.Parent is { } parent)
                    {
                        float used = Ui.TextWidth(gameObject.Name) + 10f;
                        Ui.Text(new Rect(label.X + used, label.Y, label.Width - used, label.Height), "in " + parent.GameObject.Name, Theme.TextFaint, size: Theme.SmallTextSize);
                    }
                }

                if (interaction.Clicked)
                {
                    Popup.Close();
                    Ui.FocusId = 0;
                    set(value);
                }
            }

            if (choices.Count == 1)
            {
                Ui.Text(new Rect(list.X, scroll.Top + rowHeight + 6f, list.Width, 20f), search.Length > 0 ? $"No {what} called that" : $"No {what} in the scene", Theme.TextFaint, align: 0.5f, size: Theme.SmallTextSize);
            }

            scroll.End(choices.Count * rowHeight);
        }

        private void AssetPicker(int id, Rect rect, AssetKind kind, string? current, bool currentIsNone, Action<string?> set, string? noneLabel, Func<AssetEntry, bool>? fits)
        {
            string what = kind switch
            {
                AssetKind.Texture => "textures",
                AssetKind.Material => "materials",
                AssetKind.Prefab => "prefabs",
                AssetKind.Font => "fonts",
                AssetKind.Sound => "sounds",
                AssetKind.VisualScript => "visual scripts",
                AssetKind.VisualEffect => "VFX graphs",
                AssetKind.AudioGraph => "audio graphs",
                AssetKind.Shader => "shaders",
                _ => "files"
            };
            TextInput.Draw(Ui.Id(id, "search"), rect.TakeTop(28f), ref _pickerSearch, "Search " + what, live: true, icon: Icons.Search);

            string search = _pickerSearch.Trim();
            var choices = new List<AssetEntry?> { null };
            choices.AddRange(FilesOf(kind).Where(entry => (fits == null || fits(entry)) && (search.Length == 0 || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))));

            Rect list = rect.CutTop(34f);
            int scrollId = Ui.Id(id, "pickerScroll");
            var scroll = ScrollView.Begin(scrollId, list);
            const float tile = 64f, cell = tile + 22f;
            int columns = Math.Max((int)((list.Width - 6f) / (tile + 8f)), 1);
            float cellWidth = (list.Width - 6f) / columns;

            for (int i = 0; i < choices.Count; i++)
            {
                AssetEntry? entry = choices[i];
                var box = new Rect(list.X + i % columns * cellWidth, scroll.Top + i / columns * cell, cellWidth - 4f, cell - 4f);
                if (box.Bottom < list.Y || box.Y > list.Bottom) continue;

                int itemId = Ui.Id(id, entry?.Path ?? "none");
                Interaction interaction = Ui.Interact(itemId, box);
                bool isCurrent = entry == null ? currentIsNone : current != null && string.Equals(Path.GetFullPath(entry.Path), current, StringComparison.OrdinalIgnoreCase);
                if (isCurrent) Ui.Rect(box, Theme.Selection, 5f);
                else if (interaction.Hovered) Ui.Rect(box, Theme.Hover.WithAlpha(1.6f), 5f);

                var picture = new Rect(box.X + (box.Width - tile) * 0.5f, box.Y + 2f, tile, tile);
                if (entry != null) Panels.AssetsPanel.DrawPicture(entry, picture);
                else
                {
                    Ui.Rect(picture.Shrink(6f), Theme.Background, 6f);
                    Ui.Line(picture.Shrink(14f).Position, picture.Shrink(14f).Position + picture.Shrink(14f).Size, Theme.TextFaint, 1.5f);
                }

                Ui.Text(new Rect(box.X, picture.Bottom, box.Width, 18f), entry?.NameWithoutExtension ?? noneLabel ?? "None", Theme.TextDim, align: 0.5f, size: Theme.SmallTextSize);

                if (interaction.Clicked)
                {
                    Popup.Close();
                    Ui.FocusId = 0;
                    set(entry?.Path);
                }
            }

            if (choices.Count == 1)
            {
                Ui.Text(new Rect(list.X, scroll.Top + cell + 6f, list.Width, 20f), $"No {what} in the project yet", Theme.TextFaint, align: 0.5f, size: Theme.SmallTextSize);
            }

            scroll.End(MathF.Ceiling(choices.Count / (float)columns) * cell);
        }

        /// <summary>A rotation as three angles in degrees, X, Y and Z.</summary>
        public void RotationField(int id, Rect field, object owner, string key, Quaternion rotation, Action<Quaternion> set)
        {
            if (RotationField(id, field, owner, key, rotation, [], 0) is { } next) set(next[0]);
        }

        /// <summary>
        /// A rotation as three angles, for several objects at once: <paramref name="owner"/>'s is shown, and an angle
        /// changed on it is changed on <paramref name="others"/> too, leaving their other angles be. Returns the new
        /// rotations, the owner's then the others', or null if nothing changed.
        /// </summary>
        /// <param name="mixed">Which angles differ between them, to show a dash: 1 for X, 2 for Y, 4 for Z.</param>
        public Quaternion[]? RotationField(int id, Rect field, object owner, string key, Quaternion rotation,
            IReadOnlyList<(object Owner, Quaternion Rotation)> others, int mixed)
        {
            Vector3 before = Angles(owner, key, rotation);
            Vector3 angles = before;
            if (!NumberField.Vector3(id, field, ref angles, speed: 0.5f, format: "0.##", mixed: mixed)) return null;

            var next = new Quaternion[1 + others.Count];
            next[0] = RememberAngles(owner, key, angles);
            for (int i = 0; i < others.Count; i++)
            {
                Vector3 theirs = Angles(others[i].Owner, key, others[i].Rotation);
                var blended = new Vector3(before.X != angles.X ? angles.X : theirs.X, before.Y != angles.Y ? angles.Y : theirs.Y, before.Z != angles.Z ? angles.Z : theirs.Z);
                next[i + 1] = RememberAngles(others[i].Owner, key, blended);
            }

            return next;
        }

        /// <summary>The angles shown for <paramref name="owner"/>'s rotation: the ones last shown or typed, unless it's been turned some other way since.</summary>
        public Vector3 Angles(object owner, string key, Quaternion rotation)
        {
            Dictionary<string, (Quaternion Rotation, Vector3 Angles)> remembered = _angles.GetOrCreateValue(owner);
            if (!remembered.TryGetValue(key, out var shown) || shown.Rotation != rotation)
            {
                shown = (rotation, Transform.ToEuler(rotation));
                remembered[key] = shown;
            }

            return shown.Angles;
        }

        private Quaternion RememberAngles(object owner, string key, Vector3 angles)
        {
            Quaternion rotation = Quaternion.CreateFromYawPitchRoll(angles.Y * MathF.PI / 180f, angles.X * MathF.PI / 180f, angles.Z * MathF.PI / 180f);
            _angles.GetOrCreateValue(owner)[key] = (rotation, angles);
            return rotation;
        }

        private float Nested(int id, object owner, InspectedMember member, object? value, float x, float y, float width, int depth, IReadOnlyList<object>? others)
        {
            if (value == null || depth > 2)
            {
                Row(id, x, y, width, member, out Rect empty);
                Ui.Text(empty.Shrink(8f, 0f), "None", Theme.TextFaint);
                return y + Theme.RowHeight + RowGap;
            }

            bool open = !_closed.Contains(id);
            var header = new Rect(x - 4f, y, width + 4f, Theme.RowHeight);
            bool nowOpen = Widgets.Foldout(id, header, member.Name, open, bold: false);
            if (nowOpen != open)
            {
                if (nowOpen) _closed.Remove(id);
                else _closed.Add(id);
            }

            y += Theme.RowHeight + RowGap;

            // Slides open and shut: the rows are drawn into a window that grows to fit them.
            float openness = Ui.Smooth(Ui.Id(id, "open"), nowOpen ? 1f : 0f, 16f);
            if (openness < 0.001f) return y;

            int heightKey = Ui.Id(id, "height");
            float lastHeight = Ui.Recall(heightKey);
            float shownHeight = lastHeight * openness;

            Ui.PushClip(new Rect(x - 4f, y, width + 8f, shownHeight));
            float start = y;

            // The same settings on the other objects being edited (each changed once, though several may share one).
            List<object>? nestedOthers = null;
            if (others != null)
            {
                nestedOthers = new List<object>();
                foreach (object other in others)
                {
                    if (member.TryRead(other, out object? theirs) && theirs != null && theirs.GetType() == value.GetType() && !ReferenceEquals(theirs, value) &&
                        !nestedOthers.Any(o => ReferenceEquals(o, theirs)))
                    {
                        nestedOthers.Add(theirs);
                    }
                }
            }

            // A line down the left groups the rows under their heading. A material says first which file it is, if any.
            float end = value is Material material && member.CanWrite ? MaterialFile(id, owner, member, material, x + 14f, y, width - 14f, others) : y;
            end = Draw(value, x + 14f, end, width - 14f, depth + 1, nestedOthers);
            Ui.Rect(new Rect(x + 4f, start, 1f, MathF.Max(end - start - RowGap, 0f)), Theme.Border);
            Ui.PopClip();

            Ui.Remember(heightKey, end - start);
            return y + shownHeight;
        }

        /// <summary>
        /// Which .material file a material is, if it's one: a slot to swap it for another (or none, for one of its own),
        /// and the way between the two: saving one's own as a file, or making a shared one this object's own.
        /// </summary>
        private float MaterialFile(int id, object owner, InspectedMember member, Material material, float x, float y, float width, IReadOnlyList<object>? others)
        {
            Material? MaterialOf(object o) => member.TryRead(o, out object? m) ? m as Material : null;
            bool mixed = others != null && others.Any(o => MaterialOf(o)?.Source != material.Source);

            Row(Ui.Id(id, "file"), x, y, width, "File", "A .material file shared with everything else that uses it, or this object's own material", out Rect field, mixed);
            AssetField(Ui.Id(id, "fileSlot"), field, AssetKind.Material, material.Source != null ? material : null, canWrite: true, path =>
            {
                try
                {
                    // Back to one of its own: a copy of the file's, to carry on from.
                    Material? loaded = path != null ? Assets.LoadMaterial(path) : null;
                    SetEach(owner, member, others, o => loaded ?? MaterialOf(o)?.Copy());
                }
                catch (Exception exception)
                {
                    _notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                }
            }, noneLabel: "This object's own", mixed: mixed);
            y += Theme.RowHeight + RowGap;

            float labelWidth = LabelWidth(width);
            var note = new Rect(x + labelWidth, y, width - labelWidth, 22f);
            if (material.Source != null)
            {
                if (Widgets.Button(Ui.Id(id, "unique"), note.TakeLeft(MathF.Min(note.Width, 150f)), "Make Unique", ButtonStyle.Subtle,
                        "Give this object a copy of its own, so changing it leaves the others alone")) SetEach(owner, member, others, o => MaterialOf(o)?.Copy());
                y += 22f + RowGap;
                Ui.Text(new Rect(x, y, width, 16f), $"Changes here change everything using {Path.GetFileName(material.Source)}", Theme.TextFaint, size: Theme.SmallTextSize);
                return y + 20f + RowGap;
            }

            if (_context?.Assets is { } assets && Widgets.Button(Ui.Id(id, "saveAs"), note.TakeLeft(MathF.Min(note.Width, 150f)), "Save as File", ButtonStyle.Subtle,
                    "Save it to a .material file in the project, to use on other objects too"))
            {
                string folder = Path.Combine(assets.Root, "Materials");
                Directory.CreateDirectory(folder);
                string name = owner is Component component ? component.GameObject.Name : "Material";
                string path = Panels.AssetsPanel.Unique(Path.Combine(folder, name), ".material");
                try
                {
                    var saved = material.Copy();
                    File.WriteAllText(path, Vertex.Serialization.SceneFile.SaveMaterial(saved, _context.AssetFolders));
                    Set(owner, member, Assets.LoadMaterial(path), others);
                    assets.Refresh();
                    _notify($"Saved {Path.GetFileName(path)}");
                }
                catch (Exception exception)
                {
                    _notify($"Couldn't save the material: {exception.Message}");
                }
            }

            return y + 22f + RowGap;
        }

        private int _typingIn;

        /// <summary>
        /// Keeps track of a text box being typed in, whose changes join up into one undo; when typing stops (Enter, Escape,
        /// or clicking away) the next change starts a new one.
        /// </summary>
        public void TypingSession(int id)
        {
            if (Ui.FocusId == id) _typingIn = id;
            else if (_typingIn == id)
            {
                _typingIn = 0;
                _history.EndMerge();
            }
        }

        /// <summary>
        /// Sets the value with undo, on <paramref name="target"/> and on <paramref name="others"/> if there are any: for
        /// a vector or colour, only the parts that changed, so changing X on several objects leaves each one's Y and Z be.
        /// </summary>
        private void Set(object target, InspectedMember member, object? value, IReadOnlyList<object>? others = null)
        {
            object? before = others != null && member.TryRead(target, out object? read) ? read : null;
            SetEach(target, member, others, owner => ReferenceEquals(owner, target) || !member.TryRead(owner, out object? theirs) ? value : Blend(before, value, theirs));
        }

        /// <summary>Sets a value on <paramref name="target"/> and each of <paramref name="others"/> (as one undo), each to what <paramref name="valueFor"/> gives for it.</summary>
        private void SetEach(object target, InspectedMember member, IReadOnlyList<object>? others, Func<object, object?> valueFor)
        {
            string name = $"Change {member.Name}";
            try
            {
                if (others is not { Count: > 0 })
                {
                    SetValue.Apply(_history, name, target, member.Member, valueFor(target));
                    return;
                }

                // Every value is worked out before any is set.
                var changes = new List<IUndoable>();
                foreach (object owner in others.Prepend(target))
                {
                    changes.Add(new SetValue(name, owner, member.Member, SetValue.Read(owner, member.Member), valueFor(owner)));
                }

                _history.Do(new ChangeGroup(name, changes), (target, member.Member, others.Count));
            }
            catch (Exception exception)
            {
                _notify($"Couldn't set {member.Name}: {(exception.InnerException ?? exception).Message}");
            }
        }

        /// <summary>
        /// What another object's value becomes when one shown for several is changed from <paramref name="before"/> to
        /// <paramref name="after"/>: for vectors and colours, the parts that changed are taken and the rest of
        /// <paramref name="theirs"/> kept; anything else is simply <paramref name="after"/>.
        /// </summary>
        public static object? Blend(object? before, object? after, object? theirs) => (before, after, theirs) switch
        {
            (Vector2 b, Vector2 a, Vector2 t) => new Vector2(b.X != a.X ? a.X : t.X, b.Y != a.Y ? a.Y : t.Y),
            (Vector3 b, Vector3 a, Vector3 t) => new Vector3(b.X != a.X ? a.X : t.X, b.Y != a.Y ? a.Y : t.Y, b.Z != a.Z ? a.Z : t.Z),
            (Vector4 b, Vector4 a, Vector4 t) => new Vector4(b.X != a.X ? a.X : t.X, b.Y != a.Y ? a.Y : t.Y, b.Z != a.Z ? a.Z : t.Z, b.W != a.W ? a.W : t.W),
            (Color b, Color a, Color t) => new Color(b.R != a.R ? a.R : t.R, b.G != a.G ? a.G : t.G, b.B != a.B ? a.B : t.B, b.A != a.A ? a.A : t.A),
            (FloatRange b, FloatRange a, FloatRange t) => new FloatRange(b.Min != a.Min ? a.Min : t.Min, b.Max != a.Max ? a.Max : t.Max),
            _ => after
        };
    }
}
