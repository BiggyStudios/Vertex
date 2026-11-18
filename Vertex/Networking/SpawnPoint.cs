using System.Numerics;

namespace Vertex.Networking
{
    /// <summary>
    /// A place players start, and come back to after dying: put one on an empty object where a player should stand,
    /// facing the way they should look. <see cref="NetworkManager"/> starts each player at a different one.
    /// </summary>
    public sealed class SpawnPoint : Component
    {
        /// <summary>The spawn points in the scene, in the order they're in it.</summary>
        public static List<SpawnPoint> All => Scene.Active.FindComponents<SpawnPoint>().ToList();

        /// <summary>The <paramref name="index"/>th spawn point (going round), or null if there are none.</summary>
        public static SpawnPoint? At(int index)
        {
            List<SpawnPoint> all = All;
            return all.Count > 0 ? all[((index % all.Count) + all.Count) % all.Count] : null;
        }

        /// <summary>The spawn point furthest from every other player (not <paramref name="self"/>): a fair place to come back.</summary>
        public static SpawnPoint? FurthestFromOthers(GameObject? self)
        {
            List<Vector3> others = NetworkScene.Players.Where(p => p.GameObject != self).Select(p => p.Transform.WorldPosition).ToList();
            SpawnPoint? best = null;
            float bestDistance = float.MinValue;
            foreach (SpawnPoint point in All)
            {
                Vector3 at = point.Transform.WorldPosition;
                float nearest = others.Count == 0 ? Random.Shared.NextSingle() : others.Min(o => Vector3.DistanceSquared(o, at));
                if (nearest <= bestDistance) continue;
                bestDistance = nearest;
                best = point;
            }

            return best;
        }
    }
}
