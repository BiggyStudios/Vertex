using System.Globalization;
using System.Numerics;
using System.Text;

using Silk.NET.Input;

using Vertex.Input;

namespace Vertex.UI
{
    /// <summary>
    /// A Source-style developer console. The key left of 1 (` or ~) opens it over the top of the screen. It shows
    /// everything written to the console, and runs commands: typing a variable's name shows its value, and a name
    /// followed by a value sets it. Tab completes names, Up and Down go through earlier commands, Page Up/Down or the
    /// mouse wheel scroll, and several commands can be given at once separated by semicolons.
    /// <para>Games add their own with <see cref="AddCommand"/> and <see cref="AddVariable"/>. Set <see cref="Font"/> to show it.</para>
    /// </summary>
    public static class DevConsole
    {
        private const float _fontSize = 15f;
        private const int _maxLines = 1000;
        private const int _maxSuggestions = 8;

        private static readonly Color _textColor = new(0.85f, 0.85f, 0.85f);
        private static readonly Color _echoColor = new(0.6f, 0.75f, 1f);
        private static readonly Color _errorColor = new(1f, 0.45f, 0.4f);

        public static Font? Font { get; set; }
        public static Key ToggleKey { get; set; } = Key.GraveAccent;
        public static bool IsOpen { get; private set; }

        /// <summary>A command or variable. Variables have a <see cref="Value"/>, which is shown when listing them.</summary>
        private sealed record Entry(string Name, string Description, Action<string[]> Run, Func<string>? Value);

        private static readonly SortedDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(string Text, Color Color)> _lines = new();
        private static readonly List<string> _history = new();
        private static readonly TextField _input = new() { MaxLength = 512 };
        private static readonly object _linesLock = new();
        private static int _historyIndex;
        private static int _scroll;
        private static bool _initialised;

        /// <summary>Adds a command. <paramref name="run"/> gets the words typed after its name.</summary>
        public static void AddCommand(string name, string description, Action<string[]> run)
        {
            _entries[name] = new Entry(name, description, run, null);
        }

        /// <summary>
        /// Adds a number variable. Typing its name shows the value; its name and a number calls <paramref name="set"/>.
        /// For on/off settings, use 1 and 0.
        /// </summary>
        public static void AddVariable(string name, string description, Func<float> get, Action<float> set)
        {
            _entries[name] = new Entry(name, description, args =>
            {
                if (args.Length == 0)
                {
                    Print($"{name} = {Format(get())}  ({description})");
                    return;
                }

                if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Error($"{name} needs a number, like: {name} {Format(get())}");
                    return;
                }

                set(value);
            }, () => Format(get()));
        }

        public static void Print(string text) => AddLine(text, _textColor);

        public static void Error(string text) => AddLine(text, _errorColor);

        /// <summary>Runs one or more commands, separated by semicolons, as if typed into the console.</summary>
        public static void Execute(string line)
        {
            foreach (string command in line.Split(';'))
            {
                List<string> words = Split(command);
                if (words.Count == 0) continue;

                if (!_entries.TryGetValue(words[0], out Entry? entry))
                {
                    Error($"Unknown command: {words[0]}. Type help for a list.");
                    continue;
                }

                try
                {
                    entry.Run(words.Skip(1).ToArray());
                }
                catch (Exception exception)
                {
                    Error($"{entry.Name} failed: {exception.Message}");
                }
            }
        }

        internal static void Initialise()
        {
            if (_initialised) return;
            _initialised = true;

            // Everything written to the console shows up here too.
            Console.SetOut(new CapturingWriter(Console.Out));

            AddCommand("help", "Lists commands and variables, or those containing the given text.", args =>
            {
                string filter = args.Length > 0 ? args[0] : "";
                foreach (Entry entry in _entries.Values.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    Print(entry.Value != null ? $"{entry.Name} = {entry.Value()}  {entry.Description}" : $"{entry.Name}  {entry.Description}");
                }
            });
            AddCommand("clear", "Clears the console.", _ =>
            {
                lock (_linesLock) _lines.Clear();
            });
            AddCommand("echo", "Prints the text given.", args => Print(string.Join(' ', args)));
        }

        /// <summary>Opening, closing and typing. Runs before the game's Update, so it can take keys first.</summary>
        internal static void Update()
        {
            if (InputSystem.GetKeyDown(ToggleKey))
            {
                SetOpen(!IsOpen);
                InputSystem.Consume(ToggleKey);

                // The key also types its character (` or ~); nothing should receive it.
                InputSystem.ConsumeTypedText();
                return;
            }

            if (!IsOpen) return;

            if (InputSystem.GetKeyDown(Key.Escape))
            {
                SetOpen(false);
                InputSystem.Consume(Key.Escape);
                return;
            }

            if (InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter))
            {
                string command = _input.Text.Trim();
                _input.Clear();
                _scroll = 0;

                if (command.Length > 0)
                {
                    AddLine("] " + command, _echoColor);
                    if (_history.Count == 0 || _history[^1] != command) _history.Add(command);
                    _historyIndex = _history.Count;
                    Execute(command);
                }
            }

            if (InputSystem.GetKeyDown(Key.Tab)) Complete();

            if (InputSystem.GetKeyDown(Key.Up) && _history.Count > 0)
            {
                _historyIndex = Math.Max(_historyIndex - 1, 0);
                _input.SetText(_history[_historyIndex]);
            }

            if (InputSystem.GetKeyDown(Key.Down) && _history.Count > 0)
            {
                _historyIndex = Math.Min(_historyIndex + 1, _history.Count);
                _input.SetText(_historyIndex < _history.Count ? _history[_historyIndex] : "");
            }

            int page = Math.Max(VisibleLines() - 2, 1);
            if (InputSystem.GetKeyDown(Key.PageUp)) _scroll += page;
            if (InputSystem.GetKeyDown(Key.PageDown)) _scroll -= page;
            _scroll += (int)MathF.Round(InputSystem.ScrollDelta * 3f);
            lock (_linesLock) _scroll = Math.Clamp(_scroll, 0, Math.Max(_lines.Count - 1, 0));

            _input.Update();

            // The console has the keyboard: nothing behind it gets the typing.
            InputSystem.ConsumeTypedText();
            InputSystem.Consume(Key.Enter);
            InputSystem.Consume(Key.KeypadEnter);
        }

        /// <summary>Draws the console over everything else. Runs after the game has drawn its UI.</summary>
        internal static void Draw()
        {
            if (!IsOpen || Font == null) return;

            float lineHeight = Canvas.LineHeight(Font, _fontSize);
            float height = MathF.Round(Canvas.Height * 0.45f);
            const float margin = 12f;

            Canvas.DrawRect(Vector2.Zero, new Vector2(Canvas.Width, height), new Color(0.05f, 0.06f, 0.08f, 0.92f));
            Canvas.DrawRect(new Vector2(0f, height), new Vector2(Canvas.Width, 2f), new Color(0.95f, 0.6f, 0.25f, 0.8f));

            // The typing line at the bottom, then the log above it, newest last.
            float inputY = height - lineHeight - margin;
            Canvas.DrawRect(new Vector2(margin - 4f, inputY - 3f), new Vector2(Canvas.Width - margin * 2f + 8f, lineHeight + 6f), new Color(1f, 1f, 1f, 0.06f));
            float promptWidth = Canvas.DrawText(Font, "] ", new Vector2(margin, inputY), _echoColor, _fontSize);
            _input.Draw(Font, new Vector2(margin + promptWidth, inputY), Canvas.Width - margin * 2f - promptWidth, _fontSize, Color.White);

            lock (_linesLock)
            {
                float y = inputY - 8f;
                int last = _lines.Count - 1 - _scroll;
                for (int i = last; i >= 0 && y - lineHeight > 0f; i--)
                {
                    y -= lineHeight;
                    Canvas.DrawText(Font, _lines[i].Text, new Vector2(margin, y), _lines[i].Color, _fontSize);
                }

                if (_scroll > 0)
                {
                    string more = $"↓ {_scroll} newer";
                    Canvas.DrawText(Font, more, new Vector2(Canvas.Width - margin - Canvas.MeasureText(Font, more, _fontSize).X, inputY - lineHeight - 8f), _echoColor, _fontSize);
                }
            }

            DrawSuggestions(new Vector2(margin + promptWidth, height + 4f), lineHeight);
        }

        /// <summary>Under the console: names starting with what's typed, with their values, or help for the command being typed.</summary>
        private static void DrawSuggestions(Vector2 position, float lineHeight)
        {
            string text = _input.Text;
            if (text.Length == 0 || Font == null) return;

            List<(string Left, string Right)> rows;
            int space = text.IndexOf(' ');
            if (space < 0)
            {
                rows = Matches(text).Take(_maxSuggestions).Select(e => (e.Name, e.Value != null ? e.Value() : "")).ToList();
            }
            else if (_entries.TryGetValue(text[..space], out Entry? typing))
            {
                rows = [(typing.Name, typing.Value != null ? $"{typing.Value()}  {typing.Description}" : typing.Description)];
            }
            else
            {
                return;
            }

            if (rows.Count == 0) return;

            float nameWidth = rows.Max(row => Canvas.MeasureText(Font, row.Left, _fontSize).X) + 24f;
            float width = MathF.Min(nameWidth + rows.Max(row => Canvas.MeasureText(Font, row.Right, _fontSize).X) + 16f, Canvas.Width - position.X - 12f);
            Canvas.DrawRect(position - new Vector2(8f, 0f), new Vector2(width + 8f, rows.Count * lineHeight + 8f), new Color(0.08f, 0.09f, 0.12f, 0.95f));

            float y = position.Y + 4f;
            foreach ((string left, string right) in rows)
            {
                Canvas.DrawText(Font, left, new Vector2(position.X, y), Color.White, _fontSize);
                Canvas.DrawText(Font, right, new Vector2(position.X + nameWidth, y), new Color(1f, 1f, 1f, 0.55f), _fontSize);
                y += lineHeight;
            }
        }

        /// <summary>Tab: completes a unique name, or as much as all the matching names share.</summary>
        private static void Complete()
        {
            string text = _input.Text;
            if (text.Length == 0 || text.Contains(' ')) return;

            List<Entry> matches = Matches(text).ToList();
            if (matches.Count == 1)
            {
                _input.SetText(matches[0].Name + " ");
                return;
            }

            if (matches.Count == 0) return;

            string shared = matches[0].Name;
            foreach (Entry entry in matches)
            {
                int length = 0;
                while (length < shared.Length && length < entry.Name.Length && char.ToLowerInvariant(shared[length]) == char.ToLowerInvariant(entry.Name[length])) length++;
                shared = shared[..length];
            }

            if (shared.Length > text.Length) _input.SetText(shared);
        }

        private static IEnumerable<Entry> Matches(string prefix) =>
            _entries.Values.Where(entry => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static void SetOpen(bool open)
        {
            IsOpen = open;
            _scroll = 0;
            if (open) _historyIndex = _history.Count;
        }

        private static int VisibleLines() => Font == null ? 10 : (int)(Canvas.Height * 0.45f / Canvas.LineHeight(Font, _fontSize)) - 2;

        private static void AddLine(string text, Color color)
        {
            lock (_linesLock)
            {
                foreach (string line in text.Split('\n')) _lines.Add((line.TrimEnd('\r'), color));
                if (_lines.Count > _maxLines) _lines.RemoveRange(0, _lines.Count - _maxLines);
            }
        }

        /// <summary>Words separated by spaces, with "quoted text" kept together.</summary>
        private static List<string> Split(string command)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            bool quoted = false;

            foreach (char c in command.Trim())
            {
                if (c == '"') quoted = !quoted;
                else if (c == ' ' && !quoted)
                {
                    if (word.Length > 0) words.Add(word.ToString());
                    word.Clear();
                }
                else word.Append(c);
            }

            if (word.Length > 0) words.Add(word.ToString());
            return words;
        }

        private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Passes writes on to the real console, and copies each finished line into this one.</summary>
        private sealed class CapturingWriter(TextWriter original) : TextWriter
        {
            private readonly StringBuilder _line = new();

            public override Encoding Encoding => original.Encoding;

            public override void Write(char value)
            {
                original.Write(value);

                lock (_line)
                {
                    if (value == '\n')
                    {
                        AddLine(_line.ToString(), _textColor);
                        _line.Clear();
                    }
                    else if (value != '\r')
                    {
                        _line.Append(value);
                    }
                }
            }

            public override void Write(string? value)
            {
                if (value == null) return;
                foreach (char c in value) Write(c);
            }

            public override void Flush() => original.Flush();
        }
    }
}
