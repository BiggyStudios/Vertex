using System.Globalization;
using System.Numerics;

using Vertex.Input;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// Boxes for numbers. Drag one left or right to change its value (Shift for finer steps, Ctrl for bigger ones):
    /// the cursor hides while dragging so the drag can go on past the edge of the screen. Click one without dragging to
    /// type a value instead.
    /// </summary>
    public static class NumberField
    {
        private static int _dragId;
        private static float _dragStartValue;
        private static float _dragDistance;
        private static bool _dragging;

        // Box being typed in, and its text while it is.
        private static readonly Dictionary<int, string> _typing = new();

        /// <summary>
        /// A number box. <paramref name="speed"/> is how much the value changes per UI unit dragged; 0 picks one to suit the value's size (a hundredth of it, at least 0.01). An axis colour shows
        /// as a strip down the left edge, for X, Y and Z. Returns true while the value changes.
        /// </summary>
        public static bool Float(int id, Rect rect, ref float value, float speed = 0f, string format = "0.###", Color? axis = null,
            float min = float.MinValue, float max = float.MaxValue)
        {
            if (Ui.FocusId == id) return Typed(id, rect, ref value, format, min, max, axis);

            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Hovered) Ui.Cursor = CursorShape.ResizeHorizontal;

            bool changed = false;
            if (interaction.Pressed)
            {
                _dragId = id;
                _dragStartValue = value;
                _dragDistance = 0f;
                _dragging = false;
            }

            if (interaction.Held && _dragId == id)
            {
                _dragDistance += Ui.MouseDelta.X;
                if (!_dragging && MathF.Abs(_dragDistance) > 3f)
                {
                    _dragging = true;
                    InputSystem.CursorLocked = true;
                }

                if (_dragging)
                {
                    bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);
                    bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
                    float baseStep = speed > 0f ? speed : MathF.Max(MathF.Abs(_dragStartValue) * 0.01f, 0.01f);
                    float step = baseStep * (shift ? 0.1f : 1f) * (ctrl ? 10f : 1f);
                    float next = Math.Clamp(_dragStartValue + _dragDistance * step, min, max);
                    if (next != value)
                    {
                        value = next;
                        changed = true;
                    }
                }
            }

            if (interaction.Released && _dragId == id)
            {
                if (_dragging) InputSystem.CursorLocked = false;
                else BeginTyping(id);

                _dragId = 0;
                _dragging = false;
            }

            bool active = interaction.Held && _dragging;
            Color background = Ui.Smooth(id, active ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Field, 24f);
            Ui.Rect(rect, background, Theme.ControlRadius);
            if (axis is Color axisColor) DrawAxis(rect, axisColor, interaction.Hovered || active);

            Rect textRect = rect.Shrink(8f, 0f);
            if (axis != null) textRect = textRect.CutLeft(3f);
            Ui.Text(textRect, Format(value, format), active ? Theme.AccentText : Theme.Text);
            return changed;
        }

        /// <summary>A whole-number box: <see cref="Float"/>, rounded.</summary>
        public static bool Int(int id, Rect rect, ref int value, float speed = 0.1f, int min = int.MinValue, int max = int.MaxValue)
        {
            float asFloat = value;
            if (!Float(id, rect, ref asFloat, speed, "0", null, min, max)) return false;

            int rounded = (int)MathF.Round(asFloat);
            if (rounded == value) return false;
            value = rounded;
            return true;
        }

        /// <summary>Three boxes side by side for X, Y and Z, each with its axis colour. Returns true while any changes.</summary>
        public static bool Vector3(int id, Rect rect, ref Vector3 value, float speed = 0f, string format = "0.###")
        {
            float x = value.X, y = value.Y, z = value.Z;
            bool changed = Float(Ui.Id(id, "x"), rect.Column(0, 3, 4f), ref x, speed, format, Theme.AxisX);
            changed |= Float(Ui.Id(id, "y"), rect.Column(1, 3, 4f), ref y, speed, format, Theme.AxisY);
            changed |= Float(Ui.Id(id, "z"), rect.Column(2, 3, 4f), ref z, speed, format, Theme.AxisZ);
            if (changed) value = new Vector3(x, y, z);
            return changed;
        }

        public static bool Vector2(int id, Rect rect, ref Vector2 value, float speed = 0f, string format = "0.###")
        {
            float x = value.X, y = value.Y;
            bool changed = Float(Ui.Id(id, "x"), rect.Column(0, 2, 4f), ref x, speed, format, Theme.AxisX);
            changed |= Float(Ui.Id(id, "y"), rect.Column(1, 2, 4f), ref y, speed, format, Theme.AxisY);
            if (changed) value = new Vector2(x, y);
            return changed;
        }

        /// <summary>Whether a number box is being dragged right now (and so has the cursor locked).</summary>
        public static bool Dragging => _dragging;

        /// <summary>Switches box <paramref name="id"/> to typing, with its value selected.</summary>
        internal static void BeginTyping(int id)
        {
            _typing.Remove(id);
            TextInput.Focus(id, selectAll: true);
        }

        /// <summary>The box while it's being typed in: a text box that turns back into a number when done.</summary>
        internal static bool Typed(int id, Rect rect, ref float value, string format, float min, float max, Color? axis = null)
        {
            if (!_typing.TryGetValue(id, out string? text)) text = Format(value, format);

            bool committed = TextInput.Draw(id, rect, ref text, selectAllOnFocus: true);
            if (axis is Color axisColor) DrawAxis(rect, axisColor, highlighted: true);

            if (Ui.FocusId == id)
            {
                _typing[id] = text;
                return false;
            }

            _typing.Remove(id);
            if (!committed || !TryParse(text, out float parsed)) return false;

            parsed = Math.Clamp(parsed, min, max);
            if (parsed == value) return false;
            value = parsed;
            return true;
        }

        private static void DrawAxis(Rect rect, Color color, bool highlighted)
        {
            Ui.PushClip(rect.TakeLeft(3f));
            Ui.Rect(rect, color.WithAlpha(highlighted ? 1f : 0.8f), Theme.ControlRadius);
            Ui.PopClip();
        }

        public static string Format(float value, string format)
        {
            // No "-0".
            if (value == 0f) value = 0f;
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a typed number, with a comma or a point for the decimals.</summary>
        public static bool TryParse(string text, out float value) =>
            float.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
