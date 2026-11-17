using System.Diagnostics;
using System.Numerics;

using Vertex;
using Vertex.Rendering;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The people who helped test the game, thanked on the main menu (see <see cref="MainMenu"/>), each with a link to
    /// where to find them. Add someone with a line in <see cref="Testers"/>.
    /// </summary>
    internal static class Credits
    {
        public enum Site { YouTube, Twitch, Twitter, GitHub, Instagram }

        /// <summary>Someone to thank: their name, their handle on <paramref name="Site"/>, and the link to their page there.</summary>
        public sealed record Tester(string Name, string Handle, Site Site, string Url);

        public static readonly Tester[] Testers =
        [
            new("spreaddatlove", "@spreaddatlove", Site.Twitch, "https://www.twitch.tv/spreaddatlove"),
            new("BGxXP", "@BSO193", Site.YouTube, "https://www.youtube.com/@BSO193"),
            new("nnra", "@nnra6864", Site.GitHub, "https://github.com/nnra6864"),
            new("Paweuek1k", "@Paweuek1k", Site.YouTube, "https://www.youtube.com/@Paweuek1k"),
            new("Hm_Jerry", "@Hm_Jerry", Site.YouTube, "https://www.youtube.com/@Hm_Jerry")
        ];

        /// <summary>What clicking a link does: opens it in the browser. Tests swap it out.</summary>
        public static Action<string> OpenLink { get; set; } = OpenInBrowser;

        // The logos, painted the first time they're needed.
        private const int _iconSize = 64;
        private static readonly Dictionary<Site, Texture> _icons = new();

        /// <summary>The site's logo, to draw with <see cref="Canvas.DrawImage"/>.</summary>
        public static Texture Icon(Site site)
        {
            if (!_icons.TryGetValue(site, out Texture? icon))
            {
                icon = Paint(site switch
                {
                    Site.YouTube => YouTube,
                    Site.Twitch => Twitch,
                    Site.GitHub => GitHub,
                    Site.Instagram => Instagram,
                    _ => Twitter
                });
                _icons[site] = icon;
            }

            return icon;
        }

        /// <summary>Only web links: nothing in the list should be able to start a program.</summary>
        private static void OpenInBrowser(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")) return;

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DevConsole.Error($"Couldn't open {uri.AbsoluteUri}: {exception.Message}");
            }
        }

        // ---- The logos ----
        // Each is a function from a point on the icon, from (0, 0) at the top-left to (1, 1) at the bottom-right, to the
        // colour there. Paint samples it a few times in every pixel, so the edges come out smooth.

        private static readonly Color _youTubeRed = new(1f, 0f, 0f);
        private static readonly Color _twitchPurple = new(0.57f, 0.27f, 1f);
        private static readonly Color _twitterBlack = new(0.06f, 0.06f, 0.06f);
        private static readonly Color _gitHubBlack = new(0.09f, 0.08f, 0.08f);

        // Instagram's gradient, from yellow to purple.
        private static readonly Color[] _instagram =
        [
            new(1f, 0.85f, 0.46f), new(0.98f, 0.49f, 0.12f), new(0.84f, 0.16f, 0.46f), new(0.59f, 0.18f, 0.75f), new(0.31f, 0.36f, 0.84f)
        ];
        private static readonly Color _clear = new(0f, 0f, 0f, 0f);

        /// <summary>A red rounded rectangle with a white play button in it.</summary>
        private static Color YouTube(Vector2 p)
        {
            if (InPolygon(p, [new(0.4f, 0.34f), new(0.69f, 0.5f), new(0.4f, 0.66f)])) return Color.White;
            return InRoundedRect(p, new Vector2(0.02f, 0.16f), new Vector2(0.98f, 0.84f), 0.17f) ? _youTubeRed : _clear;
        }

        /// <summary>Twitch's purple speech bubble, with its corner cut off and a tail, and two eyes in its white middle.</summary>
        private static Color Twitch(Vector2 p)
        {
            Vector2[] outside = [new(0.24f, 0.03f), new(0.92f, 0.03f), new(0.92f, 0.58f), new(0.68f, 0.82f), new(0.5f, 0.82f), new(0.34f, 0.98f),
                new(0.34f, 0.82f), new(0.1f, 0.82f), new(0.1f, 0.17f)];
            Vector2[] inside = [new(0.22f, 0.13f), new(0.83f, 0.13f), new(0.83f, 0.54f), new(0.66f, 0.71f), new(0.48f, 0.71f), new(0.37f, 0.82f),
                new(0.37f, 0.71f), new(0.22f, 0.71f)];

            if (!InPolygon(p, outside)) return _clear;
            if (!InPolygon(p, inside)) return _twitchPurple;

            bool eye = p.Y is >= 0.27f and <= 0.5f && (p.X is >= 0.44f and <= 0.52f || p.X is >= 0.63f and <= 0.71f);
            return eye ? _twitchPurple : Color.White;
        }

        /// <summary>Twitter's logo as it is now, the X: a black rounded square with a hollow stroke one way and a thin one the other.</summary>
        private static Color Twitter(Vector2 p)
        {
            if (!InRoundedRect(p, new Vector2(0.02f), new Vector2(0.98f), 0.2f)) return _clear;

            Vector2[] band = [new(0.2f, 0.21f), new(0.41f, 0.21f), new(0.8f, 0.79f), new(0.59f, 0.79f)];
            Vector2[] hollow = [new(0.27f, 0.25f), new(0.39f, 0.25f), new(0.73f, 0.75f), new(0.61f, 0.75f)];
            bool inBand = InPolygon(p, band);
            bool inHollow = InPolygon(p, hollow);
            if (inBand && !inHollow) return Color.White;

            bool thin = DistanceToSegment(p, new Vector2(0.75f, 0.21f), new Vector2(0.25f, 0.79f)) < 0.028f;
            return thin && !inHollow ? Color.White : _twitterBlack;
        }

        /// <summary>
        /// GitHub's Octocat as it's shown on dark backgrounds: a white disc with the cat in it, its head and ears, its
        /// body running down to the edge, and its tail curling off to the left.
        /// </summary>
        private static Color GitHub(Vector2 p)
        {
            if (Vector2.Distance(p, new Vector2(0.5f)) > 0.48f) return _clear;

            Vector2 head = (p - new Vector2(0.5f, 0.44f)) / new Vector2(0.25f, 0.2f);
            bool cat = head.LengthSquared() <= 1f
                || InPolygon(p, [new(0.28f, 0.38f), new(0.29f, 0.17f), new(0.43f, 0.27f)])
                || InPolygon(p, [new(0.72f, 0.38f), new(0.71f, 0.17f), new(0.57f, 0.27f)])
                || InPolygon(p, [new(0.41f, 0.58f), new(0.59f, 0.58f), new(0.61f, 1f), new(0.39f, 1f)])
                || DistanceToSegment(p, new Vector2(0.41f, 0.73f), new Vector2(0.3f, 0.71f)) < 0.035f
                || DistanceToSegment(p, new Vector2(0.3f, 0.71f), new Vector2(0.23f, 0.63f)) < 0.035f;
            return cat ? _gitHubBlack : Color.White;
        }

        /// <summary>
        /// Instagram's camera: a rounded square shading from yellow in the bottom-left corner, through orange and pink,
        /// to purple in the top-right, with a white outline of a camera on it: its body, lens and flash.
        /// </summary>
        private static Color Instagram(Vector2 p)
        {
            if (!InRoundedRect(p, new Vector2(0.02f), new Vector2(0.98f), 0.24f)) return _clear;

            bool body = InRoundedRect(p, new Vector2(0.2f), new Vector2(0.8f), 0.18f) && !InRoundedRect(p, new Vector2(0.265f), new Vector2(0.735f), 0.115f);
            bool lens = MathF.Abs(Vector2.Distance(p, new Vector2(0.5f)) - 0.125f) < 0.033f;
            bool flash = Vector2.Distance(p, new Vector2(0.65f, 0.35f)) < 0.04f;
            if (body || lens || flash) return Color.White;

            // Out from the bottom-left corner, through the colours in turn.
            float t = Math.Clamp(Vector2.Distance(p, new Vector2(0.1f, 1.05f)) / 1.25f, 0f, 1f) * (_instagram.Length - 1);
            int from = Math.Min((int)t, _instagram.Length - 2);
            return Mix(_instagram[from], _instagram[from + 1], t - from);
        }

        private static Color Mix(Color a, Color b, float t) =>
            new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

        private static Texture Paint(Func<Vector2, Color> shade)
        {
            const int samples = 4;
            byte[] pixels = new byte[_iconSize * _iconSize * 4];
            for (int y = 0; y < _iconSize; y++)
            {
                for (int x = 0; x < _iconSize; x++)
                {
                    // Add up premultiplied colour, which is what the canvas wants, and what averages correctly.
                    Vector4 sum = Vector4.Zero;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            var point = new Vector2((x + (sx + 0.5f) / samples) / _iconSize, (y + (sy + 0.5f) / samples) / _iconSize);
                            Color c = shade(point);
                            sum += new Vector4(c.R * c.A, c.G * c.A, c.B * c.A, c.A);
                        }
                    }

                    sum /= samples * samples;
                    int i = (y * _iconSize + x) * 4;
                    pixels[i] = (byte)MathF.Round(sum.X * 255f);
                    pixels[i + 1] = (byte)MathF.Round(sum.Y * 255f);
                    pixels[i + 2] = (byte)MathF.Round(sum.Z * 255f);
                    pixels[i + 3] = (byte)MathF.Round(sum.W * 255f);
                }
            }

            return new Texture(_iconSize, _iconSize, pixels);
        }

        /// <summary>Whether <paramref name="p"/> is inside the polygon with these corners, in order.</summary>
        private static bool InPolygon(Vector2 p, Vector2[] corners)
        {
            bool inside = false;
            for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
            {
                Vector2 a = corners[i], b = corners[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
            }

            return inside;
        }

        private static bool InRoundedRect(Vector2 p, Vector2 min, Vector2 max, float radius)
        {
            Vector2 nearest = Vector2.Clamp(p, min + new Vector2(radius), max - new Vector2(radius));
            return p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y && Vector2.Distance(p, nearest) <= radius;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
