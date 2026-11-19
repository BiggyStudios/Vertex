using System.Numerics;

namespace Vertex.Rendering.Shaders
{
    /// <summary>
    /// Where the sun's shadow maps go: <see cref="Count"/> of them (cascades), one after another along the view, each
    /// covering a stretch further out than the one before at the same resolution. Near the camera, where detail shows,
    /// shadows are sharp; far off they're softer but still there, out to the shadow distance.
    /// <para>
    /// Each cascade covers a ball round its stretch of the view rather than the stretch itself, so its size doesn't
    /// change as the camera turns, and its centre moves in whole shadow-map texels, so shadow edges don't shimmer as the
    /// camera moves. Both backends draw and read the cascades exactly as placed here.
    /// </para>
    /// </summary>
    internal sealed class ShadowCascades
    {
        public const int Count = ShaderLibrary.MaxCascades;
        public const int DefaultResolution = 2048;

        /// <summary>How many texels across each cascade's shadow map is (see Renderer.ShadowResolution).</summary>
        public int Resolution { get; set; } = DefaultResolution;

        // How far towards the sun past its cascade something can be and still cast a shadow into it: tall enough for
        // the highest maps.
        private const float _reach = 250f;

        // Between evenly spaced cascades (0) and each covering the same multiple further out (1): near ones are kept small.
        private const float _logarithmic = 0.82f;

        private const float _near = 0.1f;

        /// <summary>World to each cascade's shadow-map space.</summary>
        public readonly Matrix4x4[] Matrices = new Matrix4x4[Count];

        /// <summary>How far ahead of the camera each cascade reaches.</summary>
        public readonly float[] Splits = new float[Count];

        /// <summary>The world size of one shadow-map texel in each cascade.</summary>
        public readonly float[] TexelSizes = new float[Count];

        /// <summary>
        /// Places the cascades for the view in <paramref name="frame"/>, with sunlight travelling along
        /// <paramref name="lightDirection"/>.
        /// </summary>
        public void Place(RenderFrame frame, Vector3 lightDirection)
        {
            Matrix4x4 projection = frame.ProjectionMatrix;
            float tanX = 1f / projection.M11;
            float tanY = 1f / projection.M22;
            float spread = tanX * tanX + tanY * tanY;
            bool flatView = projection.M44 > 0.5f;

            float far = MathF.Max(frame.ShadowDistance, _near * 2f);
            Vector3 forward = Vector3.Normalize(frame.CameraForward);
            Vector3 up = MathF.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            Matrix4x4 rotation = Matrix4x4.CreateLookAt(Vector3.Zero, lightDirection, up);
            Matrix4x4.Invert(rotation, out Matrix4x4 inverseRotation);

            float start = _near;
            for (int i = 0; i < Count; i++)
            {
                float t = (i + 1f) / Count;
                float end = float.Lerp(_near + (far - _near) * t, _near * MathF.Pow(far / _near, t), _logarithmic);
                Splits[i] = end;

                // The smallest ball holding this stretch of the view: its centre is along the view, where the nearest and
                // furthest corners are equally far from it. An orthographic view's stretch is a box, the same size all
                // the way along (half its width and height are tanX and tanY then), so it's the box's middle.
                float centreDepth, radius;
                if (flatView)
                {
                    centreDepth = (start + end) * 0.5f;
                    radius = MathF.Sqrt((end - start) * (end - start) * 0.25f + spread);
                }
                else
                {
                    centreDepth = MathF.Min((end + start) * (1f + spread) * 0.5f, end);
                    radius = MathF.Sqrt((end - centreDepth) * (end - centreDepth) + end * end * spread);
                }

                radius = MathF.Ceiling(radius * 16f) / 16f;

                float texel = radius * 2f / Resolution;
                TexelSizes[i] = texel;

                // Snapped to whole texels across the sun's view.
                Vector3 centre = frame.CameraPosition + forward * centreDepth;
                Vector3 local = Vector3.Transform(centre, rotation);
                local.X = MathF.Round(local.X / texel) * texel;
                local.Y = MathF.Round(local.Y / texel) * texel;
                centre = Vector3.Transform(local, inverseRotation);

                Matrix4x4 view = Matrix4x4.CreateLookAt(centre - lightDirection * (radius + _reach), centre, up);
                Matrix4x4 orthographic = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0f, radius * 2f + _reach);
                Matrices[i] = view * orthographic;

                start = end;
            }
        }
    }
}
