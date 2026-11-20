using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Vertex.Animation
{
    /// <summary>How a value goes from one key to the next.</summary>
    public enum KeyInterpolation
    {
        /// <summary>Eases in and out, and flows through the keys either side without overshooting them.</summary>
        Smooth,

        /// <summary>At a steady rate, straight to the next key.</summary>
        Linear,

        /// <summary>Holds this key's value until the next key, then jumps: for switching things on and off.</summary>
        Step
    }

    /// <summary>A value at a time in a track, and how it goes on to the next key.</summary>
    public record struct Keyframe(float Time, Vector4 Value, KeyInterpolation Interpolation = KeyInterpolation.Smooth);

    /// <summary>
    /// One value of one object, changing over a clip: the position of "Arm/Hand", the brightness of a light. The object is
    /// found by its <see cref="Path"/> from the object the <see cref="Animator"/> is on; what changes is its
    /// <see cref="Property"/>.
    /// </summary>
    public sealed class AnimationTrack
    {
        /// <summary>
        /// Where the object is from the animator's own object, by name: "" for that object itself, "Arm" for a child
        /// called Arm, "Arm/Hand" for Arm's child Hand.
        /// </summary>
        public string Path { get; set; } = "";

        /// <summary>
        /// What changes: <c>Position</c>, <c>Rotation</c> (a quaternion), <c>EulerAngles</c> (degrees, which can spin
        /// past 360), <c>Scale</c>, <c>Active</c> (shown or hidden), or one of a component's settings by the component's
        /// type and the setting's name: <c>Light.Intensity</c>, <c>MeshRenderer.Material.Color</c>.
        /// </summary>
        public string Property { get; set; } = "";

        /// <summary>How many numbers each key holds: 1 for a number or true/false, 3 for a position, 4 for a colour or rotation.</summary>
        public int Size { get; set; } = 1;

        /// <summary>The keys, in time order.</summary>
        public List<Keyframe> Keys { get; } = new();

        public AnimationTrack() {}

        public AnimationTrack(string path, string property, int size)
        {
            Path = path;
            Property = property;
            Size = size;
        }

        /// <summary>The number of values a built-in property has; null for a component's setting, whose size its type decides.</summary>
        public static int? SizeOf(string property) => property switch
        {
            "Position" or "Scale" or "EulerAngles" => 3,
            "Rotation" => 4,
            "Active" => 1,
            _ => null
        };

        /// <summary>
        /// Puts a key at <paramref name="time"/> (replacing one already there), keeping the keys in order. A rotation's
        /// key is turned to the same side as the key before it, so the rotation between them goes the short way.
        /// </summary>
        public void SetKey(float time, Vector4 value, KeyInterpolation? interpolation = null)
        {
            int index = Find(time);
            if (index >= 0)
            {
                Keys[index] = Keys[index] with { Value = value, Interpolation = interpolation ?? Keys[index].Interpolation };
                FixHemisphere(index);
                return;
            }

            int at = 0;
            while (at < Keys.Count && Keys[at].Time < time) at++;
            KeyInterpolation chosen = interpolation ?? (at > 0 ? Keys[at - 1].Interpolation : at < Keys.Count ? Keys[at].Interpolation : KeyInterpolation.Smooth);
            Keys.Insert(at, new Keyframe(time, value, chosen));
            FixHemisphere(at);
        }

        /// <summary>The key at <paramref name="time"/> (within a thousandth of a second), or -1.</summary>
        public int Find(float time)
        {
            for (int i = 0; i < Keys.Count; i++)
            {
                if (MathF.Abs(Keys[i].Time - time) < 0.001f) return i;
            }

            return -1;
        }

        public bool RemoveKey(float time)
        {
            int index = Find(time);
            if (index < 0) return false;
            Keys.RemoveAt(index);
            return true;
        }

        /// <summary>Puts the keys back in time order: after they've been moved.</summary>
        public void Sort() => Keys.Sort((a, b) => a.Time.CompareTo(b.Time));

        private void FixHemisphere(int index)
        {
            if (Property != "Rotation") return;
            for (int i = Math.Max(1, index); i < Keys.Count; i++)
            {
                if (Vector4.Dot(Keys[i].Value, Keys[i - 1].Value) < 0f) Keys[i] = Keys[i] with { Value = -Keys[i].Value };
            }
        }

        /// <summary>The value at <paramref name="time"/>: before the first key it's the first, after the last it's the last.</summary>
        public Vector4 Sample(float time)
        {
            int count = Keys.Count;
            if (count == 0) return Vector4.Zero;
            if (count == 1 || time <= Keys[0].Time) return Keys[0].Value;
            if (time >= Keys[count - 1].Time) return Keys[count - 1].Value;

            // The key before: a binary search, as imported clips can have thousands.
            int low = 0, high = count - 1;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (Keys[middle].Time <= time) low = middle;
                else high = middle;
            }

            Keyframe a = Keys[low], b = Keys[low + 1];
            float span = b.Time - a.Time;
            if (span <= 1e-6f) return b.Value;
            float t = (time - a.Time) / span;

            switch (a.Interpolation)
            {
                case KeyInterpolation.Step:
                    return a.Value;
                case KeyInterpolation.Linear:
                    return Vector4.Lerp(a.Value, b.Value, t);
                default:
                    Vector4 before = Tangent(low), after = Tangent(low + 1);
                    float t2 = t * t, t3 = t2 * t;
                    return a.Value * (2f * t3 - 3f * t2 + 1f) + before * (span * (t3 - 2f * t2 + t)) + b.Value * (-2f * t3 + 3f * t2) + after * (span * (t3 - t2));
            }
        }

        /// <summary>
        /// How fast the value changes as it passes key <paramref name="index"/> (per second): flat at the first and last keys
        /// and wherever the key is a peak or a dip, otherwise along the line from the key before to the key after, limited so
        /// the curve never overshoots either.
        /// </summary>
        private Vector4 Tangent(int index)
        {
            if (index <= 0 || index >= Keys.Count - 1) return Vector4.Zero;
            Keyframe previous = Keys[index - 1], key = Keys[index], next = Keys[index + 1];
            float before = key.Time - previous.Time, after = next.Time - key.Time;
            if (before <= 1e-6f || after <= 1e-6f) return Vector4.Zero;

            Vector4 result = default;
            for (int c = 0; c < 4; c++)
            {
                float slopeIn = (key.Value[c] - previous.Value[c]) / before;
                float slopeOut = (next.Value[c] - key.Value[c]) / after;
                if (slopeIn * slopeOut <= 0f) continue;
                float slope = (next.Value[c] - previous.Value[c]) / (before + after);
                float limit = 3f * MathF.Min(MathF.Abs(slopeIn), MathF.Abs(slopeOut));
                result[c] = Math.Clamp(slope, -limit, limit);
            }

            return result;
        }
    }

    /// <summary>A name sent at a time in a clip: a footstep, a sword's swing. See <see cref="Animator.EventReached"/>.</summary>
    public sealed class AnimationEvent
    {
        public float Time { get; set; }
        public string Name { get; set; } = "";

        public AnimationEvent() {}

        public AnimationEvent(float time, string name)
        {
            Time = time;
            Name = name;
        }
    }

    /// <summary>
    /// An animation: tracks of values that change over time (an arm swinging, a door opening, a light flickering), played
    /// on an object and the objects under it by an <see cref="Animator"/>. Made in the editor's Animation panel and saved
    /// as a .anim file, or brought in with a model (a model file's own animations, its walk and run): load either with
    /// <see cref="Assets.LoadAnimation"/>.
    /// </summary>
    public sealed class AnimationClip
    {
        public const int FormatVersion = 1;

        /// <summary>The extension of animation files.</summary>
        public const string Extension = ".anim";

        /// <summary>The name it's known by: the file's, or the animation's in its model file.</summary>
        public string Name { get; set; } = "Animation";

        /// <summary>
        /// Where it came from, in full: a .anim file, or a model file and the animation's name in it ("Robot.glb#Walk").
        /// Null for one made in code.
        /// </summary>
        public string? Source { get; internal set; }

        /// <summary>Whether it came from a model file, which can't be changed (it's the model's).</summary>
        public bool IsImported => Source != null && Source.Contains('#');

        /// <summary>How long it lasts, in seconds.</summary>
        public float Length { get; set; } = 1f;

        /// <summary>Whether it starts again when it reaches the end; otherwise it holds its last pose.</summary>
        public bool Loop { get; set; } = true;

        /// <summary>The steps the editor's timeline snaps keys to, a second.</summary>
        public float FrameRate { get; set; } = 30f;

        public List<AnimationTrack> Tracks { get; } = new();
        public List<AnimationEvent> Events { get; } = new();

        /// <summary>Counts changes, so animators playing it know to look its tracks up again.</summary>
        public int Version { get; private set; }

        /// <summary>Marks it changed: after changing its tracks.</summary>
        public void Changed() => Version++;

        public AnimationTrack? Track(string path, string property)
        {
            foreach (AnimationTrack track in Tracks)
            {
                if (track.Path == path && track.Property == property) return track;
            }

            return null;
        }

        /// <summary>The track for <paramref name="property"/> of the object at <paramref name="path"/>, made if there isn't one.</summary>
        public AnimationTrack AddTrack(string path, string property, int size)
        {
            if (Track(path, property) is { } existing) return existing;
            var track = new AnimationTrack(path, property, size);
            Tracks.Add(track);
            Changed();
            return track;
        }

        /// <summary>Where in the clip <paramref name="time"/> seconds of playing is: round again if it loops, held at the end if not.</summary>
        public float Wrap(float time)
        {
            if (Length <= 0f) return 0f;
            if (!Loop) return Math.Clamp(time, 0f, Length);
            float wrapped = time % Length;
            return wrapped < 0f ? wrapped + Length : wrapped;
        }

        // ---------------------------------------------------------------- Files

        /// <summary>The JSON last saved or loaded: a file written with the same isn't read in again.</summary>
        internal string? SavedJson { get; set; }

        public static AnimationClip Load(string path)
        {
            string json = File.ReadAllText(path);
            var clip = new AnimationClip { Source = Path.GetFullPath(path), Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            clip.ReadJson(json);
            clip.SavedJson = json;
            return clip;
        }

        /// <summary>Saves it to <paramref name="path"/>, which it then comes from.</summary>
        public void Save(string path)
        {
            string json = ToJson();
            string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (directory != null) Directory.CreateDirectory(directory);
            File.WriteAllText(path, json);
            Source = System.IO.Path.GetFullPath(path);
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
            SavedJson = json;
        }

        /// <summary>Saves it back to the file it came from, if it changed since. True if it was written. A model's own clips aren't.</summary>
        public bool SaveIfChanged()
        {
            if (Source == null || IsImported) return false;
            string json = ToJson();
            if (json == SavedJson) return false;
            File.WriteAllText(Source, json);
            SavedJson = json;
            return true;
        }

        /// <summary>A copy, with its own tracks, that isn't from any file: to change a model's clip and save it as one's own.</summary>
        public AnimationClip Copy()
        {
            var copy = new AnimationClip { Name = Name };
            copy.ReadJson(ToJson());
            return copy;
        }

        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("format", FormatVersion);
                json.WriteNumber("length", Length);
                json.WriteBoolean("loop", Loop);
                json.WriteNumber("frameRate", FrameRate);

                json.WriteStartArray("tracks");
                foreach (AnimationTrack track in Tracks)
                {
                    json.WriteStartObject();
                    json.WriteString("path", track.Path);
                    json.WriteString("property", track.Property);
                    json.WriteNumber("size", track.Size);
                    json.WriteStartArray("keys");
                    foreach (Keyframe key in track.Keys)
                    {
                        json.WriteStartObject();
                        json.WriteNumber("t", MathF.Round(key.Time, 5));
                        json.WriteStartArray("v");
                        for (int c = 0; c < Math.Clamp(track.Size, 1, 4); c++) json.WriteNumberValue(key.Value[c]);
                        json.WriteEndArray();
                        if (key.Interpolation != KeyInterpolation.Smooth) json.WriteString("i", key.Interpolation.ToString());
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }

                json.WriteEndArray();

                json.WriteStartArray("events");
                foreach (AnimationEvent animationEvent in Events)
                {
                    json.WriteStartObject();
                    json.WriteNumber("time", animationEvent.Time);
                    json.WriteString("name", animationEvent.Name);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>Replaces everything in it with what's in <paramref name="json"/>, keeping it the same object (animators playing it see the change).</summary>
        public void ReadJson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            Tracks.Clear();
            Events.Clear();
            Length = root.TryGetProperty("length", out JsonElement length) ? length.GetSingle() : 1f;
            Loop = !root.TryGetProperty("loop", out JsonElement loop) || loop.GetBoolean();
            FrameRate = root.TryGetProperty("frameRate", out JsonElement rate) ? rate.GetSingle() : 30f;

            if (root.TryGetProperty("tracks", out JsonElement tracks))
            {
                foreach (JsonElement saved in tracks.EnumerateArray())
                {
                    var track = new AnimationTrack(saved.GetProperty("path").GetString() ?? "", saved.GetProperty("property").GetString() ?? "",
                        saved.TryGetProperty("size", out JsonElement size) ? size.GetInt32() : 1);
                    if (saved.TryGetProperty("keys", out JsonElement keys))
                    {
                        foreach (JsonElement key in keys.EnumerateArray())
                        {
                            Vector4 value = default;
                            int c = 0;
                            foreach (JsonElement number in key.GetProperty("v").EnumerateArray())
                            {
                                if (c < 4) value[c++] = number.GetSingle();
                            }

                            KeyInterpolation interpolation = key.TryGetProperty("i", out JsonElement i) && Enum.TryParse(i.GetString(), out KeyInterpolation parsed) ? parsed : KeyInterpolation.Smooth;
                            track.Keys.Add(new Keyframe(key.GetProperty("t").GetSingle(), value, interpolation));
                        }
                    }

                    track.Sort();
                    Tracks.Add(track);
                }
            }

            if (root.TryGetProperty("events", out JsonElement events))
            {
                foreach (JsonElement saved in events.EnumerateArray())
                {
                    Events.Add(new AnimationEvent(saved.GetProperty("time").GetSingle(), saved.GetProperty("name").GetString() ?? ""));
                }
            }

            Changed();
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({Length:0.##} s, {Tracks.Count} tracks)");
    }
}
