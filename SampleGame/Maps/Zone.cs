using System.Numerics;

using Vertex;

namespace SampleGame
{
    /// <summary>Where a player appears, and which way they face (degrees; 0 faces -Z).</summary>
    public readonly record struct SpawnPoint(Vector3 Position, float Yaw)
    {
        /// <summary>A spawn point at <paramref name="position"/>, facing <paramref name="target"/>.</summary>
        public static SpawnPoint Facing(Vector3 position, Vector3 target)
        {
            Vector3 direction = target - position;
            return new SpawnPoint(position, MathHelper.ToDegrees(MathF.Atan2(-direction.X, -direction.Z)));
        }
    }

    /// <summary>
    /// An area of the world. Players respawn at one of its spawn points, and in surf areas air control comes from
    /// sv_surf_airaccelerate. Teleport pads move players between zones.
    /// </summary>
    public sealed record Zone(string Name, bool Surf, IReadOnlyList<SpawnPoint> Spawns)
    {
        /// <summary>The starting area, with spawns spread around it, clear of the crates and the house, all facing the middle.</summary>
        public static Zone Sandbox { get; } = new("Sandbox", Surf: false,
        [
            SpawnPoint.Facing(Player.SpawnPosition, Vector3.Zero),
            SpawnPoint.Facing(new Vector3(-10f, 0f, 8f), Vector3.Zero),
            SpawnPoint.Facing(new Vector3(10f, 0f, -8f), Vector3.Zero),
            SpawnPoint.Facing(new Vector3(-11f, 0f, -11f), Vector3.Zero),
            SpawnPoint.Facing(new Vector3(11f, 0f, 11f), Vector3.Zero),
            SpawnPoint.Facing(new Vector3(0f, 0f, -12f), Vector3.Zero)
        ]);

        public SpawnPoint RandomSpawn() => Spawns[Random.Shared.Next(Spawns.Count)];
    }
}
