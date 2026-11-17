using System.Numerics;
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
    public sealed class PropertyGrid(EditorContext context)
    {
        // Rotations are shown as angles, which a rotation alone can't give back the same as typed (90 can come back as
        // -270): so the angles last shown or typed are kept, and only worked out again if the rotation is changed some other way.
        private readonly ConditionalWeakTable<object, Dictionary<string, (Quaternion Rotation, Vector3 Angles)>> _angles = new();

        // Nested settings (a material) that have been closed.
        private readonly HashSet<int> _closed = new();

        public const float RowGap = 4f;

        private static readonly string[] _meshNames = ["None", "Cube", "Sphere", "Plane"];

        /// <summary>The width of the names column for a grid <paramref name="width"/> wide.</summary>
        public static float LabelWidth(float width) => Math.Clamp(width * 0.36f, 72f, 130f);

        /// <summary>Draws a row for each of <paramref name="target"/>'s values from <paramref name="y"/> down, and returns where they end.</summary>
        public float Draw(object target, float x, float y, float width, int depth = 0)
        {
            foreach (InspectedMember member in InspectedMember.Of(target.GetType()))
            {
                y = DrawMember(target, member, x, y, width, depth);
            }

            return y;
        }

        private float DrawMember(object target, InspectedMember member, float x, float y, float width, int depth)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(target), member.Name);

            if (!member.TryRead(target, out object? value))
            {
                Row(id, x, y, width, member, out Rect broken);
                Ui.Text(broken, "—", Theme.TextFaint);
                return y + Theme.RowHeight + RowGap;
            }

            if (member.Kind == MemberKind.Nested) return Nested(id, target, member, value, x, y, width, depth);

            Row(id, x, y, width, member, out Rect field);
            DrawValue(id, target, member, value, field);
            return y + Theme.RowHeight + RowGap;
        }

        /// <summary>A row's name, and the rectangle beside it for its control.</summary>
        public static void Row(int id, float x, float y, float width, InspectedMember member, out Rect field) =>
            Row(id, x, y, width, member.Name, member.Tooltip, out field);

        public static void Row(int id, float x, float y, float width, string name, string? tooltip, out Rect field)
        {
            float labelWidth = LabelWidth(width);
            var label = new Rect(x, y, labelWidth - 6f, Theme.RowHeight);
            Widgets.Label(Ui.Id(id, "label"), label, name, tooltip);
            field = new Rect(x + labelWidth, y, width - labelWidth, Theme.RowHeight);
        }

        private void DrawValue(int id, object target, InspectedMember member, object? value, Rect field)
        {
            switch (member.Kind)
            {
                case MemberKind.Float:
                {
                    float number = value is double d ? (float)d : (float)(value ?? 0f);
                    bool changed = member.Range is { } range
                        ? Widgets.Slider(id, field, ref number, range.Min, range.Max)
                        : NumberField.Float(id, field, ref number);
                    if (changed) Set(target, member, member.Type == typeof(double) ? (double)number : number);
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
                        changed = NumberField.Int(id, field, ref number);
                    }

                    if (changed) Set(target, member, number);
                    break;
                }

                case MemberKind.Bool:
                {
                    bool on = (bool)(value ?? false);
                    if (Widgets.Checkbox(id, field.TakeLeft(Theme.RowHeight), ref on)) Set(target, member, on);
                    break;
                }

                case MemberKind.Text:
                {
                    string text = (string?)value ?? "";
                    if (TextInput.Draw(id, field, ref text)) Set(target, member, text);
                    break;
                }

                case MemberKind.Vector2:
                {
                    var vector = (Vector2)(value ?? Vector2.Zero);
                    if (NumberField.Vector2(id, field, ref vector)) Set(target, member, vector);
                    break;
                }

                case MemberKind.Vector3:
                {
                    var vector = (Vector3)(value ?? Vector3.Zero);
                    if (NumberField.Vector3(id, field, ref vector)) Set(target, member, vector);
                    break;
                }

                case MemberKind.Rotation:
                    RotationField(id, field, target, member.Member.Name, (Quaternion)(value ?? Quaternion.Identity),
                        rotation => Set(target, member, rotation));
                    break;

                case MemberKind.Color:
                    ColorField.Draw(id, field, (Color)(value ?? Color.White), (color, finished) =>
                    {
                        Set(target, member, color);
                        if (finished) context.History.EndMerge();
                    });
                    break;

                case MemberKind.Enum:
                {
                    Array values = Enum.GetValues(member.Type);
                    var names = new string[values.Length];
                    int selected = -1;
                    for (int i = 0; i < values.Length; i++)
                    {
                        object option = values.GetValue(i)!;
                        names[i] = ComponentCatalog.SplitWords(option.ToString()!);
                        if (option.Equals(value)) selected = i;
                    }

                    if (Widgets.Dropdown(id, field, names, selected) is int picked) Set(target, member, values.GetValue(picked));
                    break;
                }

                case MemberKind.Mesh:
                    MeshField(id, field, target, member, value as Mesh);
                    break;

                case MemberKind.Texture:
                    Ui.Rect(field, Theme.Field, Theme.ControlRadius);
                    Ui.Text(field.Shrink(8f, 0f), value is Texture texture ? $"{texture.GetType().Name}  {texture.Width} × {texture.Height}" : "None",
                        value == null ? Theme.TextFaint : Theme.TextDim);
                    break;

                case MemberKind.Reference:
                    Ui.Rect(field, Theme.Field, Theme.ControlRadius);
                    Ui.Text(field.Shrink(8f, 0f), value switch
                    {
                        GameObject gameObject => gameObject.Name,
                        Component component => $"{component.GameObject.Name} ({ComponentCatalog.DisplayName(component.GetType())})",
                        _ => "None"
                    }, value == null ? Theme.TextFaint : Theme.TextDim);
                    break;
            }
        }

        private void MeshField(int id, Rect field, object target, InspectedMember member, Mesh? mesh)
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

            if (Widgets.Dropdown(id, field, names, selected) is int picked && picked != selected) Set(target, member, meshes[picked]);
        }

        /// <summary>A rotation as three angles in degrees, X, Y and Z.</summary>
        public void RotationField(int id, Rect field, object owner, string key, Quaternion rotation, Action<Quaternion> set)
        {
            Dictionary<string, (Quaternion Rotation, Vector3 Angles)> remembered = _angles.GetOrCreateValue(owner);
            if (!remembered.TryGetValue(key, out var shown) || shown.Rotation != rotation)
            {
                shown = (rotation, Transform.ToEuler(rotation));
                remembered[key] = shown;
            }

            Vector3 angles = shown.Angles;
            if (!NumberField.Vector3(id, field, ref angles, speed: 0.5f, format: "0.##")) return;

            Quaternion next = Quaternion.CreateFromYawPitchRoll(angles.Y * MathF.PI / 180f, angles.X * MathF.PI / 180f, angles.Z * MathF.PI / 180f);
            remembered[key] = (next, angles);
            set(next);
        }

        private float Nested(int id, object owner, InspectedMember member, object? value, float x, float y, float width, int depth)
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

            // A line down the left groups the rows under their heading.
            float end = Draw(value, x + 14f, y, width - 14f, depth + 1);
            Ui.Rect(new Rect(x + 4f, start, 1f, MathF.Max(end - start - RowGap, 0f)), Theme.Border);
            Ui.PopClip();

            Ui.Remember(heightKey, end - start);
            return y + shownHeight;
        }

        /// <summary>Sets the value with undo, then lets <paramref name="target"/>'s owner know if it needs to.</summary>
        private void Set(object target, InspectedMember member, object? value)
        {
            try
            {
                SetValue.Apply(context.History, $"Change {member.Name}", target, member.Member, value);
            }
            catch (Exception exception)
            {
                context.Notify($"Couldn't set {member.Name}: {(exception.InnerException ?? exception).Message}");
            }
        }
    }
}
