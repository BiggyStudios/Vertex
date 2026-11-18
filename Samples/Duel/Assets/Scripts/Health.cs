using Vertex;
using Vertex.Networking;

namespace Duel
{
    /// <summary>
    /// A player's health. Hits arrive as "Hit" network events on the machine that owns the player; at none left, it tells
    /// everyone who scored and starts again at the spawn point furthest from the other player.
    /// </summary>
    public class Health : Component
    {
        public float MaxHealth { get; set; } = 100f;

        /// <summary>How much is left.</summary>
        public float Current { get; private set; }

        protected override void Start()
        {
            Current = MaxHealth;
        }

        protected override void OnNetworkEvent(NetworkEvent networkEvent)
        {
            if (networkEvent.Name != "Hit" || !IsMine) return;

            float damage = networkEvent.Values.ElementAtOrDefault(0) as float? ?? 0f;
            Current -= damage;
            if (Current > 0f) return;

            // To every script: the scoreboards count it. The second value of a hit is who shot.
            NetworkScene.SendEvent("Scored", null, NetworkTarget.Everyone, networkEvent.Values.ElementAtOrDefault(1));
            Respawn();
        }

        private void Respawn()
        {
            Current = MaxHealth;
            if (SpawnPoint.FurthestFromOthers(GameObject) is { } point)
            {
                Transform.Position = point.Transform.WorldPosition;
                Transform.Rotation = point.Transform.WorldRotation;
            }
        }
    }
}
