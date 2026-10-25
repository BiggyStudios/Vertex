using System.Numerics;

using Vertex;
using Vertex.Physics;

namespace SampleGame
{
    /// <summary>
    /// Sends the player somewhere else when they touch it, like Source's trigger_teleport. Needs a trigger collider. They
    /// keep their speed, turned to match the way they face on arrival, unless <see cref="KeepVelocity"/> is off.
    /// </summary>
    public class Teleporter : Component
    {
        public Vector3 Destination { get; set; }

        /// <summary>Which way the player faces on arrival, in degrees. 0 faces -Z.</summary>
        public float DestinationYaw { get; set; }

        /// <summary>If set, the player arrives in this zone: they respawn there, and surf zones have their own air control.</summary>
        public Zone? Zone { get; set; }

        /// <summary>Send the player to a random spawn in <see cref="Zone"/> instead of <see cref="Destination"/>, so arrivals spread out.</summary>
        public bool ToRandomSpawn { get; set; }

        /// <summary>Keep the player's speed through the teleport. Off for resets, like falling into water, which should stop them.</summary>
        public bool KeepVelocity { get; set; } = true;

        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is not Player player) return;

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
    }
}
