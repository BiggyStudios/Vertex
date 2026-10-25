using System.Numerics;

using Vertex;
using Vertex.Audio;
using Vertex.Rendering;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// The crosshair (or the sniper scope), health in the bottom-left corner, the weapon in the bottom-right, the
    /// respawn countdown after dying, and hit feedback: hit and kill sounds, a red flash when you're hurt, and a popup
    /// when you kill someone.
    /// </summary>
    public class Hud : Component
    {
        private static readonly Color _crosshairColor = new(1f, 1f, 1f, 0.9f);
        private static readonly Color _hitColor = new(1f, 0.25f, 0.2f);
        private static readonly Color _outline = new(0f, 0f, 0f, 0.5f);

        // Black outside a circle, with a soft dark edge just inside it, like looking down a scope tube. Made once.
        private static readonly Texture _scopeMask = CreateScopeMask(512);

        private static readonly Color _headshotColor = new(1f, 0.55f, 0.3f);
        private static readonly Color _noScopeColor = new(0.55f, 0.8f, 1f);
        private static readonly Color _backstabColor = new(1f, 0.4f, 0.45f);

        // How long the kill popup stays up, and how long it takes to fade out at the end.
        private const float _killPopupTime = 2.2f;
        private const float _killPopupFade = 0.4f;

        private Player _player = null!;
        private float _hitMarkerTime;
        private float _damageFlash;
        private float _sinceKill = float.PositiveInfinity;
        private string _killedName = "";
        private Color _killedColor;
        private readonly List<(string Text, Color Color)> _killTags = new();

        internal void Attach(Player player) => _player = player;

        /// <summary>You hit someone: the crosshair turns red for a moment, with a tick, or a "dink" for a headshot.</summary>
        public void ShowHit(bool headshot)
        {
            _hitMarkerTime = MathF.Max(_hitMarkerTime, 0.12f);
            Sound.Play(headshot ? Sounds.Headshot : Sounds.HitMarker, volume: 0.6f);
        }

        /// <summary>
        /// You killed someone: a popup with their name under the crosshair, and a chime. Tags under the name say if it was
        /// a headshot, a no-scope or a backstab, and how far away they were, for a <paramref name="distance"/> above 0.
        /// </summary>
        public void ShowKill(string name, Color color, bool headshot, bool noScope = false, float distance = 0f, bool backstab = false)
        {
            _hitMarkerTime = 0.4f;
            _sinceKill = 0f;
            _killedName = name;
            _killedColor = color;
            _killTags.Clear();
            if (headshot) _killTags.Add(("HEADSHOT", _headshotColor));
            if (noScope) _killTags.Add(("NOSCOPE", _noScopeColor));
            if (backstab) _killTags.Add(("BACKSTAB", _backstabColor));
            if (distance > 0f) _killTags.Add(($"{distance:0} M", new Color(0.85f, 0.85f, 0.85f)));
            Sound.Play(Sounds.Kill, volume: 0.6f);
        }

        /// <summary>You were hit: a thud, and the edges of the screen flash red, more for a bigger hit.</summary>
        public void ShowDamage(int amount)
        {
            _damageFlash = MathF.Min(MathF.Max(_damageFlash, 0.35f + amount / 100f), 1f);
            Sound.Play(Sounds.Hurt, volume: 0.8f, pitch: 0.95f + Random.Shared.NextSingle() * 0.1f);
        }

        protected override void Update()
        {
            _hitMarkerTime -= Time.DeltaTime;
            _damageFlash = MathF.Max(_damageFlash - Time.DeltaTime * 1.5f, 0f);
            _sinceKill += Time.DeltaTime;

            DrawDamageFlash();

            // No crosshair while the cursor is out for a menu or the console. The sniper rifle only gets a dot off the
            // scope, as a hint that shots from the hip go anywhere near it.
            Gun gun = _player.Gun;
            if (_player.IsDead) DrawDeathScreen();
            else if (gun.IsScoped && gun.Zoom > 1.5f) DrawScope();
            else if (!GameInput.LookBlocked && gun.Weapon.HasScope) DrawDot();
            else if (!GameInput.LookBlocked) DrawCrosshair();

            DrawHealth();
            if (!_player.IsDead) DrawWeapon(gun);
            DrawKillPopup();
        }

        /// <summary>Red bands fading in from each edge of the screen, strongest at the edge.</summary>
        private void DrawDamageFlash()
        {
            if (_damageFlash <= 0f) return;

            const int steps = 12;
            float depth = Canvas.Height * 0.18f;
            float band = depth / steps;
            for (int i = 0; i < steps; i++)
            {
                float fade = 1f - i / (float)steps;
                var color = new Color(0.8f, 0.05f, 0.05f, _damageFlash * 0.55f * fade * fade);
                float inset = i * band;
                Canvas.DrawRect(new Vector2(0f, inset), new Vector2(Canvas.Width, band), color);
                Canvas.DrawRect(new Vector2(0f, Canvas.Height - inset - band), new Vector2(Canvas.Width, band), color);
                Canvas.DrawRect(new Vector2(inset, 0f), new Vector2(band, Canvas.Height), color);
                Canvas.DrawRect(new Vector2(Canvas.Width - inset - band, 0f), new Vector2(band, Canvas.Height), color);
            }
        }

        /// <summary>
        /// "Eliminated" and the victim's name, below the crosshair: it pops in a little large, settles, and fades out.
        /// </summary>
        private void DrawKillPopup()
        {
            if (_sinceKill >= _killPopupTime) return;

            float alpha = Math.Clamp((_killPopupTime - _sinceKill) / _killPopupFade, 0f, 1f);
            float pop = 1f + 0.4f * MathF.Exp(-_sinceKill * 12f);
            float y = Canvas.Height * 0.6f;

            DrawCentred(Fonts.Regular, "ELIMINATED", y, 15f, new Color(1f, 1f, 1f, 0.75f * alpha));
            DrawCentred(Fonts.Bold, _killedName, y + 20f, 30f * pop, _killedColor with { A = alpha });
            if (_killTags.Count == 0) return;

            // The tags on one line, in their own colours, with dots between: "HEADSHOT · NOSCOPE · 87 M".
            const float tagSize = 15f;
            const string separator = "  ·  ";
            float separatorWidth = Canvas.MeasureText(Fonts.Bold, separator, tagSize).X;
            float width = _killTags.Sum(tag => Canvas.MeasureText(Fonts.Bold, tag.Text, tagSize).X) + separatorWidth * (_killTags.Count - 1);
            float x = (Canvas.Width - width) / 2f;
            float tagY = y + 20f + 38f * pop;
            for (int i = 0; i < _killTags.Count; i++)
            {
                if (i > 0) x += Canvas.DrawText(Fonts.Bold, separator, new Vector2(x, tagY), new Color(1f, 1f, 1f, 0.5f * alpha), tagSize, shadow: true);
                x += Canvas.DrawText(Fonts.Bold, _killTags[i].Text, new Vector2(x, tagY), _killTags[i].Color with { A = alpha }, tagSize, shadow: true);
            }
        }

        /// <summary>
        /// Four lines around the middle. For a weapon with spread, like the shotgun, they sit on the edge of the circle
        /// its pellets land in, so you can see what's in range of the spread.
        /// </summary>
        private void DrawCrosshair()
        {
            float spreadRadius = MathF.Tan(MathHelper.ToRadians(_player.Gun.Spread))
                / MathF.Tan(MathHelper.ToRadians(_player.Camera.FieldOfView) * 0.5f) * Canvas.Height * 0.5f;
            float gap = MathF.Max(5f, spreadRadius);
            const float length = 8f;
            const float thickness = 2f;

            Vector2 centre = Canvas.Size / 2f;
            Color color = _hitMarkerTime > 0f ? _hitColor : _crosshairColor;

            // Each line gets a dark outline drawn first, so it shows up on bright and dark backgrounds alike.
            foreach (Vector2 direction in (ReadOnlySpan<Vector2>)[Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY])
            {
                Vector2 along = direction * (gap + length * 0.5f);
                Vector2 size = direction.X != 0f ? new Vector2(length, thickness) : new Vector2(thickness, length);
                Vector2 topLeft = centre + along - size / 2f;

                Canvas.DrawRect(topLeft - Vector2.One, size + new Vector2(2f), _outline);
                Canvas.DrawRect(topLeft, size, color);
            }
        }

        private void DrawDot()
        {
            Vector2 centre = Canvas.Size / 2f;
            Color color = _hitMarkerTime > 0f ? _hitColor : _crosshairColor;
            Canvas.DrawRect(centre - new Vector2(2.5f), new Vector2(5f), _outline);
            Canvas.DrawRect(centre - new Vector2(1.5f), new Vector2(3f), color);
        }

        /// <summary>
        /// The view down the scope: a circle as tall as the screen with black either side, thin crosshairs through the
        /// middle that thicken towards the edge, as on CS's AWP.
        /// </summary>
        private void DrawScope()
        {
            float size = Canvas.Height;
            Vector2 centre = Canvas.Size / 2f;
            float left = centre.X - size / 2f;
            Color black = Color.Black;

            Canvas.DrawImage(_scopeMask, new Vector2(left, 0f), new Vector2(size, size), Color.White);
            Canvas.DrawRect(Vector2.Zero, new Vector2(left + 1f, size), black);
            Canvas.DrawRect(new Vector2(left + size - 1f, 0f), new Vector2(Canvas.Width - left - size + 1f, size), black);

            Color line = _hitMarkerTime > 0f ? _hitColor : black;
            float radius = size / 2f;
            float post = radius * 0.55f;
            Canvas.DrawRect(new Vector2(left, centre.Y - 0.5f), new Vector2(size, 1f), line);
            Canvas.DrawRect(new Vector2(centre.X - 0.5f, 0f), new Vector2(1f, size), line);

            // Thick posts from part way out to the edge, which lead the eye to the centre.
            Canvas.DrawRect(new Vector2(left, centre.Y - 2f), new Vector2(radius - post, 4f), black);
            Canvas.DrawRect(new Vector2(centre.X + post, centre.Y - 2f), new Vector2(radius - post, 4f), black);
            Canvas.DrawRect(new Vector2(centre.X - 2f, centre.Y + post), new Vector2(4f, radius - post), black);
        }

        private static Texture CreateScopeMask(int size)
        {
            var pixels = new byte[size * size * 4];
            float half = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 0 at the centre, 1 at the edge of the circle.
                    float distance = MathF.Sqrt((x + 0.5f - half) * (x + 0.5f - half) + (y + 0.5f - half) * (y + 0.5f - half)) / half;

                    // Solid outside the circle, with a two-pixel soft edge, and darkening gently over the last part inside.
                    float outside = Math.Clamp((distance - 1f) * half / 2f + 0.5f, 0f, 1f);
                    float vignette = Math.Clamp((distance - 0.8f) / 0.2f, 0f, 1f);
                    float alpha = MathF.Max(outside, vignette * vignette * 0.6f);

                    // Black, so it's the same premultiplied or not.
                    pixels[(y * size + x) * 4 + 3] = (byte)(alpha * 255f);
                }
            }

            return new Texture(size, size, pixels);
        }

        /// <summary>The weapon you're holding, and below it both slots, the one in hand lit up, and how to change class.</summary>
        private static void DrawWeapon(Gun gun)
        {
            var corner = new Vector2(Canvas.Width - 28f, Canvas.Height - 36f);
            float nameWidth = Canvas.MeasureText(Fonts.Bold, gun.Weapon.Name, 24f).X;
            Canvas.DrawText(Fonts.Bold, gun.Weapon.Name, new Vector2(corner.X - nameWidth, corner.Y - 32f), Color.White, 24f, shadow: true);

            var dim = new Color(1f, 1f, 1f, 0.55f);
            (string Text, Color Color)[] parts =
            [
                ($"1 {Weapon.Knife.Name}", gun.Weapon == Weapon.Knife ? Color.White : dim),
                ($"    2 {gun.Primary.Name}", gun.Weapon == gun.Primary ? Color.White : dim),
                ("    ·    B to change class", dim)
            ];

            const float size = 14f;
            float x = corner.X - parts.Sum(part => Canvas.MeasureText(Fonts.Regular, part.Text, size).X);
            foreach ((string text, Color color) in parts)
            {
                x += Canvas.DrawText(Fonts.Regular, text, new Vector2(x, corner.Y - 2f), color, size, shadow: true);
            }
        }

        private void DrawHealth()
        {
            const float barWidth = 180f;
            const float barHeight = 8f;

            float fraction = Math.Clamp(_player.Health / (float)Player.MaxHealth, 0f, 1f);
            var color = new Color(1f - fraction * 0.6f, 0.3f + 0.6f * fraction, 0.25f);
            var corner = new Vector2(28f, Canvas.Height - 36f);

            float width = Canvas.DrawText(Fonts.Bold, _player.Health.ToString(), corner - new Vector2(0f, 52f), color, 40f, shadow: true);
            Canvas.DrawText(Fonts.Regular, "HP", corner - new Vector2(-width - 6f, 34f), new Color(1f, 1f, 1f, 0.7f), 18f, shadow: true);

            Canvas.DrawRect(corner - Vector2.One, new Vector2(barWidth + 2f, barHeight + 2f), _outline);
            Canvas.DrawRect(corner, new Vector2(barWidth * fraction, barHeight), color);
        }

        private void DrawDeathScreen()
        {
            Canvas.DrawRect(Vector2.Zero, Canvas.Size, new Color(0.4f, 0f, 0f, 0.25f));

            DrawCentred(Fonts.Bold, "You died", Canvas.Height * 0.4f, 42f, Color.White);
            DrawCentred(Fonts.Regular, $"Respawning in {MathF.Ceiling(_player.RespawnIn):0}", Canvas.Height * 0.4f + 54f, 20f, new Color(1f, 1f, 1f, 0.8f));
        }

        private static void DrawCentred(Font font, string text, float y, float size, Color color)
        {
            float width = Canvas.MeasureText(font, text, size).X;
            Canvas.DrawText(font, text, new Vector2((Canvas.Width - width) / 2f, y), color, size, shadow: true);
        }
    }
}
