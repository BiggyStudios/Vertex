namespace Vertex.Rendering
{
    /// <summary>How long one part of drawing a frame took on the GPU, like "Shadows" or "Bloom".</summary>
    public readonly record struct GpuPassTime(string Pass, double Milliseconds);

    /// <summary>
    /// What drawing the last frame took (see <see cref="Renderer.Stats"/>). The GPU's times are only measured while
    /// <see cref="Renderer.MeasureGpuTime"/> is on, and come in a frame or two late, as the GPU finishes a frame after
    /// the CPU has moved on.
    /// </summary>
    public sealed class RenderStats
    {
        internal readonly List<GpuPassTime> GpuPassList = new();

        /// <summary>How many views were drawn: the screen and each camera drawing into a texture.</summary>
        public int Views { get; internal set; }

        /// <summary>Meshes drawn, over all views.</summary>
        public int DrawCalls { get; internal set; }

        /// <summary>Meshes not drawn because they were out of view.</summary>
        public int CulledDrawCalls { get; internal set; }

        /// <summary>Meshes drawn into shadow maps, counted once for each cascade they're drawn into.</summary>
        public int ShadowDrawCalls { get; internal set; }

        /// <summary>Time the CPU spent on drawing: gathering the scene and handing it to the GPU.</summary>
        public double CpuMilliseconds { get; internal set; }

        /// <summary>The GPU's time for each part of the frame, in the order they ran. Empty when not measuring.</summary>
        public IReadOnlyList<GpuPassTime> GpuPasses => GpuPassList;

        /// <summary>The GPU's time for the whole frame. 0 when not measuring.</summary>
        public double GpuMilliseconds
        {
            get
            {
                double total = 0;
                foreach (GpuPassTime pass in GpuPassList) total += pass.Milliseconds;
                return total;
            }
        }
    }

    /// <summary>Turns the GPU's timestamps at the start of each pass into how long each pass took.</summary>
    internal static class GpuTimestamps
    {
        /// <summary>
        /// Fills <paramref name="into"/> from <paramref name="ticks"/>, one taken as each of <paramref name="names"/>
        /// began (the last only marks the end). Passes with the same name, as in several views, are added together.
        /// </summary>
        public static void Measure(List<GpuPassTime> into, IReadOnlyList<string> names, ReadOnlySpan<ulong> ticks, double millisecondsPerTick)
        {
            into.Clear();
            for (int i = 0; i + 1 < names.Count && i + 1 < ticks.Length; i++)
            {
                // A tick count going backwards means a result that wasn't ready: no times this frame rather than wrong ones.
                if (ticks[i + 1] < ticks[i])
                {
                    into.Clear();
                    return;
                }

                double milliseconds = (ticks[i + 1] - ticks[i]) * millisecondsPerTick;
                int existing = 0;
                while (existing < into.Count && into[existing].Pass != names[i]) existing++;

                if (existing < into.Count) into[existing] = into[existing] with { Milliseconds = into[existing].Milliseconds + milliseconds };
                else into.Add(new GpuPassTime(names[i], milliseconds));
            }
        }
    }
}
