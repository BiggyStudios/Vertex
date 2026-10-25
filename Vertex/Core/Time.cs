namespace Vertex
{
    public static class Time
    {
        /// <summary>Seconds since the last frame.</summary>
        public static float DeltaTime { get; private set; }

        /// <summary>Seconds since the game started.</summary>
        public static float TotalTime { get; private set; }

        /// <summary>Seconds per physics step. FixedUpdate and the physics simulation always run at this rate.</summary>
        public static float FixedDeltaTime { get; set; } = 1f / 60f;

        /// <summary>
        /// How far this frame is between the last physics step and the next, from 0 to 1. Things moved in FixedUpdate can
        /// be drawn this far from their previous step's position to their latest, to move smoothly however fast the
        /// frame rate, as physics objects are.
        /// </summary>
        public static float FixedAlpha { get; internal set; }

        internal static void Advance(double deltaTime)
        {
            DeltaTime = (float)deltaTime;
            TotalTime += DeltaTime;
        }
    }
}
