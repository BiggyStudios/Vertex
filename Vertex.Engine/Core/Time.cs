namespace Vertex.Engine.Core
{
    /// <summary>
    /// Static class that provides timing information.
    /// </summary>
    public static class Time
    {
        private static double _deltaTime;
        private static double _totalTime;
        private static double _unscaledTotalTime;
        private static double _timeScale = 1.0;
        private static ulong _frameCount;

        /// <summary>
        /// Gets the time in seconds it took to complete the last frame, scaled by TimeScale.
        /// </summary>
        public static double DeltaTime => _deltaTime * _timeScale;

        /// <summary>
        /// Gets the time in seconds it took to complete the last frame, not scaled by TimeScale.
        /// </summary>
        public static double UnscaledDeltaTime => _deltaTime;

        /// <summary>
        /// Gets the total time in seconds since the application started, affected by TimeScale.
        /// </summary>
        public static double TotalTime => _totalTime;
        
        /// <summary>
        /// Gets the total time in seconds since the application started, not affected by TimeScale.
        /// </summary>
        public static double UnscaledTotalTime => _unscaledTotalTime;

        /// <summary>
        /// Gets or sets the scale at which time passes.
        /// 1.0 is normal time.
        /// Cannot be set to a negative value.
        /// </summary>
        public static double TimeScale
        {
            get => _timeScale;
            set => _timeScale = Math.Max(0f, value);
        }

        /// <summary>
        /// Gets the total number of frames that have been rendered since the application started.
        /// </summary>
        public static ulong FrameCount => _frameCount;

        /// <summary>
        /// Updates the timing information for the current frame.
        /// </summary>
        /// <param name="deltaTime">The time in seconds since the last frame.</param>
        internal static void Update(double deltaTime)
        {
            _deltaTime = deltaTime;
            _totalTime += deltaTime * _timeScale;
            _unscaledTotalTime += _deltaTime;
            _frameCount++;
        }
    }
}