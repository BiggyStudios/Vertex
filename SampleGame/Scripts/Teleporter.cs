using System.Numerics;

using Vertex;
using Vertex.Physics;

namespace SampleGame
{
    /// <summary>Sends the player somewhere else when they touch it, like Source's trigger_teleport. Needs a trigger collider.</summary>
    public class Teleporter : Component
    {
        public Vector3 Destination { get; set; }

        /// <summary>Which way the player faces on arrival, in degrees. 0 faces -Z.</summary>
        public float DestinationYaw { get; set; }

        /// <summary>If set, changes the player's air acceleration on arrival, the way surf servers raise sv_airaccelerate.</summary>
        public float? AirAcceleration { get; set; }

        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is not Player player) return;

            player.Teleport(Destination, DestinationYaw);
            if (AirAcceleration is float airAcceleration) player.AirAcceleration = airAcceleration;
        }
    }
}
