namespace Vertex
{
    public static class Time
    {
        /// <summary>Seconds since the last frame, sped up or slowed down by <see cref="Scale"/>.</summary>
        public static float DeltaTime { get; private set; }

        /// <summary>Seconds since the last frame as they really passed, whatever <see cref="Scale"/> is: for menus and the like.</summary>
        public static float UnscaledDeltaTime { get; private set; }

        /// <summary>Game seconds since the game started: they go slower while <see cref="Scale"/> is below 1.</summary>
        public static float TotalTime { get; private set; }

        /// <summary>Seconds since the game started as they really passed, whatever <see cref="Scale"/> has been.</summary>
        public static float UnscaledTotalTime { get; private set; }

        /// <summary>
        /// How fast the game's time goes: 1 is normal, 0.5 half speed, 0 stopped. Physics, <see cref="DeltaTime"/> and
        /// <see cref="TotalTime"/> all follow it. A moment of almost nothing as a big hit lands (a hit-stop) makes it feel
        /// heavier.
        /// </summary>
        public static float Scale
        {
            get => _scale;
            set => _scale = float.IsFinite(value) ? Math.Max(value, 0f) : 1f;
        }

        private static float _scale = 1f;

        /// <summary>Seconds per physics step. FixedUpdate and the physics simulation always run at this rate.</summary>
        public static float FixedDeltaTime { get; set; } = 1f / 60f;

        /// <summary>
        /// How far this frame is between the last physics step and the next, from 0 to 1. Things moved in FixedUpdate can
        /// be drawn this far from their previous step's position to their latest, to move smoothly however fast the
        /// frame rate, as physics objects are.
        /// </summary>
        public static float FixedAlpha { get; internal set; }

        /// <summary>
        /// <see cref="TotalTime"/> kept to more places: what's worked out from the difference between two times (as GPU
        /// particles' steps are) stays even after hours, when a float's seconds have grown too coarse.
        /// </summary>
        internal static double PreciseTotalTime { get; private set; }

        private static double _unscaledTotal;

        internal static void Advance(double deltaTime)
        {
            UnscaledDeltaTime = (float)deltaTime;
            _unscaledTotal += deltaTime;
            UnscaledTotalTime = (float)_unscaledTotal;

            deltaTime *= _scale;
            DeltaTime = (float)deltaTime;
            PreciseTotalTime += deltaTime;
            TotalTime = (float)PreciseTotalTime;
        }
    }
}
