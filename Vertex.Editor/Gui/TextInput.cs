using System.Numerics;

using Vertex.Input;
using Vertex.UI;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// A one-line text box. Editing is the engine's <see cref="TextField"/> (arrows, Shift to select, Ctrl for words,
    /// copy and paste); this draws it the editor's way. The cursor glides to where it's going instead of jumping, the
    /// selection stretches and shrinks smoothly, long text scrolls smoothly to keep the cursor in view, and the cursor
    /// fades in and out while idle rather than blinking.
    /// <para>
    /// Click to type, at the click; drag to select; double-click selects everything. Enter or clicking away finishes,
    /// Escape puts the text back as it was.
    /// </para>
    /// </summary>
    public static class TextInput
    {
        private sealed class State
        {
            public readonly TextField Field = new();
            public bool WasFocused;
            public string Original = "";
            public int Frame;

            // How far the text is scrolled left to keep the cursor in view: where it's going, and where it's got to.
            public float ScrollTarget;
            public float Scroll;
        }

        private static readonly Dictionary<int, State> _states = new();

        // Asked for by Focus, to happen when the box is next drawn.
        private static int _pendingFocus;
        private static bool _pendingSelectAll;

        private const float _innerPadding = 8f;

        /// <summary>Gives the box <paramref name="id"/> the keyboard the next time it's drawn, as if clicked, optionally with all its text selected.</summary>
        public static void Focus(int id, bool selectAll = true)
        {
            _pendingFocus = id;
            _pendingSelectAll = selectAll;
            if (Ui.FocusId != id && _states.TryGetValue(id, out State? state)) state.WasFocused = false;
            Ui.Focus(id);
        }

        /// <summary>
        /// Draws the text box <paramref name="id"/> over <paramref name="rect"/>, editing <paramref name="text"/>.
        /// <para>
        /// With <paramref name="live"/>, <paramref name="text"/> changes as each key is typed, for a search box, and the
        /// result is true whenever it does. Otherwise it changes only when typing is finished (Enter, or clicking away),
        /// for a name, and the result is true then.
        /// </para>
        /// </summary>
        public static bool Draw(int id, Rect rect, ref string text, string placeholder = "", bool live = false, bool selectAllOnFocus = false,
            Icon? icon = null, float textSize = Theme.TextSize)
        {
            if (!_states.TryGetValue(id, out State? state))
            {
                state = new State();
                _states[id] = state;
            }

            state.Frame = Ui.Frame;
            Interaction interaction = Ui.Interact(id, rect);
            if (interaction.Hovered) Ui.Cursor = CursorShape.IBeam;

            Rect inner = rect.Shrink(_innerPadding, 0f);
            if (icon != null) inner = inner.CutLeft(18f);

            TextField field = state.Field;
            float lineHeight = Ui.LineHeight(textSize);

            // Measured from the start of the text, in UI units.
            string display = Ui.FocusId == id ? field.Text : text;
            float Width(int count) => count > 0 ? Canvas.MeasureText(Theme.Font, display[..Math.Min(count, display.Length)], textSize).X : 0f;
            int IndexAt(float x)
            {
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int i = 0; i <= display.Length; i++)
                {
                    float distance = MathF.Abs(Width(i) - x);
                    if (distance < bestDistance)
                    {
                        best = i;
                        bestDistance = distance;
                    }
                }

                return best;
            }

            float scroll = state.Scroll;
            bool changed = false;

            // Starting to type: by a click, or by Focus from elsewhere. Either starts afresh, even if the box was last
            // left without being drawn again to finish (clicked away from as its panel went).
            if (interaction.Pressed && Ui.FocusId != id)
            {
                Ui.Focus(id);
                state.WasFocused = false;
            }            bool focused = Ui.FocusId == id;

            if (focused && !state.WasFocused)
            {
                field.SetText(text);
                display = field.Text;
                state.Original = text;

                bool selectAll = _pendingFocus == id ? _pendingSelectAll : selectAllOnFocus;
                if (selectAll) field.SelectAll();
                else if (interaction.Pressed) field.Select(IndexAt(Ui.Mouse.X - inner.X + scroll), IndexAt(Ui.Mouse.X - inner.X + scroll));

                if (_pendingFocus == id) _pendingFocus = 0;

                // The cursor starts where it is, rather than gliding in from the left.
                Ui.Snap(Ui.Id(id, "caret"), Width(field.Caret));
                Ui.Snap(Ui.Id(id, "from"), Width(field.SelectionStart));
                Ui.Snap(Ui.Id(id, "to"), Width(field.SelectionEnd));
            }
            else if (focused)
            {
                if (interaction.DoubleClicked) field.SelectAll();
                else if (interaction.Pressed) field.Select(IndexAt(Ui.Mouse.X - inner.X + scroll), IndexAt(Ui.Mouse.X - inner.X + scroll));
                else if (interaction.Held && !interaction.Pressed) field.Select(field.SelectionStart == field.Caret ? field.SelectionEnd : field.SelectionStart, IndexAt(Ui.Mouse.X - inner.X + scroll));
            }

            if (focused)
            {
                Ui.KeepFocus(id);
                string before = field.Text;
                field.Update();
                display = field.Text;

                if (live && display != before)
                {
                    text = display;
                    changed = true;
                }

                if (InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter) || InputSystem.GetKeyDown(Key.Tab))
                {
                    InputSystem.Consume(Key.Enter);
                    InputSystem.Consume(Key.KeypadEnter);
                    InputSystem.Consume(Key.Tab);
                    Ui.FocusId = 0;
                    focused = false;
                }
                else if (InputSystem.GetKeyDown(Key.Escape))
                {
                    // Put back as it was before typing started.
                    InputSystem.Consume(Key.Escape);
                    field.SetText(state.Original);
                    display = field.Text;
                    if (live && text != state.Original)
                    {
                        text = state.Original;
                        changed = true;
                    }

                    Ui.FocusId = 0;
                    focused = false;
                    state.WasFocused = false;
                    DrawBox(id, rect, inner, focused, interaction.Hovered, icon);
                    DrawText(inner, text, placeholder, textSize, lineHeight, 0f);
                    return changed;
                }

                InputSystem.ConsumeTypedText();
            }

            // Finished typing (Enter, Tab, or the keyboard went elsewhere): hand the text over.
            if (!focused && state.WasFocused && !live && field.Text != text)
            {
                text = field.Text;
                changed = true;
            }

            state.WasFocused = focused;

            DrawBox(id, rect, inner, focused, interaction.Hovered, icon);

            if (!focused)
            {
                state.Scroll = state.ScrollTarget = 0f;
                DrawText(inner, text, placeholder, textSize, lineHeight, 0f);
                return changed;
            }

            // Keep the cursor in view, scrolling smoothly to it.
            float caretX = Width(field.Caret);
            float target = state.ScrollTarget;
            if (caretX - target > inner.Width - 2f) target = caretX - inner.Width + 2f;
            if (caretX < target) target = caretX;
            target = Math.Clamp(target, 0f, MathF.Max(Width(display.Length) - inner.Width + 2f, 0f));
            state.ScrollTarget = target;
            state.Scroll = scroll = Ui.Approach(state.Scroll, target, 22f, Ui.DeltaTime);

            float caret = Ui.Smooth(Ui.Id(id, "caret"), caretX, 30f);
            float from = Ui.Smooth(Ui.Id(id, "from"), Width(field.SelectionStart), 30f);
            float to = Ui.Smooth(Ui.Id(id, "to"), Width(field.SelectionEnd), 30f);

            Ui.PushClip(inner.Grow(1f));
            float textY = inner.Y + (inner.Height - lineHeight) * 0.5f;

            if (to - from > 0.5f)
            {
                var selection = new Rect(inner.X + from - scroll, textY + 1f, to - from, lineHeight - 2f);
                Ui.Rect(selection, Theme.Selection.WithAlpha(1.6f), 2.5f);
            }

            if (display.Length > 0) Canvas.DrawText(Theme.Font, display, new Vector2(inner.X - scroll, textY), Ui.Fade(Theme.Text), textSize);

            // Solid while typing; once idle, fades out and back in, gently.
            float idle = Vertex.Time.TotalTime - field.LastEdited;
            float alpha = idle < 0.5f ? 1f : Math.Clamp(0.5f + 0.5f * MathF.Cos((idle - 0.5f) * MathF.PI * 2f / 1.1f) * 1.8f, 0f, 1f);
            if (alpha > 0.01f)
            {
                var caretRect = new Rect(inner.X + caret - scroll - 0.75f, textY + 2f, 1.5f, lineHeight - 4f);
                Ui.Rect(caretRect, Theme.AccentText.WithAlpha(alpha), 0.75f);
            }

            Ui.PopClip();
            return changed;
        }

        private static void DrawBox(int id, Rect rect, Rect inner, bool focused, bool hovered, Icon? icon)
        {
            float focus = Ui.Smooth(Ui.Id(id, "focus"), focused ? 1f : 0f, 18f);
            Color background = Color.Lerp(hovered ? Theme.Control : Theme.Field, Theme.Field, focus);
            Ui.Rect(rect, Ui.Smooth(Ui.Id(id, "background"), background, 24f), Theme.ControlRadius);
            Ui.Outline(rect, Color.Lerp(Theme.Border, Theme.Accent, focus), Theme.ControlRadius, 1f + focus * 0.5f);

            icon?.Invoke(new Vector2(rect.X + _innerPadding + 6f, rect.Centre.Y), 14f, focused ? Theme.AccentText : Theme.TextFaint);
        }

        private static void DrawText(Rect inner, string text, string placeholder, float textSize, float lineHeight, float scroll)
        {
            var position = new Vector2(inner.X - scroll, inner.Y + (inner.Height - lineHeight) * 0.5f);
            Ui.PushClip(inner);
            if (text.Length > 0) Canvas.DrawText(Theme.Font, Ui.Fit(text, inner.Width, textSize), position, Ui.Fade(Theme.Text), textSize);
            else if (placeholder.Length > 0) Canvas.DrawText(Theme.Font, placeholder, position, Ui.Fade(Theme.TextFaint), textSize);
            Ui.PopClip();
        }

        /// <summary>Forgets text boxes that haven't been drawn in a while.</summary>
        internal static void Tidy()
        {
            if (Ui.Frame % 300 != 0) return;
            foreach (int id in _states.Where(entry => Ui.Frame - entry.Value.Frame > 300 && Ui.FocusId != entry.Key).Select(entry => entry.Key).ToList())
            {
                _states.Remove(id);
            }
        }
    }
}
