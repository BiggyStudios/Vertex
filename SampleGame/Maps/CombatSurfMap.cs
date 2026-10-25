using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// surf_vertex: a small combat surf arena in the style of CS:S combat surf maps, on the other side of the sandbox
    /// from the practice course.
    /// <para>
    /// Two spawns face each other from opposite ends, high up. Red's lane runs south down the east side and Blue's runs
    /// north down the west side, 50 units apart, so players on the two lanes surf past each other with a clear shot across
    /// the gap. Each lane is two downhill ramps with a gap between them, and ends on a platform whose pad sends you to the
    /// <em>other</em> spawn, so the lanes make one continuous loop. Between the lanes is an island with cover and a tower,
    /// for anyone who bails off a ramp to fight on foot, with bounce pads that throw you back up to either spawn.
    /// Falling into the water below is a death.
    /// </para>
    /// </summary>
    public static class CombatSurfMap
    {
        /// <summary>The middle of the arena. Everything else is placed relative to it.</summary>
        public static Vector3 Centre { get; } = new(-220f, 0f, 0f);

        private const float _laneOffset = 25f;    // each lane's ridge, either side of the middle
        private const float _spawnHeight = 62f;   // the top of the spawn platforms
        private const float _islandTop = 29f;

        private static readonly Color _red = new(0.95f, 0.3f, 0.25f);
        private static readonly Color _blue = new(0.3f, 0.55f, 1f);

        // Each spawn sits just inside its lane, so walking off the edge drops you onto the slope facing the middle.
        public static SpawnPoint RedSpawn { get; } = new(Centre + new Vector3(21f, _spawnHeight, 79f), 0f);
        public static SpawnPoint BlueSpawn { get; } = new(Centre + new Vector3(-21f, _spawnHeight, -79f), 180f);

        public static Zone Zone { get; } = new("surf_vertex", Surf: true,
        [
            RedSpawn,
            RedSpawn with { Position = RedSpawn.Position + new Vector3(-6f, 0f, 3f) },
            BlueSpawn,
            BlueSpawn with { Position = BlueSpawn.Position + new Vector3(6f, 0f, -3f) }
        ]);

        /// <param name="exit">Where the pads in the spawns send the player, back in the sandbox.</param>
        public static void Build(SpawnPoint exit)
        {
            Texture redRamps = MapBuilder.Checker(new Color(0.85f, 0.55f, 0.45f), new Color(0.7f, 0.4f, 0.32f));
            Texture blueRamps = MapBuilder.Checker(new Color(0.5f, 0.62f, 0.85f), new Color(0.36f, 0.47f, 0.7f));
            Texture stone = MapBuilder.Checker(new Color(0.62f, 0.62f, 0.6f), new Color(0.52f, 0.52f, 0.5f));

            BuildSide("Red", _red, redRamps, stone, RedSpawn, sign: 1f, loopTo: BlueSpawn, exit);
            BuildSide("Blue", _blue, blueRamps, stone, BlueSpawn, sign: -1f, loopTo: RedSpawn, exit);
            BuildIsland(stone);

            // Tall pillars at the corners, so it's easy to tell which way you're facing.
            foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[new(-48f, -100f), new(48f, -100f), new(-48f, 100f), new(48f, 100f)])
            {
                Vector3 position = Centre + new Vector3(corner.X, 40f, corner.Y);
                MapBuilder.Block("Pillar", position, new Vector3(6f, 80f, 6f), new Color(0.35f, 0.36f, 0.4f), stone);
                MapBuilder.Glow("Pillar Light", position + new Vector3(0f, 40.5f, 0f), new Vector3(6.2f, 1f, 6.2f), corner.Y > 0f ? _red : _blue);
            }

            // Falling off is a death here, as on combat surf servers; you respawn at one of the spawns.
            MapBuilder.Water("surf_vertex Water", Centre, new Vector2(150f, 240f));
            MapBuilder.Trigger("surf_vertex Kill", Centre + new Vector3(0f, -1.5f, 0f), new Vector3(150f, 5f, 240f)).AddComponent<KillTrigger>();
        }

        /// <summary>
        /// One team's spawn, lane and end platform. <paramref name="sign"/> is 1 for Red (north end, east lane, heading
        /// south) and -1 for Blue, which is the same thing turned around.
        /// </summary>
        private static void BuildSide(string team, Color color, Texture ramps, Texture stone, SpawnPoint spawn, float sign, SpawnPoint loopTo, SpawnPoint exit)
        {
            Vector3 Place(float x, float y, float z) => Centre + new Vector3(x * sign, y, z * sign);
            float heading = sign > 0f ? 0f : 180f;

            // The spawn: a platform with a wall behind, a glowing team-coloured edge, two crates for cover and a pad home.
            MapBuilder.Block($"{team} Spawn", Place(21f, _spawnHeight - 1f, 79f), new Vector3(24f, 2f, 18f), new Color(0.8f, 0.8f, 0.8f), stone);
            var paleColor = new Color(color.R + (1f - color.R) * 0.35f, color.G + (1f - color.G) * 0.35f, color.B + (1f - color.B) * 0.35f);
            MapBuilder.Block($"{team} Spawn Wall", Place(21f, _spawnHeight + 4f, 89f), new Vector3(24f, 10f, 2f), paleColor, stone);
            MapBuilder.Glow($"{team} Spawn Edge", Place(21f, _spawnHeight - 0.4f, 69.95f), new Vector3(24.2f, 0.8f, 0.2f), color);
            MapBuilder.Block($"{team} Spawn Crate", Place(13f, _spawnHeight + 1f, 82f), new Vector3(2f, 2f, 2f), new Color(0.65f, 0.45f, 0.25f));
            MapBuilder.Block($"{team} Spawn Crate", Place(28f, _spawnHeight + 1.5f, 81f), new Vector3(3f, 3f, 2f), new Color(0.65f, 0.45f, 0.25f));
            MapBuilder.TeleportPad($"{team} Exit", Place(30f, _spawnHeight, 86f), exit, Zone.Sandbox, new Color(0.2f, 0.9f, 1f));

            // The lane: two downhill ramps with an 8-unit gap in the middle. The second starts well below where the first
            // ends, because you drop a few units flying across the gap and would otherwise hit its end.
            MapBuilder.Ramp($"{team} Ramp 1", Place(_laneOffset, 58f, 64f), length: 56f, drop: 8f, ramps, heading);
            MapBuilder.Ramp($"{team} Ramp 2", Place(_laneOffset, 43f, 0f), length: 56f, drop: 8f, ramps, heading);

            // The end: a platform with a pad, in the other team's colour, to the other spawn, so the lanes make a loop.
            Color otherColor = sign > 0f ? _blue : _red;
            MapBuilder.Block($"{team} Lane End", Place(_laneOffset, 26f, -72f), new Vector3(20f, 2f, 20f), new Color(0.8f, 0.8f, 0.8f), stone);
            MapBuilder.TeleportPad($"{team} Lane End Pad", Place(_laneOffset, 27f, -76f), loopTo, Zone, otherColor);
        }

        /// <summary>
        /// The island between the lanes: a long platform with crates for cover and a tower in the middle. Wide enough that
        /// letting go of strafe on either lane's first ramp (sliding off its inner side) lands you on it.
        /// </summary>
        private static void BuildIsland(Texture stone)
        {
            var crate = new Color(0.65f, 0.45f, 0.25f);

            // Reaching to just inside the lanes' inner edges, and below the ramps, so it never touches them.
            MapBuilder.Block("Island", Centre + new Vector3(0f, 28f, 0f), new Vector3(32f, 2f, 34f), new Color(0.75f, 0.75f, 0.72f), stone);
            MapBuilder.Block("Island Tower", Centre + new Vector3(0f, 33f, 0f), new Vector3(4f, 8f, 4f), new Color(0.55f, 0.56f, 0.6f), stone);
            MapBuilder.Glow("Island Tower Light", Centre + new Vector3(0f, 37.1f, 0f), new Vector3(4.2f, 0.2f, 4.2f), new Color(1f, 0.85f, 0.4f));

            MapBuilder.Block("Island Crate", Centre + new Vector3(-9f, 30f, 9f), new Vector3(2f, 2f, 2f), crate);
            MapBuilder.Block("Island Crate", Centre + new Vector3(9f, 30f, -9f), new Vector3(2f, 2f, 2f), crate);
            MapBuilder.Block("Island Crate", Centre + new Vector3(6f, 30.75f, 10f), new Vector3(1.5f, 3.5f, 4f), crate);
            MapBuilder.Block("Island Crate", Centre + new Vector3(-6f, 30.75f, -10f), new Vector3(1.5f, 3.5f, 4f), crate);
            MapBuilder.Block("Island Crate", Centre + new Vector3(-11f, 30f, -3f), new Vector3(2f, 2f, 3f), crate);
            MapBuilder.Block("Island Crate", Centre + new Vector3(11f, 30f, 3f), new Vector3(2f, 2f, 3f), crate);

            // A bounce pad either side of the tower throws you up and forward the way you face, so the island isn't a dead
            // end. Each is about as far from both spawns (80 and 84 m), so face either one and it gets you there, as
            // long as you aim well and strafe a little. On the way up you're an easy target for anyone on the ramps.
            var padColor = new Color(1f, 0.85f, 0.4f);
            float spawnRise = _spawnHeight - _islandTop;
            float reach = 82f;
            MapBuilder.BouncePad("Island Pad East", Centre + new Vector3(8f, _islandTop, 0f), padColor, height: spawnRise + 9f, reach, spawnRise);
            MapBuilder.BouncePad("Island Pad West", Centre + new Vector3(-8f, _islandTop, 0f), padColor, height: spawnRise + 9f, reach, spawnRise);
        }
    }
}
