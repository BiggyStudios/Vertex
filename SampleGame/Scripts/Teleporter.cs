using System.Numerics;

using Silk.NET.Input;

using Vertex;
using Vertex.Input;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.UI;

namespace SampleGame
{
    /// <summary>
    /// Sends the player somewhere else, like Source's trigger_teleport. Needs a trigger collider. They keep their
    /// speed, turned to match the way they face on arrival, unless <see cref="KeepVelocity"/> is off.
    /// <para>
    /// With <see cref="OnPress"/>, as on the pads between maps, standing in it does nothing until E is pressed, with a
    /// prompt on screen. Without it, it goes the moment it's touched, as resets and surf loops need.
    /// </para>
    /// </summary>
    public class Teleporter : Component
    {
        private const float _keySize = 30f;
        private const float _textSize = 20f;

        public Vector3 Destination { get; set; }

        /// <summary>Which way the player faces on arrival, in degrees. 0 faces -Z.</summary>
        public float DestinationYaw { get; set; }

        /// <summary>If set, the player arrives in this zone: they respawn there, and surf zones have their own air control.</summary>
        public Zone? Zone { get; set; }

        /// <summary>Send the player to a random spawn in <see cref="Zone"/> instead of <see cref="Destination"/>, so arrivals spread out.</summary>
        public bool ToRandomSpawn { get; set; }

        /// <summary>Keep the player's speed through the teleport. Off for resets, like falling into water, which should stop them.</summary>
        public bool KeepVelocity { get; set; } = true;

        /// <summary>Wait for E, with a prompt, rather than going on touch.</summary>
        public bool OnPress { get; set; }

        /// <summary>The pad's glowing material, which brightens while the player stands on it. Optional.</summary>
        public Material? Glow { get; set; }

        // The player standing in it, waiting to press E, and the pad's normal glow.
        private Player? _inside;
        private float _restingGlow;

        protected override void Start()
        {
            if (Glow != null) _restingGlow = Glow.EmissionStrength;
        }

        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is not Player player) return;

            if (!OnPress) Send(player);
            else _inside = player;
        }

        protected override void OnTriggerExit(Collider other)
        {
            if (other.GameObject == _inside) _inside = null;
        }

        protected override void Update()
        {
            // Noclip flies up on E, so it doesn't count as asking to go.
            bool ready = _inside is { IsDead: false, Noclip: false };
            if (Glow != null) Glow.EmissionStrength = ready ? _restingGlow * (1.8f + 0.4f * MathF.Sin(Time.TotalTime * 6f)) : _restingGlow;
            if (!ready || GameInput.LookBlocked) return;

            DrawPrompt();
            if (!GameInput.Blocked && InputSystem.GetKeyDown(Key.E))
            {
                Player player = _inside!;
                _inside = null;
                Send(player);
            }
        }

        private void Send(Player player)
        {
            if (ToRandomSpawn && Zone != null)
            {
                SpawnPoint spawn = Zone.RandomSpawn();
                player.Teleport(spawn.Position, spawn.Yaw, KeepVelocity);
            }
            else
            {
                player.Teleport(Destination, DestinationYaw, KeepVelocity);
            }

            if (Zone != null) player.Zone = Zone;
        }

        /// <summary>A key cap with E on it, and what it does, below the crosshair.</summary>
        private void DrawPrompt()
        {
            const string text = "Teleport";
            float textWidth = Canvas.MeasureText(Fonts.Bold, text, _textSize).X;
            float width = _keySize + 12f + textWidth;
            var at = new Vector2((Canvas.Width - width) / 2f, Canvas.Height * 0.62f);

            Canvas.DrawRect(at - new Vector2(14f, 10f), new Vector2(width + 28f, _keySize + 20f), new Color(0.05f, 0.06f, 0.08f, 0.7f));
            Canvas.DrawRect(at, new Vector2(_keySize), new Color(1f, 1f, 1f, 0.92f));
            Vector2 key = Canvas.MeasureText(Fonts.Bold, "E", _textSize);
            Canvas.DrawText(Fonts.Bold, "E", at + (new Vector2(_keySize) - key) / 2f, new Color(0.08f, 0.09f, 0.11f), _textSize);
            float textY = at.Y + (_keySize - Canvas.LineHeight(Fonts.Bold, _textSize)) / 2f;
            Canvas.DrawText(Fonts.Bold, text, new Vector2(at.X + _keySize + 12f, textY), Color.White, _textSize, shadow: true);
        }
    }
}
