using System.Numerics;

using Vertex.Input;

namespace Vertex.Editor.Gui
{
    /// <summary>Draws an icon into a square around a centre: one of the methods on <see cref="Icons"/>.</summary>
    public delegate void Icon(Vector2 centre, float size, Color color);

    public enum ButtonStyle
    {
        /// <summary>A filled grey button.</summary>
        Normal,

        /// <summary>Filled with the accent colour: the main thing to do.</summary>
        Primary,

        /// <summary>Invisible until the mouse is over it: for toolbars and buttons inside rows.</summary>
        Subtle
    }

    /// <summary>The editor's basic controls. Each takes an id (see <see cref="Ui.Id(string)"/>) and the rectangle it fills.</summary>
    public static class Widgets
    {
        private static Color ButtonColor(ButtonStyle style, Interaction interaction, bool selected = false) => style switch
        {
            ButtonStyle.Primary => interaction.Held ? Theme.Accent.WithAlpha(0.85f) : interaction.Hovered ? Theme.AccentHover : Theme.Accent,
            ButtonStyle.Subtle when selected => interaction.Hovered ? Theme.Selection.WithAlpha(1.3f) : Theme.Selection,
            ButtonStyle.Subtle => interaction.Held ? Theme.ControlPressed : interaction.Hovered ? Theme.ControlHover : Theme.ControlHover.WithAlpha(0f),
            _ => interaction.Held ? Theme.ControlPressed : interaction.Hovered ? Theme.ControlHover : Theme.Control
        };

        /// <summary>A button with a label. True on the frame it's clicked.</summary>
        public static bool Button(int id, Rect rect, string text, ButtonStyle style = ButtonStyle.Normal, string? tooltip = null, Icon? icon = null)
        {
            Interaction interaction = Ui.Interact(id, rect);
            Color color = Ui.Smooth(id, ButtonColor(style, interaction), 24f);
            Ui.Rect(rect, color, Theme.ControlRadius);

            Color textColor = style == ButtonStyle.Primary ? Color.White : Theme.Text;
            if (icon != null)
            {
                float width = Ui.TextWidth(text) + 20f;
                float left = rect.X + MathF.Max((rect.Width - width) * 0.5f, 6f);
                icon(new Vector2(left + 7f, rect.Centre.Y), 14f, textColor);
                Ui.Text(new Rect(left + 20f, rect.Y, rect.Right - left - 20f, rect.Height), text, textColor);
            }
            else
            {
                Ui.Text(rect.Shrink(8f, 0f), text, textColor, align: 0.5f);
            }

            Ui.Tooltip(id, tooltip);
            return interaction.Clicked;
        }

        /// <summary>A square button showing only an icon, highlighted while <paramref name="selected"/>.</summary>
        public static bool IconButton(int id, Rect rect, Icon icon, string? tooltip = null, bool selected = false, Color? color = null)
        {
            Interaction interaction = Ui.Interact(id, rect);
            Color background = Ui.Smooth(id, ButtonColor(ButtonStyle.Subtle, interaction, selected), 24f);
            Ui.Rect(rect, background, Theme.ControlRadius);

            Color iconColor = color ?? (selected ? Theme.AccentText : interaction.Hovered ? Theme.Text : Theme.TextDim);
            icon(rect.Centre, MathF.Min(rect.Width, rect.Height) * 0.75f, Ui.Smooth(Ui.Id(id, "icon"), iconColor, 24f));

            Ui.Tooltip(id, tooltip);
            return interaction.Clicked;
        }

        /// <summary>A tick box, in the middle of <paramref name="rect"/>. The tick draws itself on when it's turned on. Returns true if clicked.</summary>
        public static bool Checkbox(int id, Rect rect, ref bool value, string? tooltip = null)
        {
            Rect box = rect.CentreOf(new Vector2(15f, 15f));
            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Clicked) value = !value;

            float on = Ui.Smooth(id, value ? 1f : 0f, 20f);
            Color fill = Color.Lerp(interaction.Hovered ? Theme.ControlHover : Theme.Field, interaction.Hovered ? Theme.AccentHover : Theme.Accent, on);
            Ui.Rect(box, fill, 3.5f);
            if (on < 0.99f) Ui.Outline(box, Theme.BorderStrong.WithAlpha(1f - on), 3.5f);
            Icons.Check(box.Centre, 15f, Color.White, on);

            Ui.Tooltip(id, tooltip);
            return interaction.Clicked;
        }

        /// <summary>
        /// A header that opens and closes what's under it: an arrow that turns as it does, and a title. Returns whether
        /// it's open now. Use <see cref="Ui.Smooth(int, float, float, float?)"/> with <c>open ? 1 : 0</c> to slide the contents.
        /// </summary>
        public static bool Foldout(int id, Rect rect, string text, bool open, bool bold = true, Icon? icon = null)
        {
            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Clicked) open = !open;

            Ui.Rect(rect, Ui.Smooth(id, interaction.Hovered ? Theme.Hover : Theme.Hover.WithAlpha(0f), 20f), Theme.ControlRadius);

            float turn = Ui.Smooth(Ui.Id(id, "turn"), open ? 90f : 0f, 22f);
            Icons.Chevron(new Vector2(rect.X + 11f, rect.Centre.Y), 14f, Theme.TextDim, turn);

            float textLeft = rect.X + 22f;
            if (icon != null)
            {
                icon(new Vector2(textLeft + 7f, rect.Centre.Y), 15f, Theme.TextDim);
                textLeft += 21f;
            }

            Ui.Text(new Rect(textLeft, rect.Y, rect.Right - textLeft - 4f, rect.Height), text, Theme.Text, bold: bold, size: bold ? Theme.TitleSize : Theme.TextSize);
            return open;
        }

        /// <summary>
        /// A box showing <paramref name="options"/>[<paramref name="selected"/>] that opens a list to pick from. Returns the
        /// picked index on the frame after it's picked, otherwise null.
        /// </summary>
        public static int? Dropdown(int id, Rect rect, IReadOnlyList<string> options, int selected, string? tooltip = null)
        {
            Interaction interaction = Ui.Interact(id, rect);
            bool open = Menu.IsOpen(id);

            Color background = Ui.Smooth(id, interaction.Hovered || open ? Theme.ControlHover : Theme.Control, 24f);
            Ui.Rect(rect, background, Theme.ControlRadius);
            string shown = selected >= 0 && selected < options.Count ? options[selected] : "—";
            Ui.Text(rect.Shrink(8f, 0f).CutRight(16f), shown, Theme.Text);
            Icons.Chevron(new Vector2(rect.Right - 12f, rect.Centre.Y), 13f, Theme.TextDim, Ui.Smooth(Ui.Id(id, "turn"), open ? -90f : 90f, 22f));

            if (interaction.Clicked && !open)
            {
                var items = new List<MenuItem>(options.Count);
                for (int i = 0; i < options.Count; i++) items.Add(new MenuItem(options[i]) { Checked = i == selected });
                Menu.OpenBelow(id, rect, items);
            }

            Ui.Tooltip(id, tooltip);
            return Menu.TakeChoice(id);
        }

        /// <summary>
        /// A bar to drag between <paramref name="min"/> and <paramref name="max"/>, its value written over it. Double-click
        /// to type a value. Returns true while the value changes.
        /// </summary>
        public static bool Slider(int id, Rect rect, ref float value, float min, float max, string format = "0.##")
        {
            if (Ui.FocusId == id) return NumberField.Typed(id, rect, ref value, format, min, max);

            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Hovered) Ui.Cursor = CursorShape.ResizeHorizontal;

            if (interaction.DoubleClicked)
            {
                NumberField.BeginTyping(id);
                return false;
            }

            bool changed = false;
            if (interaction.Held && max > min)
            {
                float t = Math.Clamp((Ui.Mouse.X - rect.X) / rect.Width, 0f, 1f);
                float next = min + (max - min) * t;
                if (next != value)
                {
                    value = next;
                    changed = true;
                }
            }

            float fraction = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
            float shown = Ui.Smooth(Ui.Id(id, "fill"), fraction, 28f);

            Ui.Rect(rect, Ui.Smooth(id, interaction.Hovered ? Theme.ControlHover : Theme.Field, 24f), Theme.ControlRadius);
            if (shown > 0.001f)
            {
                Ui.PushClip(rect.TakeLeft(rect.Width * shown));
                Ui.Rect(rect, Theme.Accent.WithAlpha(interaction.Held ? 0.55f : 0.42f), Theme.ControlRadius);
                Ui.PopClip();
            }

            Ui.Text(rect, value.ToString(format, System.Globalization.CultureInfo.InvariantCulture), Theme.Text, align: 0.5f);
            return changed;
        }

        /// <summary>
        /// A divider to drag, for resizing panels: <paramref name="value"/> follows the mouse along X (or Y when
        /// <paramref name="vertical"/>), kept between <paramref name="min"/> and <paramref name="max"/>. Lights up under the mouse.
        /// </summary>
        public static bool Splitter(int id, Rect rect, ref float value, float min, float max, bool vertical = false, bool inverted = false)
        {
            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Hovered) Ui.Cursor = vertical ? CursorShape.ResizeVertical : CursorShape.ResizeHorizontal;

            bool changed = false;
            if (interaction.Held)
            {
                float delta = vertical ? Ui.MouseDelta.Y : Ui.MouseDelta.X;
                if (inverted) delta = -delta;
                float next = Math.Clamp(value + delta, min, max);
                changed = next != value;
                value = next;
            }

            float glow = Ui.Smooth(id, interaction.Held ? 1f : interaction.Hovered ? 0.6f : 0f, 16f);
            if (glow > 0.01f)
            {
                Rect line = vertical ? rect.CentreOf(new Vector2(rect.Width - 12f, 2f)) : rect.CentreOf(new Vector2(2f, rect.Height - 12f));
                Ui.Rect(line, Theme.Accent.WithAlpha(glow), 1f);
            }

            return changed;
        }

        /// <summary>Dim text in a property's name column, with a tooltip if given.</summary>
        public static void Label(int id, Rect rect, string text, string? tooltip = null, Color? color = null)
        {
            if (tooltip != null) Ui.Interact(id, rect);
            Ui.Text(rect, text, color ?? Theme.TextDim);
            Ui.Tooltip(id, tooltip);
        }
    }
}
