using System.Numerics;

using Vertex;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>Text the menus share: centred titles and lines, and wrapping long text.</summary>
    internal static class MenuText
    {
        public static void Title(string text, float y) => Centred(Fonts.Bold, text, y, 40f, Color.White);

        public static void Centred(Font font, string text, float y, float size, Color color)
        {
            float width = Canvas.MeasureText(font, text, size).X;
            Canvas.DrawText(font, text, new Vector2((Canvas.Width - width) / 2f, y), color, size, shadow: true);
        }

        /// <summary>Splits text into lines no wider than <paramref name="width"/>, breaking between words.</summary>
        public static List<string> Wrap(Font font, string text, float size, float width)
        {
            var lines = new List<string>();
            string line = "";

            foreach (string word in text.Split(' '))
            {
                string attempt = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && Canvas.MeasureText(font, attempt, size).X > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = attempt;
                }
            }

            if (line.Length > 0) lines.Add(line);
            return lines;
        }
    }
}
