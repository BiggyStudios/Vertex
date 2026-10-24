using System.Numerics;

using Vertex.Audio.OpenAL;
using Vertex.Rendering;

namespace Vertex.Audio
{
    /// <summary>
    /// Plays sounds. You hear from <see cref="Camera.Main"/>: sounds placed with <see cref="PlayAt"/> get quieter with
    /// distance and come from their direction as the camera turns. If the computer has no audio device, nothing plays.
    /// </summary>
    public static class Sound
    {
        private static OpenALDevice? _device;

        /// <summary>Overall loudness, 0 to 1.</summary>
        public static float Volume { get; set; } = 1f;

        public static bool IsAvailable => _device != null;

        /// <summary>Plays a clip once, the same in both ears: for menus, or the player's own gun.</summary>
        public static void Play(AudioClip clip, float volume = 1f, float pitch = 1f)
        {
            _device?.Play(clip, position: null, volume, pitch, fullVolumeDistance: 0f);
        }

        /// <summary>
        /// Plays a mono clip once at a point in the world. Within <paramref name="fullVolumeDistance"/> it plays at full
        /// volume; beyond that it halves in loudness each time the distance doubles, like sound in open air.
        /// </summary>
        public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float fullVolumeDistance = 3f)
        {
            _device?.Play(clip, position, volume, pitch, fullVolumeDistance);
        }

        internal static void Initialise()
        {
            try
            {
                _device = new OpenALDevice();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Vertex] No audio: {exception.Message}");
            }
        }

        /// <summary>Moves the listener to the camera. Called once per frame.</summary>
        internal static void Update()
        {
            if (_device == null) return;

            _device.SetVolume(Volume);

            Camera? camera = Camera.Main;
            if (camera != null) _device.SetListener(camera.Transform.WorldPosition, camera.Transform.Forward, camera.Transform.Up);
        }

        internal static void Shutdown()
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
