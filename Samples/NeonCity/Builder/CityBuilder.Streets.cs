using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace NeonCity.Builder
{
    // The streets, the buildings along them and the city beyond, the signs, lamps and stalls, and the walker.
    //
    // The main street runs along Z, the road from x = -5 to 5 with pavements out to 9 either side. A cross street runs
    // along X at z = -36 to -28, and an alley goes off west at z = 18 to 26. The night market is on the east side,
    // between z = 4 and 30 (see CityBuilder.Market.cs).
    internal static partial class CityBuilder
    {
        private static Transform _bounds = null!;

        private static void Streets()
        {
            Transform streets = Group("Streets").Transform;
            _bounds = Group("Bounds").Transform;

            var road = new MeshBuilder { TexturesPerUnit = 0.125f };
            road.Span(new Vector3(-5f, -0.3f, -160f), new Vector3(5f, 0f, 66f));
            road.Span(new Vector3(-70f, -0.3f, -36f), new Vector3(-5f, 0f, -28f));
            road.Span(new Vector3(5f, -0.3f, -36f), new Vector3(70f, 0f, -28f));
            road.Span(new Vector3(-30f, -0.3f, 18f), new Vector3(-9f, 0f, 26f));
            Add("Road", road, _road, streets, solid: true, shadows: false);

            // Pavements, a step up from the road, with a kerb along the edge.
            var pavements = new MeshBuilder { TexturesPerUnit = 0.5f };
            var kerbs = new MeshBuilder();
            void Pavement(float minX, float maxX, float minZ, float maxZ)
            {
                pavements.Span(new Vector3(minX, -0.3f, minZ), new Vector3(maxX, 0.15f, maxZ));
                float kerbX = minX < 0f ? maxX : minX;
                kerbs.Span(new Vector3(kerbX - 0.12f, -0.1f, minZ), new Vector3(kerbX + 0.12f, 0.16f, maxZ));
            }

            Pavement(-9f, -5f, 26f, 66f);
            Pavement(-9f, -5f, -28f, 18f);
            Pavement(-9f, -5f, -160f, -36f);
            Pavement(5f, 9f, -28f, 66f);
            Pavement(5f, 9f, -160f, -36f);
            pavements.Span(new Vector3(-9f, -0.3f, 18f), new Vector3(-5f, 0.15f, 26f));
            Add("Pavements", pavements, _sidewalk, streets, solid: true, shadows: false);
            Add("Kerbs", kerbs, _wallMaterial, streets, solid: true, shadows: false);

            // Dashes down the middle, lines along the edges, and crossings either side of the cross street.
            var marks = new MeshBuilder();
            for (float z = -158f; z < 64f; z += 6f)
            {
                if (z > -40f && z < -24f) continue;
                marks.Span(new Vector3(-0.08f, 0f, z), new Vector3(0.08f, 0.012f, z + 3f));
            }

            foreach (float x in (ReadOnlySpan<float>)[-4.6f, 4.6f])
            {
                marks.Span(new Vector3(x - 0.06f, 0f, -24f), new Vector3(x + 0.06f, 0.012f, 64f));
                marks.Span(new Vector3(x - 0.06f, 0f, -158f), new Vector3(x + 0.06f, 0.012f, -40f));
            }

            for (float x = -4.3f; x < 4.4f; x += 1f)
            {
                marks.Span(new Vector3(x, 0f, -40.5f), new Vector3(x + 0.5f, 0.012f, -37.2f));
                marks.Span(new Vector3(x, 0f, -26.8f), new Vector3(x + 0.5f, 0.012f, -23.5f));
            }

            Add("Road Markings", marks, _marking, streets, shadows: false);

            // Puddles, still and black, catching the lights.
            var puddles = new MeshBuilder();
            for (int i = 0; i < 18; i++)
            {
                bool onPavement = i % 3 == 0;
                float x = onPavement ? (i % 2 == 0 ? Range(-8.5f, -5.6f) : Range(5.6f, 8.5f)) : Range(-4.4f, 4.4f);
                float z = Range(-120f, 62f);
                float y = onPavement ? 0.152f : 0.004f;
                var size = new Vector2(Range(0.7f, onPavement ? 2f : 3.2f), Range(1f, 4.5f));
                puddles.Span(new Vector3(x - size.X * 0.5f, y - 0.004f, z - size.Y * 0.5f), new Vector3(x + size.X * 0.5f, y + 0.008f, z + size.Y * 0.5f));
            }

            Add("Puddles", puddles, _puddle, streets, shadows: false);

            // The ground beyond, under the city round about.
            var ground = new MeshBuilder { TexturesPerUnit = 0.05f };
            ground.Span(new Vector3(-400f, -0.6f, -500f), new Vector3(400f, -0.05f, 260f));
            Add("Ground", ground, _ground, streets, shadows: false);

            // Where the walkable streets end.
            Wall(new Vector3(-9f, -1f, -96f), new Vector3(9f, 20f, -94f), _bounds);
            Wall(new Vector3(-64f, -1f, -36f), new Vector3(-62f, 20f, -28f), _bounds);
            Wall(new Vector3(62f, -1f, -36f), new Vector3(64f, 20f, -28f), _bounds);
            Wall(new Vector3(28f, -1f, 30f), new Vector3(29f, 20f, 32f), _bounds);
            Wall(new Vector3(12f, -1f, 0f), new Vector3(13f, 20f, 4f), _bounds);
        }

        // ---------------------------------------------------------------- Buildings

        private static void Buildings()
        {
            Transform group = Group("Buildings").Transform;

            Building(group, "Ramen House", -26f, -9f, 26f, 62f, 34f);
            Building(group, "Arcade Tower", -28f, -9f, -28f, 18f, 52f);
            Building(group, "West Tower", -30f, -9f, -94f, -36f, 78f);
            Building(group, "Alley End", -52f, -30f, 12f, 32f, 40f);
            Building(group, "Sushi Tower", 9f, 28f, 32f, 62f, 46f);
            Building(group, "Billboard Tower", 9f, 30f, -28f, 0f, 62f);
            Building(group, "East Tower", 9f, 32f, -94f, -36f, 70f);
            Building(group, "North Block", -26f, 28f, 66f, 92f, 30f);
            Building(group, "South West Tower", -30f, -9f, -160f, -96f, 96f);
            Building(group, "South East Tower", 9f, 32f, -160f, -96f, 112f);
            Building(group, "Cross Street North West", -62f, -30f, -28f, -4f, 38f);
            Building(group, "Cross Street North East", 32f, 64f, -28f, -4f, 48f);
            Building(group, "Cross Street South West", -64f, -32f, -70f, -36f, 56f);
            Building(group, "Cross Street South East", 34f, 66f, -70f, -36f, 40f);

            Signs(Group("Signs").Transform);
        }

        /// <summary>
        /// A building filling a block, its street side set back at the bottom for shopfronts under a canopy, tall ones
        /// narrowing near the top. Windows on every side but the back, some lit.
        /// </summary>
        private static void Building(Transform parent, string name, float minX, float maxX, float minZ, float maxZ, float height)
        {
            Transform building = Group(name, parent).Transform;
            var shell = new MeshBuilder();
            var windows = new MeshBuilder();
            var trim = new MeshBuilder();
            var shops = new MeshBuilder();
            var glass = new MeshBuilder();

            // Which side faces the street, and which is the back.
            Vector3 street = minX >= 8f ? -Vector3.UnitX : maxX <= -8f ? Vector3.UnitX : -Vector3.UnitZ;
            const float shopHeight = 4.4f;
            const float setBack = 0.8f;

            // The ground floor, set back on the street side; everything above it.
            Vector3 min = new(minX, 0f, minZ), max = new(maxX, height, maxZ);
            Vector3 groundMin = min - Vector3.Min(street, Vector3.Zero) * setBack, groundMax = new Vector3(maxX, shopHeight, maxZ) - Vector3.Max(street, Vector3.Zero) * setBack;
            shell.Span(groundMin, groundMax);

            float split = height > 40f ? MathF.Round(height * Range(0.55f, 0.75f) / 3.6f) * 3.6f + shopHeight : height;
            shell.Span(new Vector3(minX, shopHeight, minZ), new Vector3(maxX, split, maxZ));
            const float inset = 2f;
            if (split < height) shell.Span(new Vector3(minX + inset, split, minZ + inset), new Vector3(maxX - inset, height, maxZ - inset));

            float lit = Range(0.3f, 0.55f);
            FacadeWindows(windows, minX, maxX, minZ, maxZ, shopHeight + 1f, split, street, lit);
            if (split < height) FacadeWindows(windows, minX + inset, maxX - inset, minZ + inset, maxZ - inset, split + 1f, height, street, lit);

            // Ledges every few floors on the street side, and a parapet round the roof.
            (Vector3 faceAt, Vector3 along, float faceWidth) = Face(minX, maxX, minZ, maxZ, street);
            for (float y = shopHeight + 3.6f * 4f; y < split - 2f; y += 3.6f * 4f)
            {
                trim.Box(faceAt + along * (faceWidth * 0.5f) + new Vector3(0f, y, 0f) + street * 0.12f, Size(along, faceWidth, 0.25f, 0.3f, street));
            }

            float roof = height;
            float rMinX = split < height ? minX + inset : minX, rMaxX = split < height ? maxX - inset : maxX;
            float rMinZ = split < height ? minZ + inset : minZ, rMaxZ = split < height ? maxZ - inset : maxZ;
            trim.Span(new Vector3(rMinX, roof, rMinZ), new Vector3(rMaxX, roof + 0.9f, rMinZ + 0.3f));
            trim.Span(new Vector3(rMinX, roof, rMaxZ - 0.3f), new Vector3(rMaxX, roof + 0.9f, rMaxZ));
            trim.Span(new Vector3(rMinX, roof, rMinZ), new Vector3(rMinX + 0.3f, roof + 0.9f, rMaxZ));
            trim.Span(new Vector3(rMaxX - 0.3f, roof, rMinZ), new Vector3(rMaxX, roof + 0.9f, rMaxZ));

            // Machinery on the roof, and on tall ones a mast with a warning light.
            for (int i = 0; i < 3; i++)
            {
                var at = new Vector3(Range(rMinX + 2f, rMaxX - 2f), roof, Range(rMinZ + 2f, rMaxZ - 2f));
                var size = new Vector3(Range(1.5f, 4f), Range(1f, 3f), Range(1.5f, 4f));
                shell.Box(at + new Vector3(0f, size.Y * 0.5f, 0f), size);
            }

            if (height > 45f)
            {
                var mast = new Vector3((rMinX + rMaxX) * 0.5f, roof, (rMinZ + rMaxZ) * 0.5f);
                float tall = Range(6f, 14f);
                trim.Beam(mast, mast + new Vector3(0f, tall, 0f), 0.25f);
                var beacon = new MeshBuilder();
                beacon.Box(Vector3.Zero, new Vector3(0.5f));
                AddAt("Warning Light", mast + new Vector3(0f, tall + 0.3f, 0f), beacon, Glow(new Color(1f, 0.08f, 0.05f), 12f), building, shadows: false)
                    .AddComponent<Flicker>(f => { f.Style = FlickerStyle.Blink; f.Seconds = Range(1.6f, 2.4f); f.Low = 0f; });
            }

            // Shopfronts: lit shops and dark glass between steel posts, under a canopy with a strip of light beneath its edge.
            Vector3 shopFace = faceAt + street * (-setBack + 0.02f);
            for (float a = 0.5f; a + 3.5f <= faceWidth; a += 4f)
            {
                Vector3 centre = shopFace + along * (a + 1.75f) + new Vector3(0f, 2.1f, 0f);
                (Vector2 shopMin, Vector2 shopMax) = WindowPicture(_random.Next(_litColors));
                if (_random.NextSingle() < 0.55f) shops.Panel(centre, street, Vector3.UnitY, 3.5f, 3.4f, shopMin, shopMax);
                else glass.Panel(centre, street, Vector3.UnitY, 3.5f, 3.4f);
                trim.Box(shopFace + along * a + new Vector3(0f, shopHeight * 0.5f, 0f), Size(along, 0.18f, shopHeight, 0.18f, street));
            }

            Vector3 canopyMiddle = faceAt + along * (faceWidth * 0.5f) + street * 0.6f + new Vector3(0f, shopHeight - 0.15f, 0f);
            trim.Box(canopyMiddle, Size(along, faceWidth, 0.3f, 1.2f + setBack, street));
            var strip = new MeshBuilder();
            strip.Box(canopyMiddle + street * 0.95f - new Vector3(0f, 0.17f, 0f), Size(along, faceWidth - 0.4f, 0.05f, 0.08f, street));

            Add("Walls", shell, _wallMaterial, building, solid: true);
            Add("Windows", windows, _windows, building, shadows: false);
            Add("Trim", trim, _darkMetal, building);
            Add("Shops", shops, _windows, building, shadows: false);
            Add("Shop Glass", glass, _glass, building, shadows: false);
            Add("Canopy Light", strip, Glow(Pick(new[] { new Color(1f, 0.75f, 0.45f), new Color(0.6f, 0.85f, 1f), new Color(1f, 0.4f, 0.7f) }), 3f), building, shadows: false);
        }

        /// <summary>A box's size given along the street face, up, and out from it.</summary>
        private static Vector3 Size(Vector3 along, float alongSize, float up, float out_, Vector3 street) =>
            MathF.Abs(along.X) > 0.5f ? new Vector3(alongSize, up, out_) : new Vector3(out_, up, alongSize);

        /// <summary>The street side of a block: its corner at ground level, the way along it, and how wide it is.</summary>
        private static (Vector3 Corner, Vector3 Along, float Width) Face(float minX, float maxX, float minZ, float maxZ, Vector3 street)
        {
            if (street.X < -0.5f) return (new Vector3(minX, 0f, minZ), Vector3.UnitZ, maxZ - minZ);
            if (street.X > 0.5f) return (new Vector3(maxX, 0f, minZ), Vector3.UnitZ, maxZ - minZ);
            return (new Vector3(minX, 0f, minZ), Vector3.UnitX, maxX - minX);
        }

        /// <summary>Rows of windows, a floor apart, on every side of a block but the one facing away from the street.</summary>
        private static void FacadeWindows(MeshBuilder windows, float minX, float maxX, float minZ, float maxZ, float fromY, float toY, Vector3 street, float lit)
        {
            (Vector3 Corner, Vector3 Along, Vector3 Normal, float Width)[] faces =
            [
                (new Vector3(minX, 0f, minZ), Vector3.UnitZ, -Vector3.UnitX, maxZ - minZ),
                (new Vector3(maxX, 0f, minZ), Vector3.UnitZ, Vector3.UnitX, maxZ - minZ),
                (new Vector3(minX, 0f, minZ), Vector3.UnitX, -Vector3.UnitZ, maxX - minX),
                (new Vector3(minX, 0f, maxZ), Vector3.UnitX, Vector3.UnitZ, maxX - minX)
            ];

            foreach ((Vector3 corner, Vector3 along, Vector3 normal, float width) in faces)
            {
                if (Vector3.Dot(normal, street) < -0.5f) continue;

                for (float y = fromY; y + 2.4f < toY; y += 3.6f)
                {
                    bool darkFloor = _random.NextSingle() < 0.12f;
                    int floorColor = _random.Next(_litColors);
                    for (float a = 1.6f; a + 1.2f <= width; a += 2.7f)
                    {
                        // Lit windows on a floor are mostly the same colour, as one office or flat is.
                        bool on = !darkFloor && _random.NextSingle() < lit;
                        int color = on ? (_random.NextSingle() < 0.7f ? floorColor : _random.Next(_litColors)) : _litColors + _random.Next(_windowColors.Length - _litColors);
                        Vector3 centre = corner + along * a + new Vector3(0f, y + 1.1f, 0f) + normal * 0.04f;
                        (Vector2 pictureMin, Vector2 pictureMax) = WindowPicture(color);
                        windows.Panel(centre, normal, Vector3.UnitY, 1.7f, 2.1f, pictureMin, pictureMax);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- Signs

        private static readonly Color _pink = new(1f, 0.18f, 0.6f), _cyan = new(0.2f, 0.85f, 1f), _orange = new(1f, 0.5f, 0.12f),
            _purple = new(0.65f, 0.3f, 1f), _green = new(0.3f, 1f, 0.45f), _gold = new(1f, 0.75f, 0.2f), _blue = new(0.25f, 0.45f, 1f),
            _red = new(1f, 0.12f, 0.1f);

        private static void Signs(Transform group)
        {
            // Flat on the west side's walls they face +X, read with right along -Z; on the east side they face -X, right along +Z.
            Vector3 westRight = -Vector3.UnitZ, eastRight = Vector3.UnitZ, up = Vector3.UnitY;

            NeonSign("Ramen Sign", "RAMEN", new Vector3(-8.75f, 5.4f, 46f), westRight, up, _pink, 0.9f, group);
            NeonSign("Hotel Sign", "HOTEL", new Vector3(-8.25f, 12f, 30.5f), Vector3.UnitX, up, _cyan, 1.2f, group, vertical: true, twoSided: true);
            GlyphSign("Ramen House Sign", 4, new Vector3(-8.1f, 10f, 57f), Vector3.UnitX, up, _orange, 1f, group, twoSided: true);

            NeonSign("Arcade Sign", "ARCADE", new Vector3(-8.75f, 5.4f, 2f), westRight, up, _purple, 1f, group);
            NeonSign("Karaoke Sign", "KARAOKE", new Vector3(-8.3f, 15f, -14f), Vector3.UnitX, up, _orange, 1.1f, group, vertical: true, twoSided: true);
            GlyphSign("Arcade Tower Sign", 5, new Vector3(-8.1f, 11f, 12f), Vector3.UnitX, up, _green, 1f, group, twoSided: true);
            NeonSign("Open All Night Sign", "24H", new Vector3(-8.1f, 3.1f, -9f), westRight, up, _green, 0.5f, group, light: 1.2f, flicker: FlickerStyle.Buzz);

            NeonSign("Drink Sign", "DRINK", new Vector3(-8.75f, 5.4f, -52f), westRight, up, _blue, 0.9f, group);
            GlyphSign("West Tower Sign", 6, new Vector3(-7.95f, 17f, -72f), Vector3.UnitX, up, _pink, 1.2f, group, twoSided: true);

            NeonSign("Sushi Sign", "SUSHI", new Vector3(8.3f, 12f, 52f), Vector3.UnitX, up, _orange, 1.1f, group, vertical: true, twoSided: true);
            NeonSign("Open Sign", "OPEN", new Vector3(8.1f, 3.1f, 40f), eastRight, up, _green, 0.5f, group, light: 1.2f, flicker: FlickerStyle.Buzz);
            GlyphSign("Sushi Tower Sign", 4, new Vector3(8.17f, 9f, 37f), Vector3.UnitX, up, _purple, 0.9f, group, twoSided: true);

            NeonSign("Bar Sign", "BAR", new Vector3(8.1f, 3.1f, -21f), eastRight, up, _blue, 0.6f, group, light: 1.5f, flicker: FlickerStyle.Buzz);
            NeonSign("Pachinko Sign", "PACHINKO", new Vector3(8.2f, 19f, -62f), Vector3.UnitX, up, _gold, 1.3f, group, vertical: true, twoSided: true);
            GlyphSign("East Tower Sign", 5, new Vector3(8.75f, 12f, -44f), eastRight, up, _cyan, 1.1f, group);

            NeonSign("Vertex Sign", "VERTEX", new Vector3(1f, 16f, 65.7f), -Vector3.UnitX, up, _cyan, 2.2f, group, light: 4f);
            NeonSign("Exit Sign", "EXIT", new Vector3(-29.75f, 3.2f, 22f), westRight, up, _red, 0.45f, group, light: 1.2f, flicker: FlickerStyle.Buzz);

            // The big screen on the billboard tower: one picture, its colour (and the light it throws on the street) changing.
            var screen = new MeshBuilder();
            screen.Panel(new Vector3(2.35f, 0f, 0f), -Vector3.UnitX, up, 9f, 18f);
            GameObject advert = AddAt("Big Screen", new Vector3(6.5f, 19f, -14f), screen, Picture(_advert, Color.White), group, shadows: false);
            advert.AddComponent<Light>(light =>
            {
                light.Type = LightType.Point;
                light.Intensity = 3f;
                light.Range = 24f;
                light.FogStrength = 1.3f;
                light.CastShadows = false;
            });
            advert.AddComponent<ColorCycle>(cycle => cycle.TintColor = true);

            var frame = new MeshBuilder();
            Vector3[] corners = [new(8.8f, 9.8f, -18.6f), new(8.8f, 9.8f, -9.4f), new(8.8f, 28.2f, -9.4f), new(8.8f, 28.2f, -18.6f)];
            for (int i = 0; i < 4; i++) frame.Beam(corners[i], corners[(i + 1) % 4], 0.12f, -Vector3.UnitX);
            Add("Big Screen Frame", frame, Glow(_cyan, 4f), group, shadows: false);
        }
    }
}
