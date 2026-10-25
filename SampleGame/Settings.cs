using System.Text.Json;

using Vertex.Audio;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The player's own settings, saved between runs in their app data folder. Sensitivity and field of view follow
    /// Source's conventions, so numbers from CS:S feel the same here.
    /// </summary>
    public sealed class Settings
    {
        // Source turns this many degrees per mouse count, times the sensitivity (its m_yaw and m_pitch).
        private const float _degreesPerCount = 0.022f;

        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VertexSample", "settings.json");

        public static Settings Current { get; } = Load();

        public float Sensitivity { get; set; } = 4.5f;

        /// <summary>Horizontal field of view in degrees, measured on a 4:3 screen like Source's fov_desired. Wider screens see more.</summary>
        public float FieldOfView { get; set; } = 90f;

        /// <summary>0 to 1.</summary>
        public float Volume { get; set; } = 1f;

        public float UiScale { get; set; } = 1f;

        /// <summary>
        /// Puts the settings into effect. The interface size can be left out, for while its slider is being dragged:
        /// rescaling the UI would move the slider out from under the mouse.
        /// </summary>
        public void Apply(Player player, bool includeUiScale = true)
        {
            Sensitivity = Math.Clamp(Sensitivity, 0.1f, 20f);
            FieldOfView = Math.Clamp(FieldOfView, 60f, 130f);
            Volume = Math.Clamp(Volume, 0f, 1f);
            UiScale = Math.Clamp(UiScale, 0.5f, 2f);

            player.LookSensitivity = Sensitivity * _degreesPerCount;
            player.FieldOfView = VerticalFieldOfView(FieldOfView);
            Sound.Volume = Volume;
            if (includeUiScale) Canvas.UiScale = UiScale;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Couldn't save settings: {exception.Message}");
            }
        }

        private static Settings Load()
        {
            try
            {
                if (File.Exists(_path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path)) ?? new Settings();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.WriteLine($"Couldn't read settings, using defaults: {exception.Message}");
            }

            return new Settings();
        }

        /// <summary>The camera's vertical angle for a horizontal one on a 4:3 screen.</summary>
        private static float VerticalFieldOfView(float horizontal43)
        {
            float halfHorizontal = horizontal43 * MathF.PI / 360f;
            return MathF.Atan(MathF.Tan(halfHorizontal) * 3f / 4f) * 360f / MathF.PI;
        }
    }
}
