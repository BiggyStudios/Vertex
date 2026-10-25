using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>One row of the scoreboard.</summary>
    public readonly record struct ScoreEntry(string Name, Color Color, int Kills, int Deaths, bool IsLocal, bool Alive);

    /// <summary>Hold Tab to see everyone's kills and deaths, most kills first.</summary>
    public class Scoreboard : Component
    {
        private const float _width = 560f;
        private const float _rowHeight = 34f;
        private const float _fontSize = 18f;

        /// <summary>Where the rows and the status line come from; set by the MultiplayerManager.</summary>
        internal MultiplayerManager Source { get; set; } = null!;

        protected override void Update()
        {
            if (InputSystem.GetKey(Key.Tab) && !GameInput.Blocked) Draw();
        }

        private void Draw()
        {
            List<ScoreEntry> rows = Source.GetScores()
                .OrderByDescending(row => row.Kills)
                .ThenBy(row => row.Deaths)
                .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            float height = 96f + rows.Count * _rowHeight;
            var topLeft = new Vector2((Canvas.Width - _width) / 2f, MathF.Max(40f, (Canvas.Height - height) / 2.5f));
            float kills = topLeft.X + _width - 170f;
            float deaths = topLeft.X + _width - 80f;

            Canvas.DrawRect(topLeft, new Vector2(_width, height), new Color(0.05f, 0.06f, 0.08f, 0.85f));

            Canvas.DrawText(Fonts.Bold, "Scoreboard", topLeft + new Vector2(20f, 14f), Color.White, 22f);
            string status = Source.SessionStatus;
            float statusWidth = Canvas.MeasureText(Fonts.Regular, status, 15f).X;
            Canvas.DrawText(Fonts.Regular, status, topLeft + new Vector2(_width - 20f - statusWidth, 19f), new Color(1f, 1f, 1f, 0.6f), 15f);

            float y = topLeft.Y + 54f;
            var headerColor = new Color(1f, 1f, 1f, 0.55f);
            Canvas.DrawText(Fonts.Regular, "Player", new Vector2(topLeft.X + 20f, y), headerColor, 15f);
            Canvas.DrawText(Fonts.Regular, "Kills", new Vector2(kills, y), headerColor, 15f);
            Canvas.DrawText(Fonts.Regular, "Deaths", new Vector2(deaths, y), headerColor, 15f);
            y += 28f;

            foreach (ScoreEntry row in rows)
            {
                if (row.IsLocal) Canvas.DrawRect(new Vector2(topLeft.X, y - 4f), new Vector2(_width, _rowHeight), new Color(1f, 1f, 1f, 0.08f));

                // A colour swatch, matching the player's body, then their name; greyed out while dead.
                Canvas.DrawRect(new Vector2(topLeft.X + 20f, y + 4f), new Vector2(12f, 12f), row.Color);
                Color text = row.Alive ? Color.White : new Color(1f, 1f, 1f, 0.45f);
                Canvas.DrawText(row.IsLocal ? Fonts.Bold : Fonts.Regular, row.Name, new Vector2(topLeft.X + 42f, y - 2f), text, _fontSize);
                Canvas.DrawText(Fonts.Regular, row.Kills.ToString(), new Vector2(kills, y - 2f), text, _fontSize);
                Canvas.DrawText(Fonts.Regular, row.Deaths.ToString(), new Vector2(deaths, y - 2f), text, _fontSize);

                y += _rowHeight;
            }
        }
    }
}
