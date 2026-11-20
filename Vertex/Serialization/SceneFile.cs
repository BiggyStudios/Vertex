using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Serialization
{
    /// <summary>
    /// Saves scenes (or a few objects) as JSON and builds them again from it: every object's name, transform and
    /// components, and each component's public properties and fields. What can be saved:
    /// <list type="bullet">
    /// <item>numbers, text, true/false, enums, vectors, rotations and colours;</item>
    /// <item>meshes and textures: built-in shapes by name, ones loaded from files by their path, and ones made in code
    /// whole, inside the file;</item>
    /// <item>other objects and components in what's being saved, which come back joined up the same way;</item>
    /// <item>settings objects of their own (a material), saved inside.</item>
    /// </list>
    /// Anything else (lists, delegates, references to things outside what's saved) is left out. Values that are only for
    /// code can be kept out with <see cref="NonSerializedAttribute"/> on a field or <see cref="DontSaveAttribute"/>.
    /// <para>
    /// Files under one of the asset folders given are saved by their path inside it, so the scene still finds them when
    /// the game is built somewhere else; by default those are <see cref="Application.AssetFolders"/>.
    /// </para>
    /// </summary>
    public static class SceneFile
    {
        public const int FormatVersion = 1;

        // ---------------------------------------------------------------- Saving

        /// <summary>Saves the whole scene, with its lighting, to a file.</summary>
        public static void Save(Scene scene, string path, params string[] assetFolders)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory != null) Directory.CreateDirectory(directory);
            File.WriteAllText(path, Save(scene, assetFolders));
        }

        /// <summary>The whole scene, with its lighting, as JSON.</summary>
        public static string Save(Scene scene, params string[] assetFolders)
        {
            scene.ApplyChanges();
            return Write(scene.GameObjects, scene.Lighting, assetFolders);
        }

        /// <summary>Just these objects and everything under them, as JSON: for copying and duplicating.</summary>
        public static string SaveObjects(IEnumerable<GameObject> roots, params string[] assetFolders)
        {
            var objects = new List<GameObject>();
            foreach (GameObject root in roots) AddSubtree(root, objects);
            return Write(objects, null, assetFolders);
        }

        private static void AddSubtree(GameObject gameObject, List<GameObject> into)
        {
            if (into.Contains(gameObject)) return;
            into.Add(gameObject);
            foreach (Transform child in gameObject.Transform.Children) AddSubtree(child.GameObject, into);
        }

        /// <summary>
        /// A copy of the scene kept in memory, to put back later (as an editor does when play mode stops). Meshes and
        /// textures aren't copied: the scene put back shares the same ones.
        /// </summary>
        public sealed class Snapshot
        {
            internal string Json { get; init; } = "";
            internal Dictionary<string, object> Resources { get; init; } = new();

            /// <summary>Builds what was saved into <paramref name="scene"/> again.</summary>
            public List<GameObject> Restore(Scene scene)
            {
                using JsonDocument document = JsonDocument.Parse(Json);
                return new Reader(document.RootElement, scene, Application.AssetFolders, Resources).Read();
            }
        }

        /// <summary>Takes a <see cref="Snapshot"/> of the whole scene, or of just <paramref name="roots"/> and what's under them.</summary>
        public static Snapshot TakeSnapshot(Scene scene, IEnumerable<GameObject>? roots = null)
        {
            scene.ApplyChanges();
            var objects = new List<GameObject>();
            if (roots == null) objects.AddRange(scene.GameObjects);
            else foreach (GameObject root in roots) AddSubtree(root, objects);

            var resources = new Dictionary<string, object>();
            string json = Write(objects, roots == null ? scene.Lighting : null, [], resources);
            return new Snapshot { Json = json, Resources = resources };
        }

        /// <summary>
        /// <paramref name="root"/> and everything under it as a prefab's JSON. Its own link to a prefab (it's an instance of
        /// the one being saved) is left out; links of objects under it to other prefabs are kept.
        /// </summary>
        internal static string SavePrefab(GameObject root, params string[] assetFolders)
        {
            var objects = new List<GameObject>();
            AddSubtree(root, objects);
            return Write(objects, null, assetFolders, skipPrefabLinkOf: root);
        }

        /// <summary>A material as the JSON of a .material file.</summary>
        public static string SaveMaterial(Material material, params string[] assetFolders)
        {
            var writer = new Writer([], assetFolders.Length > 0 ? assetFolders : Application.AssetFolders, null);
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) writer.WriteSettingsFile(json, "material", material);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Reads a .material file's JSON into <paramref name="material"/> (an existing one, so whatever uses it sees the change).</summary>
        public static void LoadMaterial(string json, Material material, params string[] assetFolders)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("material", out JsonElement values)) throw new InvalidDataException("Not a material file.");
            var reader = new Reader(document.RootElement, Scene.Active, assetFolders.Length > 0 ? assetFolders : Application.AssetFolders);
            reader.ReadInto(values, material);
        }

        private static string Write(IReadOnlyList<GameObject> objects, Lighting? lighting, string[] assetFolders, Dictionary<string, object>? keepResources = null,
            GameObject? skipPrefabLinkOf = null)
        {
            var writer = new Writer(objects, assetFolders.Length > 0 ? assetFolders : Application.AssetFolders, keepResources) { SkipPrefabLinkOf = skipPrefabLinkOf };
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.Write(json, lighting);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private sealed class Writer(IReadOnlyList<GameObject> objects, string[] assetFolders, Dictionary<string, object>? keepResources)
        {
            private readonly Dictionary<GameObject, int> _ids = objects.Select((o, i) => (o, i)).ToDictionary(p => p.o, p => p.i + 1);
            private readonly Dictionary<object, string> _resourceIds = new(ReferenceEqualityComparer.Instance);
            private readonly List<(string Id, object Resource)> _resources = new();

            /// <summary>An object whose own prefab link isn't written: the root of a prefab being saved, which is that prefab.</summary>
            public GameObject? SkipPrefabLinkOf { get; init; }

            public void Write(Utf8JsonWriter json, Lighting? lighting)
            {
                // The objects first, as resources (meshes and textures made in code) are found on the way.
                using var objectStream = new MemoryStream();
                using (var objectJson = new Utf8JsonWriter(objectStream, new JsonWriterOptions { Indented = true }))
                {
                    objectJson.WriteStartArray();
                    foreach (GameObject gameObject in objects) WriteObject(objectJson, gameObject);
                    objectJson.WriteEndArray();
                }

                json.WriteStartObject();
                json.WriteNumber("version", FormatVersion);

                if (lighting != null)
                {
                    json.WritePropertyName("lighting");
                    WriteValues(json, lighting);
                }

                json.WritePropertyName("objects");
                using (JsonDocument written = JsonDocument.Parse(objectStream.ToArray())) written.WriteTo(json);

                json.WriteStartObject("resources");
                foreach ((string id, object resource) in _resources)
                {
                    // Kept in memory instead (a snapshot): only noted, not written out.
                    if (keepResources != null)
                    {
                        keepResources[id] = resource;
                        continue;
                    }

                    json.WritePropertyName(id);
                    WriteResource(json, resource);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            private void WriteObject(Utf8JsonWriter json, GameObject gameObject)
            {
                Transform transform = gameObject.Transform;
                json.WriteStartObject();
                json.WriteNumber("id", _ids[gameObject]);
                json.WriteString("name", gameObject.Name);
                if (!gameObject.Active) json.WriteBoolean("active", false);
                if (gameObject.GetType() != typeof(GameObject)) json.WriteString("type", TypeName(gameObject.GetType()));
                if (transform.Parent != null && _ids.TryGetValue(transform.Parent.GameObject, out int parent)) json.WriteNumber("parent", parent);
                if (gameObject.Prefab is { } prefab && gameObject != SkipPrefabLinkOf) json.WriteString("prefab", "file:" + AssetPath(prefab.Source));

                WriteVector(json, "position", transform.Position);
                WriteFloats(json, "rotation", transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
                WriteVector(json, "scale", transform.Scale);

                json.WriteStartArray("components");
                foreach (Component component in gameObject.Components)
                {
                    json.WriteStartObject();
                    json.WriteString("type", TypeName(component.GetType()));
                    if (!component.Enabled) json.WriteBoolean("enabled", false);
                    json.WritePropertyName("values");
                    WriteValues(json, component);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            private void WriteValues(Utf8JsonWriter json, object target, int depth = 0)
            {
                json.WriteStartObject();
                foreach (SavedMember member in SavedMember.Of(target.GetType()))
                {
                    object? value;
                    try
                    {
                        value = member.Get(target);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    // Written to a buffer first, so a value that can't be saved leaves nothing behind.
                    using var buffer = new MemoryStream();
                    using (var valueJson = new Utf8JsonWriter(buffer))
                    {
                        if (!WriteValue(valueJson, member.Type, value, depth)) continue;
                    }

                    json.WritePropertyName(member.Name);
                    using JsonDocument written = JsonDocument.Parse(buffer.ToArray());
                    written.WriteTo(json);
                }

                json.WriteEndObject();
            }

            private bool WriteValue(Utf8JsonWriter json, Type type, object? value, int depth)
            {
                switch (value)
                {
                    case null:
                        json.WriteNullValue();
                        return true;
                    case bool b: json.WriteBooleanValue(b); return true;
                    case int i: json.WriteNumberValue(i); return true;
                    case float f: json.WriteNumberValue(f); return true;
                    case double d: json.WriteNumberValue(d); return true;
                    case string s: json.WriteStringValue(s); return true;
                    case Enum e: json.WriteStringValue(e.ToString()); return true;
                    case Vector2 v: WriteFloats(json, null, v.X, v.Y); return true;
                    case Vector3 v: WriteFloats(json, null, v.X, v.Y, v.Z); return true;
                    case Vector4 v: WriteFloats(json, null, v.X, v.Y, v.Z, v.W); return true;
                    case Quaternion q: WriteFloats(json, null, q.X, q.Y, q.Z, q.W); return true;
                    case Color c: WriteFloats(json, null, c.R, c.G, c.B, c.A); return true;
                    case FloatRange r: WriteFloats(json, null, r.Min, r.Max); return true;

                    case RenderTexture:
                        return false;

                    case Mesh mesh:
                        json.WriteStartObject();
                        json.WriteString("mesh", MeshReference(mesh));
                        json.WriteEndObject();
                        return true;

                    case Texture texture:
                        json.WriteStartObject();
                        json.WriteString("texture", TextureReference(texture));
                        json.WriteEndObject();
                        return true;

                    // Files of their own, referred to by where they are.
                    case Material { Source: { } materialFile }:
                        WriteReference(json, "material", materialFile);
                        return true;

                    case Prefab prefab:
                        WriteReference(json, "prefab", prefab.Source);
                        return true;

                    case Font font:
                        if (font.Source == null) return false;
                        WriteReference(json, "font", font.Source);
                        return true;

                    case Graphs.Graph { Source: { } graphFile }:
                        WriteReference(json, "graph", graphFile);
                        return true;

                    case Shader { Source: { } shaderFile }:
                        WriteReference(json, "shader", shaderFile);
                        return true;

                    case Audio.AudioClip clip:
                        if (clip.Source == null) return false;
                        WriteReference(json, "sound", clip.Source);
                        return true;

                    // A model's own animation is kept as the model's path, a #, and its name.
                    case Animation.AnimationClip animation:
                        if (animation.Source is not { } animationFile) return false;
                        int hash = animation.IsImported ? animationFile.LastIndexOf('#') : -1;
                        json.WriteStartObject();
                        json.WriteString("animation", "file:" + (hash > 0 ? AssetPath(animationFile[..hash]) + animationFile[hash..] : AssetPath(animationFile)));
                        json.WriteEndObject();
                        return true;

                    case GameObject gameObject:
                        if (!_ids.TryGetValue(gameObject, out int id)) return false;
                        json.WriteStartObject();
                        json.WriteNumber("object", id);
                        json.WriteEndObject();
                        return true;

                    case Component component:
                        if (!_ids.TryGetValue(component.GameObject, out int owner)) return false;
                        json.WriteStartObject();
                        json.WriteNumber("object", owner);
                        json.WriteNumber("component", component.GameObject.Components.ToList().IndexOf(component));
                        json.WriteEndObject();
                        return true;
                }

                // A list (or array) of things that can be saved: each in turn, with a gap for one that can't (an object
                // that isn't being saved with it), so the rest keep their places.
                if (value is System.Collections.IList list && ItemType(type) is { } itemType)
                {
                    if (!CanSave(itemType) || depth > 4) return false;
                    json.WriteStartArray();
                    foreach (object? item in list)
                    {
                        if (!WriteValue(json, itemType, item, depth + 1)) json.WriteNullValue();
                    }

                    json.WriteEndArray();
                    return true;
                }

                if (!IsSettingsObject(value.GetType()) || depth > 4) return false;

                json.WriteStartObject();
                if (value.GetType() != type) json.WriteString("type", TypeName(value.GetType()));
                json.WritePropertyName("values");
                WriteValues(json, value, depth + 1);
                json.WriteEndObject();
                return true;
            }

            private void WriteReference(Utf8JsonWriter json, string kind, string file)
            {
                json.WriteStartObject();
                json.WriteString(kind, "file:" + AssetPath(file));
                json.WriteEndObject();
            }

            /// <summary>A file of settings of its own (a material): its values, and any resources made in code that it uses.</summary>
            public void WriteSettingsFile(Utf8JsonWriter json, string name, object settings)
            {
                using var valueStream = new MemoryStream();
                using (var valueJson = new Utf8JsonWriter(valueStream, new JsonWriterOptions { Indented = true })) WriteValues(valueJson, settings);

                json.WriteStartObject();
                json.WriteNumber("version", FormatVersion);
                json.WritePropertyName(name);
                using (JsonDocument written = JsonDocument.Parse(valueStream.ToArray())) written.WriteTo(json);
                json.WriteStartObject("resources");
                foreach ((string id, object resource) in _resources)
                {
                    json.WritePropertyName(id);
                    WriteResource(json, resource);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            private string MeshReference(Mesh mesh)
            {
                if (mesh.Source is { } source)
                {
                    if (source.StartsWith("builtin:", StringComparison.Ordinal)) return source;

                    int hash = source.LastIndexOf('#');
                    if (hash > 0) return "file:" + AssetPath(source[..hash]) + source[hash..];
                }

                return "resource:" + Resource(mesh, "mesh");
            }

            private string TextureReference(Texture texture) =>
                texture.Source is { } source ? "file:" + AssetPath(source) : "resource:" + Resource(texture, "texture");

            private string AssetPath(string path)
            {
                string full = Path.GetFullPath(path);
                foreach (string folder in assetFolders)
                {
                    string root = Path.GetFullPath(folder);
                    string relative = Path.GetRelativePath(root, full);
                    if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative.Replace('\\', '/');
                }

                return full;
            }

            private string Resource(object resource, string kind)
            {
                if (_resourceIds.TryGetValue(resource, out string? id)) return id;

                id = $"{kind}{_resources.Count + 1}";
                _resourceIds[resource] = id;
                _resources.Add((id, resource));
                return id;
            }

            /// <summary>
            /// A mesh's corners as they're saved: position, normal and texture coordinates, eight floats each. Lightmap
            /// coordinates aren't: a mesh drawn with them is made from the scene's baked lighting when it loads.
            /// </summary>
            internal static float[] SavedVertices(MeshVertex[] vertices)
            {
                var floats = new float[vertices.Length * 8];
                for (int i = 0; i < vertices.Length; i++)
                {
                    MeshVertex v = vertices[i];
                    v.Position.CopyTo(floats, i * 8);
                    v.Normal.CopyTo(floats, i * 8 + 3);
                    v.UV.CopyTo(floats, i * 8 + 6);
                }

                return floats;
            }

            private static void WriteResource(Utf8JsonWriter json, object resource)
            {
                json.WriteStartObject();
                switch (resource)
                {
                    case Mesh mesh:
                        json.WriteString("kind", "mesh");
                        json.WriteBase64String("vertices", MemoryMarshal.AsBytes(SavedVertices(mesh.Vertices).AsSpan()));
                        json.WriteBase64String("indices", MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
                        break;
                    case Texture texture:
                        json.WriteString("kind", "texture");
                        json.WriteNumber("width", texture.Width);
                        json.WriteNumber("height", texture.Height);
                        json.WriteBase64String("pixels", texture.Pixels);
                        break;
                }

                json.WriteEndObject();
            }
        }

        private static void WriteVector(Utf8JsonWriter json, string name, Vector3 v) => WriteFloats(json, name, v.X, v.Y, v.Z);

        private static void WriteFloats(Utf8JsonWriter json, string? name, params ReadOnlySpan<float> values)
        {
            if (name != null) json.WriteStartArray(name);
            else json.WriteStartArray();
            foreach (float value in values) json.WriteNumberValue(value);
            json.WriteEndArray();
        }

        // ---------------------------------------------------------------- Loading

        /// <summary>Adds the objects saved in a file to <paramref name="scene"/>, and sets its lighting as saved.</summary>
        public static List<GameObject> Load(string path, Scene scene, params string[] assetFolders) =>
            LoadJson(File.ReadAllText(path), scene, assetFolders);

        /// <summary>
        /// Adds the objects saved in <paramref name="json"/> to <paramref name="scene"/> (and its lighting, if saved),
        /// and returns them in the order saved. They're added without being started: the scene starts them when it next
        /// updates, as with any new object.
        /// </summary>
        public static List<GameObject> LoadJson(string json, Scene scene, params string[] assetFolders)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            var reader = new Reader(document.RootElement, scene, assetFolders.Length > 0 ? assetFolders : Application.AssetFolders);
            return reader.Read();
        }

        private sealed class Reader(JsonElement root, Scene scene, string[] assetFolders, Dictionary<string, object>? keptResources = null)
        {
            private readonly Dictionary<int, GameObject> _objects = new();
            private readonly Dictionary<string, object?> _resources = keptResources?.ToDictionary(p => p.Key, p => (object?)p.Value) ?? new();

            /// <summary>Things that couldn't be loaded, such as a script whose code is gone.</summary>
            public List<string> Problems { get; } = new();

            public List<GameObject> Read()
            {
                if (root.TryGetProperty("lighting", out JsonElement lighting)) ReadValues(lighting, scene.Lighting);

                var created = new List<GameObject>();
                var componentValues = new List<(Component Component, JsonElement Values)>();
                if (!root.TryGetProperty("objects", out JsonElement objects)) return created;

                // Every object first, so parents and references can be found whatever order they come in.
                foreach (JsonElement element in objects.EnumerateArray())
                {
                    string name = element.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "GameObject" : "GameObject";
                    GameObject gameObject = CreateObject(element, name);
                    if (element.TryGetProperty("active", out JsonElement active)) gameObject.Active = active.GetBoolean();
                    if (element.TryGetProperty("prefab", out JsonElement prefab) && prefab.GetString() is { } prefabFile)
                    {
                        gameObject.Prefab = LoadFile(prefabFile, "Prefab", Assets.LoadPrefab);
                    }
                    _objects[element.GetProperty("id").GetInt32()] = gameObject;
                    created.Add(gameObject);
                }

                foreach (JsonElement element in objects.EnumerateArray())
                {
                    GameObject gameObject = _objects[element.GetProperty("id").GetInt32()];
                    Transform transform = gameObject.Transform;
                    if (element.TryGetProperty("parent", out JsonElement parent) && _objects.TryGetValue(parent.GetInt32(), out GameObject? parentObject))
                    {
                        transform.Parent = parentObject.Transform;
                    }

                    if (element.TryGetProperty("position", out JsonElement position)) transform.Position = ReadVector3(position);
                    if (element.TryGetProperty("rotation", out JsonElement rotation)) transform.Rotation = ReadQuaternion(rotation);
                    if (element.TryGetProperty("scale", out JsonElement scale)) transform.Scale = ReadVector3(scale);

                    if (!element.TryGetProperty("components", out JsonElement components)) continue;
                    foreach (JsonElement saved in components.EnumerateArray())
                    {
                        string typeName = saved.GetProperty("type").GetString()!;
                        Type? type = FindType(typeName);
                        if (type == null || !type.IsSubclassOf(typeof(Component)))
                        {
                            Problems.Add($"{gameObject.Name}: no component type {typeName}");
                            continue;
                        }

                        Component component;
                        try
                        {
                            component = (Component)Activator.CreateInstance(type)!;
                        }
                        catch (Exception exception)
                        {
                            Problems.Add($"{gameObject.Name}: couldn't make {type.Name}: {exception.Message}");
                            continue;
                        }

                        gameObject.AttachComponent(component, gameObject.Components.Count);
                        if (saved.TryGetProperty("enabled", out JsonElement enabled)) component.Enabled = enabled.GetBoolean();
                        if (saved.TryGetProperty("values", out JsonElement values)) componentValues.Add((component, values));
                    }
                }

                // Values last, once every component they might refer to exists.
                foreach ((Component component, JsonElement values) in componentValues) ReadValues(values, component);
                foreach (string problem in Problems) Log.Warning(problem);
                return created;
            }

            private GameObject CreateObject(JsonElement element, string name)
            {
                if (element.TryGetProperty("type", out JsonElement typeElement) && FindType(typeElement.GetString()!) is { } type &&
                    type.IsSubclassOf(typeof(GameObject)) && type.GetConstructor([typeof(string), typeof(Scene)]) is { } constructor)
                {
                    try
                    {
                        return (GameObject)constructor.Invoke([name, scene]);
                    }
                    catch (Exception exception)
                    {
                        Problems.Add($"{name}: couldn't make {type.Name}: {exception.Message}");
                    }
                }

                return new GameObject(name, scene);
            }

            /// <summary>Reads saved values into an object made elsewhere (a material being loaded from its file).</summary>
            public void ReadInto(JsonElement values, object target) => ReadValues(values, target);

            private void ReadValues(JsonElement values, object target)
            {
                if (values.ValueKind != JsonValueKind.Object) return;

                foreach (SavedMember member in SavedMember.Of(target.GetType()))
                {
                    if (!values.TryGetProperty(member.Name, out JsonElement element)) continue;
                    try
                    {
                        if (TryRead(element, member.Type, member.Get(target), out object? value)) member.Set(target, value);
                    }
                    catch (Exception exception)
                    {
                        Problems.Add($"{target.GetType().Name}.{member.Name}: {exception.Message}");
                    }
                }
            }

            private bool TryRead(JsonElement element, Type type, object? current, out object? value)
            {
                value = null;
                if (element.ValueKind == JsonValueKind.Null) return !type.IsValueType;

                if (type == typeof(bool)) value = element.GetBoolean();
                else if (type == typeof(int)) value = element.GetInt32();
                else if (type == typeof(float)) value = element.GetSingle();
                else if (type == typeof(double)) value = element.GetDouble();
                else if (type == typeof(string)) value = element.GetString();
                else if (type.IsEnum) value = Enum.Parse(type, element.GetString()!);
                else if (type == typeof(Vector2)) value = new Vector2(element[0].GetSingle(), element[1].GetSingle());
                else if (type == typeof(FloatRange)) value = element.ValueKind == JsonValueKind.Number ? new FloatRange(element.GetSingle(), element.GetSingle()) : new FloatRange(element[0].GetSingle(), element[1].GetSingle());
                else if (type == typeof(Vector3)) value = ReadVector3(element);
                else if (type == typeof(Vector4)) value = new Vector4(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle(), element[3].GetSingle());
                else if (type == typeof(Quaternion)) value = ReadQuaternion(element);
                else if (type == typeof(Color)) value = new Color(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle(), element[3].GetSingle());
                else if (ItemType(type) is { } itemType)
                {
                    if (element.ValueKind != JsonValueKind.Array) return false;

                    // A new list each time: one the constructor made may be shared, or hold starting values.
                    var items = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
                    foreach (JsonElement saved in element.EnumerateArray())
                    {
                        items.Add(TryRead(saved, itemType, null, out object? item) ? item : itemType.IsValueType ? Activator.CreateInstance(itemType) : null);
                    }

                    if (type.IsArray)
                    {
                        var array = Array.CreateInstance(itemType, items.Count);
                        items.CopyTo(array, 0);
                        value = array;
                    }
                    else
                    {
                        value = items;
                    }
                }
                else if (element.ValueKind != JsonValueKind.Object) return false;
                else if (element.TryGetProperty("mesh", out JsonElement mesh)) value = LoadMesh(mesh.GetString()!);
                else if (element.TryGetProperty("texture", out JsonElement texture)) value = LoadTexture(texture.GetString()!);
                else if (element.TryGetProperty("material", out JsonElement material)) value = LoadFile(material.GetString()!, "Material", Assets.LoadMaterial);
                else if (element.TryGetProperty("prefab", out JsonElement prefab)) value = LoadFile(prefab.GetString()!, "Prefab", Assets.LoadPrefab);
                else if (element.TryGetProperty("font", out JsonElement font)) value = LoadFile(font.GetString()!, "Font", Assets.LoadFont);
                else if (element.TryGetProperty("sound", out JsonElement sound)) value = LoadFile(sound.GetString()!, "Sound", Assets.LoadSound);
                else if (element.TryGetProperty("graph", out JsonElement graph)) value = LoadFile(graph.GetString()!, "Graph", Assets.LoadGraph);
                else if (element.TryGetProperty("shader", out JsonElement shader)) value = LoadFile(shader.GetString()!, "Shader", Assets.LoadShader);
                else if (element.TryGetProperty("animation", out JsonElement animation)) value = LoadAnimation(animation.GetString()!);
                else if (element.TryGetProperty("component", out JsonElement index))
                {
                    if (!_objects.TryGetValue(element.GetProperty("object").GetInt32(), out GameObject? owner)) return false;
                    int i = index.GetInt32();
                    value = i >= 0 && i < owner.Components.Count ? owner.Components[i] : null;
                }
                else if (element.TryGetProperty("object", out JsonElement id))
                {
                    if (!_objects.TryGetValue(id.GetInt32(), out GameObject? gameObject)) return false;
                    value = gameObject;
                }
                else if (element.TryGetProperty("values", out JsonElement values))
                {
                    Type objectType = element.TryGetProperty("type", out JsonElement saved) && FindType(saved.GetString()!) is { } found && type.IsAssignableFrom(found)
                        ? found
                        : type;

                    // Filled in where it is, if there's already one (its constructor may have set it up); otherwise made new.
                    object target = current != null && current.GetType() == objectType ? current : Activator.CreateInstance(objectType)!;
                    ReadValues(values, target);
                    value = target;
                }
                else return false;

                return value == null || type.IsInstanceOfType(value);
            }

            private Mesh? LoadMesh(string reference)
            {
                if (reference.StartsWith("builtin:", StringComparison.Ordinal)) return Primitives.Builtin(reference["builtin:".Length..]);
                if (reference.StartsWith("resource:", StringComparison.Ordinal)) return Resource(reference["resource:".Length..]) as Mesh;

                string file = reference["file:".Length..];
                int hash = file.LastIndexOf('#');
                string? path = FindAsset(file[..hash]);
                if (path == null)
                {
                    Problems.Add($"Model not found: {file[..hash]}");
                    return null;
                }

                return Model.LoadMesh(path, int.Parse(file[(hash + 1)..]));
            }

            private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);

            private Texture? LoadTexture(string reference)
            {
                if (reference.StartsWith("resource:", StringComparison.Ordinal)) return Resource(reference["resource:".Length..]) as Texture;

                string file = reference["file:".Length..];
                if (_textures.TryGetValue(file, out Texture? loaded)) return loaded;

                string? path = FindAsset(file);
                if (path == null) Problems.Add($"Texture not found: {file}");
                Texture? texture = path != null ? Assets.LoadTexture(path) : null;
                _textures[file] = texture;
                return texture;
            }

            /// <summary>A file a saved reference points at ("file:Materials/Brick.material"), loaded through <paramref name="load"/>; null if it's gone.</summary>
            private T? LoadFile<T>(string reference, string what, Func<string, T> load) where T : class
            {
                string file = reference.StartsWith("file:", StringComparison.Ordinal) ? reference["file:".Length..] : reference;
                string? path = FindAsset(file);
                if (path == null)
                {
                    Problems.Add($"{what} not found: {file}");
                    return null;
                }

                try
                {
                    return load(path);
                }
                catch (Exception exception)
                {
                    Problems.Add($"{what} {file}: {exception.Message}");
                    return null;
                }
            }

            /// <summary>An animation a saved reference points at: a .anim file, or a model's ("file:Models/Robot.glb#Walk").</summary>
            private Animation.AnimationClip? LoadAnimation(string reference)
            {
                string file = reference.StartsWith("file:", StringComparison.Ordinal) ? reference["file:".Length..] : reference;
                int hash = file.LastIndexOf('#');
                string? path = FindAsset(hash > 0 ? file[..hash] : file);
                if (path == null)
                {
                    Problems.Add($"Animation not found: {file}");
                    return null;
                }

                try
                {
                    return Assets.LoadAnimation(hash > 0 ? path + file[hash..] : path);
                }
                catch (Exception exception)
                {
                    Problems.Add($"Animation {file}: {exception.Message}");
                    return null;
                }
            }

            private string? FindAsset(string path)
            {
                if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
                foreach (string folder in assetFolders)
                {
                    string full = Path.Combine(folder, path);
                    if (File.Exists(full)) return full;
                }

                return null;
            }

            /// <summary>A mesh's corners as they're saved (see <see cref="Writer.SavedVertices"/>).</summary>
            private static MeshVertex[] LoadedVertices(ReadOnlySpan<float> floats)
            {
                var vertices = new MeshVertex[floats.Length / 8];
                for (int i = 0; i < vertices.Length; i++)
                {
                    ReadOnlySpan<float> v = floats.Slice(i * 8, 8);
                    vertices[i] = new MeshVertex(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]), new Vector2(v[6], v[7]));
                }

                return vertices;
            }

            private object? Resource(string id)
            {
                if (_resources.TryGetValue(id, out object? made)) return made;
                if (!root.TryGetProperty("resources", out JsonElement resources) || !resources.TryGetProperty(id, out JsonElement saved)) return null;

                object? resource = saved.GetProperty("kind").GetString() switch
                {
                    "mesh" => new Mesh(LoadedVertices(MemoryMarshal.Cast<byte, float>(saved.GetProperty("vertices").GetBytesFromBase64())),
                        MemoryMarshal.Cast<byte, uint>(saved.GetProperty("indices").GetBytesFromBase64()).ToArray()),
                    "texture" => new Texture(saved.GetProperty("width").GetInt32(), saved.GetProperty("height").GetInt32(), saved.GetProperty("pixels").GetBytesFromBase64()),
                    _ => null
                };

                _resources[id] = resource;
                return resource;
            }
        }

        private static Vector3 ReadVector3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        private static Quaternion ReadQuaternion(JsonElement e) => Quaternion.Normalize(new Quaternion(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle()));

        // ---------------------------------------------------------------- Types and members

        /// <summary>A type's name as saved: its full name and the assembly it's in, so a game's scripts are found again.</summary>
        public static string TypeName(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

        private static readonly Dictionary<string, Type?> _types = new();

        /// <summary>Finds a type by its saved name in any loaded code, the game's included.</summary>
        public static Type? FindType(string name)
        {
            // A game's type from before its code was rebuilt is looked up again, in the new code.
            if (_types.TryGetValue(name, out Type? cached) && cached != null && !LoadedCode.IsGone(cached.Assembly)) return cached;

            string[] parts = name.Split(',', 2, StringSplitOptions.TrimEntries);
            Type? found = null;
            foreach (Assembly assembly in LoadedCode.Assemblies)
            {
                if (parts.Length > 1 && assembly.GetName().Name != parts[1]) continue;
                found = assembly.GetType(parts[0]);
                if (found != null) break;
            }

            // Code that's there but not loaded yet (a game whose scripts nothing has touched before its first scene):
            // .NET loads it by name.
            if (found == null && parts.Length > 1)
            {
                try
                {
                    found = Type.GetType(name, throwOnError: false);
                }
                catch (Exception exception) when (exception is IOException or BadImageFormatException)
                {
                    // Not there to load: missing, as it would be anyway.
                }
            }

            // Not cached when missing: the game's code may be loaded later.
            if (found != null) _types[name] = found;
            return found;
        }

        /// <summary>What a <c>List&lt;T&gt;</c> or array holds, or null if <paramref name="type"/> isn't one.</summary>
        internal static Type? ItemType(Type type)
        {
            if (type.IsArray) return type.GetArrayRank() == 1 ? type.GetElementType() : null;
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;
        }

        /// <summary>Whether a value of <paramref name="type"/> can be saved in a scene: what lists are checked against before they're written.</summary>
        internal static bool CanSave(Type type) =>
            type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(double) || type.IsEnum || type == typeof(string) || type == typeof(Vector2) || type == typeof(Vector3) ||
            type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(FloatRange) ||
            typeof(Mesh).IsAssignableFrom(type) || typeof(Texture).IsAssignableFrom(type) || type == typeof(Material) || type == typeof(Prefab) ||
            type == typeof(Font) || type == typeof(Graphs.Graph) || type == typeof(Audio.AudioClip) || type == typeof(Shader) || type == typeof(Animation.AnimationClip) ||
            typeof(GameObject).IsAssignableFrom(type) || typeof(Component).IsAssignableFrom(type) || IsSettingsObject(type);

        /// <summary>A class of its own whose values can be saved inside whatever has it (a material).</summary>
        internal static bool IsSettingsObject(Type type) =>
            type.IsClass && !type.IsAbstract && type != typeof(string) && !typeof(Delegate).IsAssignableFrom(type) &&
            !typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type.Namespace?.StartsWith("System") != true &&
            !typeof(Texture).IsAssignableFrom(type) && !typeof(Mesh).IsAssignableFrom(type) && type != typeof(Animation.AnimationClip) &&
            !typeof(GameObject).IsAssignableFrom(type) && !typeof(Component).IsAssignableFrom(type) &&
            type.GetConstructor(Type.EmptyTypes) != null;

        private sealed class SavedMember
        {
            public required string Name { get; init; }
            public required Type Type { get; init; }
            public required Func<object, object?> Get { get; init; }
            public required Action<object, object?> Set { get; init; }

            private static readonly ConditionalWeakTable<Type, List<SavedMember>> _cache = new();

            public static List<SavedMember> Of(Type type) => _cache.GetValue(type, Find);

            private static List<SavedMember> Find(Type type)
            {
                var members = new List<SavedMember>();
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true }) continue;
                    if (property.DeclaringType == typeof(Component) || Skip(property)) continue;
                    members.Add(new SavedMember { Name = property.Name, Type = property.PropertyType, Get = property.GetValue, Set = property.SetValue });
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (field.IsInitOnly || field.IsDefined(typeof(NonSerializedAttribute), inherit: false) || Skip(field)) continue;
                    members.Add(new SavedMember { Name = field.Name, Type = field.FieldType, Get = field.GetValue, Set = field.SetValue });
                }

                return members;
            }

            private static bool Skip(MemberInfo member) =>
                member.IsDefined(typeof(DontSaveAttribute), inherit: true) || member.IsDefined(typeof(ObsoleteAttribute), inherit: true);
        }
    }

    /// <summary>Keeps a public property or field out of saved scenes, for values that only make sense while the game runs.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DontSaveAttribute : Attribute;
}
