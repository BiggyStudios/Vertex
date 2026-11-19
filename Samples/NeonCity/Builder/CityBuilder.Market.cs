using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace NeonCity.Builder
{
    // The night market: the ground floor of a tower on the east side of the street (x = 9 to 31, z = 4 to 30), open to
    // walk into. A ramen bar along the back, a dumpling stall, a bar, tables, and arcade machines by the door.
    internal static partial class CityBuilder
    {
        private const float _floor = 0.15f, _ceiling = 5.6f;

        private static void NightMarket()
        {
            Transform market = Group("Night Market").Transform;
            MarketBuilding(market);
            Transform inside = Group("Inside", market).Transform;
            RamenBar(Group("Ramen Bar", inside).Transform);
            DumplingStall(Group("Dumpling Stall", inside).Transform);
            Bar(Group("Bar", inside).Transform);
            Tables(Group("Tables", inside).Transform);
            Arcade(Group("Arcade", inside).Transform);
            Ceiling(Group("Ceiling", inside).Transform);

            // Clearer air inside than out, but steam hanging over the kitchen.
            var clear = new GameObject("Clearer Air");
            clear.Transform.Position = new Vector3(20f, 3f, 17f);
            clear.Transform.Scale = new Vector3(21.2f, 6f, 25.2f);
            clear.Transform.Parent = inside;
            clear.AddComponent<FogVolume>(f => { f.Density = -0.024f; f.Softness = 0.1f; });

            var steam = new GameObject("Kitchen Steam");
            steam.Transform.Position = new Vector3(20.5f, 3.2f, 28.3f);
            steam.Transform.Scale = new Vector3(15f, 3.6f, 2.6f);
            steam.Transform.Parent = inside;
            steam.AddComponent<FogVolume>(f => { f.Density = 0.09f; f.Softness = 1f; });
        }

        private static void MarketBuilding(Transform market)
        {
            const float top = 42f;
            var walls = new MeshBuilder();
            var frames = new MeshBuilder();
            var windows = new MeshBuilder();

            // The front wall: posts between a doorway and two wide windows, wall under the windows, and over them all.
            foreach ((float from, float to) in new[] { (4f, 5.5f), (12f, 14f), (20f, 22f), (28.5f, 30f) })
                walls.Span(new Vector3(9f, 0f, from), new Vector3(9.4f, _ceiling, to));
            walls.Span(new Vector3(9f, 0f, 5.5f), new Vector3(9.4f, 1f, 12f));
            walls.Span(new Vector3(9f, 0f, 22f), new Vector3(9.4f, 1f, 28.5f));
            walls.Span(new Vector3(9f, 3.6f, 5.5f), new Vector3(9.4f, _ceiling, 12f));
            walls.Span(new Vector3(9f, 3.9f, 14f), new Vector3(9.4f, _ceiling, 20f));
            walls.Span(new Vector3(9f, 3.6f, 22f), new Vector3(9.4f, _ceiling, 28.5f));

            // The other three walls, the ceiling, and the tower on top.
            walls.Span(new Vector3(9f, 0f, 29.6f), new Vector3(31f, _ceiling, 30f));
            walls.Span(new Vector3(9f, 0f, 4f), new Vector3(31f, _ceiling, 4.4f));
            walls.Span(new Vector3(30.6f, 0f, 4f), new Vector3(31f, _ceiling, 30f));
            walls.Span(new Vector3(9f, _ceiling, 4f), new Vector3(31f, 6.2f, 30f));
            walls.Span(new Vector3(9f, 6.2f, 4f), new Vector3(31f, top, 30f));
            FacadeWindows(windows, 9f, 31f, 4f, 30f, 7.4f, top, -Vector3.UnitX, 0.5f);

            // Frames round the openings, and a bar across each window.
            foreach ((float from, float to, float low, float high) in new[] { (5.5f, 12f, 1f, 3.6f), (14f, 20f, _floor, 3.9f), (22f, 28.5f, 1f, 3.6f) })
            {
                frames.Span(new Vector3(8.95f, high - 0.1f, from), new Vector3(9.45f, high, to));
                frames.Span(new Vector3(8.95f, low, from), new Vector3(9.45f, low + 0.08f, from + 0.1f));
                frames.Span(new Vector3(8.95f, low, from), new Vector3(9.45f, high, from + 0.1f));
                frames.Span(new Vector3(8.95f, low, to - 0.1f), new Vector3(9.45f, high, to));
                if (low > 0.5f) frames.Span(new Vector3(9.1f, low, from), new Vector3(9.3f, low + 0.08f, to));
            }

            // A canopy over the door, lit underneath, and the roof's edge.
            frames.Span(new Vector3(7.4f, 4.15f, 13f), new Vector3(9.4f, 4.35f, 21f));
            frames.Span(new Vector3(9f, top, 4f), new Vector3(31f, top + 0.9f, 4.3f));
            frames.Span(new Vector3(9f, top, 29.7f), new Vector3(31f, top + 0.9f, 30f));
            frames.Span(new Vector3(9f, top, 4f), new Vector3(9.3f, top + 0.9f, 30f));
            frames.Span(new Vector3(30.7f, top, 4f), new Vector3(31f, top + 0.9f, 30f));
            var canopyLight = new MeshBuilder();
            canopyLight.Span(new Vector3(7.5f, 4.1f, 13.2f), new Vector3(7.6f, 4.15f, 20.8f));

            Add("Walls", walls, _wallMaterial, market, solid: true);
            Add("Frames", frames, _darkMetal, market);
            Add("Windows", windows, _windows, market, shadows: false);
            Add("Canopy Light", canopyLight, Glow(new Color(1f, 0.75f, 0.45f), 5f), market, shadows: false);

            NeonSign("Night Market Sign", "NIGHT MARKET", new Vector3(8.75f, 5.35f, 17f), Vector3.UnitZ, Vector3.UnitY, _orange, 0.75f, market, light: 3f);
            GlyphSign("Night Market Tower Sign", 5, new Vector3(7.9f, 12f, 26f), Vector3.UnitX, Vector3.UnitY, _pink, 1.1f, market, twoSided: true);

            // A hologram turning over the doorway: a ring round a diamond.
            var hologram = new MeshBuilder();
            const int sides = 20;
            for (int i = 0; i < sides; i++)
            {
                float a = i * MathF.Tau / sides, b = (i + 1) * MathF.Tau / sides;
                hologram.Beam(new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * 1.1f, new Vector3(MathF.Cos(b), MathF.Sin(b), 0f) * 1.1f, 0.05f, Vector3.UnitZ);
            }

            Vector3[] diamond = [new(0f, 0.75f, 0f), new(0.45f, 0f, 0f), new(0f, -0.75f, 0f), new(-0.45f, 0f, 0f)];
            for (int i = 0; i < 4; i++) hologram.Beam(diamond[i], diamond[(i + 1) % 4], 0.06f, Vector3.UnitZ);
            AddAt("Hologram", new Vector3(6.6f, 7.6f, 17f), hologram, Glow(_cyan, 6f), market, shadows: false).AddComponent<Hologram>();
            PointLight("Hologram Glow", new Vector3(6.6f, 7.6f, 17f), _cyan, 1.5f, 6f, market, fog: 1.5f);
        }

        /// <summary>Along the back wall: a long counter with stools, the kitchen behind it, menus, lamps and the curtain over it all.</summary>
        private static void RamenBar(Transform group)
        {
            var wood = new MeshBuilder { TexturesPerUnit = 0.5f };
            var top = new MeshBuilder { TexturesPerUnit = 0.5f };
            var steel = new MeshBuilder();
            var bowls = new MeshBuilder();
            var dark = new MeshBuilder();

            wood.Span(new Vector3(13f, _floor, 26.4f), new Vector3(27f, 1f, 27.2f));
            top.Span(new Vector3(12.9f, 1f, 26.15f), new Vector3(27.1f, 1.08f, 27.45f));

            // The kitchen: a steel counter along the wall, shelves of bowls over it, pots steaming on it.
            steel.Span(new Vector3(12.5f, _floor, 28.9f), new Vector3(28.5f, 0.95f, 29.6f));
            for (float y = 1.7f; y < 2.5f; y += 0.6f)
            {
                steel.Span(new Vector3(13f, y, 29.25f), new Vector3(28f, y + 0.04f, 29.6f));
                for (float x = 13.4f; x < 27.8f; x += 0.45f) bowls.Box(new Vector3(x, y + 0.1f, 29.42f), new Vector3(0.3f, 0.14f, 0.3f));
            }

            foreach (float x in (ReadOnlySpan<float>)[15f, 17.5f, 20f, 22.5f])
            {
                dark.Box(new Vector3(x, 1.2f, 29.2f), new Vector3(0.6f, 0.5f, 0.55f));
            }

            Add("Counter", wood, _darkWood, group, solid: true);
            Add("Counter Top", top, _woodMaterial, group, solid: true);
            Add("Kitchen", steel, _steel, group, solid: true);
            Add("Bowls", bowls, _ceramic, group);
            Add("Pots", dark, _darkMetal, group);

            // A glowing strip along the foot of the counter.
            var kick = new MeshBuilder();
            kick.Span(new Vector3(13f, 0.2f, 26.36f), new Vector3(27f, 0.25f, 26.4f));
            Add("Counter Light", kick, Glow(new Color(1f, 0.55f, 0.2f), 3f), group, shadows: false);

            var steam = new GameObject("Pot Steam");
            steam.Transform.Position = new Vector3(18.75f, 1.5f, 29.2f);
            steam.Transform.Parent = group;
            steam.AddComponent<ParticleSystem>(p =>
            {
                p.Rate = 9f;
                p.Lifetime = new FloatRange(1.8f, 2.6f);
                p.Speed = new FloatRange(0.4f, 0.7f);
                p.Spread = 15f;
                p.Size = new FloatRange(0.25f, 0.4f);
                p.EndSize = 3f;
                p.StartColor = new Color(0.9f, 0.88f, 0.85f, 0.22f);
                p.EndColor = new Color(0.8f, 0.8f, 0.8f, 0f);
                p.Blend = ParticleBlend.Alpha;
                p.Area = new Vector3(8f, 0f, 0.3f);
            });

            // Stools along the counter.
            var poles = new MeshBuilder();
            var seats = new MeshBuilder();
            for (float x = 14f; x <= 26.1f; x += 2f)
            {
                poles.Beam(new Vector3(x, _floor, 25.7f), new Vector3(x, 0.72f, 25.7f), 0.07f);
                poles.Box(new Vector3(x, _floor + 0.02f, 25.7f), new Vector3(0.4f, 0.04f, 0.4f));
                seats.Box(new Vector3(x, 0.76f, 25.7f), new Vector3(0.42f, 0.09f, 0.42f));
            }

            Add("Stool Legs", poles, _steel, group);
            Add("Stool Seats", seats, _vinyl, group);

            // Short curtains hung over the counter, the shop's name across them.
            var curtain = new MeshBuilder();
            for (float x = 13.2f; x < 26.9f; x += 2.25f) curtain.Span(new Vector3(x, 2.35f, 26.25f), new Vector3(x + 2.15f, 3.05f, 26.3f));
            curtain.Span(new Vector3(13f, 3.05f, 26.2f), new Vector3(27f, 3.1f, 26.35f));
            Add("Curtain", curtain, _redCloth, group);
            Text("RAMEN  SHOYU  MISO  TONKOTSU", new Vector3(20f, 2.7f, 26.2f), -Vector3.UnitZ, 0.32f, new Color(1f, 0.95f, 0.85f), group);

            // Menus on the wall over the kitchen, lit from behind.
            var boards = new MeshBuilder();
            var backlight = new MeshBuilder();
            string[][] menus =
            [
                ["SHOYU RAMEN   850", "MISO RAMEN    900", "TONKOTSU      980"],
                ["GYOZA (6)     450", "KARAAGE       520", "RICE          200"],
                ["BEER          600", "HIGHBALL      550", "GREEN TEA     150"]
            ];
            for (int i = 0; i < menus.Length; i++)
            {
                float x = 15.5f + i * 4.5f;
                boards.Span(new Vector3(x - 1.9f, 3.25f, 29.5f), new Vector3(x + 1.9f, 4.65f, 29.58f));
                backlight.Span(new Vector3(x - 2f, 3.15f, 29.58f), new Vector3(x + 2f, 4.75f, 29.6f));
                for (int line = 0; line < menus[i].Length; line++)
                    Text(menus[i][line], new Vector3(x, 4.35f - line * 0.42f, 29.48f), -Vector3.UnitZ, 0.26f, new Color(1f, 0.9f, 0.7f), group);
            }

            Add("Menu Boards", boards, _signPanel, group);
            Add("Menu Backlight", backlight, Glow(new Color(1f, 0.7f, 0.35f), 2f), group, shadows: false);

            // Lamps hung low over the counter.
            Lamps(group, [new Vector3(16f, 3.3f, 26.8f), new Vector3(20f, 3.3f, 26.8f), new Vector3(24f, 3.3f, 26.8f)], new Color(1f, 0.68f, 0.38f), 5f, 6f);
        }

        /// <summary>Lamps hanging from the ceiling on cords, each with a shade glowing underneath and a warm light.</summary>
        private static void Lamps(Transform group, Vector3[] at, Color color, float intensity, float range)
        {
            var cords = new MeshBuilder();
            var shades = new MeshBuilder();
            var bulbs = new MeshBuilder();
            foreach (Vector3 p in at)
            {
                cords.Beam(new Vector3(p.X, _ceiling, p.Z), p + new Vector3(0f, 0.2f, 0f), 0.02f);
                shades.Box(p + new Vector3(0f, 0.1f, 0f), new Vector3(0.5f, 0.22f, 0.5f));
                bulbs.Box(p - new Vector3(0f, 0.02f, 0f), new Vector3(0.36f, 0.03f, 0.36f));
                PointLight("Lamp", p - new Vector3(0f, 0.25f, 0f), color, intensity, range, group, fog: 0.6f);
            }

            Add("Lamp Cords", cords, _darkMetal, group, shadows: false);
            Add("Lamp Shades", shades, _darkMetal, group, shadows: false);
            Add("Lamp Bulbs", bulbs, Glow(color, 6f), group, shadows: false);
        }

        /// <summary>Against the far wall: a counter of steamer baskets, a glowing drinks fridge, and a green sign.</summary>
        private static void DumplingStall(Transform group)
        {
            var counter = new MeshBuilder { TexturesPerUnit = 0.5f };
            var baskets = new MeshBuilder();
            counter.Span(new Vector3(28.4f, _floor, 9f), new Vector3(29.3f, 1.05f, 21f));
            counter.Span(new Vector3(28.3f, 1.05f, 8.9f), new Vector3(29.4f, 1.12f, 21.1f));
            for (float z = 10f; z < 20.5f; z += 1.4f)
            {
                baskets.Box(new Vector3(28.85f, 1.22f, z), new Vector3(0.55f, 0.2f, 0.55f));
                baskets.Box(new Vector3(28.85f, 1.42f, z), new Vector3(0.5f, 0.18f, 0.5f));
            }

            Add("Counter", counter, _darkWood, group, solid: true);
            Add("Steamer Baskets", baskets, Lit(new Color(0.75f, 0.6f, 0.35f), 0.8f), group);

            var fridge = new MeshBuilder();
            fridge.Span(new Vector3(29.7f, _floor, 22.2f), new Vector3(30.6f, 2.3f, 24.4f));
            Add("Fridge", fridge, _steel, group, solid: true);
            var door = new MeshBuilder();
            door.Panel(new Vector3(29.69f, 1.25f, 23.3f), -Vector3.UnitX, Vector3.UnitY, 2f, 1.9f);
            Add("Fridge Door", door, Picture(_vending, new Color(0.85f, 0.95f, 1f)), group, shadows: false);
            PointLight("Fridge Glow", new Vector3(29f, 1.4f, 23.3f), new Color(0.6f, 0.85f, 1f), 1.2f, 4f, group, fog: 0.5f);

            NeonSign("Gyoza Sign", "GYOZA", new Vector3(30.45f, 3.4f, 15f), Vector3.UnitZ, Vector3.UnitY, _green, 0.7f, group, light: 2f);
            Lamps(group, [new Vector3(28.85f, 3.1f, 12f), new Vector3(28.85f, 3.1f, 18f)], new Color(1f, 0.72f, 0.45f), 4f, 5.5f);
        }

        /// <summary>In the corner by the front: a bar, bottles glowing on shelves behind it, a blue sign.</summary>
        private static void Bar(Transform group)
        {
            var counter = new MeshBuilder { TexturesPerUnit = 0.5f };
            counter.Span(new Vector3(20f, _floor, 5.6f), new Vector3(29f, 1.1f, 6.4f));
            counter.Span(new Vector3(19.9f, 1.1f, 5.5f), new Vector3(29.1f, 1.18f, 6.6f));
            Add("Bar Counter", counter, _darkWood, group, solid: true);

            var shelves = new MeshBuilder();
            Color[] bottleColors = [new(1f, 0.6f, 0.15f), new(0.3f, 1f, 0.5f), new(0.4f, 0.6f, 1f)];
            var bottles = bottleColors.Select(_ => new MeshBuilder()).ToArray();
            for (float y = 1.4f; y < 2.9f; y += 0.55f)
            {
                shelves.Span(new Vector3(20.5f, y, 4.4f), new Vector3(29f, y + 0.04f, 4.8f));
                for (float x = 20.7f; x < 28.8f; x += Range(0.15f, 0.3f))
                {
                    float h = Range(0.22f, 0.36f);
                    bottles[_random.Next(bottles.Length)].Box(new Vector3(x, y + 0.04f + h * 0.5f, 4.6f), new Vector3(0.08f, h, 0.08f));
                }
            }

            Add("Shelves", shelves, _darkMetal, group);
            for (int i = 0; i < bottles.Length; i++) Add("Bottles", bottles[i], Glow(bottleColors[i], 1.6f), group, shadows: false);

            var poles = new MeshBuilder();
            var seats = new MeshBuilder();
            for (float x = 21f; x < 28.6f; x += 1.5f)
            {
                poles.Beam(new Vector3(x, _floor, 7.2f), new Vector3(x, 0.8f, 7.2f), 0.07f);
                seats.Box(new Vector3(x, 0.84f, 7.2f), new Vector3(0.42f, 0.09f, 0.42f));
            }

            Add("Bar Stool Legs", poles, _steel, group);
            Add("Bar Stool Seats", seats, Lit(new Color(0.15f, 0.12f, 0.35f), 0.4f), group);

            NeonSign("Bar Sign", "BAR", new Vector3(24.8f, 3.7f, 4.55f), Vector3.UnitX, Vector3.UnitY, _blue, 0.8f, group, light: 2.2f);
            PointLight("Bar Light", new Vector3(24.5f, 3f, 8f), _purple, 2.5f, 8f, group, fog: 0.6f);
        }

        /// <summary>Tables in the middle, each with four stools and a lamp over it.</summary>
        private static void Tables(Transform group)
        {
            var tops = new MeshBuilder { TexturesPerUnit = 0.5f };
            var legs = new MeshBuilder();
            var seats = new MeshBuilder();
            Vector3[] centres = [new(15.5f, 0f, 10.5f), new(21.5f, 0f, 12f), new(15.5f, 0f, 22.5f), new(21.5f, 0f, 21.5f)];
            foreach (Vector3 c in centres)
            {
                tops.Box(c + new Vector3(0f, 0.79f, 0f), new Vector3(1.3f, 0.06f, 1.3f));
                legs.Beam(c + new Vector3(0f, _floor, 0f), c + new Vector3(0f, 0.76f, 0f), 0.1f);
                legs.Box(c + new Vector3(0f, _floor + 0.02f, 0f), new Vector3(0.6f, 0.04f, 0.6f));
                foreach (Vector3 side in (ReadOnlySpan<Vector3>)[Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ])
                {
                    Vector3 s = c + side * 1f;
                    legs.Beam(s + new Vector3(0f, _floor, 0f), s + new Vector3(0f, 0.45f, 0f), 0.05f);
                    seats.Box(s + new Vector3(0f, 0.48f, 0f), new Vector3(0.36f, 0.06f, 0.36f));
                }
            }

            Add("Table Tops", tops, _woodMaterial, group, solid: true);
            Add("Legs", legs, _darkMetal, group);
            Add("Stools", seats, _vinyl, group);
            Lamps(group, centres.Select(c => c + new Vector3(0f, 2.7f, 0f)).ToArray(), new Color(1f, 0.7f, 0.45f), 3.5f, 5.5f);
        }

        /// <summary>Three arcade machines against the front wall, their screens glowing.</summary>
        private static void Arcade(Transform group)
        {
            var bodies = new MeshBuilder();
            var screens = new MeshBuilder();
            Color[] marquees = [_pink, _cyan, _gold];
            for (int i = 0; i < 3; i++)
            {
                float x = 11.2f + i * 1.4f;
                bodies.Span(new Vector3(x - 0.55f, _floor, 4.4f), new Vector3(x + 0.55f, 1.95f, 5.3f));
                bodies.Span(new Vector3(x - 0.55f, 0.9f, 5.3f), new Vector3(x + 0.55f, 1.0f, 5.7f));
                float u = i / 3f;
                screens.Panel(new Vector3(x, 1.4f, 5.31f), Vector3.UnitZ, Vector3.UnitY, 0.8f, 0.65f, new Vector2(u, 0f), new Vector2(u + 0.6f, 0.8f));

                var marquee = new MeshBuilder();
                marquee.Span(new Vector3(x - 0.5f, 1.75f, 5.3f), new Vector3(x + 0.5f, 1.92f, 5.33f));
                Add("Marquee", marquee, Glow(marquees[i], 4f), group, shadows: false);
            }

            Add("Cabinets", bodies, Lit(new Color(0.06f, 0.05f, 0.1f), 0.4f), group, solid: true);
            Add("Screens", screens, Picture(_screens, new Color(1.2f, 1.2f, 1.2f)), group, shadows: false);
            PointLight("Screen Glow", new Vector3(12.6f, 1.5f, 6.4f), new Color(0.6f, 0.4f, 1f), 1.4f, 4.5f, group, fog: 0.5f);
            Text("INSERT COIN", new Vector3(12.6f, 2.25f, 5.36f), Vector3.UnitZ, 0.22f, _gold, group);
        }

        /// <summary>The floor, panelling round the walls, pipes across the ceiling and strips of light between them.</summary>
        private static void Ceiling(Transform group)
        {
            var floor = new MeshBuilder { TexturesPerUnit = 0.5f };
            floor.Span(new Vector3(9.4f, 0f, 4.4f), new Vector3(30.6f, _floor, 29.6f));
            floor.Span(new Vector3(9f, 0f, 14f), new Vector3(9.4f, _floor, 20f));
            Add("Floor", floor, _floorTiles, group, solid: true, shadows: false);

            var panels = new MeshBuilder { TexturesPerUnit = 0.5f };
            panels.Span(new Vector3(9.4f, _floor, 29.54f), new Vector3(30.6f, 1.1f, 29.6f));
            panels.Span(new Vector3(9.4f, _floor, 4.4f), new Vector3(30.6f, 1.1f, 4.46f));
            panels.Span(new Vector3(30.54f, _floor, 4.4f), new Vector3(30.6f, 1.1f, 29.6f));
            Add("Panelling", panels, _darkWood, group, shadows: false);

            var plaster = new MeshBuilder();
            plaster.Span(new Vector3(9.4f, 1.1f, 29.57f), new Vector3(30.6f, _ceiling, 29.6f));
            plaster.Span(new Vector3(9.4f, 1.1f, 4.4f), new Vector3(30.6f, _ceiling, 4.43f));
            plaster.Span(new Vector3(30.57f, 1.1f, 4.4f), new Vector3(30.6f, _ceiling, 29.6f));
            Add("Plaster", plaster, _plaster, group, shadows: false);

            var pipes = new MeshBuilder();
            foreach ((float z, float y, float thick) in new[] { (8f, 5.3f, 0.22f), (8.4f, 5.35f, 0.12f), (25f, 5.3f, 0.25f), (14f, 5.42f, 0.1f) })
                pipes.Beam(new Vector3(9.4f, y, z), new Vector3(30.6f, y, z), thick);
            Add("Pipes", pipes, _metal, group, shadows: false);

            var strips = new MeshBuilder();
            foreach (float z in (ReadOnlySpan<float>)[11f, 17f, 23f])
                strips.Span(new Vector3(11f, _ceiling - 0.05f, z - 0.06f), new Vector3(29f, _ceiling, z + 0.06f));
            Add("Light Strips", strips, Glow(new Color(0.7f, 0.82f, 1f), 2f), group, shadows: false);

            PointLight("Ceiling Light", new Vector3(14f, 5f, 17f), new Color(0.95f, 0.88f, 0.8f), 2.2f, 13f, group, fog: 0.3f);
            PointLight("Ceiling Light", new Vector3(25f, 5f, 17f), new Color(0.95f, 0.88f, 0.8f), 2.2f, 13f, group, fog: 0.3f);
        }
    }
}
