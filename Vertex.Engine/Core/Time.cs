namespace Vertex.Engine.Core
{
    public static class Time
    {
        private static double _deltaTime;
        private static double _totalTime;
        private static double _timeScale = 1.0;
        private static ulong _frameCount;

        public static double DeltaTime => _deltaTime * _timeScale;
        public static double UnscaledDeltaTime => _deltaTime;
        public static double TotalTime => _totalTime;
        public static double TimeScale
        {
            get => _timeScale;
            set => _timeScale = Math.Max(0f, value);
        }

        public static ulong FrameCount => _frameCount;

        internal static void Update(double deltaTime)
        {
            _deltaTime = deltaTime;
            _totalTime += deltaTime * _timeScale;
            _frameCount++;
        }
    }
}