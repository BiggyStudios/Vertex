using Silk.NET.OpenGL;

namespace Vertex.Rendering.OpenGL
{
    /// <summary>
    /// Times each pass on the GPU with timestamp queries: one as each pass begins and one at the frame's end. The GPU
    /// runs behind, so each frame's are read a few frames later, and only once they're ready, so reading never waits.
    /// </summary>
    internal sealed unsafe class GLGpuTimer : IDisposable
    {
        // How many frames' queries are kept going round: more than the GPU will be behind.
        private const int _frames = 4;
        private const int _maxPerFrame = 64;

        private readonly GL _gl;
        private readonly uint[] _queries = new uint[_frames * _maxPerFrame];
        private readonly List<string>[] _names = new List<string>[_frames];
        private readonly ulong[] _ticks = new ulong[_maxPerFrame];
        private int _slot;
        private bool _timing;

        public bool Enabled { get; set; }

        public GLGpuTimer(GL gl)
        {
            _gl = gl;
            for (int i = 0; i < _frames; i++) _names[i] = new List<string>();
            fixed (uint* queries = _queries) _gl.GenQueries((uint)_queries.Length, queries);
        }

        /// <summary>
        /// Moves on to the next frame's queries, first reading the times they took last time round into
        /// <paramref name="times"/> if they're ready. True if it did.
        /// </summary>
        public bool BeginFrame(List<GpuPassTime> times)
        {
            _slot = (_slot + 1) % _frames;
            List<string> names = _names[_slot];
            bool read = false;

            if (names.Count >= 2)
            {
                int first = _slot * _maxPerFrame;
                _gl.GetQueryObject(_queries[first + names.Count - 1], QueryObjectParameterName.ResultAvailable, out int available);
                if (available != 0)
                {
                    for (int i = 0; i < names.Count; i++) _gl.GetQueryObject(_queries[first + i], QueryObjectParameterName.Result, out _ticks[i]);

                    // OpenGL's timestamps are in nanoseconds.
                    GpuTimestamps.Measure(times, names, _ticks.AsSpan(0, names.Count), 1e-6);
                    read = true;
                }
            }

            names.Clear();
            _timing = Enabled;
            return read;
        }

        /// <summary>Notes the time on the GPU as <paramref name="pass"/> begins, if timing. An empty name marks the frame's end.</summary>
        public void Mark(string pass)
        {
            List<string> names = _names[_slot];
            if (!_timing || names.Count >= _maxPerFrame) return;

            _gl.QueryCounter(_queries[_slot * _maxPerFrame + names.Count], QueryCounterTarget.Timestamp);
            names.Add(pass);
        }

        public void Dispose()
        {
            fixed (uint* queries = _queries) _gl.DeleteQueries((uint)_queries.Length, queries);
        }
    }
}
