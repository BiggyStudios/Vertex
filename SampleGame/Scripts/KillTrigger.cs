using Vertex;
using Vertex.Physics;

namespace SampleGame
{
    /// <summary>Kills any player who touches it, like Source's trigger_hurt at the bottom of a surf map. Needs a trigger collider.</summary>
    public class KillTrigger : Component
    {
        protected override void OnTriggerEnter(Collider other)
        {
            if (other.GameObject is Player player) player.Kill(fell: true);
        }
    }
}
