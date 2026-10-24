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

        internal static void Advance(double deltaTime)
        {
            DeltaTime = (float)deltaTime;
            TotalTime += DeltaTime;
        }
    }
}
