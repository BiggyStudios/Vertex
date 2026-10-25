using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// surf_skyhall: a bigger surf and bhop map, north of the sandbox, laid out as one loop you can ride round and round.
    /// <para>
    /// In the middle are two decks: a wide lower deck and an upper deck held up on pillars above it, both open for
    /// bhopping. The upper deck has a square hole in its middle, and a pad on the lower deck right under it throws you up
    /// through the hole: drift to one side at the top to land on the deck. The covered space under the upper deck has two
    /// small surf ramps and a couple of hop pads, and a pad at each end of the lower deck throws you onto the upper deck too.
    /// </para>
    /// <para>
    /// From the upper deck, a straight ramp runs east and another west, each launching you across a gap into a long roofed
    /// hall floating in the air, to bhop through, with walls to weave round and a small ramp. Pads at the edge of the lower
    /// deck throw you into the halls as well. At the far end of each hall, a bounce pad throws you onto a curved ramp that
    /// turns all the way back, runs along the outside of the hall and bends in to drop you on the lower deck: the east one
    /// comes in from the north, the west one from the south. Orange arrows on the ramps are boosters. Falling into the
    /// water is a death.
    /// </para>
    /// <para>
    /// High above, a sky platform floats off the north and south ends, reached by a pad on that end of the upper deck.
    /// From each, one curved ramp swings round over the upper deck and lines you up to land on a hall roof, where a pad at
    /// the far end throws you onto that hall's curved ramp; another drops steeply round to the lower deck.
    /// </para>
    /// <para>
    /// Blue orbs are grapple points (aim at one and hold Shift). Three in a line high over the middle let you swing from
    /// one sky platform to the other. Two more off each sky platform's outer end lead to a grapple island further out,
    /// with a tower, a ring of ramp that loops round and back, and a cannon pad home. There's one over the end of each hall
    /// roof and one in the middle of each curved ramp's U-turn, for swinging round the turn.
    /// </para>
    /// </summary>
    public static class SkyhallMap
    {
        /// <summary>The middle of the map. Everything else is placed relative to it, and the west half is the east half turned round.</summary>
        public static Vector3 Centre { get; } = new(0f, 0f, 420f);

        private const float _lowerDeck = 20f;   // the top of the lower deck
        private const float _upperDeck = 40f;   // the top of the upper deck
        private const float _lowerHalf = 50f;   // half the lower deck's width
        private const float _upperHalf = 25f;   // half the upper deck's width
        private const float _holeHalf = 5f;     // half the width of the hole in the upper deck
        private const float _hallFloor = 24f;   // the top of the halls' floors and their exits
        private const float _hallStart = 78f;   // how far east (or west) of the middle each hall's walls start
        private const float _hallLength = 70f;
        private const float _hallHalfWidth = 13f;
        private const float _hallHeight = 14f;
        private const float _apron = 7f;        // how far each hall's floor reaches out past its walls, to land on
        private const float _sky = 72f;         // the top of the sky platforms
        private const float _skyOut = 100f;     // how far north (or south) of the middle the sky platforms are
        private const float _island = 50f;      // the top of the grapple islands

        private static readonly Color _east = new(0.75f, 0.45f, 1f);
        private static readonly Color _west = new(0.3f, 0.9f, 0.85f);
        private static readonly Color _gold = new(1f, 0.85f, 0.4f);
        private static readonly Color _skyColor = new(1f, 0.45f, 0.35f);
        private static readonly Color _islandColor = new(0.45f, 1f, 0.55f);
        private static readonly Color _stoneColor = new(0.78f, 0.78f, 0.76f);
        private static readonly Color _pillarColor = new(0.55f, 0.56f, 0.6f);

        public static Zone Zone { get; } = new("surf_skyhall", Surf: true,
        [
            SpawnPoint.Facing(Centre + new Vector3(15f, _upperDeck, 15f), Centre with { Y = _upperDeck }),
            SpawnPoint.Facing(Centre + new Vector3(-15f, _upperDeck, 15f), Centre with { Y = _upperDeck }),
            SpawnPoint.Facing(Centre + new Vector3(15f, _upperDeck, -15f), Centre with { Y = _upperDeck }),
            SpawnPoint.Facing(Centre + new Vector3(40f, _lowerDeck, 40f), Centre with { Y = _lowerDeck }),
            SpawnPoint.Facing(Centre + new Vector3(-40f, _lowerDeck, -40f), Centre with { Y = _lowerDeck }),
            SpawnPoint.Facing(Centre + new Vector3(0f, _sky, -_skyOut - 5f), Centre with { Y = _sky }),
            SpawnPoint.Facing(Centre + new Vector3(0f, _sky, _skyOut + 5f), Centre with { Y = _sky })
        ]);

        /// <param name="exit">Where the pad on the upper deck sends the player, back in the sandbox.</param>
        public static void Build(SpawnPoint exit)
        {
            Texture stone = MapBuilder.Checker(new Color(0.62f, 0.62f, 0.6f), new Color(0.52f, 0.52f, 0.5f));

            BuildDecks(stone);
            BuildSide("East", 1f, _east, MapBuilder.Checker(new Color(0.62f, 0.5f, 0.85f), new Color(0.48f, 0.37f, 0.7f)), stone);
            BuildSide("West", -1f, _west, MapBuilder.Checker(new Color(0.42f, 0.75f, 0.74f), new Color(0.3f, 0.6f, 0.6f)), stone);
            Texture skyRamps = MapBuilder.Checker(new Color(0.9f, 0.55f, 0.48f), new Color(0.75f, 0.4f, 0.35f));
            BuildSky("North", 1f, skyRamps, stone);
            BuildSky("South", -1f, skyRamps, stone);
            BuildSkyLine();
            Texture islandRamps = MapBuilder.Checker(new Color(0.5f, 0.82f, 0.55f), new Color(0.36f, 0.66f, 0.42f));
            BuildIsland("North-West", 1f, islandRamps, stone);
            BuildIsland("South-East", -1f, islandRamps, stone);

            MapBuilder.TeleportPad("surf_skyhall Exit", Centre + new Vector3(-15f, _upperDeck, -15f), exit, Zone.Sandbox, new Color(0.2f, 0.9f, 1f));

            // Tall pillars round the outside, coloured for the side they're on, so it's easy to tell which way you face.
            foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[new(-80f, -85f), new(80f, -85f), new(-80f, 85f), new(80f, 85f)])
            {
                Vector3 position = Centre + new Vector3(corner.X, 35f, corner.Y);
                MapBuilder.Block("Skyhall Pillar", position, new Vector3(6f, 70f, 6f), new Color(0.35f, 0.36f, 0.4f), stone);
                MapBuilder.Glow("Skyhall Pillar Light", position + new Vector3(0f, 35.5f, 0f), new Vector3(6.2f, 1f, 6.2f), corner.X > 0f ? _east : _west);
            }

            MapBuilder.Water("surf_skyhall Water", Centre, new Vector2(480f, 310f));
            MapBuilder.Trigger("surf_skyhall Kill", Centre + new Vector3(0f, -1.5f, 0f), new Vector3(480f, 5f, 310f)).AddComponent<KillTrigger>();
        }

        /// <summary>
        /// The two decks in the middle, the hole through the upper one and the pads between them, and cover. The
        /// north-east and south-west corners of the lower deck are kept clear: that's where the curved ramps drop you.
        /// </summary>
        private static void BuildDecks(Texture stone)
        {
            var crate = new Color(0.65f, 0.45f, 0.25f);

            MapBuilder.Block("Lower Deck", Centre + new Vector3(0f, _lowerDeck - 1f, 0f), new Vector3(_lowerHalf * 2f, 2f, _lowerHalf * 2f), _stoneColor, stone);

            // The upper deck is four slabs round the hole: a long one to the north and south, a short one east and west.
            float slab = _upperHalf - _holeHalf;
            float slabMiddle = (_upperHalf + _holeHalf) * 0.5f;
            foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
            {
                MapBuilder.Block("Upper Deck", Centre + new Vector3(0f, _upperDeck - 1f, side * slabMiddle), new Vector3(_upperHalf * 2f, 2f, slab), _stoneColor, stone);
                MapBuilder.Block("Upper Deck", Centre + new Vector3(side * slabMiddle, _upperDeck - 1f, 0f), new Vector3(slab, 2f, _holeHalf * 2f), _stoneColor, stone);
            }

            // Glowing edges, so the drops are easy to see, and a ring round the hole at the top and bottom.
            AddEdges("Lower Deck Edge", _lowerDeck, _lowerHalf, _gold);
            AddEdges("Upper Deck Edge", _upperDeck, _upperHalf, _gold);
            AddEdges("Hole Edge", _upperDeck, -_holeHalf, _gold);
            AddEdges("Hole Edge", _upperDeck - 1.4f, -_holeHalf, _gold);

            // A halo floating high over the hole, to find it from anywhere on the map.
            AddEdges("Hole Halo", _upperDeck + 18f, 6f, _gold);

            // The upper deck stands on eight pillars, at its corners and halfway along each side, each with a band of light.
            float pillarHeight = _upperDeck - 2f - _lowerDeck;
            const float inset = _upperHalf - 3f;
            foreach (Vector2 spot in (ReadOnlySpan<Vector2>)[new(-inset, -inset), new(0f, -inset), new(inset, -inset), new(-inset, 0f),
                new(inset, 0f), new(-inset, inset), new(0f, inset), new(inset, inset)])
            {
                Vector3 position = Centre + new Vector3(spot.X, _lowerDeck + pillarHeight * 0.5f, spot.Y);
                MapBuilder.Block("Upper Deck Pillar", position, new Vector3(3f, pillarHeight, 3f), _pillarColor, stone);
                MapBuilder.Glow("Upper Deck Pillar Light", position, new Vector3(3.1f, 0.6f, 3.1f), spot.X > 0f ? _east : spot.X < 0f ? _west : _gold);
            }

            // Under the hole, a pad throws you straight up through it, 8 m clear of the deck. Fall back and it throws you again.
            MapBuilder.BouncePad("Hole Pad", Centre + new Vector3(0f, _lowerDeck, 0f), _gold, height: _upperDeck - _lowerDeck + 8f);

            // Two small hop pads under the upper deck, between the small ramps, low enough not to bump your head.
            var hop = new Color(1f, 0.6f, 0.75f);
            MapBuilder.BouncePad("Hop Pad", Centre + new Vector3(0f, _lowerDeck, -16f), hop, height: 9f);
            MapBuilder.BouncePad("Hop Pad", Centre + new Vector3(0f, _lowerDeck, 16f), hop, height: 9f);

            // Cover on the lower deck, in the two corners nobody lands in.
            foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
            {
                MapBuilder.Block("Lower Cover", Centre + new Vector3(side * 36f, _lowerDeck + 1.5f, side * 26f), new Vector3(10f, 3f, 1.5f), crate);
                MapBuilder.Block("Lower Cover", Centre + new Vector3(side * 26f, _lowerDeck + 1f, side * 38f), new Vector3(2f, 2f, 2f), crate);
            }

            // Cover on the upper deck, clear of the hole and of where the pads land you.
            MapBuilder.Block("Upper Cover", Centre + new Vector3(10f, _upperDeck + 1f, -10f), new Vector3(2f, 2f, 2f), crate);
            MapBuilder.Block("Upper Cover", Centre + new Vector3(-10f, _upperDeck + 1f, 10f), new Vector3(2f, 2f, 2f), crate);

            // A pad at the north and south ends of the lower deck throws you onto the upper deck: face the middle and it lands
            // you 8 m in from the upper deck's edge.
            float rise = _upperDeck - _lowerDeck;
            MapBuilder.BouncePad("Deck Pad North", Centre + new Vector3(0f, _lowerDeck, -37f), _gold, height: rise + 7f, reach: 20f, landingRise: rise);
            MapBuilder.BouncePad("Deck Pad South", Centre + new Vector3(0f, _lowerDeck, 37f), _gold, height: rise + 7f, reach: 20f, landingRise: rise);
        }

        /// <summary>
        /// One side's small ramp under the upper deck, its straight ramp down from the upper deck, its hall in the air and the
        /// pad up to it, the pad at the far end and the curved ramp back. <paramref name="sign"/> is 1 for the east side and
        /// -1 for the west, which is the same thing turned round.
        /// </summary>
        private static void BuildSide(string side, float sign, Color color, Texture ramps, Texture stone)
        {
            Vector3 Place(float x, float y, float z) => Centre + new Vector3(x * sign, y, z * sign);
            float outward = sign > 0f ? -90f : 90f; // the heading away from the middle
            float along = sign > 0f ? 0f : 180f;    // the heading north on the east side, south on the west

            // A small surf ramp under the upper deck, between its pillars, lit from the deck above.
            const float smallRamp = 5f;
            MapBuilder.Ramp($"{side} Small Ramp", Place(15f, _lowerDeck + smallRamp, 17f), length: 34f, drop: 1.5f, ramps, along, height: smallRamp);
            MapBuilder.Glow($"{side} Under Deck Light", Place(15f, _upperDeck - 2.05f, 0f), new Vector3(0.8f, 0.1f, 34f), color);

            // Down from the upper deck's edge, with two boosters on the way. It ends short of the hall, so you fly the gap.
            MapBuilder.Ramp($"{side} Drop", Place(_upperHalf + 2f, _upperDeck - 2.5f, 0f), length: 36f, drop: 4f, ramps, outward, boosters: [0.35f, 0.75f]);

            // A pad at the lower deck's edge either side of the drop, which throws you up into the hall if you face it.
            float hallLanding = _hallStart + 6f;
            foreach (float across in (ReadOnlySpan<float>)[-10f, 10f])
            {
                MapBuilder.BouncePad($"{side} Hall Launch", Place(_lowerHalf - 4f, _lowerDeck, across), color, height: 12f,
                    reach: hallLanding - (_lowerHalf - 4f), landingRise: _hallFloor - _lowerDeck);
            }

            // The hall, floating in the air: open at both ends, with an apron at the front to land on, lit along the ceiling.
            float middle = _hallStart + _hallLength * 0.5f;
            float floorStart = _hallStart - _apron;
            float floorMiddle = (floorStart + _hallStart + _hallLength) * 0.5f;
            float floorLength = _hallLength + _apron;
            float roof = _hallFloor + _hallHeight;
            float wallAcross = _hallHalfWidth + 0.5f;
            MapBuilder.Block($"{side} Hall Floor", Place(floorMiddle, _hallFloor - 1f, 0f), new Vector3(floorLength, 2f, _hallHalfWidth * 2f), _stoneColor, stone);
            MapBuilder.Block($"{side} Hall Wall", Place(middle, _hallFloor + _hallHeight * 0.5f, -wallAcross), new Vector3(_hallLength, _hallHeight, 1f), _stoneColor, stone);
            MapBuilder.Block($"{side} Hall Wall", Place(middle, _hallFloor + _hallHeight * 0.5f, wallAcross), new Vector3(_hallLength, _hallHeight, 1f), _stoneColor, stone);
            MapBuilder.Block($"{side} Hall Roof", Place(middle, roof + 0.5f, 0f), new Vector3(_hallLength + 2f, 1f, wallAcross * 2f + 1f), new Color(0.5f, 0.5f, 0.52f), stone);
            MapBuilder.Glow($"{side} Hall Light", Place(middle, roof - 0.05f, 0f), new Vector3(_hallLength - 4f, 0.1f, 0.8f), color);
            MapBuilder.Glow($"{side} Hall Trim", Place(middle, _hallFloor + 0.1f, -_hallHalfWidth + 0.05f), new Vector3(_hallLength, 0.2f, 0.1f), color);
            MapBuilder.Glow($"{side} Hall Trim", Place(middle, _hallFloor + 0.1f, _hallHalfWidth - 0.05f), new Vector3(_hallLength, 0.2f, 0.1f), color);

            // A glowing frame round the way in, a lip along the front of the apron, and a glow along the floor's sides underneath.
            float frameX = _hallStart - 0.1f;
            MapBuilder.Glow($"{side} Hall Frame", Place(frameX, _hallFloor + _hallHeight * 0.5f, -_hallHalfWidth), new Vector3(0.4f, _hallHeight, 0.4f), color);
            MapBuilder.Glow($"{side} Hall Frame", Place(frameX, _hallFloor + _hallHeight * 0.5f, _hallHalfWidth), new Vector3(0.4f, _hallHeight, 0.4f), color);
            MapBuilder.Glow($"{side} Hall Frame", Place(frameX, roof - 0.2f, 0f), new Vector3(0.4f, 0.4f, _hallHalfWidth * 2f + 0.4f), color);
            MapBuilder.Glow($"{side} Hall Lip", Place(floorStart - 0.05f, _hallFloor - 0.3f, 0f), new Vector3(0.1f, 0.6f, _hallHalfWidth * 2f), color);
            MapBuilder.Glow($"{side} Hall Underglow", Place(floorMiddle, _hallFloor - 1.4f, -_hallHalfWidth - 0.05f), new Vector3(floorLength, 0.4f, 0.1f), color);
            MapBuilder.Glow($"{side} Hall Underglow", Place(floorMiddle, _hallFloor - 1.4f, _hallHalfWidth + 0.05f), new Vector3(floorLength, 0.4f, 0.1f), color);

            // Walls reaching in from alternate sides, leaving a wide gap each time, then a small ramp down the middle to surf.
            MapBuilder.Block($"{side} Hall Divider", Place(_hallStart + 12f, _hallFloor + 1.5f, -8.5f), new Vector3(1.5f, 3f, 9f), _pillarColor, stone);
            MapBuilder.Block($"{side} Hall Divider", Place(_hallStart + 22f, _hallFloor + 1.5f, 8.5f), new Vector3(1.5f, 3f, 9f), _pillarColor, stone);
            const float hallRamp = 4.5f;
            MapBuilder.Ramp($"{side} Hall Ramp", Place(_hallStart + 30f, _hallFloor + hallRamp, 0f), length: 24f, drop: 1f, ramps, outward, height: hallRamp);

            // The exit platform, with a pad that throws you up onto the start of the curved ramp. It's straight ahead and a
            // little up: aim slightly left of it (towards the inside of the bend) to land on the face you surf.
            float exitEnd = _hallStart + _hallLength;
            MapBuilder.Block($"{side} Hall Exit", Place(exitEnd + 6f, _hallFloor - 1f, 0f), new Vector3(12f, 2f, _hallHalfWidth * 2f), _stoneColor, stone);
            float curveTop = _hallFloor + 18f;
            MapBuilder.BouncePad($"{side} Hall Pad", Place(exitEnd + 7f, _hallFloor, 0f), color, height: curveTop - _hallFloor + 4f,
                reach: 17f, landingRise: curveTop - _hallFloor - 4f);

            // The roof is somewhere to bhop too, where a sky ramp drops you: glowing along its edges, with a pad at the far
            // end that throws you onto the same spot on the curved ramp as the hall pad does.
            float roofTop = roof + 1f;
            float roofEdge = wallAcross + 0.55f;
            MapBuilder.Glow($"{side} Roof Edge", Place(middle, roofTop - 0.3f, -roofEdge - 0.05f), new Vector3(_hallLength + 2f, 0.6f, 0.1f), color);
            MapBuilder.Glow($"{side} Roof Edge", Place(middle, roofTop - 0.3f, roofEdge + 0.05f), new Vector3(_hallLength + 2f, 0.6f, 0.1f), color);
            MapBuilder.BouncePad($"{side} Roof Pad", Place(exitEnd - 3f, roofTop, 0f), _skyColor, height: curveTop - roofTop + 4f,
                reach: exitEnd + 24f - (exitEnd - 3f), landingRise: curveTop - 4f - roofTop);

            // The curved ramp: a U-turn round to the outside of the hall, a straight run back along it, then a bend in towards
            // the lower deck, dropping you on its far corner. Four boosters keep the speed up.
            const float uTurnRadius = 30f;
            const float bendRadius = 40f;
            const float bend = 50f;
            MapBuilder.CurvedRamp($"{side} Curve", Place(exitEnd + 20f, curveTop, 0f), outward, drop: 16f, ramps,
            [
                new RampSegment(MathF.PI * uTurnRadius, 180f),
                new RampSegment(98f),
                new RampSegment(MathHelper.ToRadians(bend) * bendRadius, bend)
            ],
            boosters: [0.12f, 0.38f, 0.6f, 0.85f]);

            // Grapple points: one over the end of the roof, to swing off it round onto the start of the curved ramp instead of
            // taking the pad, and one high in the middle of the U-turn, to whip round it or catch yourself if you fall off.
            MapBuilder.GrapplePoint($"{side} Roof Grapple", Place(exitEnd + 10f, roofTop + 13f, -4f));
            MapBuilder.GrapplePoint($"{side} Curve Grapple", Place(exitEnd + 20f, curveTop + 18f, -uTurnRadius));
        }

        /// <summary>
        /// One sky platform and its two ramps, with the pad up to it from the upper deck. <paramref name="sign"/> is 1 for the
        /// north one and -1 for the south, which is the same thing turned round: the north one's ramp to a roof goes to the
        /// east hall, and its drop to the lower deck comes down in the north-west corner.
        /// </summary>
        private static void BuildSky(string side, float sign, Texture ramps, Texture stone)
        {
            Vector3 Place(float x, float y, float z) => Centre + new Vector3(x * sign, y, z * sign);
            float inward = sign > 0f ? 180f : 0f;  // the heading back towards the middle
            float west = sign > 0f ? 90f : -90f;   // the heading west from the north platform, east from the south one
            const float halfWidth = 24f;
            const float halfDepth = 12f;
            float near = _skyOut - halfDepth;      // how far out the platform's edge nearest the middle is

            // The platform, floating, with glowing edges above and below and a little cover.
            MapBuilder.Block($"{side} Sky Platform", Place(0f, _sky - 1f, -_skyOut), new Vector3(halfWidth * 2f, 2f, halfDepth * 2f), _stoneColor, stone);
            AddEdges($"{side} Sky Edge", Place(0f, _sky, -_skyOut), new Vector2(halfWidth, halfDepth), _skyColor);
            AddEdges($"{side} Sky Underglow", Place(0f, _sky - 1.4f, -_skyOut), new Vector2(halfWidth, halfDepth), _skyColor);
            MapBuilder.Block($"{side} Sky Cover", Place(-10f, _sky + 1f, -_skyOut - 5f), new Vector3(2f, 2f, 2f), new Color(0.65f, 0.45f, 0.25f));
            MapBuilder.Block($"{side} Sky Cover", Place(8f, _sky + 1.5f, -_skyOut + 4f), new Vector3(6f, 3f, 1.5f), _pillarColor, stone);

            // A pad on this end of the upper deck: face out and it throws you up and across, onto the middle of the platform.
            float padOut = _upperHalf - 4f;
            MapBuilder.BouncePad($"{side} Sky Pad", Place(0f, _upperDeck, -padOut), _skyColor, height: _sky - _upperDeck + 8f,
                reach: _skyOut - padOut - 4f, landingRise: _sky - _upperDeck);

            // Off the platform's inner edge, a ramp that runs back towards the middle, swings left over the corner of the upper
            // deck and straightens out along the drop below, lined up with the hall: fly off its end onto the roof.
            const float roofTurn = 45f;
            MapBuilder.CurvedRamp($"{side} Sky Roof Ramp", Place(20f, _sky - 2.5f, -near), inward, drop: 19.5f, ramps,
            [
                new RampSegment(near - roofTurn),
                new RampSegment(MathF.PI * 0.5f * roofTurn, 90f),
                new RampSegment(6f)
            ],
            boosters: [0.25f, 0.6f], boosterColor: _skyColor);

            // Off its outer end, a steep drop that turns back round and comes in over the lower deck's corner.
            const float dropTurn = 30f;
            MapBuilder.CurvedRamp($"{side} Sky Drop", Place(-halfWidth - 2f, _sky - 2.5f, -_skyOut), west, drop: 39.5f, ramps,
            [
                new RampSegment(MathF.PI * 0.5f * dropTurn, 90f),
                new RampSegment(15f),
                new RampSegment(MathHelper.ToRadians(20f) * dropTurn, 20f)
            ],
            boosters: [0.35f], boosterColor: _skyColor);
        }

        /// <summary>
        /// Three grapple points high over the middle, in a line from one sky platform to the other: jump off the inner edge
        /// of either, swing from point to point, and land on the other.
        /// </summary>
        private static void BuildSkyLine()
        {
            MapBuilder.GrapplePoint("Sky Line Grapple", Centre + new Vector3(0f, _sky + 16f, -50f));
            MapBuilder.GrapplePoint("Sky Line Grapple", Centre + new Vector3(0f, _sky + 20f, 0f));
            MapBuilder.GrapplePoint("Sky Line Grapple", Centre + new Vector3(0f, _sky + 16f, 50f));
        }

        /// <summary>
        /// An island floating out past a sky platform's outer end, reached only by swinging there along two grapple points.
        /// On it, a pad throws you onto a tower, and off the tower a ring of ramp loops three quarters of the way round and
        /// throws you back onto the island; a cannon pad on its inner edge fires you all the way back to the sky platform.
        /// <paramref name="sign"/> is 1 for the north-west one, off the north platform, and -1 for the south-east one.
        /// </summary>
        private static void BuildIsland(string side, float sign, Texture ramps, Texture stone)
        {
            Vector3 Place(float x, float y, float z) => Centre + new Vector3(x * sign, y, z * sign);
            float west = sign > 0f ? 90f : -90f;
            const float middleX = -150f;
            const float middleZ = -112f;
            const float halfWidth = 25f;
            const float halfDepth = 18f;
            float innerEdge = middleX + halfWidth;

            MapBuilder.Block($"{side} Island", Place(middleX, _island - 1f, middleZ), new Vector3(halfWidth * 2f, 2f, halfDepth * 2f), _stoneColor, stone);
            AddEdges($"{side} Island Edge", Place(middleX, _island, middleZ), new Vector2(halfWidth, halfDepth), _islandColor);
            AddEdges($"{side} Island Underglow", Place(middleX, _island - 1.4f, middleZ), new Vector2(halfWidth, halfDepth), _islandColor);
            MapBuilder.Block($"{side} Island Cover", Place(-138f, _island + 1.5f, -122f), new Vector3(8f, 3f, 1.5f), _pillarColor, stone);
            MapBuilder.Block($"{side} Island Cover", Place(-140f, _island + 1f, -101f), new Vector3(2f, 2f, 2f), new Color(0.65f, 0.45f, 0.25f));

            // The way there: off the sky platform's outer end, swing from one point to the next and let go over the island.
            MapBuilder.GrapplePoint($"{side} Island Grapple", Place(-55f, _sky + 14f, -116f));
            MapBuilder.GrapplePoint($"{side} Island Grapple", Place(-100f, _sky + 12f, -116f));
            // And one over the island itself, to swing round on.
            MapBuilder.GrapplePoint($"{side} Island Grapple", Place(middleX + 8f, _island + 24f, middleZ));

            // The tower, and the pad that throws you onto it: face the tower.
            const float towerX = -168f;
            const float towerSize = 12f;
            const float towerTop = _island + 14f;
            MapBuilder.Block($"{side} Island Tower", Place(towerX, (_island + towerTop) * 0.5f, middleZ), new Vector3(towerSize, towerTop - _island, towerSize), _pillarColor, stone);
            AddEdges($"{side} Island Tower Edge", Place(towerX, towerTop, middleZ), new Vector2(towerSize * 0.5f), _islandColor);
            float padX = towerX + towerSize * 0.5f + 8f;
            MapBuilder.BouncePad($"{side} Tower Pad", Place(padX, _island, middleZ), _islandColor, height: towerTop - _island + 10f,
                reach: padX - (towerX - 1f), landingRise: towerTop - _island);

            // The ring: off the tower's far side, turning left three quarters of the way round, ending pointed back at the island.
            const float ringRadius = 22f;
            MapBuilder.CurvedRamp($"{side} Island Ring", Place(towerX - towerSize * 0.5f - 2f, towerTop - 2.5f, middleZ), west, drop: 5.5f, ramps,
            [
                new RampSegment(MathF.PI * 1.5f * ringRadius, 270f)
            ],
            boosters: [0.3f, 0.65f], boosterColor: _islandColor);

            // The cannon: face the sky platform and it fires you right across onto it.
            const float landX = -14f;
            MapBuilder.BouncePad($"{side} Island Cannon", Place(innerEdge - 4f, _island, -_skyOut), _islandColor, height: _sky - _island + 12f,
                reach: landX - (innerEdge - 4f), landingRise: _sky - _island);
        }

        /// <summary>
        /// A thin glowing strip round the top edge of a square, <paramref name="halfSize"/> out from the middle. A negative
        /// size lines a square hole instead, on the inside of its edge.
        /// </summary>
        private static void AddEdges(string name, float top, float halfSize, Color color)
        {
            float edge = MathF.Abs(halfSize) + (halfSize > 0f ? 0.05f : -0.05f);
            AddEdges(name, Centre with { Y = top }, new Vector2(edge), color, outside: false);
        }

        /// <summary>
        /// A thin glowing strip round the top edge of a rectangle with its top middle at <paramref name="middle"/>, just
        /// outside it unless <paramref name="outside"/> is false.
        /// </summary>
        private static void AddEdges(string name, Vector3 middle, Vector2 halfSize, Color color, bool outside = true)
        {
            Vector2 edge = outside ? halfSize + new Vector2(0.05f) : halfSize;
            Vector2 length = edge * 2f + new Vector2(0.1f);
            Vector3 centre = middle - new Vector3(0f, 0.3f, 0f);
            MapBuilder.Glow(name, centre + new Vector3(0f, 0f, -edge.Y), new Vector3(length.X, 0.6f, 0.1f), color);
            MapBuilder.Glow(name, centre + new Vector3(0f, 0f, edge.Y), new Vector3(length.X, 0.6f, 0.1f), color);
            MapBuilder.Glow(name, centre + new Vector3(-edge.X, 0f, 0f), new Vector3(0.1f, 0.6f, length.Y), color);
            MapBuilder.Glow(name, centre + new Vector3(edge.X, 0f, 0f), new Vector3(0.1f, 0.6f, length.Y), color);
        }
    }
}
