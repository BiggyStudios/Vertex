using System.Numerics;
using System.Text;

using Silk.NET.Input;

using Vertex.Input;

namespace Vertex.UI
{
    /// <summary>
    /// A one-line text box that edits like any other: arrows, Home and End move the cursor, holding Shift selects,
    /// Ctrl moves or deletes a word at a time, Ctrl+A selects everything, and Ctrl+C, Ctrl+X and Ctrl+V copy, cut and
    /// paste. Held keys repeat. Call <see cref="Update"/> while it has focus and <see cref="Draw"/> to show it;
    /// Enter, Escape and anything else are left to the owner.
    /// </summary>
    public sealed class TextField
    {
        private const float _repeatDelay = 0.4f;
        private const float _repeatInterval = 0.035f;

        private static readonly Color _selectionColor = new(0.3f, 0.5f, 1f, 0.55f);

        private readonly StringBuilder _text = new();

        // The cursor, and where the selection started. With nothing selected, they're the same.
        private int _caret;
        private int _anchor;

        // The first character shown, when the text is too long for the box.
        private int _scroll;

        // When the cursor last moved, so it stays solid while typing and only blinks when idle.
        private float _caretMoved;

        // How long each repeating key has been held.
        private readonly Dictionary<Key, float> _held = new();

        public int MaxLength { get; set; } = 256;

        public string Text => _text.ToString();

        public int Caret => _caret;

        public bool HasSelection => _anchor != _caret;
        public int SelectionStart => Math.Min(_anchor, _caret);
        public int SelectionEnd => Math.Max(_anchor, _caret);

        /// <summary>Replaces the text, with the cursor at the end.</summary>
        public void SetText(string text)
        {
            _text.Clear();
            _caret = _anchor = _scroll = 0;
            Insert(text);
        }

        public void Clear() => SetText("");

        /// <summary>Handles typing and editing keys for this frame.</summary>
        public void Update()
        {
            bool ctrl = InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
            bool shift = InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

            Insert(InputSystem.TypedText);

            if (ctrl && InputSystem.GetKeyDown(Key.A))
            {
                _anchor = 0;
                MoveCaret(_text.Length, extendSelection: true);
            }

            if (ctrl && (InputSystem.GetKeyDown(Key.C) || InputSystem.GetKeyDown(Key.X)) && HasSelection)
            {
                InputSystem.ClipboardText = _text.ToString(SelectionStart, SelectionEnd - SelectionStart);
                if (InputSystem.GetKeyDown(Key.X)) DeleteSelection();
            }

            if (ctrl && Repeating(Key.V)) Insert(InputSystem.ClipboardText);

            if (Repeating(Key.Left))
            {
                // Without Shift, Left on a selection goes to its start, as in any text box.
                int target = !shift && HasSelection ? SelectionStart : ctrl ? PreviousWord(_caret) : Math.Max(_caret - 1, 0);
                MoveCaret(target, shift);
            }

            if (Repeating(Key.Right))
            {
                int target = !shift && HasSelection ? SelectionEnd : ctrl ? NextWord(_caret) : Math.Min(_caret + 1, _text.Length);
                MoveCaret(target, shift);
            }

            if (InputSystem.GetKeyDown(Key.Home)) MoveCaret(0, shift);
            if (InputSystem.GetKeyDown(Key.End)) MoveCaret(_text.Length, shift);

            if (Repeating(Key.Backspace))
            {
                if (!HasSelection) _anchor = ctrl ? PreviousWord(_caret) : Math.Max(_caret - 1, 0);
                DeleteSelection();
            }

            if (Repeating(Key.Delete))
            {
                if (!HasSelection) _anchor = ctrl ? NextWord(_caret) : Math.Min(_caret + 1, _text.Length);
                DeleteSelection();
            }
        }

        /// <summary>
        /// Draws the text with its selection and cursor, at most <paramref name="width"/> wide.
        /// Long text scrolls to keep the cursor in view.
        /// </summary>
        public void Draw(Font font, Vector2 position, float width, float size, Color color)
        {
            string text = _text.ToString();
            float lineHeight = Canvas.LineHeight(font, size);

            float Width(int from, int to) => to > from ? Canvas.MeasureText(font, text[from..to], size).X : 0f;

            // Scroll just enough to keep the cursor in the box, and back again as text is deleted.
            _scroll = Math.Min(_scroll, _caret);
            while (_scroll < _caret && Width(_scroll, _caret) > width) _scroll++;
            while (_scroll > 0 && Width(_scroll - 1, text.Length) <= width) _scroll--;

            int end = text.Length;
            while (end > _caret && Width(_scroll, end) > width) end--;

            int selectionStart = Math.Clamp(SelectionStart, _scroll, end);
            int selectionEnd = Math.Clamp(SelectionEnd, _scroll, end);
            if (selectionEnd > selectionStart)
            {
                float from = position.X + Width(_scroll, selectionStart);
                Canvas.DrawRect(new Vector2(from, position.Y + 1f), new Vector2(Width(selectionStart, selectionEnd), lineHeight - 2f), _selectionColor);
            }

            Canvas.DrawText(font, text[_scroll..end], position, color, size);

            // Solid while typing or moving, blinking once idle.
            if ((Time.TotalTime - _caretMoved) % 1f < 0.5f)
            {
                Canvas.DrawRect(new Vector2(position.X + Width(_scroll, _caret), position.Y + 2f), new Vector2(1.5f, lineHeight - 4f), color);
            }
        }

        /// <summary>Types text at the cursor, replacing any selection. Line breaks and tabs become spaces; other control characters are dropped.</summary>
        public void Insert(string text)
        {
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c is '\n' or '\t') clean.Append(' ');
                else if (!char.IsControl(c)) clean.Append(c);
            }

            if (clean.Length == 0) return;

            DeleteSelection();
            int room = MaxLength - _text.Length;
            if (room <= 0) return;

            string inserted = clean.ToString(0, Math.Min(clean.Length, room));
            _text.Insert(_caret, inserted);
            MoveCaret(_caret + inserted.Length, extendSelection: false);
        }

        private void DeleteSelection()
        {
            if (!HasSelection) return;

            int start = SelectionStart;
            _text.Remove(start, SelectionEnd - start);
            MoveCaret(start, extendSelection: false);
        }

        private void MoveCaret(int position, bool extendSelection)
        {
            _caret = Math.Clamp(position, 0, _text.Length);
            if (!extendSelection) _anchor = _caret;
            _caretMoved = Time.TotalTime;
        }

        /// <summary>The start of the word before <paramref name="position"/>, skipping any spaces first.</summary>
        private int PreviousWord(int position)
        {
            while (position > 0 && _text[position - 1] == ' ') position--;
            while (position > 0 && _text[position - 1] != ' ') position--;
            return position;
        }

        /// <summary>The start of the next word after <paramref name="position"/>.</summary>
        private int NextWord(int position)
        {
            while (position < _text.Length && _text[position] != ' ') position++;
            while (position < _text.Length && _text[position] == ' ') position++;
            return position;
        }

        /// <summary>True when <paramref name="key"/> is first pressed, then repeatedly while it's held, like typing.</summary>
        private bool Repeating(Key key)
        {
            if (!InputSystem.GetKey(key))
            {
                _held.Remove(key);
                return false;
            }

            if (!_held.TryGetValue(key, out float before))
            {
                _held[key] = 0f;
                return true;
            }

            float after = before + Time.DeltaTime;
            _held[key] = after;
            return Repeats(after) > Repeats(before);

            static int Repeats(float held) => held < _repeatDelay ? 0 : (int)((held - _repeatDelay) / _repeatInterval) + 1;
        }
    }
}
