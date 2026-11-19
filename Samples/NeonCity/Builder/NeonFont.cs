using System.Numerics;

namespace NeonCity.Builder
{
    /// <summary>
    /// Letters drawn as bent neon tubes: each is a few lines on a grid 4 wide and 6 high, which the builder turns into
    /// glowing tubes. Capitals, numbers and a few marks.
    /// </summary>
    internal static class NeonFont
    {
        public const float Width = 4f;
        public const float Height = 6f;

        /// <summary>How far along each letter starts from the last, in grid units.</summary>
        public const float Advance = 5.6f;

        private static readonly Vector2[] _o = P(1, 0, 0, 1, 0, 5, 1, 6, 3, 6, 4, 5, 4, 1, 3, 0, 1, 0);
        private static readonly Vector2[] _p = P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3);

        private static readonly Dictionary<char, Vector2[][]> _strokes = new()
        {
            ['A'] = [P(0, 0, 0, 4, 2, 6, 4, 4, 4, 0), P(0, 3, 4, 3)],
            ['B'] = [P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3), P(3, 3, 4, 2, 4, 1, 3, 0, 0, 0)],
            ['C'] = [P(4, 6, 1, 6, 0, 5, 0, 1, 1, 0, 4, 0)],
            ['D'] = [P(0, 0, 0, 6, 3, 6, 4, 5, 4, 1, 3, 0, 0, 0)],
            ['E'] = [P(4, 6, 0, 6, 0, 0, 4, 0), P(0, 3, 3, 3)],
            ['F'] = [P(4, 6, 0, 6, 0, 0), P(0, 3, 3, 3)],
            ['G'] = [P(4, 5, 3, 6, 1, 6, 0, 5, 0, 1, 1, 0, 3, 0, 4, 1, 4, 3, 2, 3)],
            ['H'] = [P(0, 0, 0, 6), P(4, 0, 4, 6), P(0, 3, 4, 3)],
            ['I'] = [P(1, 6, 3, 6), P(2, 6, 2, 0), P(1, 0, 3, 0)],
            ['J'] = [P(4, 6, 4, 1, 3, 0, 1, 0, 0, 1)],
            ['K'] = [P(0, 0, 0, 6), P(4, 6, 0, 2), P(1, 3, 4, 0)],
            ['L'] = [P(0, 6, 0, 0, 4, 0)],
            ['M'] = [P(0, 0, 0, 6, 2, 3, 4, 6, 4, 0)],
            ['N'] = [P(0, 0, 0, 6, 4, 0, 4, 6)],
            ['O'] = [_o],
            ['P'] = [_p],
            ['Q'] = [_o, P(2, 2, 4, 0)],
            ['R'] = [_p, P(2, 3, 4, 0)],
            ['S'] = [P(4, 5, 3, 6, 1, 6, 0, 5, 0, 4, 1, 3, 3, 3, 4, 2, 4, 1, 3, 0, 1, 0, 0, 1)],
            ['T'] = [P(0, 6, 4, 6), P(2, 6, 2, 0)],
            ['U'] = [P(0, 6, 0, 1, 1, 0, 3, 0, 4, 1, 4, 6)],
            ['V'] = [P(0, 6, 2, 0, 4, 6)],
            ['W'] = [P(0, 6, 1, 0, 2, 3, 3, 0, 4, 6)],
            ['X'] = [P(0, 6, 4, 0), P(4, 6, 0, 0)],
            ['Y'] = [P(0, 6, 2, 3, 4, 6), P(2, 3, 2, 0)],
            ['Z'] = [P(0, 6, 4, 6, 0, 0, 4, 0)],
            ['0'] = [_o, P(1, 1, 3, 5)],
            ['1'] = [P(1, 5, 2, 6, 2, 0), P(1, 0, 3, 0)],
            ['2'] = [P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 0, 0, 4, 0)],
            ['3'] = [P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 3, 3, 4, 2, 4, 1, 3, 0, 1, 0, 0, 1), P(1, 3, 3, 3)],
            ['4'] = [P(3, 0, 3, 6, 0, 2, 4, 2)],
            ['5'] = [P(4, 6, 0, 6, 0, 3, 3, 3, 4, 2, 4, 1, 3, 0, 0, 0)],
            ['6'] = [P(4, 6, 1, 6, 0, 5, 0, 1, 1, 0, 3, 0, 4, 1, 4, 2, 3, 3, 0, 3)],
            ['7'] = [P(0, 6, 4, 6, 1, 0)],
            ['8'] = [P(1, 3, 0, 4, 0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 3, 3, 1, 3, 0, 2, 0, 1, 1, 0, 3, 0, 4, 1, 4, 2, 3, 3)],
            ['9'] = [P(4, 3, 1, 3, 0, 4, 0, 5, 1, 6, 3, 6, 4, 5, 4, 1, 3, 0, 0, 0)],
            ['-'] = [P(1, 3, 3, 3)],
            ['+'] = [P(0, 3, 4, 3), P(2, 1, 2, 5)],
            ['*'] = [P(2, 1, 2, 5), P(0, 2, 4, 4), P(0, 4, 4, 2)],
            ['!'] = [P(2, 6, 2, 2), P(2, 0.6f, 2, 0)],
            ['.'] = [P(2, 0.6f, 2, 0)]
        };

        /// <summary>The lines of a letter (each a run of points joined in order), or none for a space or one it doesn't have.</summary>
        public static IReadOnlyList<Vector2[]> Strokes(char letter) => _strokes.TryGetValue(char.ToUpperInvariant(letter), out Vector2[][]? strokes) ? strokes : [];

        /// <summary>How wide <paramref name="text"/> is in grid units, written in a line.</summary>
        public static float LineWidth(string text) => text.Length == 0 ? 0f : (text.Length - 1) * Advance + Width;

        private static Vector2[] P(params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return points;
        }
    }
}
