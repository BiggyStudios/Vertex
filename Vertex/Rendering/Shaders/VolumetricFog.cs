using System.Numerics;

namespace Vertex.Rendering.Shaders
{
    /// <summary>
    /// One view's volumetric fog between frames: how big its grid of cells is, which of its two images of lit fog is
    /// written this frame (the other is last frame's, read back), and where the camera was, so each cell can find what
    /// it was last frame. Each backend keeps one with each view's images.
    /// </summary>
    internal sealed class VolumetricFog
    {
        /// <summary>How much of last frame's fog each cell keeps: most of it, so a few frames' samples add up smoothly.</summary>
        public const float Keep = 0.9f;

        private Matrix4x4 _previousViewProjection;
        private bool _hasHistory;
        private int _frame;

        /// <summary>Which of the two images of lit fog this frame writes; the other holds last frame's.</summary>
        public int Current { get; private set; }

        /// <summary>
        /// How many cells across and down the fog is worked out in for a view this size: one for every 8 pixels or so,
        /// coarser on very big views so it doesn't take much longer.
        /// </summary>
        public static (int Width, int Height) GridSize(int width, int height)
        {
            int pixels = Math.Max(8, (int)MathF.Ceiling(height / 135f));
            return (Math.Max((width + pixels - 1) / pixels, 1), Math.Max((height + pixels - 1) / pixels, 1));
        }

        /// <summary>
        /// Fills in what <paramref name="uniforms"/> needs from last frame for <paramref name="frame"/>, and remembers this
        /// one for the next. <paramref name="fresh"/>: the images were just made, so there's nothing to carry on.
        /// </summary>
        public void Prepare(ref FrameUniforms uniforms, RenderFrame frame, int width, int height, bool fresh)
        {
            if (fresh) _hasHistory = false;
            Current = 1 - Current;

            Matrix4x4 viewProjection = frame.ViewMatrix * frame.ProjectionMatrix;
            uniforms.FogGrid = new Vector4(width, height, ShaderLibrary.FogSlices, 0f);
            uniforms.FogWind.W = _frame;
            uniforms.FogReach.W = _hasHistory ? Keep : 0f;
            uniforms.PreviousViewProjection = _hasHistory ? _previousViewProjection : viewProjection;

            _frame = (_frame + 1) % 1024;
            _previousViewProjection = viewProjection;
            _hasHistory = true;
        }

        /// <summary>A frame drawn without fog: what's in the images is out of date by the next one with it.</summary>
        public void Forget() => _hasHistory = false;
    }
}
