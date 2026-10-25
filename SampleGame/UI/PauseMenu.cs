using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Networking;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Escape opens it: Resume, Change class, Settings, Disconnect and Quit. The game keeps running behind it, as in any multiplayer
    /// game. Settings are saved as soon as you leave the settings page. It also looks after the mouse cursor, which is
    /// free while a menu or the console is open and locked to the game otherwise.
    /// </summary>
    public class PauseMenu : Component
    {
        private const float _buttonWidth = 280f;
        private const float _buttonHeight = 46f;
        private const float _gap = 10f;

        public static bool IsOpen { get; private set; }

        public Player Player { get; set; } = null!;
        public MultiplayerManager Multiplayer { get; set; } = null!;
        public ClassMenu ClassMenu { get; set; } = null!;

        /// <summary>What the Quit button does.</summary>
        public Action? Quit { get; set; }

        private bool _onSettingsPage;

        protected override void Update()
        {
            // Chat, the console and the class menu handle Escape themselves (and consume it) while they're open.
            if (InputSystem.GetKeyDown(Key.Escape) && !ChatBox.IsTyping && !DevConsole.IsOpen && !ClassMenu.IsOpen)
            {
                InputSystem.Consume(Key.Escape);
                if (!IsOpen) Open();
                else if (_onSettingsPage) LeaveSettings();
                else Close();
            }

            InputSystem.CursorLocked = !GameInput.LookBlocked;

            if (!IsOpen) return;

            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.02f, 0.03f, 0.05f, 0.6f));
            if (_onSettingsPage) DrawSettings();
            else DrawMain();
        }

        protected override void OnDestroy() => IsOpen = false;

        private void Open()
        {
            IsOpen = true;
            _onSettingsPage = false;
        }

        private void Close()
        {
            if (_onSettingsPage) LeaveSettings();
            IsOpen = false;
        }

        private void DrawMain()
        {
            var buttons = new List<(string Label, Action Action)>
            {
                ("Resume", Close),
                ("Change class", () => { Close(); ClassMenu.Open(); }),
                ("Settings", () => _onSettingsPage = true)
            };
            if (Network.InSession) buttons.Add(("Disconnect", () => { Multiplayer.Leave(); Close(); }));
            buttons.Add(("Quit", () => Quit?.Invoke()));

            float height = buttons.Count * (_buttonHeight + _gap) - _gap;
            float x = (Canvas.Width - _buttonWidth) / 2f;
            float y = (Canvas.Height - height) / 2f + 20f;

            DrawTitle("Paused", y - 80f);

            foreach ((string label, Action action) in buttons)
            {
                if (Gui.Button(Fonts.Bold, label, new Vector2(x, y), new Vector2(_buttonWidth, _buttonHeight))) action();
                y += _buttonHeight + _gap;
            }
        }

        private void DrawSettings()
        {
            const float width = 420f;
            const float rowHeight = 64f;
            Settings settings = Settings.Current;

            float x = (Canvas.Width - width) / 2f;
            float y = (Canvas.Height - (4 * rowHeight + _buttonHeight + 20f)) / 2f + 20f;
            DrawTitle("Settings", y - 80f);

            // A panel behind the sliders, so they're readable over whatever's in view.
            Canvas.DrawRect(new Vector2(x - 28f, y - 22f), new Vector2(width + 56f, 4 * rowHeight + 24f), new Color(0.05f, 0.06f, 0.08f, 0.75f));

            float sensitivity = Gui.Slider(Fonts.Regular, "Mouse sensitivity", settings.Sensitivity, 0.5f, 10f, new Vector2(x, y), width, "0.0");
            y += rowHeight;
            float fov = Gui.Slider(Fonts.Regular, "Field of view", settings.FieldOfView, 70f, 110f, new Vector2(x, y), width, "0°");
            y += rowHeight;
            float volume = Gui.Slider(Fonts.Regular, "Volume", settings.Volume * 100f, 0f, 100f, new Vector2(x, y), width, "0'%'") / 100f;
            y += rowHeight;
            float uiScale = Gui.Slider(Fonts.Regular, "Interface size", settings.UiScale, 0.75f, 1.5f, new Vector2(x, y), width, "0.00×");
            y += rowHeight + 10f;

            // Applied straight away, so the change can be felt while dragging; the interface size waits until Back.
            settings.Sensitivity = MathF.Round(sensitivity, 1);
            settings.FieldOfView = MathF.Round(fov);
            settings.Volume = MathF.Round(volume, 2);
            settings.UiScale = MathF.Round(uiScale, 2);
            settings.Apply(Player, includeUiScale: false);

            if (Gui.Button(Fonts.Bold, "Back", new Vector2((Canvas.Width - _buttonWidth) / 2f, y), new Vector2(_buttonWidth, _buttonHeight))) LeaveSettings();
        }

        private void LeaveSettings()
        {
            _onSettingsPage = false;
            Settings.Current.Apply(Player);
            Settings.Current.Save();
        }

        private static void DrawTitle(string text, float y)
        {
            float width = Canvas.MeasureText(Fonts.Bold, text, 40f).X;
            Canvas.DrawText(Fonts.Bold, text, new Vector2((Canvas.Width - width) / 2f, y), Color.White, 40f, shadow: true);
        }
    }
}
