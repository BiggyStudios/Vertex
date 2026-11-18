namespace Vertex.Rendering
{
    /// <summary>
    /// A texture a <see cref="Camera"/> draws into instead of the screen: set it as the camera's
    /// <see cref="Camera.Target"/>. Use it like any other texture: on a material (a security monitor, a mirror) or
    /// drawn with <see cref="UI.Canvas.DrawImage(Texture?, System.Numerics.Vector2, System.Numerics.Vector2, Color)"/> (a minimap, or an editor's view of the scene).
    /// <para>
    /// A camera shouldn't see a surface showing its own render texture: what it would show is the picture it's in the
    /// middle of drawing.
    /// </para>
    /// </summary>
    public sealed class RenderTexture : Texture
    {
        public RenderTexture(int width, int height) : base(Validate(width), Validate(height)) {}

        /// <summary>Changes the size, for a view that's been resized. Does nothing if it's already that size.</summary>
        public void Resize(int width, int height)
        {
            if (width == Width && height == Height) return;

            Width = Validate(width);
            Height = Validate(height);
            Version++;
        }

        private static int Validate(int size) =>
            size > 0 ? size : throw new ArgumentOutOfRangeException(nameof(size), "A render texture must be at least one pixel each way.");
    }
}
