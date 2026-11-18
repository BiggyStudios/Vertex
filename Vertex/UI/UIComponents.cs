using System.Numerics;

using Vertex.Rendering;
using Vertex.Serialization;

namespace Vertex.UI
{
    /// <summary>
    /// The top of a screen's worth of UI: every object under it with a <see cref="UIRect"/> is laid out on the screen and
    /// drawn over the game, each frame, as the scene runs. Several can be on screen at once; higher
    /// <see cref="Order"/>s draw on top.
    /// <para>
    /// This UI is made of objects and components, so it can be built and arranged in the editor and saved with the
    /// scene. For UI drawn straight from code every frame, there's <see cref="Canvas"/> and <see cref="Gui"/>.
    /// </para>
    /// </summary>
    public sealed class UICanvas : Component
    {
        /// <summary>Which canvas draws over which: higher on top.</summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// Where a piece of UI goes: a rectangle placed against its parent's (or the screen's, at the top of a
    /// <see cref="UICanvas"/>). Positions are in UI units, as <see cref="Canvas"/> uses, with y going down.
    /// <para>
    /// The <see cref="AnchorMin"/> and <see cref="AnchorMax"/> are points in the parent, from (0, 0) at its top-left to
    /// (1, 1) at its bottom-right. Together they say what the rectangle keeps to when the parent changes size: both at the
    /// top-left keeps it in that corner, both at (0.5, 0.5) keeps it in the middle, and (0, 0) and (1, 1) stretches it with
    /// the parent. <see cref="Position"/> is how far its <see cref="Pivot"/> is from the anchors, and <see cref="Size"/>
    /// its size (added to the anchors' span, when they're apart).
    /// </para>
    /// </summary>
    public sealed class UIRect : Component
    {
        [Tooltip("The point of the parent the rectangle's top-left keeps to: (0, 0) its top-left, (1, 1) its bottom-right")]
        public Vector2 AnchorMin { get; set; } = new(0.5f);

        [Tooltip("The point of the parent the rectangle's bottom-right keeps to: the same as Anchor Min, unless it stretches")]
        public Vector2 AnchorMax { get; set; } = new(0.5f);

        [Tooltip("The point of the rectangle that Position places, from (0, 0) at its top-left to (1, 1) at its bottom-right")]
        public Vector2 Pivot { get; set; } = new(0.5f);

        [Tooltip("How far the pivot is from the anchors, in UI units")]
        public Vector2 Position { get; set; }

        [Tooltip("The size in UI units; when the anchors are apart, added to the space between them")]
        public Vector2 Size { get; set; } = new(200f, 60f);

        /// <summary>Where it was laid out last, in UI units of the screen it's on: its top-left corner.</summary>
        [DontSave, HideInInspector]
        public Vector2 ScreenPosition { get; internal set; }

        /// <summary>Its size as laid out last, in UI units.</summary>
        [DontSave, HideInInspector]
        public Vector2 ScreenSize { get; internal set; }

        /// <summary>Whether a point (in UI units) is inside it, as last laid out.</summary>
        public bool Contains(Vector2 point) =>
            point.X >= ScreenPosition.X && point.Y >= ScreenPosition.Y && point.X < ScreenPosition.X + ScreenSize.X && point.Y < ScreenPosition.Y + ScreenSize.Y;

        /// <summary>Lays it out in <paramref name="parentPosition"/>/<paramref name="parentSize"/> (its parent's rectangle).</summary>
        internal void Layout(Vector2 parentPosition, Vector2 parentSize)
        {
            Vector2 anchorsFrom = parentPosition + AnchorMin * parentSize;
            Vector2 anchorsTo = parentPosition + AnchorMax * parentSize;
            Vector2 size = Vector2.Max(anchorsTo - anchorsFrom + Size, Vector2.Zero);
            Vector2 pivot = anchorsFrom + (anchorsTo - anchorsFrom) * Pivot + Position;
            ScreenPosition = pivot - size * Pivot;
            ScreenSize = size;
        }

        /// <summary>
        /// The <see cref="Position"/> and <see cref="Size"/> that put it at <paramref name="screenPosition"/>,
        /// <paramref name="screenSize"/> inside <paramref name="parentPosition"/>/<paramref name="parentSize"/>, keeping its
        /// anchors and pivot: for an editor dragging it about.
        /// </summary>
        public (Vector2 Position, Vector2 Size) PlacementFor(Vector2 screenPosition, Vector2 screenSize, Vector2 parentPosition, Vector2 parentSize)
        {
            Vector2 anchorsFrom = parentPosition + AnchorMin * parentSize;
            Vector2 anchorsTo = parentPosition + AnchorMax * parentSize;
            Vector2 size = screenSize - (anchorsTo - anchorsFrom);
            Vector2 pivot = screenPosition + screenSize * Pivot;
            return (pivot - anchorsFrom - (anchorsTo - anchorsFrom) * Pivot, size);
        }
    }

    /// <summary>Something that draws inside its object's <see cref="UIRect"/>. The kinds of UI are made from these.</summary>
    public abstract class UIGraphic : Component
    {
        /// <summary>Draws it in <paramref name="position"/>/<paramref name="size"/>, in UI units. <paramref name="hovered"/> and <paramref name="pressed"/> are the mouse's.</summary>
        internal abstract void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed);
    }

    /// <summary>A coloured rectangle, with rounded corners and an outline if wanted: the backing of a menu or a bar.</summary>
    public sealed class UIPanel : UIGraphic
    {
        public Color Color { get; set; } = new(0.1f, 0.11f, 0.13f, 0.85f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 8f;

        public Color OutlineColor { get; set; } = new(1f, 1f, 1f, 0f);

        [Range(0f, 8f)]
        public float OutlineWidth { get; set; } = 1f;

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            Canvas.DrawRect(position, size, Color, CornerRadius);
            if (OutlineColor.A > 0f && OutlineWidth > 0f) Canvas.DrawRectOutline(position, size, OutlineColor, OutlineWidth, CornerRadius);
        }
    }

    /// <summary>A picture: a texture, tinted by <see cref="Color"/>, stretched over the rectangle or kept in proportion.</summary>
    public sealed class UIImage : UIGraphic
    {
        public Texture? Texture { get; set; }

        /// <summary>Multiplied with the texture; with no texture, it's a plain rectangle of this colour.</summary>
        public Color Color { get; set; } = Color.White;

        [Range(0f, 64f)]
        public float CornerRadius { get; set; }

        /// <summary>Keeps the picture's own proportions, fitting it inside the rectangle, rather than stretching it to fill.</summary>
        public bool KeepAspect { get; set; } = true;

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            if (Texture is { Width: > 0, Height: > 0 } texture && KeepAspect)
            {
                float aspect = texture.Width / (float)texture.Height;
                Vector2 fit = size.X / size.Y > aspect ? new Vector2(size.Y * aspect, size.Y) : new Vector2(size.X, size.X / aspect);
                position += (size - fit) * 0.5f;
                size = fit;
            }

            Canvas.DrawImage(Texture, position, size, Vector2.Zero, Vector2.One, Color, CornerRadius);
        }
    }

    public enum TextAlign
    {
        Left,
        Centre,
        Right
    }

    public enum TextVerticalAlign
    {
        Top,
        Middle,
        Bottom
    }

    /// <summary>Words, laid out in the rectangle: on one line, or wrapped onto as many as it takes.</summary>
    public sealed class UIText : UIGraphic
    {
        public string Text { get; set; } = "Text";

        /// <summary>The font; with none, the engine's own (<see cref="UI.Font.Default"/>).</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 24f;

        public Color Color { get; set; } = Color.White;

        public TextAlign Align { get; set; } = TextAlign.Centre;

        public TextVerticalAlign VerticalAlign { get; set; } = TextVerticalAlign.Middle;

        /// <summary>Breaks the text onto more lines when it's wider than the rectangle.</summary>
        public bool Wrap { get; set; } = true;

        /// <summary>A soft dark copy just behind it, so it reads over busy backgrounds.</summary>
        public bool Shadow { get; set; }

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed) =>
            DrawText(Font ?? UI.Font.Default, Text, FontSize, Color, Align, VerticalAlign, Wrap, Shadow, position, size);

        internal static void DrawText(Font font, string text, float fontSize, Color color, TextAlign align, TextVerticalAlign verticalAlign, bool wrap,
            bool shadow, Vector2 position, Vector2 size)
        {
            if (string.IsNullOrEmpty(text)) return;

            List<string> lines = wrap ? WrapLines(font, text, fontSize, size.X) : [.. text.Replace("\r", "").Split('\n')];
            float lineHeight = Canvas.LineHeight(font, fontSize);
            float height = lines.Count * lineHeight;
            float y = verticalAlign switch
            {
                TextVerticalAlign.Top => position.Y,
                TextVerticalAlign.Bottom => position.Y + size.Y - height,
                _ => position.Y + (size.Y - height) * 0.5f
            };

            foreach (string line in lines)
            {
                float width = Canvas.MeasureText(font, line, fontSize).X;
                float x = align switch
                {
                    TextAlign.Left => position.X,
                    TextAlign.Right => position.X + size.X - width,
                    _ => position.X + (size.X - width) * 0.5f
                };

                Canvas.DrawText(font, line, new Vector2(x, y), color, fontSize, shadow);
                y += lineHeight;
            }
        }

        private static List<string> WrapLines(Font font, string text, float fontSize, float width)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Canvas.MeasureText(font, candidate, fontSize).X > width)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }

                lines.Add(line);
            }

            return lines;
        }
    }

    /// <summary>
    /// Something to click: a rounded rectangle that lights up under the mouse, with a label. Code finds out about
    /// clicks through <see cref="Clicked"/>:
    /// <code>
    /// GetComponent&lt;UIButton&gt;()!.Clicked += () => StartGame();
    /// </code>
    /// </summary>
    public sealed class UIButton : UIGraphic
    {
        public string Label { get; set; } = "Button";

        /// <summary>The label's font; with none, the engine's own.</summary>
        public Font? Font { get; set; }

        [Range(6f, 120f)]
        public float FontSize { get; set; } = 22f;

        public Color LabelColor { get; set; } = Color.White;

        public Color Color { get; set; } = new(0.2f, 0.22f, 0.26f, 0.95f);
        public Color HoverColor { get; set; } = new(0.27f, 0.3f, 0.36f, 1f);
        public Color PressedColor { get; set; } = new(0.17f, 0.18f, 0.21f, 1f);

        [Range(0f, 64f)]
        public float CornerRadius { get; set; } = 8f;

        /// <summary>Whether it can be clicked. A button that can't is drawn faded.</summary>
        public bool Interactable { get; set; } = true;

        /// <summary>Raised when it's clicked: pressed and let go with the mouse still over it.</summary>
        public event Action? Clicked;

        // Eases between its colours, so it doesn't snap.
        private float _hover;

        internal void Click() => Clicked?.Invoke();

        private static Color Faded(Color color) => new(color.R, color.G, color.B, color.A * 0.5f);

        internal override void Draw(Vector2 position, Vector2 size, bool hovered, bool pressed)
        {
            float target = Interactable && hovered ? 1f : 0f;
            _hover += (target - _hover) * (1f - MathF.Exp(-18f * Time.DeltaTime));

            Color color = !Interactable ? Faded(Color)
                : pressed ? PressedColor
                : Color.Lerp(Color, HoverColor, _hover);
            Canvas.DrawRect(position, size, color, CornerRadius);

            Color label = Interactable ? LabelColor : Faded(LabelColor);
            UIText.DrawText(Font ?? UI.Font.Default, Label, FontSize, label, TextAlign.Centre, TextVerticalAlign.Middle, wrap: false, shadow: false, position, size);
        }
    }
}
