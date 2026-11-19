using System.Numerics;

namespace Vertex.UI
{
    /// <summary>
    /// Places the pieces of UI under its object for them, one after another, instead of each by its own
    /// <see cref="UIRect"/>: a menu's buttons in a column, a toolbar's icons in a row, an inventory's slots in a grid.
    /// Adding, removing or hiding one moves the rest up. Each piece keeps its own size from its rect, unless the layout
    /// stretches them.
    /// </summary>
    public abstract class UILayoutGroup : Component
    {
        [Tooltip("Space between the edge of this rectangle and the pieces in it, in UI units")]
        public float Padding { get; set; }

        [Tooltip("Space between one piece and the next, in UI units")]
        public float Spacing { get; set; } = 8f;

        [Tooltip("Where the pieces sit across, when they're narrower than the space")]
        public TextAlign HorizontalAlign { get; set; } = TextAlign.Centre;

        [Tooltip("Where the pieces sit up and down, when they're shorter than the space")]
        public TextVerticalAlign VerticalAlign { get; set; } = TextVerticalAlign.Top;

        /// <summary>
        /// Places <paramref name="children"/> (their rects, in order) inside <paramref name="position"/>/<paramref name="size"/>,
        /// in UI units: each gets its screen rectangle set.
        /// </summary>
        internal abstract void Arrange(IReadOnlyList<UIRect> children, Vector2 position, Vector2 size);

        /// <summary>The space inside the padding.</summary>
        private protected (Vector2 Position, Vector2 Size) Inner(Vector2 position, Vector2 size)
        {
            float padding = MathF.Max(Padding, 0f);
            return (position + new Vector2(padding), Vector2.Max(size - new Vector2(padding * 2f), Vector2.Zero));
        }

        private protected static float Align(float start, float space, float used, int alignment) => alignment switch
        {
            0 => start,
            2 => start + space - used,
            _ => start + (space - used) * 0.5f
        };

        private protected int Horizontal => HorizontalAlign switch { TextAlign.Left => 0, TextAlign.Right => 2, _ => 1 };
        private protected int Vertical => VerticalAlign switch { TextVerticalAlign.Top => 0, TextVerticalAlign.Bottom => 2, _ => 1 };
    }

    /// <summary>Its pieces of UI in a column, top to bottom.</summary>
    public sealed class UIVerticalLayout : UILayoutGroup
    {
        [Tooltip("Makes each piece as wide as the column")]
        public bool StretchWidth { get; set; } = true;

        internal override void Arrange(IReadOnlyList<UIRect> children, Vector2 position, Vector2 size)
        {
            (Vector2 inner, Vector2 space) = Inner(position, size);
            float total = children.Sum(c => MathF.Max(c.Size.Y, 0f)) + Spacing * MathF.Max(children.Count - 1, 0);
            float y = Align(inner.Y, space.Y, total, Vertical);

            foreach (UIRect child in children)
            {
                float width = StretchWidth ? space.X : MathF.Max(child.Size.X, 0f);
                float height = MathF.Max(child.Size.Y, 0f);
                child.Place(new Vector2(Align(inner.X, space.X, width, Horizontal), y), new Vector2(width, height));
                y += height + Spacing;
            }
        }
    }

    /// <summary>Its pieces of UI in a row, left to right.</summary>
    public sealed class UIHorizontalLayout : UILayoutGroup
    {
        [Tooltip("Makes each piece as tall as the row")]
        public bool StretchHeight { get; set; } = true;

        internal override void Arrange(IReadOnlyList<UIRect> children, Vector2 position, Vector2 size)
        {
            (Vector2 inner, Vector2 space) = Inner(position, size);
            float total = children.Sum(c => MathF.Max(c.Size.X, 0f)) + Spacing * MathF.Max(children.Count - 1, 0);
            float x = Align(inner.X, space.X, total, Horizontal);

            foreach (UIRect child in children)
            {
                float width = MathF.Max(child.Size.X, 0f);
                float height = StretchHeight ? space.Y : MathF.Max(child.Size.Y, 0f);
                child.Place(new Vector2(x, Align(inner.Y, space.Y, height, Vertical)), new Vector2(width, height));
                x += width + Spacing;
            }
        }
    }

    /// <summary>Its pieces of UI in a grid of same-sized cells, along each row and then down: an inventory, a level select.</summary>
    public sealed class UIGridLayout : UILayoutGroup
    {
        [Tooltip("The size of each cell, in UI units")]
        public Vector2 CellSize { get; set; } = new(80f, 80f);

        [Tooltip("How many cells across. 0 fits as many as there's room for")]
        public int Columns { get; set; }

        internal override void Arrange(IReadOnlyList<UIRect> children, Vector2 position, Vector2 size)
        {
            if (children.Count == 0) return;

            (Vector2 inner, Vector2 space) = Inner(position, size);
            Vector2 cell = Vector2.Max(CellSize, Vector2.One);
            int columns = Columns > 0 ? Columns : Math.Max(1, (int)((space.X + Spacing) / (cell.X + Spacing)));
            columns = Math.Min(columns, children.Count);
            int rows = (children.Count + columns - 1) / columns;

            float width = columns * cell.X + (columns - 1) * Spacing;
            float height = rows * cell.Y + (rows - 1) * Spacing;
            float left = Align(inner.X, space.X, width, Horizontal);
            float top = Align(inner.Y, space.Y, height, Vertical);

            for (int i = 0; i < children.Count; i++)
            {
                int column = i % columns, row = i / columns;
                children[i].Place(new Vector2(left + column * (cell.X + Spacing), top + row * (cell.Y + Spacing)), cell);
            }
        }
    }

    /// <summary>
    /// Hides whatever of the UI under its object falls outside its rectangle: a scrolling list's window, a minimap's
    /// frame. What's hidden can't be clicked either.
    /// </summary>
    public sealed class UIMask : Component;
}
