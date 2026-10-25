using System.Numerics;

using Vertex;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>Who killed who, in the top-right corner: newest at the bottom, each fading out after a few seconds.</summary>
    public class KillFeed : Component
    {
        private const float _fontSize = 17f;
        private const float _lifetime = 6f;
        private const float _fadeTime = 1f;
        private const int _maxEntries = 6;

        private static readonly Color _textColor = new(0.9f, 0.9f, 0.9f);
        private static readonly Color _headshotColor = new(1f, 0.55f, 0.3f);
        private static readonly Color _noScopeColor = new(0.55f, 0.8f, 1f);
        private static readonly Color _distanceColor = new(0.75f, 0.75f, 0.75f);
        private static readonly Color _backstabColor = new(1f, 0.4f, 0.45f);

        private sealed record Entry(string? Killer, Color KillerColor, string Victim, Color VictimColor, Weapon? Weapon, bool Headshot, bool Fell, bool Involved,
            bool NoScope, float Distance, bool Backstab, float Time);

        private readonly List<Entry> _entries = new();

        /// <summary>
        /// Adds a death. <paramref name="killer"/> is null when nobody did it: falling off the map (<paramref name="fell"/>)
        /// or the kill command. <paramref name="weapon"/> is what they were killed with, and <paramref name="involvesYou"/>
        /// highlights the entry, as in most shooters. <paramref name="noScope"/> tags a sniper kill without the scope, and
        /// a <paramref name="distance"/> above 0 shows how far away the victim was, in metres. <paramref name="backstab"/>
        /// tags a knife in the back.
        /// </summary>
        public void Add(string? killer, Color killerColor, string victim, Color victimColor, Weapon? weapon, bool headshot, bool fell, bool involvesYou,
            bool noScope = false, float distance = 0f, bool backstab = false)
        {
            _entries.Add(new Entry(killer, killerColor, victim, victimColor, weapon, headshot, fell, involvesYou, noScope, distance, backstab, Time.TotalTime));
            if (_entries.Count > _maxEntries) _entries.RemoveAt(0);
        }

        protected override void Update()
        {
            _entries.RemoveAll(entry => Time.TotalTime - entry.Time > _lifetime);

            float lineHeight = Canvas.LineHeight(Fonts.Regular, _fontSize) + 8f;
            float y = 24f;

            foreach (Entry entry in _entries)
            {
                float alpha = Math.Clamp((_lifetime - (Time.TotalTime - entry.Time)) / _fadeTime, 0f, 1f);

                // Built right to left, so every entry lines up against the right edge.
                var parts = new List<(string Text, Font Font, Color Color)>();
                if (entry.Killer != null)
                {
                    parts.Add((entry.Killer, Fonts.Bold, entry.KillerColor));
                    // The weapon in brackets, standing in for the weapon icons most shooters use.
                    parts.Add((entry.Weapon is { } weapon ? $"  [{weapon.Name}]  " : "  killed  ", Fonts.Regular, _textColor));
                    parts.Add((entry.Victim, Fonts.Bold, entry.VictimColor));
                    if (entry.Headshot) parts.Add(("  (headshot)", Fonts.Regular, _headshotColor));
                    if (entry.NoScope) parts.Add(("  (noscope)", Fonts.Regular, _noScopeColor));
                    if (entry.Backstab) parts.Add(("  (backstab)", Fonts.Regular, _backstabColor));
                    if (entry.Distance > 0f) parts.Add(($"  ({entry.Distance:0} m)", Fonts.Regular, _distanceColor));
                }
                else
                {
                    parts.Add((entry.Victim, Fonts.Bold, entry.VictimColor));
                    parts.Add((entry.Fell ? " fell to their death" : " died", Fonts.Regular, _textColor));
                }

                float width = parts.Sum(part => Canvas.MeasureText(part.Font, part.Text, _fontSize).X);
                float x = Canvas.Width - 24f - width;

                Color background = entry.Involved ? new Color(0.5f, 0.1f, 0.1f, 0.6f * alpha) : new Color(0f, 0f, 0f, 0.45f * alpha);
                Canvas.DrawRect(new Vector2(x - 8f, y - 2f), new Vector2(width + 16f, lineHeight - 4f), background);

                foreach ((string text, Font font, Color color) in parts)
                {
                    x += Canvas.DrawText(font, text, new Vector2(x, y), color with { A = alpha }, _fontSize, shadow: true);
                }

                y += lineHeight;
            }
        }
    }
}
