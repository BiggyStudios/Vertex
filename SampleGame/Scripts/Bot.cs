using System.Numerics;

using Vertex;
using Vertex.Networking;

namespace SampleGame
{
    /// <summary>
    /// A practice target, put down with the bot_add console command (which needs sv_cheats): it stands where it was put,
    /// facing the way you were, and does nothing else. Shots, the knife and rockets hurt it just like a player, with hit
    /// markers, headshots, the kill popup and the kill feed, so it's good for practising and for clips. It gets back up a
    /// few seconds after dying. In a room everyone sees the same bots: the host's game decides how they're doing, and
    /// everyone else's are copies of the host's.
    /// </summary>
    public sealed class Bot : RemotePlayer
    {
        private const float _respawnTime = 3f;

        // Close enough to the player that they're standing in it, horizontally.
        private const float _overlap = 1f;

        private static readonly Color _color = new(0.62f, 0.64f, 0.68f);

        private readonly Player _player;
        private int _health;
        private float _deadFor;

        // Put down on top of the player, it lets them walk out of it before it's solid, so they don't get stuck.
        private bool _ghost;

        /// <summary>
        /// Puts bot number <paramref name="number"/> at <paramref name="position"/>, facing <paramref name="yaw"/>. Its
        /// peer id counts down from the top, where no real player's is, so it's the same in everyone's game.
        /// </summary>
        public Bot(int number, Vector3 position, float yaw, int health, Player player) : base(new PeerId(ulong.MaxValue - (ulong)number), $"Bot {number}", _color)
        {
            Number = number;
            Spot = position;
            Facing = yaw;
            _player = player;
            _health = health;
            _ghost = PlayerInside();
            Report();
        }

        public int Number { get; }

        /// <summary>Where it stands.</summary>
        public Vector3 Spot { get; }

        /// <summary>Which way it faces, as a yaw in degrees.</summary>
        public float Facing { get; }

        /// <summary>Its health as this game has it, which doesn't wait for its next update like <see cref="RemotePlayer.Health"/>.</summary>
        public int HitPoints => _health;

        /// <summary>Raised when it gets back up, in the host's game: the only one that brings bots back.</summary>
        public event Action<Bot>? GotUp;

        /// <summary>Takes <paramref name="damage"/>. True if that killed it.</summary>
        public bool Hurt(int damage)
        {
            if (_health <= 0) return false;

            SetHealth(Math.Max(_health - damage, 0));
            return _health == 0;
        }

        /// <summary>Sets its health: from the host, for a copy of its bot.</summary>
        public void SetHealth(int health)
        {
            if (health <= 0 && _health > 0) _deadFor = 0f;
            if (health > 0 && _health <= 0) _ghost = PlayerInside();
            _health = health;
        }

        protected override bool Solid => base.Solid && !_ghost;

        protected override void Update()
        {
            if (_health <= 0)
            {
                _deadFor += Time.DeltaTime;
                if (_deadFor >= _respawnTime && Network.IsHost)
                {
                    SetHealth(Player.MaxHealth);
                    GotUp?.Invoke(this);
                }
            }

            if (_ghost && !PlayerInside()) _ghost = false;

            Report();
            base.Update();
        }

        /// <summary>Tells itself where it is and how it's doing, as a player's updates would.</summary>
        private void Report() =>
            AddState(new PlayerState(Time.TotalTime, Spot, Vector3.Zero, Facing, 0f, (byte)_health, _health > 0, 0, 0, WeaponId.Deagle));

        private bool PlayerInside()
        {
            Vector3 offset = _player.Transform.WorldPosition - Spot;
            return new Vector2(offset.X, offset.Z).Length() < _overlap && MathF.Abs(offset.Y) < 2f;
        }
    }
}
