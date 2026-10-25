using System.Numerics;
using System.Text;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Chat in the bottom-left corner. Enter starts typing, Enter again sends, Escape cancels. While typing, movement
    /// and shooting are off (see <see cref="IsTyping"/>). Lines fade out after a while, and all come back while typing.
    /// Every line is also written to the console. The text box edits like any other (see <see cref="TextField"/>).
    /// </summary>
    public class ChatBox : Component
    {
        public const int MaxMessageLength = 120;

        private const float _fontSize = 17f;
        private const float _width = 520f;
        private const int _visibleLines = 8;
        private const float _lineLifetime = 10f;
        private const float _fadeTime = 1f;
        private const string _prompt = "Say: ";

        private static readonly Color _textColor = Color.White;
        private static readonly Color _systemColor = new(1f, 0.85f, 0.4f);

        /// <summary>True while the player is typing a message, so gameplay keys should be ignored.</summary>
        public static bool IsTyping { get; private set; }

        /// <summary>Raised with the text when the local player sends a message.</summary>
        public event Action<string>? MessageSent;

        /// <summary>A coloured name (or none, for game messages) and the message, as it arrived.</summary>
        private sealed record Message(string? Name, Color NameColor, string Text, Color TextColor, float Time);

        private readonly List<Message> _messages = new();
        private readonly TextField _input = new() { MaxLength = MaxMessageLength };

        public void AddMessage(string name, Color nameColor, string text)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {name}: {text}");
            Add(new Message(name, nameColor, text, _textColor, Time.TotalTime));
        }

        /// <summary>A message from the game rather than a player: joins, leaves, connection status.</summary>
        public void AddSystemMessage(string text)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
            Add(new Message(null, default, text, _systemColor, Time.TotalTime));
        }

        protected override void Update()
        {
            // The console takes the keyboard while it's open; the menu has nothing to type into.
            bool enter = InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter);
            if (IsTyping && !DevConsole.IsOpen) UpdateTyping(enter);
            else if (!IsTyping && enter && !PauseMenu.IsOpen && !DevConsole.IsOpen) StartTyping();

            Draw();
        }

        protected override void OnDestroy() => IsTyping = false;

        private void StartTyping()
        {
            IsTyping = true;
            _input.Clear();
        }

        private void UpdateTyping(bool enter)
        {
            _input.Update();

            if (InputSystem.GetKeyDown(Key.Escape))
            {
                // Handled here, so it doesn't also open the pause menu.
                InputSystem.Consume(Key.Escape);
                IsTyping = false;
            }
            else if (enter)
            {
                IsTyping = false;
                string text = _input.Text.Trim();
                if (text.Length > 0) MessageSent?.Invoke(text);
            }
        }

        private void Draw()
        {
            float lineHeight = Canvas.LineHeight(Fonts.Regular, _fontSize);
            float left = 28f;
            float bottom = Canvas.Height - 120f;

            if (IsTyping)
            {
                float boxTop = bottom - lineHeight - 8f;
                Canvas.DrawRect(new Vector2(left - 8f, boxTop), new Vector2(_width + 16f, lineHeight + 8f), new Color(0f, 0f, 0f, 0.55f));
                float promptWidth = Canvas.DrawText(Fonts.Regular, _prompt, new Vector2(left, boxTop + 3f), new Color(1f, 1f, 1f, 0.6f), _fontSize);
                _input.Draw(Fonts.Regular, new Vector2(left + promptWidth, boxTop + 3f), _width - promptWidth - 8f, _fontSize, Color.White);
                bottom = boxTop - 6f;
            }

            // Newest at the bottom. Each message may wrap onto several lines.
            int linesLeft = _visibleLines;
            for (int i = _messages.Count - 1; i >= 0 && linesLeft > 0; i--)
            {
                Message message = _messages[i];
                float age = Time.TotalTime - message.Time;
                float alpha = IsTyping ? 1f : Math.Clamp((_lineLifetime - age) / _fadeTime, 0f, 1f);
                if (alpha <= 0f) break;

                string prefix = message.Name != null ? message.Name + ": " : "";
                float prefixWidth = Canvas.MeasureText(Fonts.Bold, prefix, _fontSize).X;
                List<string> lines = Wrap(message.Text, _width - prefixWidth);

                for (int line = lines.Count - 1; line >= 0 && linesLeft > 0; line--, linesLeft--)
                {
                    bottom -= lineHeight;
                    var position = new Vector2(left + prefixWidth, bottom);
                    if (line == 0 && message.Name != null) Canvas.DrawText(Fonts.Bold, prefix, new Vector2(left, bottom), message.NameColor with { A = alpha }, _fontSize, shadow: true);
                    Canvas.DrawText(Fonts.Regular, lines[line], position, message.TextColor with { A = alpha }, _fontSize, shadow: true);
                }
            }
        }

        /// <summary>Splits text into lines no wider than <paramref name="width"/>, breaking between words where it can.</summary>
        private static List<string> Wrap(string text, float width)
        {
            var lines = new List<string>();
            var line = new StringBuilder();

            foreach (string word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Canvas.MeasureText(Fonts.Regular, candidate, _fontSize).X > width)
                {
                    lines.Add(line.ToString());
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }

                // A single word too long for a line gets broken wherever it fits.
                while (line.Length > 1 && Canvas.MeasureText(Fonts.Regular, line.ToString(), _fontSize).X > width)
                {
                    int fit = line.Length - 1;
                    while (fit > 1 && Canvas.MeasureText(Fonts.Regular, line.ToString(0, fit), _fontSize).X > width) fit--;
                    lines.Add(line.ToString(0, fit));
                    line.Remove(0, fit);
                }
            }

            lines.Add(line.ToString());
            return lines;
        }

        private void Add(Message message)
        {
            _messages.Add(message);
            if (_messages.Count > 50) _messages.RemoveAt(0);
        }
    }
}
