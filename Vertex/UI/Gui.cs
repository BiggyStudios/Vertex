using System.Numerics;

using Silk.NET.Input;

using Vertex.Input;

namespace Vertex.UI
{
    /// <summary>
    /// Simple clickable controls drawn on the <see cref="Canvas"/>: call them every frame they should show, and they
    /// report what the mouse did (a click, a new slider value) straight back. The cursor must be unlocked to use them.
    /// </summary>
    public static class Gui
    {
        public static Color TextColor { get; set; } = Color.White;
        public static Color ControlColor { get; set; } = new(1f, 1f, 1f, 0.08f);
        public static Color HoverColor { get; set; } = new(1f, 1f, 1f, 0.18f);
        public static Color AccentColor { get; set; } = new(0.95f, 0.6f, 0.25f);

        // The slider being dragged, if any, so it keeps following the mouse even when the pointer leaves it.
        private static string? _dragging;

        /// <summary>A button with centred text. True on the frame it's clicked.</summary>
        public static bool Button(Font font, string text, Vector2 position, Vector2 size, float textSize = 20f)
        {
            bool hovered = Contains(position, size, Canvas.MousePosition);
            Canvas.DrawRect(position, size, hovered ? HoverColor : ControlColor);
            if (hovered) Canvas.DrawRect(position, new Vector2(3f, size.Y), AccentColor);

            Vector2 textSizeMeasured = Canvas.MeasureText(font, text, textSize);
            Canvas.DrawText(font, text, position + (size - textSizeMeasured) / 2f, TextColor, textSize);

            return hovered && InputSystem.GetMouseButtonDown(MouseButton.Left);
        }

        /// <summary>
        /// A labelled slider from <paramref name="min"/> to <paramref name="max"/>, with the value shown using
        /// <paramref name="format"/>. Click or drag to change it; returns the (possibly new) value.
        /// </summary>
        public static float Slider(Font font, string label, float value, float min, float max, Vector2 position, float width,
            string format = "0.##", float textSize = 18f)
        {
            float lineHeight = Canvas.LineHeight(font, textSize);
            Canvas.DrawText(font, label, position, TextColor, textSize);
            string shown = value.ToString(format);
            Canvas.DrawText(font, shown, position + new Vector2(width - Canvas.MeasureText(font, shown, textSize).X, 0f), new Color(1f, 1f, 1f, 0.7f), textSize);

            var track = new Vector2(position.X, position.Y + lineHeight + 8f);
            const float trackHeight = 6f;
            const float handleSize = 16f;

            // The clickable area is taller than the track, so it's easy to grab.
            var hitPosition = new Vector2(track.X, track.Y - handleSize / 2f);
            var hitSize = new Vector2(width, trackHeight + handleSize);
            bool hovered = Contains(hitPosition, hitSize, Canvas.MousePosition);

            if (hovered && InputSystem.GetMouseButtonDown(MouseButton.Left)) _dragging = label;
            if (!InputSystem.GetMouseButton(MouseButton.Left)) _dragging = null;

            if (_dragging == label)
            {
                float t = Math.Clamp((Canvas.MousePosition.X - track.X) / width, 0f, 1f);
                value = min + (max - min) * t;
            }

            float fraction = Math.Clamp((value - min) / (max - min), 0f, 1f);
            Canvas.DrawRect(track, new Vector2(width, trackHeight), ControlColor);
            Canvas.DrawRect(track, new Vector2(width * fraction, trackHeight), AccentColor);

            var handle = new Vector2(track.X + width * fraction - handleSize / 2f, track.Y + trackHeight / 2f - handleSize / 2f);
            Canvas.DrawRect(handle, new Vector2(handleSize), hovered || _dragging == label ? Color.White : new Color(0.85f, 0.85f, 0.85f));

            return value;
        }

        /// <summary>A labelled on/off switch. Returns the (possibly flipped) value.</summary>
        public static bool Toggle(Font font, string label, bool value, Vector2 position, float width, float textSize = 18f)
        {
            float lineHeight = Canvas.LineHeight(font, textSize);
            var size = new Vector2(width, lineHeight);
            bool hovered = Contains(position, size, Canvas.MousePosition);

            Canvas.DrawText(font, label, position, TextColor, textSize);

            var box = new Vector2(position.X + width - 40f, position.Y + lineHeight / 2f - 10f);
            Canvas.DrawRect(box, new Vector2(40f, 20f), value ? AccentColor : ControlColor);
            Canvas.DrawRect(box + new Vector2(value ? 22f : 2f, 2f), new Vector2(16f, 16f), hovered ? Color.White : new Color(0.85f, 0.85f, 0.85f));

            return hovered && InputSystem.GetMouseButtonDown(MouseButton.Left) ? !value : value;
        }

        private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
            point.X >= position.X && point.Y >= position.Y && point.X < position.X + size.X && point.Y < position.Y + size.Y;
    }
}
