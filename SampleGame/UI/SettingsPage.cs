using System.Numerics;

using Vertex;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The settings page, shared by the main menu and the pause menu: sliders for the mouse, field of view, volume and
    /// interface size, and the fullscreen switch. Changes apply as they're made; <see cref="Leave"/> saves them.
    /// </summary>
    public static class SettingsPage
    {
        private const float _width = 420f;
        private const float _rowHeight = 64f;
        private const float _toggleRowHeight = 40f;
        private const float _buttonWidth = 280f;
        private const float _buttonHeight = 46f;

        /// <summary>Draws the page, centred. True when Back is clicked.</summary>
        public static bool Draw(Player player)
        {
            const float rowsHeight = 4 * _rowHeight + _toggleRowHeight;
            Settings settings = Settings.Current;

            float x = (Canvas.Width - _width) / 2f;
            float y = (Canvas.Height - (rowsHeight + _buttonHeight + 20f)) / 2f + 20f;
            MenuText.Title("Settings", y - 80f);

            // A panel behind the controls, so they're readable over whatever's in view.
            Canvas.DrawRect(new Vector2(x - 28f, y - 22f), new Vector2(_width + 56f, rowsHeight + 24f), new Color(0.05f, 0.06f, 0.08f, 0.75f));

            float sensitivity = Gui.Slider(Fonts.Regular, "Mouse sensitivity", settings.Sensitivity, 0.5f, 10f, new Vector2(x, y), _width, "0.0");
            y += _rowHeight;
            float fov = Gui.Slider(Fonts.Regular, "Field of view", settings.FieldOfView, 70f, 110f, new Vector2(x, y), _width, "0°");
            y += _rowHeight;
            float volume = Gui.Slider(Fonts.Regular, "Volume", settings.Volume * 100f, 0f, 100f, new Vector2(x, y), _width, "0'%'") / 100f;
            y += _rowHeight;
            float uiScale = Gui.Slider(Fonts.Regular, "Interface size", settings.UiScale, 0.75f, 1.5f, new Vector2(x, y), _width, "0.00×");
            y += _rowHeight;
            settings.Fullscreen = Gui.Toggle(Fonts.Regular, "Fullscreen (borderless)", settings.Fullscreen, new Vector2(x, y), _width);
            y += _toggleRowHeight + 10f;

            // Applied straight away, so the change can be felt while dragging (fullscreen switches on the click); the
            // interface size waits until Back.
            settings.Sensitivity = MathF.Round(sensitivity, 1);
            settings.FieldOfView = MathF.Round(fov);
            settings.Volume = MathF.Round(volume, 2);
            settings.UiScale = MathF.Round(uiScale, 2);
            settings.Apply(player, includeUiScale: false);

            return Gui.Button(Fonts.Bold, "Back", new Vector2((Canvas.Width - _buttonWidth) / 2f, y), new Vector2(_buttonWidth, _buttonHeight));
        }

        /// <summary>Applies everything, the interface size too, and saves.</summary>
        public static void Leave(Player player)
        {
            Settings.Current.Apply(player);
            Settings.Current.Save();
        }
    }
}
