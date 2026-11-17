using System.Globalization;
using System.Numerics;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// A colour swatch that opens a picker: a square for how strong and how bright, a strip for the hue, one for how
    /// see-through, and the colour as hex to type or paste.
    /// </summary>
    public static class ColorField
    {
        // Hue, saturation and value of the colour being picked, kept so the hue isn't lost while the colour is grey.
        private static Vector3 _hsv;
        private static int _hsvFor;
        private static string _hex = "";

        /// <summary>
        /// The swatch for <paramref name="value"/>. Changes made in its picker are passed to <paramref name="set"/> as they
        /// happen, with <paramref name="finished"/> true on the last of a drag (for undo).
        /// </summary>
        public static void Draw(int id, Rect rect, Color value, Action<Color, bool> set)
        {
            Interaction interaction = Ui.Interact(id, rect);
            bool open = Popup.IsOpen(id);

            Ui.Rect(rect, Theme.Field, Theme.ControlRadius);
            Rect swatch = rect.Shrink(3f);
            Ui.Rect(swatch, new Color(value.R, value.G, value.B), Theme.ControlRadius - 1.5f);

            // See-through colours show how much as a bar along the bottom.
            if (value.A < 0.999f)
            {
                Rect bar = swatch.TakeBottom(3f);
                Ui.Rect(bar, Color.Black, 1f);
                Ui.Rect(bar.TakeLeft(bar.Width * value.A), Color.White, 1f);
            }

            Ui.Outline(rect, Ui.Smooth(Ui.Id(id, "ring"), open ? Theme.Accent : interaction.Hovered ? Theme.BorderStrong : Theme.Border, 20f), Theme.ControlRadius);

            if (interaction.Clicked && !open)
            {
                Popup.Open(id, rect);
                _hsvFor = id;
                _hsv = ToHsv(value);
                _hex = ToHex(value);
            }

            Popup.Show(id, new Vector2(240f, 252f), inner => Picker(id, inner, value, set));
        }

        private static void Picker(int id, Rect rect, Color value, Action<Color, bool> set)
        {
            if (_hsvFor != id)
            {
                _hsvFor = id;
                _hsv = ToHsv(value);
            }

            // The colour may have changed under the picker (undo): follow it, keeping the hue if it's grey.
            Vector3 current = ToHsv(value);
            if (!SameColour(FromHsv(_hsv, value.A), value))
            {
                _hsv = current.Y < 0.001f || current.Z < 0.001f ? new Vector3(_hsv.X, current.Y, current.Z) : current;
                _hex = ToHex(value);
            }

            float alpha = value.A;
            Color hue = FromHsv(new Vector3(_hsv.X, 1f, 1f), 1f);
            bool changed = false, finished = false;

            // Saturation across, value up.
            Rect square = rect.TakeTop(140f);
            Interaction squareInteraction = Ui.Interact(Ui.Id(id, "sv"), square);
            if (squareInteraction.Held)
            {
                _hsv.Y = Math.Clamp((Ui.Mouse.X - square.X) / square.Width, 0f, 1f);
                _hsv.Z = 1f - Math.Clamp((Ui.Mouse.Y - square.Y) / square.Height, 0f, 1f);
                changed = true;
            }

            finished |= squareInteraction.Released;
            Vertex.UI.Canvas.DrawGradient(square.Position, square.Size, Ui.Fade(Color.White), Ui.Fade(hue), Ui.Fade(hue), Ui.Fade(Color.White), 5f);
            Vertex.UI.Canvas.DrawGradient(square.Position, square.Size, Ui.Fade(new Color(0f, 0f, 0f, 0f)), Ui.Fade(new Color(0f, 0f, 0f, 0f)),
                Ui.Fade(Color.Black), Ui.Fade(Color.Black), 5f);
            var knob = new Vector2(square.X + _hsv.Y * square.Width, square.Y + (1f - _hsv.Z) * square.Height);
            Ui.Circle(knob, 7f, Color.White);
            Ui.Circle(knob, 5.5f, FromHsv(_hsv, 1f));

            // Hue: round the colour wheel, left to right.
            Rect hueStrip = rect.CutTop(150f).TakeTop(12f).Shrink(7f, 0f);
            Interaction hueInteraction = Ui.Interact(Ui.Id(id, "hue"), hueStrip.Grow(3f));
            if (hueInteraction.Held)
            {
                _hsv.X = Math.Clamp((Ui.Mouse.X - hueStrip.X) / hueStrip.Width, 0f, 0.9999f);
                changed = true;
            }

            finished |= hueInteraction.Released;
            for (int i = 0; i < 6; i++)
            {
                Rect piece = hueStrip.Column(i, 6, 0f);
                Color from = FromHsv(new Vector3(i / 6f, 1f, 1f), 1f), to = FromHsv(new Vector3((i + 1) / 6f % 1f, 1f, 1f), 1f);
                Vertex.UI.Canvas.DrawGradient(piece.Position, piece.Size, Ui.Fade(from), Ui.Fade(to), Ui.Fade(to), Ui.Fade(from));
            }

            Marker(new Vector2(hueStrip.X + _hsv.X * hueStrip.Width, hueStrip.Centre.Y), hue);

            // How opaque.
            Rect alphaStrip = rect.CutTop(172f).TakeTop(12f).Shrink(7f, 0f);
            Interaction alphaInteraction = Ui.Interact(Ui.Id(id, "alpha"), alphaStrip.Grow(3f));
            if (alphaInteraction.Held)
            {
                alpha = Math.Clamp((Ui.Mouse.X - alphaStrip.X) / alphaStrip.Width, 0f, 1f);
                changed = true;
            }

            finished |= alphaInteraction.Released;
            Color opaque = FromHsv(_hsv, 1f);
            Ui.Rect(alphaStrip, Theme.Field, 3f);
            Vertex.UI.Canvas.DrawGradient(alphaStrip.Position, alphaStrip.Size, Ui.Fade(opaque.WithAlpha(0f)), Ui.Fade(opaque), Ui.Fade(opaque), Ui.Fade(opaque.WithAlpha(0f)), 3f);
            Marker(new Vector2(alphaStrip.X + alpha * alphaStrip.Width, alphaStrip.Centre.Y), opaque);

            // Before and after, and the hex to type.
            Rect bottom = rect.TakeBottom(Theme.RowHeight + 2f);
            Rect preview = bottom.TakeLeft(48f);
            Ui.Rect(preview, FromHsv(_hsv, 1f), Theme.ControlRadius);

            Rect hexRect = bottom.CutLeft(56f);
            string hex = _hex;
            if (TextInput.Draw(Ui.Id(id, "hex"), hexRect, ref hex, "RRGGBB") && TryParseHex(hex, out Color typed))
            {
                _hsv = ToHsv(typed);
                alpha = typed.A;
                changed = true;
                finished = true;
            }

            if (changed || finished)
            {
                Color picked = FromHsv(_hsv, alpha);
                if (Ui.FocusId != Ui.Id(id, "hex")) _hex = ToHex(picked);
                set(picked, finished);
            }
        }

        private static void Marker(Vector2 centre, Color color)
        {
            Ui.Circle(centre, 7.5f, Color.White);
            Ui.Circle(centre, 6f, color);
        }

        private static bool SameColour(Color a, Color b) =>
            MathF.Abs(a.R - b.R) < 0.002f && MathF.Abs(a.G - b.G) < 0.002f && MathF.Abs(a.B - b.B) < 0.002f && MathF.Abs(a.A - b.A) < 0.002f;

        /// <summary>Hue (0 to 1 round the wheel), saturation and value of a colour.</summary>
        public static Vector3 ToHsv(Color color)
        {
            float max = MathF.Max(color.R, MathF.Max(color.G, color.B));
            float min = MathF.Min(color.R, MathF.Min(color.G, color.B));
            float delta = max - min;

            float hue = 0f;
            if (delta > 0f)
            {
                if (max == color.R) hue = (color.G - color.B) / delta % 6f;
                else if (max == color.G) hue = (color.B - color.R) / delta + 2f;
                else hue = (color.R - color.G) / delta + 4f;
                hue /= 6f;
                if (hue < 0f) hue += 1f;
            }

            return new Vector3(hue, max > 0f ? delta / max : 0f, max);
        }

        public static Color FromHsv(Vector3 hsv, float alpha)
        {
            float h = hsv.X % 1f * 6f, s = hsv.Y, v = hsv.Z;
            float c = v * s;
            float x = c * (1f - MathF.Abs(h % 2f - 1f));
            float m = v - c;
            (float r, float g, float b) = (int)h switch
            {
                0 => (c, x, 0f),
                1 => (x, c, 0f),
                2 => (0f, c, x),
                3 => (0f, x, c),
                4 => (x, 0f, c),
                _ => (c, 0f, x)
            };

            return new Color(r + m, g + m, b + m, alpha);
        }

        public static string ToHex(Color color)
        {
            static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
            string hex = $"{Byte(color.R):X2}{Byte(color.G):X2}{Byte(color.B):X2}";
            return color.A < 0.999f ? hex + $"{Byte(color.A):X2}" : hex;
        }

        public static bool TryParseHex(string text, out Color color)
        {
            color = default;
            text = text.Trim().TrimStart('#');
            if (text.Length != 6 && text.Length != 8) return false;
            if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) return false;

            if (text.Length == 6) value = value << 8 | 0xFF;
            color = Color.FromBytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
    }
}
