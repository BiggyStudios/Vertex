using System.Numerics;

namespace Vertex.Editor.Gui
{
    /// <summary>The look shared by the editor's panels: a rounded card with a title along the top.</summary>
    public static class PanelFrame
    {
        public const float HeaderHeight = 36f;

        /// <summary>Set while a dock draws a panel as one of its tabs: the dock has drawn the card and the tabs, so the panel doesn't.</summary>
        public static bool Docked { get; set; }

        /// <summary>Where the title (or a dock's tabs) ends along the header: where a panel's own header controls can start.</summary>
        public static float TitleRight { get; set; }

        /// <summary>Draws the panel and its title, and returns the rectangle under the title for its contents.</summary>
        public static Rect Draw(Rect rect, string title, Icon? icon = null)
        {
            if (Docked) return rect.CutTop(HeaderHeight);

            Ui.Rect(rect, Theme.Panel, Theme.PanelRadius);
            Ui.Outline(rect, Theme.Border, Theme.PanelRadius);

            Rect header = rect.TakeTop(HeaderHeight).Shrink(Theme.Padding + 2f, 0f);
            float textLeft = header.X;
            if (icon != null)
            {
                icon(new Vector2(header.X + 7f, header.Centre.Y), 15f, Theme.TextDim);
                textLeft += 22f;
            }

            Ui.Text(new Rect(textLeft, header.Y, header.Right - textLeft, header.Height), title, Theme.Text, bold: true, size: Theme.TitleSize);
            TitleRight = textLeft + Ui.TextWidth(title, Theme.TitleSize, bold: true) + 16f;
            Ui.Rect(new Rect(rect.X, rect.Y + HeaderHeight - 1f, rect.Width, 1f), Theme.Border);

            return rect.CutTop(HeaderHeight);
        }

        /// <summary>Where a panel's header has room for buttons on the right: <paramref name="count"/> square buttons, right to left.</summary>
        public static Rect HeaderButton(Rect panel, int index)
        {
            const float size = 26f;
            return new Rect(panel.Right - Theme.Padding - size * (index + 1) - 2f * index, panel.Y + (HeaderHeight - size) * 0.5f, size, size);
        }
    }
}
