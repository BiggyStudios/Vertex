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
    public sealed class PropertyGrid
    {
        private readonly UndoHistory _history;
        private readonly Action<string> _notify;
        private readonly EditorContext? _context;

        public PropertyGrid(EditorContext context) : this(context.History, context.Notify, context) {}

        /// <summary>A grid for settings outside the scene (project settings, preferences), with a history of its own.</summary>
        public PropertyGrid(UndoHistory history, Action<string> notify, EditorContext? context = null)
        {
            _history = history;
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
                    if (changed) Set(target, member, member.Type == typeof(double) ? (double)number : (object)number);
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
                        if (finished) _history.EndMerge();
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
                case MemberKind.Asset:
                    AssetMember(id, field, target, member, value);
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

        /// <summary>The kind of project file a value of <paramref name="type"/> comes from, and how one's loaded; null if it isn't one.</summary>
        public static (AssetKind Kind, Func<string, object> Load)? AssetKindOf(Type type)
        {
            if (typeof(Texture).IsAssignableFrom(type)) return (AssetKind.Texture, Assets.LoadTexture);
            if (type == typeof(Material)) return (AssetKind.Material, Assets.LoadMaterial);
            if (type == typeof(Prefab)) return (AssetKind.Prefab, Assets.LoadPrefab);
            if (type == typeof(Vertex.UI.Font)) return (AssetKind.Font, Assets.LoadFont);
            if (type == typeof(Vertex.Audio.AudioClip)) return (AssetKind.Sound, Assets.LoadSound);
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
            _ => null
        };

        private void AssetMember(int id, Rect field, object target, InspectedMember member, object? value)
        {
            if (AssetKindOf(member.Type) is not { } kind) return;
            AssetField(id, field, kind.Kind, value, member.CanWrite, path =>
            {
                try
                {
                    Set(target, member, path != null ? kind.Load(path) : null);
                }
                catch (Exception exception)
                {
                    _notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                }
            });
        }

        /// <summary>
        /// A slot for one of the project's files: a small picture of it and its name. Click to pick one from a list, or
        /// drag one onto it from the assets. <paramref name="set"/> gets the chosen file's path, or null for none.
        /// </summary>
        public void AssetField(int id, Rect field, AssetKind kind, object? value, bool canWrite, Action<string?> set, string? noneLabel = null)
        {
            bool canPick = canWrite && _context?.Assets != null;
            Interaction interaction = Ui.Interact(id, field);

            // Something dragged from the assets lights the slot up, and lands in it when let go.
            bool dropping = canPick && DragDrop.Dragging && DragDrop.Payload is AssetEntry entry && entry.Kind == kind && field.Contains(Ui.Mouse);
            if (dropping && DragDrop.Dropped && DragDrop.Payload is AssetEntry dropped) set(dropped.Path);

            Color background = Ui.Smooth(id, interaction.Hovered && canPick ? Theme.Control : Theme.Field, 24f);
            Ui.Rect(field, background, Theme.ControlRadius);
            Ui.Outline(field, dropping ? Theme.Accent : Popup.IsOpen(id) ? Theme.Accent.WithAlpha(0.7f) : Theme.Border, Theme.ControlRadius, dropping ? 1.5f : 1f);

            var swatch = new Rect(field.X + 3f, field.Y + 3f, field.Height - 6f, field.Height - 6f);
            string? source = SourceOf(value);
            if (value is Texture texture)
            {
                Texture shown = source != null && Thumbnails.Get(source) is { } thumbnail ? thumbnail : texture;
                if (texture is not RenderTexture) Vertex.UI.Canvas.DrawImage(shown, swatch.Position, swatch.Size, Vector2.Zero, Vector2.One, Ui.Fade(Color.White), 3f);
            }
            else if (value != null)
            {
                Panels.AssetsPanel.DrawPicture(new AssetEntry(source ?? "", kind), swatch.Grow(2f), small: true);
            }
            else
            {
                Ui.Rect(swatch, Theme.Background, 3f);
            }

            string name = value == null ? noneLabel ?? "None"
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

            Popup.Show(id, new Vector2(MathF.Max(field.Width, 300f), 330f), inner => AssetPicker(id, inner, kind, source, value == null, set, noneLabel));
        }

        private string _pickerSearch = "";

        private void AssetPicker(int id, Rect rect, AssetKind kind, string? current, bool currentIsNone, Action<string?> set, string? noneLabel)
        {
            string what = kind switch
            {
                AssetKind.Texture => "textures",
                AssetKind.Material => "materials",
                AssetKind.Prefab => "prefabs",
                AssetKind.Font => "fonts",
                AssetKind.Sound => "sounds",
                _ => "files"
            };
            TextInput.Draw(Ui.Id(id, "search"), rect.TakeTop(28f), ref _pickerSearch, "Search " + what, live: true, icon: Icons.Search);

            string search = _pickerSearch.Trim();
            var choices = new List<AssetEntry?> { null };
            choices.AddRange(_context!.Assets!.All(kind).Where(entry => search.Length == 0 || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));

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

            // A line down the left groups the rows under their heading. A material says first which file it is, if any.
            float end = value is Material material && member.CanWrite ? MaterialFile(id, owner, member, material, x + 14f, y, width - 14f) : y;
            end = Draw(value, x + 14f, end, width - 14f, depth + 1);
            Ui.Rect(new Rect(x + 4f, start, 1f, MathF.Max(end - start - RowGap, 0f)), Theme.Border);
            Ui.PopClip();

            Ui.Remember(heightKey, end - start);
            return y + shownHeight;
        }

        /// <summary>
        /// Which .material file a material is, if it's one: a slot to swap it for another (or none, for one of its own),
        /// and the way between the two: saving one's own as a file, or making a shared one this object's own.
        /// </summary>
        private float MaterialFile(int id, object owner, InspectedMember member, Material material, float x, float y, float width)
        {
            Row(Ui.Id(id, "file"), x, y, width, "File", "A .material file shared with everything else that uses it, or this object's own material", out Rect field);
            AssetField(Ui.Id(id, "fileSlot"), field, AssetKind.Material, material.Source != null ? material : null, canWrite: true, path =>
            {
                try
                {
                    // Back to one of its own: a copy of the file's, to carry on from.
                    Set(owner, member, path != null ? Assets.LoadMaterial(path) : material.Copy());
                }
                catch (Exception exception)
                {
                    _notify($"Couldn't load {Path.GetFileName(path)}: {exception.Message}");
                }
            }, noneLabel: "This object's own");
            y += Theme.RowHeight + RowGap;

            float labelWidth = LabelWidth(width);
            var note = new Rect(x + labelWidth, y, width - labelWidth, 22f);
            if (material.Source != null)
            {
                if (Widgets.Button(Ui.Id(id, "unique"), note.TakeLeft(MathF.Min(note.Width, 150f)), "Make Unique", ButtonStyle.Subtle,
                        "Give this object a copy of its own, so changing it leaves the others alone")) Set(owner, member, material.Copy());
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
                    Set(owner, member, Assets.LoadMaterial(path));
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

        /// <summary>Sets the value with undo, then lets <paramref name="target"/>'s owner know if it needs to.</summary>
        private void Set(object target, InspectedMember member, object? value)
        {
            try
            {
                SetValue.Apply(_history, $"Change {member.Name}", target, member.Member, value);
            }
            catch (Exception exception)
            {
                _notify($"Couldn't set {member.Name}: {(exception.InnerException ?? exception).Message}");
            }
        }
    }
}
