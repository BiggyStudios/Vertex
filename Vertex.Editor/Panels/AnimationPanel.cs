using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Vertex.Animation;
using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Editor.Inspection;
using Vertex.Input;
using Vertex.Rendering;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// Makes and changes animations for the object selected (its nearest Animator, and the objects under it): a timeline
    /// with a row of keys for each thing the animation moves. While the panel's showing an animation, the objects are
    /// posed as it has them at the playhead (and put back as they were when it stops showing it, before saving, and before
    /// play).
    /// <para>
    /// With Record on, whatever's changed in the scene or the inspector on those objects becomes a key at the playhead:
    /// move a hand at 0.5 seconds and that's where the hand is at 0.5 seconds. Keys can be dragged along, given a different
    /// way to the next (right-click), and deleted. Double-click a row to add a key there.
    /// </para>
    /// </summary>
    public sealed class AnimationPanel : IDockPanel
    {
        private const float _rowHeight = 24f;
        private const float _rulerHeight = 26f;
        private const float _listWidth = 230f;
        private const float _laneMargin = 12f;

        private readonly EditorContext _context;

        // What it shows: the animator (from the selection), and the animation chosen for each.
        private Animator? _animator;
        private readonly ConditionalWeakTable<Animator, AnimationClip> _clipFor = new();
        private AnimationClip? _opened;
        private float _time;
        private bool _playing, _recording;

        // The preview: which animator it has posed, and what it posed it as (so it poses again only when something changed).
        private Animator? _previewing;
        private (AnimationClip? Clip, int Version, float Time, int History) _sampled;
        private readonly Dictionary<(GameObject Object, string Property), Vector4> _expected = new();
        private readonly Dictionary<GameObject, string> _paths = new();
        private bool _reverted;

        // A change being recorded (a drag of a handle goes on for many frames): the animation as it was before.
        private string? _recordBefore;

        // The timeline: how many pixels a second, and the time at its left edge.
        private float _pixelsPerSecond;
        private float _viewStart;
        private bool _fitted;
        private AnimationClip? _fittedTo;

        // Keys picked, by track and time; the one event picked.
        private readonly List<(AnimationTrack Track, float Time)> _selected = new();
        private AnimationEvent? _selectedEvent;

        // A drag of keys or an event along the timeline.
        private sealed class Drag
        {
            public float StartX;
            public string Before = "";
            public Dictionary<AnimationTrack, (List<Keyframe> Original, HashSet<int> Moving)> Tracks = new();
            public float EventStart;
            public float Moved;
        }

        private Drag? _drag;

        // Where each key was drawn this frame, by track and number: for tests, which drag them.
        private readonly Dictionary<(AnimationTrack, int), Vector2> _keysOnScreen = new();

        internal Vector2? KeyOnScreen(AnimationTrack track, int index) => _keysOnScreen.TryGetValue((track, index), out Vector2 at) ? at : null;
        private int _drawnFrame = -1;

        public AnimationPanel(EditorContext context)
        {
            _context = context;
            context.SceneSettling += EndPreview;
            context.AnimationOpenRequested += Open;
        }

        // The undo histories it hears undos from: the scene's, and each prefab tab's as it's shown.
        private readonly ConditionalWeakTable<UndoHistory, object> _heard = new();

        private void HearUndos()
        {
            UndoHistory history = _context.History;
            if (_heard.TryGetValue(history, out _)) return;
            history.Reverted += (_, _) => _reverted = true;
            _heard.Add(history, new object());
        }

        public string Title => "Animation";
        public Icon Icon => Icons.Keyframes;

        /// <summary>The animation it shows, if any: for tests.</summary>
        internal AnimationClip? Clip => _animator != null ? ClipOf(_animator) : null;

        /// <summary>Where the playhead is, in seconds.</summary>
        internal float Time
        {
            get => _time;
            set => _time = MathF.Max(0f, value);
        }

        internal bool Recording
        {
            get => _recording;
            set => _recording = value && Clip is { IsImported: false };
        }

        internal bool Playing => _playing;

        /// <summary>Shows an animation (a .anim file or a model's own): for the animator selected, or the next one selected.</summary>
        private void Open(string path)
        {
            if (path.Length == 0) return;
            try
            {
                _opened = Vertex.Assets.LoadAnimation(path);
                if (_animator != null) Choose(_animator, _opened);
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't open the animation: {exception.Message}");
            }
        }

        private void Choose(Animator animator, AnimationClip clip)
        {
            _clipFor.AddOrUpdate(animator, clip);
            _selected.Clear();
            _selectedEvent = null;
            _fitted = false;
            if (clip.IsImported) _recording = false;
            _time = Math.Clamp(_time, 0f, MathF.Max(clip.Length, 0f));
        }

        /// <summary>The animation shown for <paramref name="animator"/>: the one chosen for it, or the one it plays.</summary>
        private AnimationClip? ClipOf(Animator animator)
        {
            if (_clipFor.TryGetValue(animator, out AnimationClip? chosen)) return chosen;
            return animator.Clip ?? ClipsOf(animator).FirstOrDefault();
        }

        /// <summary>The animations to choose between for an animator: its clip, its graph's, and any opened.</summary>
        private List<AnimationClip> ClipsOf(Animator animator)
        {
            var clips = new List<AnimationClip>();
            void Add(AnimationClip? clip)
            {
                if (clip != null && !clips.Contains(clip)) clips.Add(clip);
            }

            Add(animator.Clip);
            if (animator.AnimationGraph is { } graph)
            {
                foreach (Vertex.Graphs.GraphNode node in graph.Nodes)
                {
                    if (node.Type == AnimationNodes.Clip) Add(Vertex.Graphs.PortType.Animation.Convert(node.Values.GetValueOrDefault("Animation")) as AnimationClip);
                }
            }

            if (_clipFor.TryGetValue(animator, out AnimationClip? chosen)) Add(chosen);
            Add(_opened);
            return clips;
        }

        /// <summary>The animator the selected object is animated by: its own, or the nearest above it.</summary>
        private Animator? Selected()
        {
            for (Transform? t = _context.Selection.Object?.Transform; t != null; t = t.Parent)
            {
                if (t.GameObject.GetComponent<Animator>() is { } animator) return animator;
            }

            return null;
        }

        // ---------------------------------------------------------------- Preview

        /// <summary>Puts the objects back as they were before the preview posed them: when it stops showing them, and before saving or playing.</summary>
        public void EndPreview()
        {
            if (_previewing != null)
            {
                FinishRecording();
                _previewing.ResetPose();
                _previewing = null;
            }

            _sampled = default;
            _playing = false;
        }

        /// <summary>Called once a frame after everything's drawn: a preview the panel didn't draw this frame (it's hidden) ends.</summary>
        public void AfterFrame()
        {
            if (_drawnFrame != Ui.Frame) EndPreview();
        }

        /// <summary>Poses the animator's objects as the animation has them at the playhead, when anything that changes that has changed.</summary>
        private void Preview(Animator animator, AnimationClip clip)
        {
            if (_previewing != animator)
            {
                EndPreview();
                // How the objects are now is how they rest: what's put back when the preview ends.
                animator.Rebind();
                _previewing = animator;
            }

            if (_reverted)
            {
                // An undo or redo changed things: that isn't something to record.
                _reverted = false;
                _sampled = default;
            }
            else if (_recording)
            {
                RecordChanges(animator, clip);
            }

            var stamp = (clip, clip.Version, _time, _context.History.Version);
            if (stamp == _sampled) return;

            animator.Sample(clip, _time);
            _sampled = stamp;
            Snapshot(animator);
        }

        /// <summary>Notes every value that can be animated on the objects as posed, to tell what's changed since.</summary>
        private void Snapshot(Animator animator)
        {
            _expected.Clear();
            _paths.Clear();
            foreach (Transform transform in animator.Rig.Transforms)
            {
                if (AnimationRig.PathOf(animator.Transform, transform) is not { } path) continue;
                _paths[transform.GameObject] = path;
                foreach ((string property, _, _) in PropertiesOf(transform.GameObject, rotationAs: "Rotation"))
                {
                    if (ReadSafely(transform.GameObject, property) is { } value) _expected[(transform.GameObject, property)] = value;
                }
            }
        }

        /// <summary>
        /// With Record on: whatever's been changed on the objects since they were posed becomes a key at the playhead. A
        /// change that goes on over several frames (a drag) is one undo, together with the move that made it.
        /// </summary>
        private void RecordChanges(Animator animator, AnimationClip clip)
        {
            var changed = new List<(GameObject Object, string Path, string Property, Vector4 Was, Vector4 Now)>();
            foreach (((GameObject gameObject, string property), Vector4 was) in _expected)
            {
                if (gameObject.IsDestroyed || ReadSafely(gameObject, property) is not { } now) continue;
                if (Vector4.DistanceSquared(was, now) < 1e-10f) continue;
                if (property == "Rotation" && MathF.Abs(Vector4.Dot(was, now)) > 0.999999f) continue;
                changed.Add((gameObject, _paths[gameObject], property, was, now));
            }

            if (changed.Count == 0)
            {
                if (!Ui.MouseHeld()) FinishRecording();
                return;
            }

            _recordBefore ??= clip.ToJson();
            AnimationRig rig = animator.Rig;
            foreach ((GameObject gameObject, string path, string property, Vector4 was, Vector4 now) in changed)
            {
                if (property == "Rotation")
                {
                    // Kept as angles (so it can turn past a whole turn), each the way round nearest to what it was.
                    if (clip.Track(path, "Rotation") is { } quaternions)
                    {
                        quaternions.SetKey(_time, now);
                        continue;
                    }

                    Vector3 reference = clip.Track(path, "EulerAngles") is { Keys.Count: > 0 } existing
                        ? AsVector3(existing.Sample(_time))
                        : Transform.ToEuler(new Quaternion(was.X, was.Y, was.Z, was.W));
                    Vector3 angles = Nearest(gameObject.Transform.EulerAngles, reference);
                    Vector3 rest = Transform.ToEuler(rig.Rest.Rotations[rig.IndexOf(gameObject.Transform)]);
                    RecordKey(clip, path, "EulerAngles", 3, new Vector4(angles, 0f), new Vector4(Nearest(rest, reference), 0f));
                }
                else if (property is "Position" or "Scale")
                {
                    int index = rig.IndexOf(gameObject.Transform);
                    Vector3 rest = property == "Position" ? rig.Rest.Positions[index] : rig.Rest.Scales[index];
                    RecordKey(clip, path, property, 3, now, new Vector4(rest, 0f));
                }
                else
                {
                    // A setting is put back as it was, so the animator takes that as how it rests; the key poses it.
                    RecordKey(clip, path, property, AnimationTrack.SizeOf(property) ?? SizeOfSetting(gameObject, property), now, was);
                    AnimationRig.Write(gameObject, property, was);
                }
            }

            clip.Changed();
            if (!Ui.MouseHeld()) FinishRecording();
        }

        /// <summary>A key at the playhead; for a new track whose first key isn't at the start, one at the start too, as it rests.</summary>
        private void RecordKey(AnimationClip clip, string path, string property, int size, Vector4 value, Vector4 rest)
        {
            bool isNew = clip.Track(path, property) == null;
            AnimationTrack track = clip.AddTrack(path, property, size);
            if (isNew && _time > 0.001f) track.SetKey(0f, rest);
            track.SetKey(_time, value);
            if (_time > clip.Length) clip.Length = _time;
        }

        /// <summary>The change being recorded is done: it's remembered as one undo with the move that made it.</summary>
        private void FinishRecording()
        {
            if (_recordBefore == null) return;
            AnimationClip? clip = _previewing != null ? ClipOf(_previewing) : null;
            string before = _recordBefore;
            _recordBefore = null;
            if (clip == null) return;

            string after = clip.ToJson();
            if (after != before) _context.History.RecordWithLast(new ClipChange(clip, "Record Keys", before, after));
        }

        private static Vector3 AsVector3(Vector4 value) => new(value.X, value.Y, value.Z);

        /// <summary>A value as a track has it, or null if it can't be read (a setting whose getter fails).</summary>
        private static Vector4? ReadSafely(GameObject gameObject, string property)
        {
            try
            {
                return AnimationRig.Read(gameObject, property);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Each angle a whole number of turns from where it is, to be as near as it can to <paramref name="reference"/>.</summary>
        private static Vector3 Nearest(Vector3 angles, Vector3 reference)
        {
            static float One(float angle, float near) => angle + 360f * MathF.Round((near - angle) / 360f);
            return new Vector3(One(angles.X, reference.X), One(angles.Y, reference.Y), One(angles.Z, reference.Z));
        }

        private static int SizeOfSetting(GameObject gameObject, string property) =>
            AnimationRig.Read(gameObject, property) is { } value ? value.W != 0f ? 4 : value.Z != 0f ? 3 : value.Y != 0f ? 2 : 1 : 1;

        // ---------------------------------------------------------------- What can be animated

        private static readonly Dictionary<Type, List<(string Name, int Size)>> _animatable = new();

        /// <summary>A type's settings an animation can change: public, readable and writable, and a number, true/false, vector or colour.</summary>
        private static List<(string Name, int Size)> Animatable(Type type)
        {
            if (_animatable.TryGetValue(type, out List<(string, int)>? known)) return known;
            var found = new List<(string, int)>();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetMethod is not { IsPublic: true } || property.SetMethod is not { IsPublic: true }) continue;
                if (property.IsDefined(typeof(HideInInspectorAttribute), true) || property.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                if (AnimationRig.SizeOf(property.PropertyType) is int size) found.Add((property.Name, size));
            }

            _animatable[type] = found;
            return found;
        }

        /// <summary>
        /// What can be animated on an object, as a track names it, with how many numbers it takes and what to call it: its
        /// position, rotation, scale and whether it's shown, then its components' settings (and a renderer's material's).
        /// </summary>
        internal static IEnumerable<(string Property, int Size, string Label)> PropertiesOf(GameObject gameObject, string rotationAs = "EulerAngles")
        {
            yield return ("Position", 3, "Position");
            yield return (rotationAs, rotationAs == "Rotation" ? 4 : 3, "Rotation");
            yield return ("Scale", 3, "Scale");
            yield return ("Active", 1, "Shown");

            foreach (Component component in gameObject.Components)
            {
                if (component is Animator) continue;
                Type type = component.GetType();
                string name = ComponentCatalog.DisplayName(type);
                foreach ((string member, int size) in Animatable(type)) yield return ($"{type.Name}.{member}", size, $"{name} › {ComponentCatalog.SplitWords(member)}");
                if (component is MeshRenderer)
                {
                    foreach ((string member, int size) in Animatable(typeof(Material))) yield return ($"{type.Name}.Material.{member}", size, $"Material › {ComponentCatalog.SplitWords(member)}");
                }
            }
        }

        /// <summary>What a track's property is called: "Position", "Light › Intensity", "Material › Color".</summary>
        private static string PropertyLabel(string property)
        {
            switch (property)
            {
                case "EulerAngles" or "Rotation": return "Rotation";
                case "Active": return "Shown";
                case "Position" or "Scale": return property;
            }

            string[] parts = property.Split('.');
            if (parts.Length >= 3 && parts[^2] == "Material") return $"Material › {ComponentCatalog.SplitWords(parts[^1])}";
            return parts.Length >= 2 ? $"{ComponentCatalog.SplitWords(parts[0])} › {ComponentCatalog.SplitWords(parts[^1])}" : property;
        }

        private static string ObjectLabel(Animator animator, string path) => path.Length == 0 ? animator.GameObject.Name : path[(path.LastIndexOf('/') + 1)..];

        // ---------------------------------------------------------------- Drawing

        public void Draw(Rect rect)
        {
            _drawnFrame = Ui.Frame;
            HearUndos();
            Rect content = PanelFrame.Draw(rect, Title, Icon);

            if (_context.Play.Playing)
            {
                if (_animator != null) EndPreview();
                _animator = null;
                Message(content, "Animations can't be changed while the game plays.", null);
                return;
            }

            Animator? animator = Selected();
            if (animator != _animator)
            {
                EndPreview();
                _animator = animator;
                _selected.Clear();
                _selectedEvent = null;
                _recording = false;
                _fitted = false;
                if (animator != null && _opened != null && !_clipFor.TryGetValue(animator, out _)) _clipFor.AddOrUpdate(animator, _opened);
            }

            if (animator == null)
            {
                NoAnimator(content);
                return;
            }

            AnimationClip? clip = ClipOf(animator);
            if (clip == null)
            {
                EndPreview();
                NoClip(content, animator);
                return;
            }

            if (_fittedTo != clip) _fitted = false;
            if (_playing)
            {
                _time += Ui.DeltaTime;
                if (_time > clip.Length)
                {
                    if (clip.Loop && clip.Length > 0f) _time %= clip.Length;
                    else
                    {
                        _time = clip.Length;
                        _playing = false;
                    }
                }
            }

            // The controls go along the header, right of the tabs, or on a row of their own when that's too narrow.
            Rect bar = new(PanelFrame.TitleRight, rect.Y, rect.Right - PanelFrame.TitleRight, PanelFrame.HeaderHeight);
            if (bar.Width < 640f)
            {
                bar = content.TakeTop(34f).Shrink(4f, 0f);
                content = content.CutTop(32f);
            }

            DrawHeader(bar, animator, clip);
            Preview(animator, clip);
            DrawTimeline(content.Shrink(4f, 4f), animator, clip);
            HandleKeys(content, clip);
        }

        private static void Message(Rect content, string text, Action? button, string? buttonText = null, Icon? icon = null)
        {
            float y = content.Centre.Y - (button != null ? 30f : 10f);
            Ui.Text(new Rect(content.X + 20f, y, content.Width - 40f, 20f), text, Theme.TextDim, align: 0.5f);
            if (button == null) return;
            float width = Ui.TextWidth(buttonText!) + 56f;
            if (Widgets.Button(Ui.Id("animation.empty"), new Rect(content.Centre.X - width * 0.5f, y + 32f, width, 30f), buttonText!, ButtonStyle.Primary, icon: icon)) button();
        }

        private void NoAnimator(Rect content)
        {
            if (_context.Selection.Object is not { } selected)
            {
                Message(content, "Select an object to animate it, and the objects under it.", null);
                return;
            }

            Message(content, $"To animate {selected.Name} and what's under it, give it an Animator.", () =>
            {
                var added = new Animator { PlayOnStart = true };
                _context.History.Do(new AddComponent(selected, added));
            }, "Add Animator", Icons.Plus);
        }

        private void NoClip(Rect content, Animator animator)
        {
            if (_context.Assets is not { } assets)
            {
                Message(content, $"{animator.GameObject.Name} has no animation yet. Open a project to make animations for it.", null);
                return;
            }

            Message(content, $"{animator.GameObject.Name} has no animation yet.", () => NewClip(animator, assets), "New Animation", Icons.Plus);
        }

        /// <summary>Makes a new animation in Assets/Animations for <paramref name="animator"/>: what it plays, if it has nothing yet.</summary>
        private void NewClip(Animator animator, AssetDatabase assets)
        {
            try
            {
                string path = _context.CreateAnimation(Path.Combine(assets.Root, "Animations"), SafeName(animator.GameObject.Name));
                AnimationClip clip = Vertex.Assets.LoadAnimation(path);
                if (animator.Clip == null && animator.AnimationGraph == null)
                {
                    SetValue.Apply(_context.History, "Choose Animation", animator, typeof(Animator).GetProperty(nameof(Animator.Clip))!, clip);
                }

                Choose(animator, clip);
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't make the animation: {exception.Message}");
            }
        }

        /// <summary>A model's animation copied into a .anim file of its own, to change: chosen in its place.</summary>
        private void Duplicate(Animator animator, AnimationClip clip, AssetDatabase assets)
        {
            try
            {
                string folder = Path.Combine(assets.Root, "Animations");
                Directory.CreateDirectory(folder);
                string path = AssetsPanel.Unique(Path.Combine(folder, SafeName(clip.Name)), AnimationClip.Extension);
                clip.Copy().Save(path);
                assets.Refresh();
                AnimationClip copy = Vertex.Assets.LoadAnimation(path);
                if (animator.Clip == clip) SetValue.Apply(_context.History, "Choose Animation", animator, typeof(Animator).GetProperty(nameof(Animator.Clip))!, copy);
                Choose(animator, copy);
                _context.Notify($"Made {Path.GetFileName(path)} in Assets/Animations, to change");
            }
            catch (Exception exception)
            {
                _context.Notify($"Couldn't copy the animation: {exception.Message}");
            }
        }

        private static string SafeName(string name)
        {
            string safe = new(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
            return safe.Trim().Length > 0 ? safe.Trim() : "Animation";
        }

        private static string ClipLabel(AnimationClip clip) =>
            clip.IsImported && clip.Source is { } source ? $"{clip.Name}  ({Path.GetFileNameWithoutExtension(source[..source.LastIndexOf('#')])})" : clip.Name;

        private void DrawHeader(Rect bar, Animator animator, AnimationClip clip)
        {
            float x = bar.X + 4f;
            float y = bar.Centre.Y - 13f;

            // Which animation: its own, its graph's, or a new one.
            List<AnimationClip> clips = ClipsOf(animator);
            var names = clips.Select(ClipLabel).ToList();
            bool canMake = _context.Assets != null;
            if (canMake) names.Add("New Animation…");
            var choose = new Rect(x, y, 190f, 26f);
            if (Widgets.Dropdown(Ui.Id("animation.clip"), choose, names, clips.IndexOf(clip), "Which animation to show") is int picked)
            {
                if (picked < clips.Count) Choose(animator, clips[picked]);
                else if (_context.Assets is { } assets) NewClip(animator, assets);
            }

            x = choose.Right + 10f;
            bool readOnly = clip.IsImported;
            var record = new Rect(x, y, 26f, 26f);
            if (Widgets.IconButton(Ui.Id("animation.record"), record, Icons.Record, readOnly ? "A model's own animation can't be recorded into: duplicate it first" : "Record: changes made to the objects become keys at the playhead",
                    selected: _recording, color: _recording || !readOnly ? Theme.Danger : Theme.TextFaint) && !readOnly)
            {
                FinishRecording();
                _recording = !_recording;
            }

            var start = new Rect(record.Right + 4f, y, 26f, 26f);
            if (Widgets.IconButton(Ui.Id("animation.start"), start, Icons.ToStart, "Back to the start"))
            {
                _time = 0f;
                _playing = false;
            }

            var play = new Rect(start.Right + 4f, y, 26f, 26f);
            if (Widgets.IconButton(Ui.Id("animation.play"), play, _playing ? Icons.Pause : Icons.Play, _playing ? "Pause" : "Play the animation here", selected: _playing))
            {
                _playing = !_playing;
                if (_playing && _time >= clip.Length) _time = 0f;
            }

            var time = new Rect(play.Right + 8f, y, 64f, 26f);
            float now = _time;
            if (NumberField.Float(Ui.Id("animation.time"), time, ref now, speed: 0.01f, format: "0.00", min: 0f))
            {
                _time = MathF.Max(0f, now);
                _playing = false;
            }

            Ui.Tooltip(Ui.Id("animation.time"), "Where the playhead is, in seconds");
            x = time.Right + 18f;

            // How long it is and whether it loops: a model's own can't be changed.
            Ui.Text(new Rect(x, y, 50f, 26f), "Length", Theme.TextDim, size: Theme.SmallTextSize);
            var length = new Rect(x + 44f, y, 56f, 26f);
            float seconds = clip.Length;
            if (!readOnly && NumberField.Float(Ui.Id("animation.length"), length, ref seconds, speed: 0.01f, format: "0.00", min: 0.01f))
            {
                ClipChange.Make(_context.History, clip, "Change Length", () => clip.Length = MathF.Max(0.01f, seconds), (clip, "length"));
            }
            else if (readOnly)
            {
                Ui.Rect(length, Theme.Field, Theme.ControlRadius);
                Ui.Text(length.Shrink(8f, 0f), clip.Length.ToString("0.00", CultureInfo.InvariantCulture), Theme.TextFaint);
            }

            var loop = new Rect(length.Right + 12f, y + 4f, 18f, 18f);
            bool loops = clip.Loop;
            if (!readOnly && Widgets.Checkbox(Ui.Id("animation.loop"), loop, ref loops, "Start again from the beginning when it reaches the end"))
            {
                ClipChange.Make(_context.History, clip, loops ? "Loop" : "Don't Loop", () => clip.Loop = loops);
            }
            else if (readOnly)
            {
                Ui.Outline(loop, Theme.Border, 4f);
            }

            Ui.Text(new Rect(loop.Right + 6f, y, 40f, 26f), "Loop", Theme.TextDim, size: Theme.SmallTextSize);

            // On the right: add a key to everything the selected object has tracks for, or copy a model's animation to change.
            var right = new Rect(bar.Right - Theme.Padding - 26f, y, 26f, 26f);
            if (readOnly)
            {
                if (_context.Assets is { } assets)
                {
                    float width = Ui.TextWidth("Duplicate to Change") + 40f;
                    if (Widgets.Button(Ui.Id("animation.duplicate"), new Rect(bar.Right - Theme.Padding - width, y, width, 26f), "Duplicate to Change",
                            tooltip: "A model's own animation can't be changed: copy it into a .anim file of your own", icon: Icons.Plus))
                    {
                        Duplicate(animator, clip, assets);
                    }
                }
            }
            else if (Widgets.IconButton(Ui.Id("animation.key"), right, (c, s, col) => Icons.Key(c, s * 0.3f, col), "Add a key at the playhead to each of the selected object's tracks"))
            {
                KeySelectedObject(animator, clip);
            }
        }

        /// <summary>A key at the playhead on each track of the selected object (every track, if the animator's own is selected), at its value now.</summary>
        private void KeySelectedObject(Animator animator, AnimationClip clip)
        {
            GameObject? selected = _context.Selection.Object;
            string? path = selected != null ? AnimationRig.PathOf(animator.Transform, selected.Transform) : null;
            ClipChange.Make(_context.History, clip, "Add Keys", () =>
            {
                foreach (AnimationTrack track in clip.Tracks)
                {
                    if (path is { Length: > 0 } && track.Path != path) continue;
                    if (AnimationRig.Find(animator.Transform, track.Path) is not { } transform) continue;
                    Vector4 value = AnimationRig.Read(transform.GameObject, track.Property) ?? track.Sample(_time);
                    if (track.Property == "EulerAngles") value = new Vector4(Nearest(AsVector3(value), AsVector3(track.Sample(_time))), 0f);
                    track.SetKey(Snap(clip, _time), value);
                }
            });
        }

        // ---------------------------------------------------------------- The timeline

        private float X(Rect lane, float time) => lane.X + _laneMargin + (time - _viewStart) * _pixelsPerSecond;
        private float TimeAt(Rect lane, float x) => _viewStart + (x - lane.X - _laneMargin) / _pixelsPerSecond;

        /// <summary>A time on the clip's frames (unless Shift is held, for in between).</summary>
        private static float Snap(AnimationClip clip, float time)
        {
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (shift || clip.FrameRate <= 0f) return MathF.Max(0f, time);
            return MathF.Max(0f, MathF.Round(time * clip.FrameRate) / clip.FrameRate);
        }

        private void DrawTimeline(Rect body, Animator animator, AnimationClip clip)
        {
            Rect list = body.TakeLeft(_listWidth);
            Rect lane = body.CutLeft(_listWidth, 1f);
            if (lane.Width < 40f) return;

            // Fits the whole clip to start with.
            if (!_fitted)
            {
                _pixelsPerSecond = (lane.Width - _laneMargin * 2f) / MathF.Max(clip.Length, 0.25f);
                _viewStart = 0f;
                _fitted = true;
                _fittedTo = clip;
            }

            _keysOnScreen.Clear();
            Ui.Rect(lane, Theme.Field, 6f);
            if (_recording) Ui.Outline(lane.Grow(1f), Theme.Danger.WithAlpha(0.8f), 7f, 1.5f);

            Rect ruler = lane.TakeTop(_rulerHeight);
            Rect rows = new(body.X, ruler.Bottom + 2f, body.Width, body.Bottom - ruler.Bottom - 2f);
            Rect laneRows = new(lane.X, rows.Y, lane.Width, rows.Height);

            ZoomAndPan(lane, clip);
            DrawAddProperty(list.TakeTop(_rulerHeight), animator, clip);

            // The rows: every key together, the events, then a row for each track.
            List<AnimationTrack> tracks = clip.Tracks.OrderBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.Property, StringComparer.Ordinal).ToList();
            int scrollId = Ui.Id("animation.rows");
            ScrollView scroll = ScrollView.Begin(scrollId, rows);
            Ui.PushClip(rows);

            float top = scroll.Top;
            DrawSummaryRow(new Rect(list.X, top, list.Width, _rowHeight), laneRows with { Y = top, Height = _rowHeight }, clip, tracks);
            top += _rowHeight;
            DrawEventsRow(new Rect(list.X, top, list.Width, _rowHeight), laneRows with { Y = top, Height = _rowHeight }, clip);
            top += _rowHeight;

            string? lastPath = null;
            foreach (AnimationTrack track in tracks)
            {
                var label = new Rect(list.X, top, list.Width, _rowHeight);
                var row = new Rect(lane.X, top, lane.Width, _rowHeight);
                if (label.Bottom >= rows.Y && label.Y <= rows.Bottom) DrawTrack(label, row, animator, clip, track, track.Path != lastPath);
                lastPath = track.Path;
                top += _rowHeight;
            }

            if (tracks.Count == 0)
            {
                string hint = clip.IsImported ? "This animation has no tracks." : "Turn on Record and move the objects, or add a property, to start animating.";
                Ui.Text(new Rect(list.X + 8f, top + 4f, body.Width - 16f, 20f), hint, Theme.TextFaint, size: Theme.SmallTextSize);
            }

            Ui.PopClip();
            scroll.End((tracks.Count + 2) * _rowHeight + 24f);

            DrawRuler(ruler, clip);
            DrawPlayhead(lane, ruler, clip);
            FinishDrag(clip);
        }

        private void ZoomAndPan(Rect lane, AnimationClip clip)
        {
            if (!lane.Contains(Ui.Mouse)) return;

            float wheel = InputSystem.ScrollDelta;
            bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (wheel != 0f && ctrl)
            {
                // Zooms round the time under the mouse.
                float at = TimeAt(lane, Ui.Mouse.X);
                _pixelsPerSecond = Math.Clamp(_pixelsPerSecond * MathF.Pow(1.15f, wheel), 20f, 4000f);
                _viewStart = at - (Ui.Mouse.X - lane.X - _laneMargin) / _pixelsPerSecond;
            }
            else if (wheel != 0f && shift)
            {
                _viewStart -= wheel * 40f / _pixelsPerSecond;
            }

            if (Ui.MouseHeld(MouseButton.Middle)) _viewStart -= Ui.MouseDelta.X / _pixelsPerSecond;
            _viewStart = MathF.Max(_viewStart, -_laneMargin / _pixelsPerSecond);
        }

        private void DrawRuler(Rect ruler, AnimationClip clip)
        {
            Ui.Rect(ruler, Theme.PanelRaised, 6f);

            // Labelled every so often (at least 60 pixels apart), with smaller ticks between.
            float[] steps = [0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f];
            float step = steps.FirstOrDefault(s => s * _pixelsPerSecond >= 60f, 60f);
            float minor = step / 5f;
            float first = MathF.Floor(TimeAt(ruler, ruler.X) / minor) * minor;

            Ui.PushClip(ruler);
            for (float t = MathF.Max(first, 0f); X(ruler, t) < ruler.Right; t += minor)
            {
                float x = X(ruler, t);
                bool major = MathF.Abs(t / step - MathF.Round(t / step)) < 0.001f;
                Ui.Line(new Vector2(x, ruler.Bottom - (major ? 9f : 4f)), new Vector2(x, ruler.Bottom), major ? Theme.TextDim : Theme.TextFaint.WithAlpha(0.6f), 1f);
                if (major) Ui.Text(t.ToString(step < 0.1f ? "0.00" : step < 1f ? "0.0#" : "0.#", CultureInfo.InvariantCulture), new Vector2(x + 3f, ruler.Y + 3f), Theme.TextFaint, Theme.SmallTextSize);
            }

            // Past the end, dimmed.
            float end = X(ruler, clip.Length);
            if (end < ruler.Right) Ui.Rect(new Rect(end, ruler.Y, ruler.Right - end, ruler.Height), Theme.Background.WithAlpha(0.45f));
            Ui.PopClip();

            // Pressed and dragged along, it scrubs.
            int id = Ui.Id("animation.ruler");
            Interaction interaction = Ui.Interact(id, ruler);
            if (interaction.Pressed || interaction.Held)
            {
                _time = Snap(clip, TimeAt(ruler, Ui.Mouse.X));
                _playing = false;
                Ui.Cursor = CursorShape.ResizeHorizontal;
            }

            Ui.Tooltip(id, "Drag to move the playhead (hold Shift to go between frames). Ctrl+wheel zooms, Shift+wheel or the middle button scrolls");
        }

        private void DrawPlayhead(Rect lane, Rect ruler, AnimationClip clip)
        {
            float x = X(lane, _time);
            if (x < lane.X || x > lane.Right) return;
            Color color = _recording ? Theme.Danger : Theme.Accent;
            Ui.Line(new Vector2(x, ruler.Y + 2f), new Vector2(x, lane.Bottom - 2f), color, 1.5f);

            string text = _time.ToString("0.00", CultureInfo.InvariantCulture);
            float width = Ui.TextWidth(text, Theme.SmallTextSize) + 10f;
            var pill = new Rect(x - width * 0.5f, ruler.Y + 2f, width, 16f);
            Ui.Rect(pill, color, 4f);
            Ui.Text(pill, text, Color.White, align: 0.5f, size: Theme.SmallTextSize);
        }

        private void DrawAddProperty(Rect area, Animator animator, AnimationClip clip)
        {
            var button = new Rect(area.X, area.Y + 1f, area.Width - 4f, area.Height - 2f);
            int id = Ui.Id("animation.addProperty");
            if (clip.IsImported)
            {
                Ui.Text(button.Shrink(8f, 0f), "A model's own animation", Theme.TextFaint, size: Theme.SmallTextSize);
                return;
            }

            if (!Widgets.Button(id, button, "Add Property", ButtonStyle.Subtle, "Animate one of the selected object's values", Icons.Plus)) return;

            GameObject target = _context.Selection.Object ?? animator.GameObject;
            string? path = AnimationRig.PathOf(animator.Transform, target.Transform);
            if (path == null) return;

            var items = new List<MenuItem>();
            foreach ((string property, int size, string label) in PropertiesOf(target))
            {
                bool has = clip.Track(path, property) != null || property == "EulerAngles" && clip.Track(path, "Rotation") != null;
                items.Add(new MenuItem(label, () => ClipChange.Make(_context.History, clip, $"Animate {label}", () =>
                {
                    AnimationTrack track = clip.AddTrack(path, property, size);
                    if (AnimationRig.Read(target, property) is { } value) track.SetKey(Snap(clip, _time), value);
                })) { Enabled = !has });
            }

            Menu.Open(id, new Vector2(button.X, button.Bottom + 3f), items, button.Width);
        }

        private void DrawTrack(Rect label, Rect row, Animator animator, AnimationClip clip, AnimationTrack track, bool firstOfObject)
        {
            int id = Ui.Id(RuntimeHelpers.GetHashCode(track));
            Interaction hover = Ui.Interact(Ui.Id(id, "label"), label);
            Transform? transform = AnimationRig.Find(animator.Transform, track.Path);
            bool selectedObject = transform != null && _context.Selection.Contains(transform.GameObject);
            if (selectedObject) Ui.Rect(label.Shrink(2f, 1f), Theme.Selection.WithAlpha(0.6f), 4f);
            else if (hover.Hovered) Ui.Rect(label.Shrink(2f, 1f), Theme.Hover, 4f);

            // The object's name on its first row; its other rows just the property.
            string objectName = ObjectLabel(animator, track.Path);
            Color nameColor = transform == null ? Theme.Danger : firstOfObject ? Theme.Text : Theme.TextFaint;
            float x = label.X + 8f;
            if (firstOfObject || transform == null)
            {
                Ui.Text(new Rect(x, label.Y, 110f, label.Height), Ui.Fit(objectName, 104f), nameColor);
                x += MathF.Min(Ui.TextWidth(objectName), 104f) + 8f;
            }
            else
            {
                x += 12f;
            }

            Ui.Text(new Rect(x, label.Y, label.Right - x - 26f, label.Height), PropertyLabel(track.Property), Theme.TextDim, size: Theme.SmallTextSize);
            if (transform == null) Ui.Tooltip(Ui.Id(id, "label"), $"There's no object at {track.Path} under {animator.GameObject.Name}: this track does nothing");
            if (hover.Clicked && transform != null) _context.Selection.Select(transform.GameObject);

            if (!clip.IsImported && (hover.Hovered || Ui.HotId == Ui.Id(id, "remove")))
            {
                var remove = new Rect(label.Right - 24f, label.Y + 2f, 20f, 20f);
                if (Widgets.IconButton(Ui.Id(id, "remove"), remove, Icons.Close, "Stop animating this"))
                {
                    ClipChange.Make(_context.History, clip, "Remove Track", () => clip.Tracks.Remove(track));
                    _selected.RemoveAll(k => k.Track == track);
                }
            }

            // The lane: a faint band, then the keys.
            Ui.Rect(row.Shrink(0f, 1f), Theme.Hover.WithAlpha(0.6f));
            Interaction lane = Ui.Interact(Ui.Id(id, "lane"), row, rightClick: true);
            for (int i = 0; i < track.Keys.Count; i++)
            {
                Keyframe key = track.Keys[i];
                float kx = X(row, key.Time);
                if (kx < row.X - 8f || kx > row.Right + 8f) continue;
                _keysOnScreen[(track, i)] = new Vector2(kx, row.Centre.Y);
                bool picked = IsSelected(track, key.Time);
                DrawKey(new Vector2(kx, row.Centre.Y), key.Interpolation, picked, Ui.Id(id, i), clip, [(track, key.Time)]);
            }

            if (lane.DoubleClicked && !clip.IsImported)
            {
                float time = Snap(clip, TimeAt(row, Ui.Mouse.X));
                ClipChange.Make(_context.History, clip, "Add Key", () => track.SetKey(time, track.Sample(time)));
            }
            else if (lane.Pressed && Ui.HotId == Ui.Id(id, "lane"))
            {
                _selected.Clear();
                _selectedEvent = null;
            }
        }

        private bool IsSelected(AnimationTrack track, float time) => _selected.Any(k => k.Track == track && MathF.Abs(k.Time - time) < 0.0005f);

        /// <summary>A key's diamond: picked when clicked (Shift adds to what's picked), dragged along with the others picked, right-clicked for how it goes on.</summary>
        private void DrawKey(Vector2 centre, KeyInterpolation interpolation, bool picked, int id, AnimationClip clip, List<(AnimationTrack Track, float Time)> keys)
        {
            var hit = new Rect(centre.X - 7f, centre.Y - 9f, 14f, 18f);
            Interaction interaction = Ui.Interact(id, hit, rightClick: true);
            Color color = picked ? Theme.AccentText : interaction.Hovered ? Theme.Text : Theme.TextDim;
            switch (interpolation)
            {
                case KeyInterpolation.Step:
                    Ui.Rect(new Rect(centre.X - 4.5f, centre.Y - 4.5f, 9f, 9f), color, 1f);
                    break;
                case KeyInterpolation.Linear:
                    Icons.Key(centre, 6f, color);
                    break;
                default:
                    Ui.Circle(centre, 5f, color);
                    break;
            }

            if (picked) Ui.Circle(centre, 2f, Theme.Field);

            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (interaction.Pressed)
            {
                bool allPicked = keys.All(k => IsSelected(k.Track, k.Time));
                if (shift && allPicked) _selected.RemoveAll(s => keys.Any(k => k.Track == s.Track && MathF.Abs(k.Time - s.Time) < 0.0005f));
                else
                {
                    if (!shift && !allPicked) _selected.Clear();
                    foreach ((AnimationTrack track, float time) in keys)
                    {
                        if (!IsSelected(track, time)) _selected.Add((track, time));
                    }
                }

                _selectedEvent = null;
                if (!clip.IsImported && _selected.Count > 0) BeginDrag(clip);
            }

            if (interaction.RightClicked && !clip.IsImported)
            {
                if (!keys.All(k => IsSelected(k.Track, k.Time)))
                {
                    _selected.Clear();
                    _selected.AddRange(keys);
                }

                KeyInterpolation now = interpolation;
                Menu.Open(id, Ui.Mouse,
                [
                    new MenuItem("Smooth", () => SetInterpolation(clip, KeyInterpolation.Smooth)) { Checked = now == KeyInterpolation.Smooth },
                    new MenuItem("Linear", () => SetInterpolation(clip, KeyInterpolation.Linear)) { Checked = now == KeyInterpolation.Linear },
                    new MenuItem("Step", () => SetInterpolation(clip, KeyInterpolation.Step)) { Checked = now == KeyInterpolation.Step },
                    MenuItem.Separator,
                    new MenuItem(_selected.Count == 1 ? "Delete Key" : "Delete Keys", () => DeleteSelected(clip)) { Shortcut = "Del", Icon = Icons.Trash }
                ]);
            }

            Ui.Tooltip(id, interpolation switch
            {
                KeyInterpolation.Step => "A key that holds until the next. Right-click to change how it goes on",
                KeyInterpolation.Linear => "A key that goes straight to the next. Right-click to change how it goes on",
                _ => "A key that eases into the next. Right-click to change how it goes on"
            });
        }

        private void SetInterpolation(AnimationClip clip, KeyInterpolation interpolation)
        {
            ClipChange.Make(_context.History, clip, $"Make Keys {interpolation}", () =>
            {
                foreach ((AnimationTrack track, float time) in _selected)
                {
                    int index = track.Find(time);
                    if (index >= 0) track.Keys[index] = track.Keys[index] with { Interpolation = interpolation };
                }
            });
        }

        private void DeleteSelected(AnimationClip clip)
        {
            if (clip.IsImported) return;
            if (_selectedEvent is { } picked)
            {
                ClipChange.Make(_context.History, clip, "Delete Event", () => clip.Events.Remove(picked));
                _selectedEvent = null;
                return;
            }

            if (_selected.Count == 0) return;
            ClipChange.Make(_context.History, clip, _selected.Count == 1 ? "Delete Key" : "Delete Keys", () =>
            {
                foreach ((AnimationTrack track, float time) in _selected) track.RemoveKey(time);
            });
            _selected.Clear();
        }

        private void DrawSummaryRow(Rect label, Rect row, AnimationClip clip, List<AnimationTrack> tracks)
        {
            Ui.Text(label.Shrink(8f, 0f), "All", Theme.TextDim, size: Theme.SmallTextSize, bold: true);
            Ui.Rect(row.Shrink(0f, 1f), Theme.Hover.WithAlpha(1.2f));

            // A key wherever any track has one: picking it picks all of them at that time.
            var times = tracks.SelectMany(t => t.Keys.Select(k => k.Time)).Distinct().OrderBy(t => t).ToList();
            for (int i = 0; i < times.Count; i++)
            {
                float time = times[i];
                if (i > 0 && time - times[i - 1] < 0.0005f) continue;
                float x = X(row, time);
                if (x < row.X - 8f || x > row.Right + 8f) continue;
                var keys = tracks.Where(t => t.Find(time) >= 0).Select(t => (t, t.Keys[t.Find(time)].Time)).ToList();
                bool picked = keys.All(k => IsSelected(k.Item1, k.Item2));
                DrawKey(new Vector2(x, row.Centre.Y), KeyInterpolation.Linear, picked, Ui.Id(Ui.Id("animation.summary"), i), clip, keys);
            }
        }

        private void DrawEventsRow(Rect label, Rect row, AnimationClip clip)
        {
            int id = Ui.Id("animation.events");
            if (_selectedEvent is { } picked && clip.Events.Contains(picked) && !clip.IsImported)
            {
                // The picked event's name, to change.
                string name = picked.Name;
                var field = new Rect(label.X + 4f, label.Y + 1f, label.Width - 8f, label.Height - 2f);
                if (TextInput.Draw(Ui.Id(id, "name"), field, ref name, "Event name") && name != picked.Name)
                {
                    ClipChange.Make(_context.History, clip, "Rename Event", () => picked.Name = name.Trim());
                }
            }
            else
            {
                Ui.Text(label.Shrink(8f, 0f), "Events", Theme.TextDim, size: Theme.SmallTextSize, bold: true);
            }

            Ui.Rect(row.Shrink(0f, 1f), Theme.Hover.WithAlpha(0.6f));
            Interaction lane = Ui.Interact(Ui.Id(id, "lane"), row);
            Ui.Tooltip(Ui.Id(id, "lane"), "Names sent as the animation passes them (footsteps, a sword's swing): visual scripts on the object hear them as events. Double-click to add one");

            for (int i = 0; i < clip.Events.Count; i++)
            {
                AnimationEvent animationEvent = clip.Events[i];
                float x = X(row, animationEvent.Time);
                if (x < row.X - 8f || x > row.Right + 60f) continue;
                int eventId = Ui.Id(id, i);
                var hit = new Rect(x - 5f, row.Y + 2f, MathF.Max(Ui.TextWidth(animationEvent.Name, Theme.SmallTextSize) + 16f, 12f), row.Height - 4f);
                Interaction interaction = Ui.Interact(eventId, hit);
                bool isPicked = animationEvent == _selectedEvent;
                Color color = isPicked ? Theme.AccentText : interaction.Hovered ? Theme.Text : Theme.Warning;
                Ui.Rect(new Rect(x - 1f, row.Y + 3f, 2f, row.Height - 6f), color);
                Vertex.UI.Canvas.DrawTriangle(new Vector2(x + 1f, row.Y + 3f), new Vector2(x + 8f, row.Y + 7f), new Vector2(x + 1f, row.Y + 11f), Ui.Fade(color));
                Ui.Text(animationEvent.Name, new Vector2(x + 11f, row.Y + 5f), color.WithAlpha(0.9f), Theme.SmallTextSize);

                if (interaction.Pressed)
                {
                    _selectedEvent = animationEvent;
                    _selected.Clear();
                    if (!clip.IsImported)
                    {
                        _drag = new Drag { StartX = Ui.Mouse.X, Before = clip.ToJson(), EventStart = animationEvent.Time };
                    }
                }
            }

            if (lane.DoubleClicked && !clip.IsImported)
            {
                float time = Snap(clip, TimeAt(row, Ui.Mouse.X));
                var added = new AnimationEvent(time, "Event");
                ClipChange.Make(_context.History, clip, "Add Event", () => clip.Events.Add(added));
                _selectedEvent = added;
                TextInput.Focus(Ui.Id(id, "name"), selectAll: true);
            }
            else if (lane.Pressed && Ui.HotId == Ui.Id(id, "lane"))
            {
                _selectedEvent = null;
                _selected.Clear();
            }
        }

        // ---------------------------------------------------------------- Dragging keys

        private void BeginDrag(AnimationClip clip)
        {
            var drag = new Drag { StartX = Ui.Mouse.X, Before = clip.ToJson() };
            foreach ((AnimationTrack track, float time) in _selected)
            {
                if (!drag.Tracks.TryGetValue(track, out var entry))
                {
                    entry = (track.Keys.ToList(), new HashSet<int>());
                    drag.Tracks[track] = entry;
                }

                int index = entry.Original.FindIndex(k => MathF.Abs(k.Time - time) < 0.0005f);
                if (index >= 0) entry.Moving.Add(index);
            }

            _drag = drag;
        }

        /// <summary>Moves what's being dragged as the mouse moves; when it's let go, the move is one undo.</summary>
        private void FinishDrag(AnimationClip clip)
        {
            if (_drag is not { } drag) return;

            if (Ui.MouseHeld())
            {
                float moved = (Ui.Mouse.X - drag.StartX) / _pixelsPerSecond;
                if (MathF.Abs(Ui.Mouse.X - drag.StartX) < 3f) moved = 0f;
                if (moved == drag.Moved) return;
                drag.Moved = moved;
                Ui.Cursor = CursorShape.ResizeHorizontal;

                if (_selectedEvent is { } dragged && drag.Tracks.Count == 0)
                {
                    dragged.Time = Snap(clip, drag.EventStart + moved);
                    clip.Changed();
                    return;
                }

                // Each track's keys again from how they were, the moving ones shifted (a moved key lands on a still one in its place).
                _selected.Clear();
                foreach ((AnimationTrack track, (List<Keyframe> original, HashSet<int> moving)) in drag.Tracks)
                {
                    var shifted = moving.Select(i => original[i] with { Time = Snap(clip, original[i].Time + moved) }).ToList();
                    track.Keys.Clear();
                    for (int i = 0; i < original.Count; i++)
                    {
                        if (!moving.Contains(i) && shifted.All(s => MathF.Abs(s.Time - original[i].Time) > 0.0005f)) track.Keys.Add(original[i]);
                    }

                    foreach (Keyframe key in shifted)
                    {
                        track.Keys.RemoveAll(k => MathF.Abs(k.Time - key.Time) < 0.0005f);
                        track.Keys.Add(key);
                        _selected.Add((track, key.Time));
                    }

                    track.Sort();
                }

                clip.Changed();
                return;
            }

            _drag = null;
            string after = clip.ToJson();
            if (after != drag.Before) _context.History.Record(new ClipChange(clip, drag.Tracks.Count > 0 ? "Move Keys" : "Move Event", drag.Before, after));
        }

        private void HandleKeys(Rect content, AnimationClip clip)
        {
            if (Ui.KeyboardTaken || !content.Contains(Ui.Mouse)) return;
            if (InputSystem.GetKeyDown(Key.Delete) || InputSystem.GetKeyDown(Key.Backspace))
            {
                if (_selected.Count == 0 && _selectedEvent == null) return;
                InputSystem.Consume(Key.Delete);
                InputSystem.Consume(Key.Backspace);
                DeleteSelected(clip);
            }

            if (InputSystem.GetKeyDown(Key.Space))
            {
                InputSystem.Consume(Key.Space);
                _playing = !_playing;
                if (_playing && _time >= clip.Length) _time = 0f;
            }
        }
    }
}
