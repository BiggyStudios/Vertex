using System.Numerics;

namespace Vertex.Editor.Gui
{
    /// <summary>
    /// A rectangle in UI units, from its top-left corner. Layout cuts rectangles into smaller ones: <see cref="TakeLeft"/>
    /// gives the strip off the left and <see cref="CutLeft"/> what's left over, and the same for each side.
    /// </summary>
    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Rect(Vector2 position, Vector2 size) : this(position.X, position.Y, size.X, size.Y) {}

        public Vector2 Position => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Centre => new(X + Width * 0.5f, Y + Height * 0.5f);
        public float Right => X + Width;
        public float Bottom => Y + Height;

        public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;

        public bool Overlaps(Rect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

        /// <summary>Smaller by <paramref name="amount"/> on every side.</summary>
        public Rect Shrink(float amount) => Shrink(amount, amount);

        public Rect Shrink(float x, float y) => new(X + x, Y + y, MathF.Max(Width - x * 2f, 0f), MathF.Max(Height - y * 2f, 0f));

        public Rect Grow(float amount) => new(X - amount, Y - amount, Width + amount * 2f, Height + amount * 2f);

        public Rect Offset(Vector2 by) => new(X + by.X, Y + by.Y, Width, Height);

        /// <summary>The strip <paramref name="width"/> wide off the left.</summary>
        public Rect TakeLeft(float width) => new(X, Y, MathF.Min(width, Width), Height);
        public Rect TakeRight(float width) => new(MathF.Max(Right - width, X), Y, MathF.Min(width, Width), Height);
        public Rect TakeTop(float height) => new(X, Y, Width, MathF.Min(height, Height));
        public Rect TakeBottom(float height) => new(X, MathF.Max(Bottom - height, Y), Width, MathF.Min(height, Height));

        /// <summary>What's left once <paramref name="width"/> (and a <paramref name="gap"/>) is taken off the left.</summary>
        public Rect CutLeft(float width, float gap = 0f) => new(X + width + gap, Y, MathF.Max(Width - width - gap, 0f), Height);
        public Rect CutRight(float width, float gap = 0f) => new(X, Y, MathF.Max(Width - width - gap, 0f), Height);
        public Rect CutTop(float height, float gap = 0f) => new(X, Y + height + gap, Width, MathF.Max(Height - height - gap, 0f));
        public Rect CutBottom(float height, float gap = 0f) => new(X, Y, Width, MathF.Max(Height - height - gap, 0f));

        /// <summary>A <paramref name="size"/> rectangle in the middle of this one.</summary>
        public Rect CentreOf(Vector2 size) => new(X + (Width - size.X) * 0.5f, Y + (Height - size.Y) * 0.5f, size.X, size.Y);

        /// <summary>Splits it into <paramref name="count"/> equal columns with <paramref name="gap"/> between, and returns column <paramref name="index"/>.</summary>
        public Rect Column(int index, int count, float gap)
        {
            float width = (Width - gap * (count - 1)) / count;
            return new Rect(X + (width + gap) * index, Y, width, Height);
        }
    }
}
