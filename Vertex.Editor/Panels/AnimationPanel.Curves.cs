using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Animation;
using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The Animation panel's curves: each track's values drawn as lines over time (x, y and z each in their own colour),
    /// to shape. Keys are dragged in time and value; a picked smooth key has handles either side, dragged to set how
    /// steeply the curve arrives and leaves (both together, or one side alone with Alt held). Right-click a key for how it
    /// goes on, or to put its slopes back to worked out.
    /// </summary>
    public sealed partial class AnimationPanel
    {
        private const float _handleLength = 42f;

        private bool _curves;

        // The value axis: the value at the middle of the graph, and how many pixels a unit.
        private float _valueCentre;
        private float _pixelsPerUnit = 40f;
        private bool _curvesFitted;

        // The tracks hidden from the graph, by object and property (so they stay hidden through an undo).
        private readonly HashSet<(string Path, string Property)> _hiddenCurves = new();

        // The keys picked on the graph: a key's one value (its channel) on a track.
        private readonly List<(AnimationTrack Track, float Time, int Channel)> _curveKeys = new();

        // A drag on the graph: keys, or one key's slope handle.
        private sealed class CurveDrag
        {
            public Vector2 Start;
            public string Before = "";
            public Dictionary<AnimationTrack, (List<Keyframe> Original, Dictionary<int, HashSet<int>> Moving)> Tracks = new();
            public (AnimationTrack Track, int Index, int Channel, bool Out)? Handle;
            public bool Moved;
        }

        private CurveDrag? _curveDrag;

        // Where keys and handles were drawn this frame: for tests.
        private readonly Dictionary<(AnimationTrack, int, int), Vector2> _curveKeysOnScreen = new();
        private readonly Dictionary<(AnimationTrack, int, int, bool), Vector2> _handlesOnScreen = new();

        internal Vector2? CurveKeyOnScreen(AnimationTrack track, int index, int channel) =>
            _curveKeysOnScreen.TryGetValue((track, index, channel), out Vector2 at) ? at : null;

        internal Vector2? HandleOnScreen(AnimationTrack track, int index, int channel, bool outgoing) =>
            _handlesOnScreen.TryGetValue((track, index, channel, outgoing), out Vector2 at) ? at : null;

        /// <summary>Whether it shows curves instead of rows of keys.</summary>
        internal bool Curves
        {
            get => _curves;
            set
            {
                if (_curves == value) return;
                _curves = value;
                _curveKeys.Clear();
                _curveDrag = null;
                _curvesFitted = false;
            }
        }

        private void ForgetCurves()
        {
            _curveKeys.Clear();
            _curveDrag = null;
            _curvesFitted = false;
        }

        private float ValueY(Rect graph, float value) => graph.Centre.Y - (value - _valueCentre) * _pixelsPerUnit;
        private float ValueAt(Rect graph, float y) => _valueCentre - (y - graph.Centre.Y) / _pixelsPerUnit;

        /// <summary>The colour of a track's value: x, y and z (or red, green and blue) their own, a single number the accent.</summary>
        private static Color ChannelColor(AnimationTrack track, int channel)
        {
            if (track.Size <= 1) return Theme.Accent;
            return channel switch
            {
                0 => new Color(0.92f, 0.38f, 0.38f),
                1 => new Color(0.46f, 0.82f, 0.38f),
                2 => new Color(0.38f, 0.6f, 0.98f),
                _ => new Color(0.86f, 0.76f, 0.34f)
            };
        }

        private static string ChannelName(AnimationTrack track, int channel)
        {
            if (track.Size <= 1) return "";
            bool color = track.Property.EndsWith("Color", StringComparison.Ordinal) || track.Size == 4 && track.Property != "Rotation";
            return color ? "RGBA"[channel].ToString() : "XYZW"[channel].ToString();
        }

        private static int Channels(AnimationTrack track) => Math.Clamp(track.Size, 1, 4);

        private bool Shown(AnimationTrack track) => !_hiddenCurves.Contains((track.Path, track.Property));

        // ---------------------------------------------------------------- Drawing

        private void DrawCurves(Rect list, Rect graph, Animator animator, AnimationClip clip, List<AnimationTrack> tracks)
        {
            _curveKeysOnScreen.Clear();
            _handlesOnScreen.Clear();
            _curveKeys.RemoveAll(k => !clip.Tracks.Contains(k.Track) || k.Track.Find(k.Time) < 0);

            DrawCurveList(list, animator, clip, tracks);

            List<AnimationTrack> shown = tracks.Where(Shown).ToList();
            if (!_curvesFitted)
            {
                FitValues(graph, shown, clip);
                _curvesFitted = true;
            }

            Ui.PushClip(graph);
            ZoomValues(graph);
            DrawValueGrid(graph, clip);

            int id = Ui.Id("animation.curves.graph");
            Interaction background = Ui.Interact(id, graph, rightClick: true);

            foreach (AnimationTrack track in shown)
            {
                for (int c = 0; c < Channels(track); c++) DrawCurve(graph, track, c);
            }

            foreach (AnimationTrack track in shown) DrawCurveKeys(graph, clip, track);
            DrawHandles(graph, clip);

            if (shown.Count == 0)
            {
                string hint = tracks.Count == 0 ? "Nothing's animated yet: record some keys, or add a property." : "Every track is hidden: click one on the left to show it.";
                Ui.Text(new Rect(graph.X, graph.Y + 20f, graph.Width, 20f), hint, Theme.TextFaint, align: 0.5f, size: Theme.SmallTextSize);
            }

            if (background.DoubleClicked && !clip.IsImported) AddKeyOnCurve(graph, clip, shown);
            else if (background.Pressed && Ui.HotId == id) _curveKeys.Clear();
            Ui.Tooltip(id, "Drag keys to change them. Wheel zooms the values, Ctrl+wheel the time; the middle button moves round. F fits it all in. Double-click a curve to add a key");

            Ui.PopClip();
            FinishCurveDrag(graph, clip);
        }

        /// <summary>The tracks down the left, each with its colours: click one to show or hide its curves.</summary>
        private void DrawCurveList(Rect list, Animator animator, AnimationClip clip, List<AnimationTrack> tracks)
        {
            ScrollView scroll = ScrollView.Begin(Ui.Id("animation.curves.list"), list);
            Ui.PushClip(list);
            float top = scroll.Top;
            string? lastPath = null;
            foreach (AnimationTrack track in tracks)
            {
                var row = new Rect(list.X, top, list.Width, _rowHeight);
                top += _rowHeight;
                bool first = track.Path != lastPath;
                lastPath = track.Path;
                if (row.Bottom < list.Y || row.Y > list.Bottom) continue;

                int id = Ui.Id(Ui.Id("animation.curves.track"), RuntimeHelpers.GetHashCode(track));
                Interaction interaction = Ui.Interact(id, row);
                bool shown = Shown(track);
                if (interaction.Hovered) Ui.Rect(row.Shrink(2f, 1f), Theme.Hover, 4f);

                float x = row.X + 8f;
                if (first)
                {
                    string name = ObjectLabel(animator, track.Path);
                    Ui.Text(new Rect(x, row.Y, 104f, row.Height), Ui.Fit(name, 98f), shown ? Theme.Text : Theme.TextFaint);
                    x += MathF.Min(Ui.TextWidth(name), 98f) + 8f;
                }
                else
                {
                    x += 12f;
                }

                Ui.Text(new Rect(x, row.Y, row.Right - x - 50f, row.Height), PropertyLabel(track.Property), shown ? Theme.TextDim : Theme.TextFaint, size: Theme.SmallTextSize);

                // A dot a channel, in its colour (hollow while hidden).
                for (int c = Channels(track) - 1, i = 0; c >= 0; c--, i++)
                {
                    var dot = new Vector2(row.Right - 10f - i * 11f, row.Centre.Y);
                    if (shown) Ui.Circle(dot, 3.5f, ChannelColor(track, c));
                    else Vertex.UI.Canvas.DrawCircleOutline(dot, 3.5f, Ui.Fade(ChannelColor(track, c).WithAlpha(0.6f)), 1f);
                }

                if (interaction.Clicked)
                {
                    if (!_hiddenCurves.Remove((track.Path, track.Property))) _hiddenCurves.Add((track.Path, track.Property));
                    _curveKeys.RemoveAll(k => k.Track == track);
                }

                Ui.Tooltip(id, shown ? "Hide its curves" : "Show its curves");
            }

            Ui.PopClip();
            scroll.End(tracks.Count * _rowHeight + 12f);
        }

        /// <summary>Fits the value axis round every curve shown, with a little room above and below.</summary>
        private void FitValues(Rect graph, List<AnimationTrack> shown, AnimationClip clip)
        {
            float low = float.MaxValue, high = float.MinValue;
            foreach (AnimationTrack track in shown)
            {
                for (int c = 0; c < Channels(track); c++)
                {
                    foreach (Keyframe key in track.Keys)
                    {
                        low = MathF.Min(low, key.Value[c]);
                        high = MathF.Max(high, key.Value[c]);
                    }

                    // Between keys too, for set slopes that bulge past them.
                    for (int i = 0; i <= 40; i++)
                    {
                        float value = track.Sample(clip.Length * i / 40f)[c];
                        low = MathF.Min(low, value);
                        high = MathF.Max(high, value);
                    }
                }
            }

            if (low > high)
            {
                low = -1f;
                high = 1f;
            }

            float span = MathF.Max(high - low, 0.5f);
            _valueCentre = (low + high) * 0.5f;
            _pixelsPerUnit = Math.Clamp((graph.Height - 40f) / span, 0.01f, 100000f);
        }

        private void ZoomValues(Rect graph)
        {
            if (!graph.Contains(Ui.Mouse)) return;
            float wheel = InputSystem.ScrollDelta;
            bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            if (wheel != 0f && !ctrl && !shift)
            {
                // Round the value under the mouse.
                float at = ValueAt(graph, Ui.Mouse.Y);
                _pixelsPerUnit = Math.Clamp(_pixelsPerUnit * MathF.Pow(1.15f, wheel), 0.01f, 100000f);
                _valueCentre = at + (Ui.Mouse.Y - graph.Centre.Y) / _pixelsPerUnit;
            }

            if (Ui.MouseHeld(MouseButton.Middle)) _valueCentre += Ui.MouseDelta.Y / _pixelsPerUnit;
        }

        private void DrawValueGrid(Rect graph, AnimationClip clip)
        {
            Ui.Rect(graph, Theme.Field, 6f);
            if (_recording) Ui.Outline(graph.Grow(1f), Theme.Danger.WithAlpha(0.8f), 7f, 1.5f);

            // A line every so often (at least 28 pixels apart), labelled.
            float[] steps = [0.001f, 0.002f, 0.005f, 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f, 1f, 2f, 5f, 10f, 20f, 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f];
            float step = steps.FirstOrDefault(s => s * _pixelsPerUnit >= 28f, 10000f);
            float topValue = ValueAt(graph, graph.Y), bottomValue = ValueAt(graph, graph.Bottom);
            for (float v = MathF.Floor(bottomValue / step) * step; v <= topValue + step; v += step)
            {
                float y = ValueY(graph, v);
                if (y < graph.Y || y > graph.Bottom) continue;
                bool zero = MathF.Abs(v) < step * 0.001f;
                Ui.Line(new Vector2(graph.X, y), new Vector2(graph.Right, y), zero ? Theme.TextFaint.WithAlpha(0.5f) : Theme.Border.WithAlpha(0.5f), 1f);
                string label = (zero ? 0f : v).ToString(step < 0.01f ? "0.###" : step < 1f ? "0.##" : "0.#", CultureInfo.InvariantCulture);
                Ui.Text(label, new Vector2(graph.X + 4f, y - 15f), Theme.TextFaint, Theme.SmallTextSize);
            }

            // Past the end, dimmed.
            float end = X(graph, clip.Length);
            if (end < graph.Right) Ui.Rect(new Rect(end, graph.Y, graph.Right - end, graph.Height), Theme.Background.WithAlpha(0.35f));
            float start = X(graph, 0f);
            if (start > graph.X) Ui.Rect(new Rect(graph.X, graph.Y, start - graph.X, graph.Height), Theme.Background.WithAlpha(0.35f));
        }

        private void DrawCurve(Rect graph, AnimationTrack track, int channel)
        {
            if (track.Keys.Count == 0) return;
            Color color = ChannelColor(track, channel);
            float from = MathF.Max(TimeAt(graph, graph.X), 0f), to = TimeAt(graph, graph.Right);
            float step = 3f / _pixelsPerSecond;
            Vector2 previous = new(X(graph, from), ValueY(graph, track.Sample(from)[channel]));
            for (float t = from + step; t <= to + step; t += step)
            {
                var point = new Vector2(X(graph, t), ValueY(graph, track.Sample(t)[channel]));
                Ui.Line(previous, point, color, 1.6f);
                previous = point;
            }
        }

        private bool IsCurveKeyPicked(AnimationTrack track, float time, int channel) =>
            _curveKeys.Any(k => k.Track == track && k.Channel == channel && MathF.Abs(k.Time - time) < 0.0005f);

        private void DrawCurveKeys(Rect graph, AnimationClip clip, AnimationTrack track)
        {
            int trackId = Ui.Id(Ui.Id("animation.curves.keys"), RuntimeHelpers.GetHashCode(track));
            for (int i = 0; i < track.Keys.Count; i++)
            {
                Keyframe key = track.Keys[i];
                float x = X(graph, key.Time);
                if (x < graph.X - 8f || x > graph.Right + 8f) continue;
                for (int c = 0; c < Channels(track); c++)
                {
                    var centre = new Vector2(x, ValueY(graph, key.Value[c]));
                    _curveKeysOnScreen[(track, i, c)] = centre;
                    if (centre.Y < graph.Y - 8f || centre.Y > graph.Bottom + 8f) continue;

                    int id = Ui.Id(trackId, i * 4 + c);
                    Interaction interaction = Ui.Interact(id, new Rect(centre.X - 6f, centre.Y - 6f, 12f, 12f), rightClick: true);
                    bool picked = IsCurveKeyPicked(track, key.Time, c);
                    Color color = picked ? Theme.AccentText : interaction.Hovered ? Theme.Text : ChannelColor(track, c);
                    switch (key.Interpolation)
                    {
                        case KeyInterpolation.Step: Ui.Rect(new Rect(centre.X - 4f, centre.Y - 4f, 8f, 8f), color, 1f); break;
                        case KeyInterpolation.Linear: Icons.Key(centre, 5.5f, color); break;
                        default: Ui.Circle(centre, 4.5f, color); break;
                    }

                    if (picked) Ui.Circle(centre, 1.8f, Theme.Field);

                    if (interaction.Pressed) PressCurveKey(clip, track, key.Time, c);
                    if (interaction.RightClicked && !clip.IsImported)
                    {
                        if (!picked)
                        {
                            _curveKeys.Clear();
                            _curveKeys.Add((track, key.Time, c));
                        }

                        Menu.Open(id, Ui.Mouse,
                        [
                            new MenuItem("Smooth", () => SetCurveInterpolation(clip, KeyInterpolation.Smooth)) { Checked = key.Interpolation == KeyInterpolation.Smooth },
                            new MenuItem("Linear", () => SetCurveInterpolation(clip, KeyInterpolation.Linear)) { Checked = key.Interpolation == KeyInterpolation.Linear },
                            new MenuItem("Step", () => SetCurveInterpolation(clip, KeyInterpolation.Step)) { Checked = key.Interpolation == KeyInterpolation.Step },
                            MenuItem.Separator,
                            new MenuItem("Worked-Out Slopes", () => SetSlopes(clip, flat: false)) { Checked = !key.HasSlopes },
                            new MenuItem("Flat", () => SetSlopes(clip, flat: true)),
                            MenuItem.Separator,
                            new MenuItem(_curveKeys.Count == 1 ? "Delete Key" : "Delete Keys", () => DeleteCurveKeys(clip)) { Shortcut = "Del", Icon = Icons.Trash }
                        ]);
                    }

                    string name = ChannelName(track, c);
                    Ui.Tooltip(id, string.Create(CultureInfo.InvariantCulture,
                        $"{PropertyLabel(track.Property)}{(name.Length > 0 ? " " + name : "")}: {key.Value[c]:0.###} at {key.Time:0.##} s. Drag to change, right-click for more"));
                }
            }
        }

        /// <summary>A key pressed: it's picked (Shift adds it to those picked), and a drag of the picked keys starts.</summary>
        private void PressCurveKey(AnimationClip clip, AnimationTrack track, float time, int channel)
        {
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
            bool picked = IsCurveKeyPicked(track, time, channel);
            if (shift && picked)
            {
                _curveKeys.RemoveAll(k => k.Track == track && k.Channel == channel && MathF.Abs(k.Time - time) < 0.0005f);
                return;
            }

            if (!shift && !picked) _curveKeys.Clear();
            if (!picked) _curveKeys.Add((track, time, channel));
            if (clip.IsImported) return;

            var drag = new CurveDrag { Start = Ui.Mouse, Before = clip.ToJson() };
            foreach ((AnimationTrack t, float at, int c) in _curveKeys)
            {
                if (!drag.Tracks.TryGetValue(t, out var entry))
                {
                    entry = (t.Keys.ToList(), new Dictionary<int, HashSet<int>>());
                    drag.Tracks[t] = entry;
                }

                int index = entry.Original.FindIndex(k => MathF.Abs(k.Time - at) < 0.0005f);
                if (index < 0) continue;
                if (!entry.Moving.TryGetValue(index, out HashSet<int>? channels)) entry.Moving[index] = channels = new HashSet<int>();
                channels.Add(c);
            }

            _curveDrag = drag;
        }

        /// <summary>The picked smooth keys' slope handles: a line each way along the curve, with a knob at each end to drag.</summary>
        private void DrawHandles(Rect graph, AnimationClip clip)
        {
            if (clip.IsImported) return;
            foreach ((AnimationTrack track, float time, int channel) in _curveKeys)
            {
                int index = track.Find(time);
                if (index < 0 || !Shown(track)) continue;
                Keyframe key = track.Keys[index];
                var centre = new Vector2(X(graph, key.Time), ValueY(graph, key.Value[channel]));
                Color color = ChannelColor(track, channel);

                // The side going in shows if the key before eases into it; the side going out, if this one eases on.
                bool incoming = index > 0 && track.Keys[index - 1].Interpolation == KeyInterpolation.Smooth;
                bool outgoing = index < track.Keys.Count - 1 && key.Interpolation == KeyInterpolation.Smooth;
                if (incoming) DrawHandle(graph, clip, track, index, channel, false, centre, track.InSlopeAt(index)[channel], color);
                if (outgoing) DrawHandle(graph, clip, track, index, channel, true, centre, track.OutSlopeAt(index)[channel], color);
            }
        }

        private void DrawHandle(Rect graph, AnimationClip clip, AnimationTrack track, int index, int channel, bool outgoing, Vector2 centre, float slope, Color color)
        {
            // Along the slope on screen, a fixed length out from the key.
            var along = new Vector2(_pixelsPerSecond, -slope * _pixelsPerUnit);
            if (along.LengthSquared() < 1e-8f) along = Vector2.UnitX;
            along = Vector2.Normalize(along) * _handleLength;
            Vector2 knob = outgoing ? centre + along : centre - along;
            _handlesOnScreen[(track, index, channel, outgoing)] = knob;

            Ui.Line(centre, knob, color.WithAlpha(0.7f), 1.2f);
            int id = Ui.Id(Ui.Id(Ui.Id("animation.curves.handle"), RuntimeHelpers.GetHashCode(track)), index * 8 + channel * 2 + (outgoing ? 1 : 0));
            Interaction interaction = Ui.Interact(id, new Rect(knob.X - 6f, knob.Y - 6f, 12f, 12f));
            Ui.Circle(knob, interaction.Hovered || _curveDrag?.Handle == (track, index, channel, outgoing) ? 4.5f : 3.5f, interaction.Hovered ? Theme.Text : color);
            Ui.Tooltip(id, "Drag to set how steeply the curve " + (outgoing ? "leaves" : "arrives at") + " the key. Hold Alt to set this side alone");

            if (interaction.Pressed)
            {
                _curveDrag = new CurveDrag { Start = Ui.Mouse, Before = clip.ToJson(), Handle = (track, index, channel, outgoing) };
            }
        }

        /// <summary>Moves what's being dragged on the graph as the mouse moves; letting go makes it one undo.</summary>
        private void FinishCurveDrag(Rect graph, AnimationClip clip)
        {
            if (_curveDrag is not { } drag) return;

            if (!Ui.MouseHeld())
            {
                _curveDrag = null;
                string after = clip.ToJson();
                if (drag.Moved && after != drag.Before) _context.History.Record(new ClipChange(clip, drag.Handle != null ? "Change Slope" : "Move Keys", drag.Before, after));
                return;
            }

            Vector2 moved = Ui.Mouse - drag.Start;
            if (!drag.Moved && moved.Length() < 3f) return;
            drag.Moved = true;

            if (drag.Handle is { } handle)
            {
                DragHandle(graph, handle.Track, handle.Index, handle.Channel, handle.Out);
                clip.Changed();
                return;
            }

            // Each track's keys again from how they were: the moving ones shifted along (on whole frames) and up or down.
            float dt = moved.X / _pixelsPerSecond, dv = -moved.Y / _pixelsPerUnit;
            _curveKeys.Clear();
            foreach ((AnimationTrack track, (List<Keyframe> original, Dictionary<int, HashSet<int>> moving)) in drag.Tracks)
            {
                var shifted = new List<(Keyframe Key, HashSet<int> Channels)>();
                foreach ((int index, HashSet<int> channels) in moving)
                {
                    Keyframe key = original[index];
                    Vector4 value = key.Value;
                    foreach (int c in channels) value[c] += dv;
                    shifted.Add((key with { Time = Snap(clip, key.Time + dt), Value = value }, channels));
                }

                track.Keys.Clear();
                for (int i = 0; i < original.Count; i++)
                {
                    if (!moving.ContainsKey(i) && shifted.All(s => MathF.Abs(s.Key.Time - original[i].Time) > 0.0005f)) track.Keys.Add(original[i]);
                }

                foreach ((Keyframe key, HashSet<int> channels) in shifted)
                {
                    track.Keys.RemoveAll(k => MathF.Abs(k.Time - key.Time) < 0.0005f);
                    track.Keys.Add(key);
                    foreach (int c in channels) _curveKeys.Add((track, key.Time, c));
                }

                track.Sort();
            }

            clip.Changed();
        }

        /// <summary>Sets a key's slope from where its handle's been dragged to: both sides together, or (with Alt) the one alone.</summary>
        private void DragHandle(Rect graph, AnimationTrack track, int index, int channel, bool outgoing)
        {
            if (index >= track.Keys.Count) return;
            Keyframe key = track.Keys[index];
            var centre = new Vector2(X(graph, key.Time), ValueY(graph, key.Value[channel]));
            Vector2 offset = Ui.Mouse - centre;
            // Kept on its own side of the key, so the curve doesn't fold back on itself.
            float dx = outgoing ? MathF.Max(offset.X, 1f) : MathF.Min(offset.X, -1f);
            float slope = -(offset.Y / _pixelsPerUnit) / (dx / _pixelsPerSecond);

            bool alone = InputSystem.GetKey(Key.AltLeft) || InputSystem.GetKey(Key.AltRight);
            Vector4 inSlope = track.InSlopeAt(index), outSlope = track.OutSlopeAt(index);
            if (outgoing || !alone) outSlope[channel] = slope;
            if (!outgoing || !alone) inSlope[channel] = slope;
            track.Keys[index] = key with { InSlope = inSlope, OutSlope = outSlope };
        }

        private void AddKeyOnCurve(Rect graph, AnimationClip clip, List<AnimationTrack> shown)
        {
            // The curve nearest the mouse, if it's near enough.
            float time = TimeAt(graph, Ui.Mouse.X);
            AnimationTrack? nearest = null;
            float best = 10f;
            foreach (AnimationTrack track in shown)
            {
                for (int c = 0; c < Channels(track); c++)
                {
                    float distance = MathF.Abs(ValueY(graph, track.Sample(time)[c]) - Ui.Mouse.Y);
                    if (distance < best)
                    {
                        best = distance;
                        nearest = track;
                    }
                }
            }

            if (nearest == null) return;
            float at = Snap(clip, time);
            AnimationTrack chosen = nearest;
            ClipChange.Make(_context.History, clip, "Add Key", () => chosen.SetKey(at, chosen.Sample(at)));
        }

        private void SetCurveInterpolation(AnimationClip clip, KeyInterpolation interpolation)
        {
            ClipChange.Make(_context.History, clip, $"Make Keys {interpolation}", () =>
            {
                foreach ((AnimationTrack track, float time, _) in _curveKeys)
                {
                    int index = track.Find(time);
                    if (index >= 0) track.Keys[index] = track.Keys[index] with { Interpolation = interpolation };
                }
            });
        }

        /// <summary>The picked keys' slopes worked out again (as a key's are to start with), or made flat.</summary>
        private void SetSlopes(AnimationClip clip, bool flat)
        {
            ClipChange.Make(_context.History, clip, flat ? "Flatten Keys" : "Work Out Slopes", () =>
            {
                foreach (IGrouping<AnimationTrack, (AnimationTrack Track, float Time, int Channel)> keys in _curveKeys.GroupBy(k => k.Track))
                {
                    AnimationTrack track = keys.Key;
                    foreach ((_, float time, int channel) in keys)
                    {
                        int index = track.Find(time);
                        if (index < 0) continue;
                        Keyframe key = track.Keys[index];
                        if (!flat)
                        {
                            track.Keys[index] = key with { InSlope = null, OutSlope = null };
                            continue;
                        }

                        Vector4 inSlope = track.InSlopeAt(index), outSlope = track.OutSlopeAt(index);
                        inSlope[channel] = 0f;
                        outSlope[channel] = 0f;
                        track.Keys[index] = key with { InSlope = inSlope, OutSlope = outSlope };
                    }
                }
            });
        }

        private void DeleteCurveKeys(AnimationClip clip)
        {
            if (clip.IsImported || _curveKeys.Count == 0) return;
            var times = _curveKeys.Select(k => (k.Track, k.Time)).Distinct().ToList();
            ClipChange.Make(_context.History, clip, times.Count == 1 ? "Delete Key" : "Delete Keys", () =>
            {
                foreach ((AnimationTrack track, float time) in times) track.RemoveKey(time);
            });
            _curveKeys.Clear();
        }

        /// <summary>The keyboard over the curves: Delete deletes the picked keys, F fits everything in. True if it used a key.</summary>
        private bool CurveKeys(AnimationClip clip)
        {
            if ((InputSystem.GetKeyDown(Key.Delete) || InputSystem.GetKeyDown(Key.Backspace)) && _curveKeys.Count > 0)
            {
                InputSystem.Consume(Key.Delete);
                InputSystem.Consume(Key.Backspace);
                DeleteCurveKeys(clip);
                return true;
            }

            if (InputSystem.GetKeyDown(Key.F))
            {
                InputSystem.Consume(Key.F);
                _curvesFitted = false;
                _fitted = false;
                return true;
            }

            return false;
        }
    }
}
