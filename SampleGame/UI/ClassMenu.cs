using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Pick a class, like CS's team and model select: it opens when the game starts, and B (or Change class in the pause
    /// menu) brings it back. Each class is a card with its weapon and a line about how it plays; click one or press its
    /// number. The change is immediate. Escape or B closes it without changing.
    /// </summary>
    public class ClassMenu : Component
    {
        private const float _cardWidth = 300f;
        private const float _cardHeight = 150f;
        private const float _gap = 30f;
        private const float _padding = 22f;

        private static readonly Color _panel = new(0.07f, 0.08f, 0.1f, 0.85f);
        private static readonly Color _panelHovered = new(0.12f, 0.13f, 0.16f, 0.92f);
        private static readonly Color _dim = new(1f, 1f, 1f, 0.6f);

        public static bool IsOpen { get; private set; }

        public Player Player { get; set; } = null!;

        /// <summary>Open the menu when the game starts, so new players choose a class first.</summary>
        public bool OpenOnStart { get; set; } = true;

        // The click that opened the menu (from the pause menu) mustn't also pick whichever card is under the pointer.
        private bool _ignoreClick;

        protected override void Start()
        {
            if (OpenOnStart) Open();
        }

        protected override void OnDestroy() => IsOpen = false;

        public void Open()
        {
            IsOpen = true;
            _ignoreClick = InputSystem.GetMouseButton(MouseButton.Left);
        }

        public void Close() => IsOpen = false;

        protected override void Update()
        {
            bool otherInputOpen = ChatBox.IsTyping || DevConsole.IsOpen || PauseMenu.IsOpen;

            if (!IsOpen)
            {
                if (InputSystem.GetKeyDown(Key.B) && !otherInputOpen) Open();
                return;
            }

            if (!InputSystem.GetMouseButton(MouseButton.Left)) _ignoreClick = false;

            if (!DevConsole.IsOpen)
            {
                if (InputSystem.GetKeyDown(Key.Escape) || InputSystem.GetKeyDown(Key.B))
                {
                    InputSystem.Consume(Key.Escape);
                    Close();
                    return;
                }

                for (int i = 0; i < PlayerClass.All.Count && i < 9; i++)
                {
                    if (InputSystem.GetKeyDown(Key.Number1 + i))
                    {
                        // So the same press doesn't also switch weapon slot once the menu is gone.
                        InputSystem.Consume(Key.Number1 + i);
                        Choose(PlayerClass.All[i]);
                        return;
                    }
                }
            }

            Draw();
        }

        private void Choose(PlayerClass playerClass)
        {
            Player.SetClass(playerClass);
            Close();
        }

        private void Draw()
        {
            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.02f, 0.03f, 0.05f, 0.55f));

            IReadOnlyList<PlayerClass> classes = PlayerClass.All;
            float totalWidth = classes.Count * (_cardWidth + _gap) - _gap;
            float x = (Canvas.Width - totalWidth) / 2f;
            float y = (Canvas.Height - _cardHeight) / 2f + 20f;

            DrawCentred(Fonts.Bold, "Choose your class", y - 92f, 40f, Color.White);
            DrawCentred(Fonts.Regular, "Click a class or press its number. B to change class later.", y - 40f, 17f, _dim);

            for (int i = 0; i < classes.Count; i++)
            {
                var position = new Vector2(x + i * (_cardWidth + _gap), y);
                if (DrawCard(classes[i], i + 1, position) && !_ignoreClick && !DevConsole.IsOpen)
                {
                    Choose(classes[i]);
                    return;
                }
            }
        }

        /// <summary>Draws one class's card. True if it was clicked.</summary>
        private bool DrawCard(PlayerClass playerClass, int number, Vector2 position)
        {
            var size = new Vector2(_cardWidth, _cardHeight);
            bool hovered = Contains(position, size, Canvas.MousePosition);
            bool current = playerClass == Player.Class;
            Weapon weapon = playerClass.Weapon;

            Canvas.DrawRect(position, size, hovered ? _panelHovered : _panel);
            // A bar across the top, in the accent colour for the class you have now.
            Canvas.DrawRect(position, new Vector2(_cardWidth, 4f), current ? Gui.AccentColor : new Color(1f, 1f, 1f, hovered ? 0.5f : 0.2f));

            float left = position.X + _padding;
            float right = position.X + _cardWidth - _padding;
            float y = position.Y + _padding;

            // The key to press, in the top-right corner.
            string key = number.ToString();
            var badge = new Vector2(right - 26f, y);
            Canvas.DrawRect(badge, new Vector2(26f, 26f), new Color(1f, 1f, 1f, 0.12f));
            Canvas.DrawText(Fonts.Bold, key, badge + (new Vector2(26f) - Canvas.MeasureText(Fonts.Bold, key, 16f)) / 2f, Color.White, 16f);

            Canvas.DrawText(Fonts.Bold, playerClass.Name, new Vector2(left, y - 4f), Color.White, 30f);
            y += 40f;
            // Both weapons for a class with two, and "current" too if there's room for it.
            string weapons = playerClass.Secondary is { } secondary ? $"{weapon.Name} + {secondary.Name}" : weapon.Name;
            string weaponLine = current && Canvas.MeasureText(Fonts.Regular, $"{weapons} · current", 17f).X <= right - left ? $"{weapons} · current" : weapons;
            Canvas.DrawText(Fonts.Regular, weaponLine, new Vector2(left, y), Gui.AccentColor, 17f);
            y += 30f;

            foreach (string line in Wrap(Fonts.Regular, playerClass.Description, 16f, right - left))
            {
                Canvas.DrawText(Fonts.Regular, line, new Vector2(left, y), _dim, 16f);
                y += Canvas.LineHeight(Fonts.Regular, 16f);
            }

            return hovered && InputSystem.GetMouseButtonDown(MouseButton.Left);
        }

        /// <summary>Splits text into lines no wider than <paramref name="width"/>, breaking between words.</summary>
        private static List<string> Wrap(Font font, string text, float size, float width)
        {
            var lines = new List<string>();
            string line = "";

            foreach (string word in text.Split(' '))
            {
                string attempt = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && Canvas.MeasureText(font, attempt, size).X > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = attempt;
                }
            }

            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        private static void DrawCentred(Font font, string text, float y, float size, Color color)
        {
            float width = Canvas.MeasureText(font, text, size).X;
            Canvas.DrawText(font, text, new Vector2((Canvas.Width - width) / 2f, y), color, size, shadow: true);
        }

        private static bool Contains(Vector2 position, Vector2 size, Vector2 point) =>
            point.X >= position.X && point.Y >= position.Y && point.X < position.X + size.X && point.Y < position.Y + size.Y;
    }
}
