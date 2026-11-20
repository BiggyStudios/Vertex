using System.Numerics;
using System.Reflection;

namespace Vertex.Animation
{
    /// <summary>
    /// Every value an animator can change, at one moment: where each object under it is, how it's turned and scaled, and
    /// the component settings its clips animate. Clips are sampled into poses, poses blended together, and the result
    /// put onto the objects.
    /// </summary>
    internal sealed class Pose
    {
        public Vector3[] Positions = [];
        public Quaternion[] Rotations = [];
        public Vector3[] Scales = [];
        public Vector4[] Values = [];

        /// <summary>
        /// With root motion (<see cref="Animator.RootMotion"/>): how far the character moves this frame, in its own space,
        /// and how much it turns. Blended like the rest, so half a walk moves half as far.
        /// </summary>
        public Vector3 Move;
        public Quaternion Turn = Quaternion.Identity;

        /// <summary>Makes it the same as <paramref name="other"/>, resizing to fit if the rig has grown.</summary>
        public void CopyFrom(Pose other)
        {
            Move = other.Move;
            Turn = other.Turn;
            Fit(ref Positions, other.Positions.Length);
            Fit(ref Rotations, other.Rotations.Length);
            Fit(ref Scales, other.Scales.Length);
            Fit(ref Values, other.Values.Length);
            other.Positions.CopyTo(Positions, 0);
            other.Rotations.CopyTo(Rotations, 0);
            other.Scales.CopyTo(Scales, 0);
            other.Values.CopyTo(Values, 0);
        }

        private static void Fit<T>(ref T[] array, int length)
        {
            if (array.Length != length) array = new T[length];
        }

        /// <summary>
        /// Moves it <paramref name="weight"/> of the way to <paramref name="other"/> (0 leaves it, 1 makes it the same).
        /// With a <paramref name="mask"/>, only the objects marked in it move, and the character's root motion stays this
        /// pose's; component settings always do.
        /// </summary>
        public void Blend(Pose other, float weight, bool[]? mask = null)
        {
            if (weight <= 0f) return;
            if (weight >= 1f && mask == null)
            {
                CopyFrom(other);
                return;
            }

            if (mask == null)
            {
                Move = Vector3.Lerp(Move, other.Move, Math.Min(weight, 1f));
                Turn = Quaternion.Slerp(Turn, other.Turn, Math.Min(weight, 1f));
            }

            int transforms = Math.Min(Positions.Length, other.Positions.Length);
            for (int i = 0; i < transforms; i++)
            {
                if (mask != null && (i >= mask.Length || !mask[i])) continue;
                Positions[i] = Vector3.Lerp(Positions[i], other.Positions[i], weight);
                Rotations[i] = Quaternion.Slerp(Rotations[i], other.Rotations[i], weight);
                Scales[i] = Vector3.Lerp(Scales[i], other.Scales[i], weight);
            }

            int values = Math.Min(Values.Length, other.Values.Length);
            for (int i = 0; i < values; i++) Values[i] = Vector4.Lerp(Values[i], other.Values[i], weight);
        }
    }

    /// <summary>
    /// What an animator animates: every object under it (each one's place in a <see cref="Pose"/>) as it was when it was
    /// bound, its rest pose, and which values each clip's tracks change. A track whose object or setting isn't there is
    /// left out.
    /// </summary>
    internal sealed class AnimationRig
    {
        /// <summary>One component setting (or an object's Active) that a clip animates.</summary>
        private sealed class ValueChannel(object target, MemberInfo? member, Type type)
        {
            public readonly object Target = target;
            public readonly MemberInfo? Member = member;
            public readonly Type Type = type;
            public Vector4 Rest;

            public Vector4 Get()
            {
                object? value = Member switch
                {
                    PropertyInfo property => property.GetValue(Target),
                    FieldInfo field => field.GetValue(Target),
                    _ => ((GameObject)Target).Active
                };
                return ToVector(value);
            }

            public void Set(Vector4 value)
            {
                object boxed = FromVector(value, Type);
                switch (Member)
                {
                    case PropertyInfo property: property.SetValue(Target, boxed); break;
                    case FieldInfo field: field.SetValue(Target, boxed); break;
                    default: ((GameObject)Target).Active = (bool)boxed; break;
                }
            }
        }

        /// <summary>What one track changes: an object's position, rotation, euler angles or scale, or a setting.</summary>
        private enum ChannelKind : byte { Position, Rotation, Euler, Scale, Value }

        private readonly record struct BoundTrack(AnimationTrack Track, ChannelKind Kind, int Index);

        private sealed class ClipBinding(int version, BoundTrack[] tracks)
        {
            public readonly int Version = version;
            public readonly BoundTrack[] Tracks = tracks;
        }

        private const byte _usesPosition = 1, _usesRotation = 2, _usesScale = 4;

        private readonly List<Transform> _transforms = new();
        private readonly Dictionary<Transform, int> _indices = new();
        private readonly List<int> _parents = new();
        private readonly List<int> _depths = new();
        private readonly List<ValueChannel> _values = new();
        private readonly Dictionary<string, int> _valueIndices = new();
        private readonly Dictionary<AnimationClip, ClipBinding> _bindings = new(ReferenceEqualityComparer.Instance);
        private byte[] _uses;

        public GameObject Root { get; }

        /// <summary>The pose the objects were in when bound: what's left where a clip doesn't animate something.</summary>
        public Pose Rest { get; } = new();

        public int TransformCount => _transforms.Count;
        public IReadOnlyList<Transform> Transforms => _transforms;

        public AnimationRig(GameObject root)
        {
            Root = root;
            Collect(root.Transform, -1, 0);
            _uses = new byte[_transforms.Count];
            Rest.Positions = new Vector3[_transforms.Count];
            Rest.Rotations = new Quaternion[_transforms.Count];
            Rest.Scales = new Vector3[_transforms.Count];
            for (int i = 0; i < _transforms.Count; i++)
            {
                Rest.Positions[i] = _transforms[i].Position;
                Rest.Rotations[i] = _transforms[i].Rotation;
                Rest.Scales[i] = _transforms[i].Scale;
            }
        }

        private void Collect(Transform transform, int parent, int depth)
        {
            int index = _transforms.Count;
            _indices[transform] = index;
            _transforms.Add(transform);
            _parents.Add(parent);
            _depths.Add(depth);
            foreach (Transform child in transform.Children) Collect(child, index, depth + 1);
        }

        /// <summary>A new pose, at rest.</summary>
        public Pose NewPose()
        {
            var pose = new Pose();
            pose.CopyFrom(Rest);
            return pose;
        }

        /// <summary>The object at <paramref name="path"/> from the root ("" for the root, "Arm/Hand"), or null.</summary>
        public static Transform? Find(Transform root, string path)
        {
            if (path.Length == 0) return root;
            Transform current = root;
            foreach (string name in path.Split('/'))
            {
                Transform? next = null;
                foreach (Transform child in current.Children)
                {
                    if (child.GameObject.Name == name)
                    {
                        next = child;
                        break;
                    }
                }

                if (next == null) return null;
                current = next;
            }

            return current;
        }

        /// <summary>The path from <paramref name="root"/> to <paramref name="transform"/>, or null if it isn't under it.</summary>
        public static string? PathOf(Transform root, Transform transform)
        {
            var names = new List<string>();
            for (Transform? t = transform; t != null; t = t.Parent)
            {
                if (t == root)
                {
                    names.Reverse();
                    return string.Join('/', names);
                }

                names.Add(t.GameObject.Name);
            }

            return null;
        }

        /// <summary>Which place in a pose <paramref name="transform"/> has; -1 if it isn't under the root.</summary>
        public int IndexOf(Transform transform) => _indices.TryGetValue(transform, out int index) ? index : -1;

        /// <summary>A mask marking the object called <paramref name="name"/> (searched for under the root) and everything under it; null for every object.</summary>
        public bool[]? MaskFrom(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var mask = new bool[_transforms.Count];
            Transform? from = _transforms.FirstOrDefault(t => t.GameObject.Name == name.Trim());
            if (from != null) Mark(from);
            return mask;

            void Mark(Transform transform)
            {
                if (_indices.TryGetValue(transform, out int index)) mask[index] = true;
                foreach (Transform child in transform.Children) Mark(child);
            }
        }

        private ClipBinding Bind(AnimationClip clip)
        {
            if (_bindings.TryGetValue(clip, out ClipBinding? bound) && bound.Version == clip.Version) return bound;

            var tracks = new List<BoundTrack>();
            foreach (AnimationTrack track in clip.Tracks)
            {
                if (track.Keys.Count == 0 || Find(Root.Transform, track.Path) is not { } transform || !_indices.TryGetValue(transform, out int index)) continue;
                switch (track.Property)
                {
                    case "Position":
                        tracks.Add(new BoundTrack(track, ChannelKind.Position, index));
                        _uses[index] |= _usesPosition;
                        break;
                    case "Rotation":
                        tracks.Add(new BoundTrack(track, ChannelKind.Rotation, index));
                        _uses[index] |= _usesRotation;
                        break;
                    case "EulerAngles":
                        tracks.Add(new BoundTrack(track, ChannelKind.Euler, index));
                        _uses[index] |= _usesRotation;
                        break;
                    case "Scale":
                        tracks.Add(new BoundTrack(track, ChannelKind.Scale, index));
                        _uses[index] |= _usesScale;
                        break;
                    default:
                        if (Value(transform.GameObject, track.Path, track.Property) is int value) tracks.Add(new BoundTrack(track, ChannelKind.Value, value));
                        break;
                }
            }

            bound = new ClipBinding(clip.Version, tracks.ToArray());
            _bindings[clip] = bound;
            return bound;
        }

        /// <summary>The place in a pose's values for a setting, made the first time it's asked for; null if there's no such setting.</summary>
        private int? Value(GameObject gameObject, string path, string property)
        {
            string key = path + "|" + property;
            if (_valueIndices.TryGetValue(key, out int existing)) return existing;
            if (Resolve(gameObject, property) is not { } channel) return null;

            channel.Rest = channel.Get();
            int index = _values.Count;
            _values.Add(channel);
            _valueIndices[key] = index;

            // Every pose made from here on has room for it, at rest.
            var values = new Vector4[_values.Count];
            Rest.Values.CopyTo(values, 0);
            values[index] = channel.Rest;
            Rest.Values = values;
            return index;
        }

        /// <summary>The setting <paramref name="property"/> names on <paramref name="gameObject"/>: "Active", or "Light.Intensity".</summary>
        private static ValueChannel? Resolve(GameObject gameObject, string property)
        {
            if (property == "Active") return new ValueChannel(gameObject, null, typeof(bool));

            string[] parts = property.Split('.');
            if (parts.Length < 2) return null;
            object? target = gameObject.Components.FirstOrDefault(c => c.GetType().Name == parts[0]);
            if (target == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                MemberInfo? member = (MemberInfo?)target.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance)
                    ?? target.GetType().GetField(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (member == null) return null;

                if (i == parts.Length - 1)
                {
                    Type type = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
                    bool writable = member is PropertyInfo { CanWrite: true, SetMethod.IsPublic: true } || member is FieldInfo { IsInitOnly: false };
                    return writable && SizeOf(type) != null ? new ValueChannel(target, member, type) : null;
                }

                target = member is PropertyInfo property2 ? property2.GetValue(target) : ((FieldInfo)member).GetValue(target);
                if (target == null || target.GetType().IsValueType) return null;
            }

            return null;
        }

        /// <summary>The value <paramref name="property"/> (as a track names it) has on <paramref name="gameObject"/> now; null if there's no such.</summary>
        internal static Vector4? Read(GameObject gameObject, string property)
        {
            Transform transform = gameObject.Transform;
            return property switch
            {
                "Position" => new Vector4(transform.Position, 0f),
                "Scale" => new Vector4(transform.Scale, 0f),
                "EulerAngles" => new Vector4(transform.EulerAngles, 0f),
                "Rotation" => ToVector(transform.Rotation),
                _ => Resolve(gameObject, property)?.Get()
            };
        }

        /// <summary>Sets <paramref name="property"/> (as a track names it) on <paramref name="gameObject"/>, if it has it.</summary>
        internal static void Write(GameObject gameObject, string property, Vector4 value)
        {
            Transform transform = gameObject.Transform;
            switch (property)
            {
                case "Position": transform.Position = new Vector3(value.X, value.Y, value.Z); break;
                case "Scale": transform.Scale = new Vector3(value.X, value.Y, value.Z); break;
                case "EulerAngles": transform.EulerAngles = new Vector3(value.X, value.Y, value.Z); break;
                case "Rotation": transform.Rotation = (Quaternion)FromVector(value, typeof(Quaternion)); break;
                default: Resolve(gameObject, property)?.Set(value); break;
            }
        }

        /// <summary>How many numbers a value of <paramref name="type"/> takes in a track; null for a type that can't be animated.</summary>
        public static int? SizeOf(Type type)
        {
            if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(bool)) return 1;
            if (type == typeof(Vector2)) return 2;
            if (type == typeof(Vector3)) return 3;
            if (type == typeof(Vector4) || type == typeof(Color) || type == typeof(Quaternion)) return 4;
            return null;
        }

        /// <summary>A value as a track's numbers.</summary>
        public static Vector4 ToVector(object? value) => value switch
        {
            float f => new Vector4(f, 0f, 0f, 0f),
            double d => new Vector4((float)d, 0f, 0f, 0f),
            int i => new Vector4(i, 0f, 0f, 0f),
            bool b => new Vector4(b ? 1f : 0f, 0f, 0f, 0f),
            Vector2 v => new Vector4(v, 0f, 0f),
            Vector3 v => new Vector4(v, 0f),
            Vector4 v => v,
            Color c => new Vector4(c.R, c.G, c.B, c.A),
            Quaternion q => new Vector4(q.X, q.Y, q.Z, q.W),
            _ => Vector4.Zero
        };

        /// <summary>A track's numbers as a value of <paramref name="type"/>: true/false from halfway, whole numbers rounded.</summary>
        public static object FromVector(Vector4 value, Type type)
        {
            if (type == typeof(float)) return value.X;
            if (type == typeof(double)) return (double)value.X;
            if (type == typeof(int)) return (int)MathF.Round(value.X);
            if (type == typeof(bool)) return value.X >= 0.5f;
            if (type == typeof(Vector2)) return new Vector2(value.X, value.Y);
            if (type == typeof(Vector3)) return new Vector3(value.X, value.Y, value.Z);
            if (type == typeof(Color)) return new Color(value.X, value.Y, value.Z, value.W);
            if (type == typeof(Quaternion)) return Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
            return value;
        }

        /// <summary>
        /// Puts <paramref name="clip"/> at <paramref name="time"/> seconds (in the clip, not wrapped) into <paramref name="pose"/>:
        /// what the clip animates is set, the rest is left as the pose had it.
        /// </summary>
        public void Sample(AnimationClip clip, float time, Pose pose)
        {
            ClipBinding binding = Bind(clip);
            if (pose.Values.Length != Rest.Values.Length)
            {
                // The rig has learnt of settings since the pose was made: they start at rest.
                var values = (Vector4[])Rest.Values.Clone();
                Array.Copy(pose.Values, values, Math.Min(pose.Values.Length, values.Length));
                pose.Values = values;
            }

            foreach (BoundTrack bound in binding.Tracks)
            {
                Vector4 value = bound.Track.Sample(time);
                switch (bound.Kind)
                {
                    case ChannelKind.Position: pose.Positions[bound.Index] = new Vector3(value.X, value.Y, value.Z); break;
                    case ChannelKind.Scale: pose.Scales[bound.Index] = new Vector3(value.X, value.Y, value.Z); break;
                    case ChannelKind.Rotation:
                        var rotation = new Quaternion(value.X, value.Y, value.Z, value.W);
                        pose.Rotations[bound.Index] = rotation.LengthSquared() > 1e-8f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
                        break;
                    case ChannelKind.Euler: pose.Rotations[bound.Index] = Transform.FromEuler(new Vector3(value.X, value.Y, value.Z)); break;
                    case ChannelKind.Value: pose.Values[bound.Index] = value; break;
                }
            }
        }

        /// <summary>Learns what <paramref name="clip"/> animates without sampling it, so <see cref="Apply"/> puts it back to rest too.</summary>
        public void Prepare(AnimationClip clip) => Bind(clip);

        /// <summary>Puts <paramref name="pose"/> onto the objects: only what some clip animates, so objects no clip touches are left to the game.</summary>
        public void Apply(Pose pose)
        {
            int transforms = Math.Min(_transforms.Count, pose.Positions.Length);
            for (int i = 0; i < transforms; i++)
            {
                byte uses = _uses[i];
                if (uses == 0) continue;
                Transform transform = _transforms[i];
                if ((uses & _usesPosition) != 0) transform.Position = pose.Positions[i];
                if ((uses & _usesRotation) != 0) transform.Rotation = pose.Rotations[i];
                if ((uses & _usesScale) != 0) transform.Scale = pose.Scales[i];
            }

            int values = Math.Min(_values.Count, pose.Values.Length);
            for (int i = 0; i < values; i++)
            {
                try
                {
                    _values[i].Set(pose.Values[i]);
                }
                catch (Exception)
                {
                    // A setting that refuses the value (or whose object's gone): left as it is.
                }
            }
        }

        /// <summary>Puts everything any clip has animated back as it was when the rig was made.</summary>
        public void ResetPose() => Apply(Rest);

        // ---------------------------------------------------------------- Root motion

        /// <summary>
        /// The object whose movement through an animation is the character's (see <see cref="Animator.RootMotion"/>): the one
        /// called <paramref name="name"/>, or with no name, the highest one an animation moves (the animator's own object, or
        /// a model's hips). -1 if there's none.
        /// </summary>
        public int MotionRoot(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string trimmed = name.Trim();
                for (int i = 0; i < _transforms.Count; i++)
                {
                    if (_transforms[i].GameObject.Name == trimmed) return i;
                }

                return -1;
            }

            if ((_uses[0] & (_usesPosition | _usesRotation)) != 0) return 0;
            int best = -1;
            for (int i = 1; i < _transforms.Count; i++)
            {
                if ((_uses[i] & _usesPosition) != 0 && (best < 0 || _depths[i] < _depths[best])) best = i;
            }

            return best;
        }

        /// <summary>
        /// Works out how far <paramref name="clip"/> moves and turns the character going from <paramref name="from"/> to
        /// <paramref name="to"/> seconds of playing (round the loop as many times as that takes), into the pose's
        /// <see cref="Pose.Move"/> and <see cref="Pose.Turn"/>.
        /// </summary>
        public void Move(AnimationClip clip, float from, float to, int root, Pose pose)
        {
            pose.Move = Vector3.Zero;
            pose.Turn = Quaternion.Identity;
            float length = clip.Length;
            if (root < 0 || root >= _transforms.Count || to == from || length <= 0f) return;

            ClipBinding binding = Bind(clip);
            (Vector3 Move, Quaternion Turn) total;
            if (!clip.Loop)
            {
                total = Step(binding, root, Math.Clamp(from, 0f, length), Math.Clamp(to, 0f, length));
            }
            else
            {
                float round = MathF.Floor(from / length), last = MathF.Floor(to / length);
                float start = from - round * length, end = to - last * length;
                if (round == last) total = Step(binding, root, start, end);
                else if (to > from)
                {
                    // To the end, round again as many whole times as it went, then on from the start.
                    total = Step(binding, root, start, length);
                    var whole = Step(binding, root, 0f, length);
                    for (float r = round + 1f; r < last && r < round + 8f; r++) total = Then(total, whole);
                    total = Then(total, Step(binding, root, 0f, end));
                }
                else
                {
                    total = Then(Step(binding, root, start, 0f), Step(binding, root, length, end));
                }
            }

            pose.Move = total.Move;
            pose.Turn = total.Turn;
        }

        private static (Vector3 Move, Quaternion Turn) Then((Vector3 Move, Quaternion Turn) first, (Vector3 Move, Quaternion Turn) second) =>
            (first.Move + Vector3.Transform(second.Move, first.Turn), Quaternion.Normalize(first.Turn * second.Turn));

        /// <summary>How the character moves from one moment of a clip to another, in the way it faces at the first.</summary>
        private (Vector3 Move, Quaternion Turn) Step(ClipBinding binding, int root, float from, float to)
        {
            (Vector3 p0, Quaternion q0) = RootAt(binding, root, from);
            (Vector3 p1, Quaternion q1) = RootAt(binding, root, to);
            Quaternion facing0 = Facing(root, q0), facing1 = Facing(root, q1);
            Vector3 move = Vector3.Transform(p1 - p0, Quaternion.Inverse(facing0));
            // A bone's movement up and down (a walk's bob) stays in the pose: only its movement along the ground is the character's.
            if (root != 0) move.Y = 0f;
            return (move, Quaternion.Normalize(Quaternion.Inverse(facing0) * facing1));
        }

        /// <summary>Which way the character faces, from how its root is turned: all of the turn for its own object, a bone's turn round the up axis.</summary>
        private Quaternion Facing(int root, Quaternion rotation)
        {
            if (root == 0) return rotation;
            Quaternion rest = InRootSpace(root, Rest.Positions[root], Rest.Rotations[root], Rest).Rotation;
            return Yaw(rotation * Quaternion.Inverse(rest));
        }

        /// <summary>The part of a rotation that's round the up axis.</summary>
        private static Quaternion Yaw(Quaternion rotation)
        {
            var twist = new Quaternion(0f, rotation.Y, 0f, rotation.W);
            float length = twist.Length();
            return length < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(twist);
        }

        /// <summary>Where the root is in a clip at <paramref name="time"/>, in the animator's space (its own object's: in its parent's).</summary>
        private (Vector3 Position, Quaternion Rotation) RootAt(ClipBinding binding, int root, float time)
        {
            Vector3 position = Rest.Positions[root];
            Quaternion rotation = Rest.Rotations[root];
            foreach (BoundTrack bound in binding.Tracks)
            {
                if (bound.Kind == ChannelKind.Value || bound.Index != root) continue;
                Vector4 value = bound.Track.Sample(time);
                switch (bound.Kind)
                {
                    case ChannelKind.Position: position = new Vector3(value.X, value.Y, value.Z); break;
                    case ChannelKind.Rotation:
                        var q = new Quaternion(value.X, value.Y, value.Z, value.W);
                        rotation = q.LengthSquared() > 1e-8f ? Quaternion.Normalize(q) : Quaternion.Identity;
                        break;
                    case ChannelKind.Euler: rotation = Transform.FromEuler(new Vector3(value.X, value.Y, value.Z)); break;
                }
            }

            return root == 0 ? (position, rotation) : InRootSpace(root, position, rotation, Rest);
        }

        /// <summary>A position and rotation of object <paramref name="index"/>, from its parent's space into the root's (with the objects between as <paramref name="chain"/> has them).</summary>
        private (Vector3 Position, Quaternion Rotation) InRootSpace(int index, Vector3 position, Quaternion rotation, Pose chain)
        {
            for (int p = _parents[index]; p > 0; p = _parents[p])
            {
                position = Vector3.Transform(position * chain.Scales[p], chain.Rotations[p]) + chain.Positions[p];
                rotation = chain.Rotations[p] * rotation;
            }

            return (position, rotation);
        }

        /// <summary>The other way to <see cref="InRootSpace"/>: from the root's space into object <paramref name="index"/>'s parent's.</summary>
        private (Vector3 Position, Quaternion Rotation) FromRootSpace(int index, Vector3 position, Quaternion rotation, Pose chain)
        {
            var between = new List<int>();
            for (int p = _parents[index]; p > 0; p = _parents[p]) between.Add(p);
            for (int i = between.Count - 1; i >= 0; i--)
            {
                int p = between[i];
                Quaternion inverse = Quaternion.Inverse(chain.Rotations[p]);
                Vector3 scale = chain.Scales[p];
                position = Vector3.Transform(position - chain.Positions[p], inverse);
                position = new Vector3(Divide(position.X, scale.X), Divide(position.Y, scale.Y), Divide(position.Z, scale.Z));
                rotation = inverse * rotation;
            }

            return (position, rotation);

            static float Divide(float value, float by) => MathF.Abs(by) > 1e-8f ? value / by : value;
        }

        /// <summary>
        /// Takes the root's movement along the ground and its turn out of <paramref name="pose"/>, so the character animates
        /// in place while the animator moves it instead. For the animator's own object, nothing's taken: it's moved.
        /// </summary>
        public void KeepInPlace(int root, Pose pose)
        {
            if (root <= 0 || root >= _transforms.Count || root >= pose.Positions.Length) return;
            (Vector3 position, Quaternion rotation) = InRootSpace(root, pose.Positions[root], pose.Rotations[root], pose);
            (Vector3 restPosition, _) = InRootSpace(root, Rest.Positions[root], Rest.Rotations[root], Rest);
            Quaternion facing = Facing(root, rotation);
            position = new Vector3(restPosition.X, position.Y, restPosition.Z);
            rotation = Quaternion.Normalize(Quaternion.Inverse(facing) * rotation);
            (pose.Positions[root], pose.Rotations[root]) = FromRootSpace(root, position, rotation, pose);
        }
    }
}
