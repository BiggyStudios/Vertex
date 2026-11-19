using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;

namespace NeonCity.Builder
{
    // Lamps, cables, stalls and steam along the street; rain and flying cars over it; the city beyond; the moon; the walker.
    internal static partial class CityBuilder
    {
        private static void StreetLife()
        {
            Transform group = Group("Street").Transform;
            StreetLamps(Group("Street Lamps", group).Transform);
            Cables(Group("Cables", group).Transform);
            FoodStall(group, "Yakitori Stall", 36f, "YAKITORI", _orange);
            FoodStall(group, "Takoyaki Stall", 41f, "TAKOYAKI", _pink);
            VendingMachines(group);
            Steam(group, new Vector3(1.8f, 0f, 10f));
            Steam(group, new Vector3(-2.2f, 0f, -52f));
            Clutter(group);
            Rain(group);
            FlyingCars(Group("Flying Cars", group).Transform);
        }

        /// <summary>Lamps along both pavements, each a pole with an arm out over the road and a cone of light down from it.</summary>
        private static void StreetLamps(Transform group)
        {
            var poles = new MeshBuilder();
            var heads = new MeshBuilder();
            float[] west = [52f, 20f, -12f, -46f, -78f], east = [36f, 3f, -22f, -54f, -86f];
            foreach ((float[] zs, float side) in new[] { (west, -1f), (east, 1f) })
            {
                foreach (float z in zs)
                {
                    var foot = new Vector3(side * 8.4f, 0.15f, z);
                    var top = foot + new Vector3(0f, 6.6f, 0f);
                    var end = top + new Vector3(-side * 2.2f, 0f, 0f);
                    poles.Beam(foot, top, 0.16f);
                    poles.Beam(top, end, 0.1f);
                    poles.Box(foot + new Vector3(0f, 0.3f, 0f), new Vector3(0.35f, 0.6f, 0.35f));
                    poles.Box(end + new Vector3(0f, -0.08f, 0f), new Vector3(0.7f, 0.18f, 0.32f));
                    heads.Box(end + new Vector3(0f, -0.18f, 0f), new Vector3(0.6f, 0.03f, 0.24f));

                    // Shining down, leaning a little out over the road.
                    SpotLight("Lamp", end - new Vector3(0f, 0.25f, 0f), new Vector3(-side * 0.25f, -1f, 0f), new Color(0.75f, 0.85f, 1f), 14f, 13f, 75f, group, fog: 1.4f);
                }
            }

            Add("Lamp Posts", poles, _darkMetal, group, solid: true);
            Add("Lamp Glass", heads, Glow(new Color(0.8f, 0.9f, 1f), 8f), group, shadows: false);
        }

        /// <summary>Power cables slung across the street between the buildings, sagging, and paper lanterns hung along two.</summary>
        private static void Cables(Transform group)
        {
            var cables = new MeshBuilder();
            var lanterns = new MeshBuilder();
            float[] crossings = [58f, 47f, 33f, 22f, 10f, -6f, -18f, -44f, -60f];
            for (int c = 0; c < crossings.Length; c++)
            {
                float z = crossings[c];
                float height = Range(7.5f, 12f);
                int strands = 1 + _random.Next(3);
                for (int s = 0; s < strands; s++)
                {
                    var from = new Vector3(-9f, height + s * 0.4f, z + Range(-1.5f, 1.5f));
                    var to = new Vector3(9f, height + Range(-1f, 1.5f) + s * 0.4f, z + Range(-1.5f, 1.5f));
                    float sag = Range(0.8f, 1.8f);
                    Vector3 Point(float t) => Vector3.Lerp(from, to, t) - new Vector3(0f, sag * 4f * t * (1f - t), 0f);
                    for (int i = 0; i < 10; i++) cables.Beam(Point(i / 10f), Point((i + 1) / 10f), 0.035f);

                    // Lanterns on the first strand of two of the crossings.
                    if (s == 0 && (c == 1 || c == 4))
                    {
                        for (int l = 1; l < 8; l++)
                        {
                            Vector3 hang = Point(l / 8f);
                            cables.Beam(hang, hang - new Vector3(0f, 0.35f, 0f), 0.015f);
                            lanterns.Box(hang - new Vector3(0f, 0.65f, 0f), new Vector3(0.3f, 0.42f, 0.3f));
                            lanterns.Box(hang - new Vector3(0f, 0.65f, 0f), new Vector3(0.3f, 0.42f, 0.3f), 45f);
                        }

                        PointLight("Lanterns", Point(0.5f) - new Vector3(0f, 1f, 0f), new Color(1f, 0.25f, 0.12f), 2.5f, 9f, group, fog: 1.4f);
                    }
                }
            }

            Add("Cables", cables, _darkMetal, group, shadows: false);
            Add("Paper Lanterns", lanterns, Glow(new Color(1f, 0.22f, 0.1f), 1.6f), group, shadows: false);
        }

        /// <summary>A street food stall against the wall on the west pavement: a counter under an awning, stools, a grill smoking.</summary>
        private static void FoodStall(Transform parent, string name, float z, string sign, Color color)
        {
            Transform stall = Group(name, parent).Transform;
            var wood = new MeshBuilder();
            var metal = new MeshBuilder();
            const float back = -8.9f, front = -6.6f;

            wood.Span(new Vector3(back, 0.15f, z - 1.6f), new Vector3(front, 1.05f, z + 1.6f));
            wood.Span(new Vector3(back, 1.05f, z - 1.7f), new Vector3(front + 0.25f, 1.12f, z + 1.7f));
            metal.Span(new Vector3(back + 0.3f, 1.12f, z - 1.1f), new Vector3(back + 1.3f, 1.3f, z + 1.1f));
            metal.Beam(new Vector3(front - 0.1f, 0.15f, z - 1.6f), new Vector3(front - 0.1f, 2.75f, z - 1.6f), 0.08f);
            metal.Beam(new Vector3(front - 0.1f, 0.15f, z + 1.6f), new Vector3(front - 0.1f, 2.75f, z + 1.6f), 0.08f);
            Add("Counter", wood, _darkWood, stall, solid: true);
            Add("Grill", metal, _darkMetal, stall);

            // The awning, sloping down towards the road, with a strip of light along its edge.
            var awning = new MeshBuilder();
            awning.Box(new Vector3(-7.6f, 2.85f, z), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(2.9f, 0.08f, 3.8f));
            Add("Awning", awning, _redCloth, stall);
            var edge = new MeshBuilder();
            edge.Box(new Vector3(-6.15f, 2.76f, z), new Vector3(0.06f, 0.1f, 3.8f));
            Add("Awning Light", edge, Glow(color, 4f), stall, shadows: false);

            for (int i = -1; i <= 1; i++)
            {
                var stool = new MeshBuilder();
                stool.Beam(new Vector3(-6.1f, 0.15f, z + i * 1f), new Vector3(-6.1f, 0.7f, z + i * 1f), 0.06f);
                stool.Box(new Vector3(-6.1f, 0.72f, z + i * 1f), new Vector3(0.38f, 0.07f, 0.38f));
                Add("Stool", stool, _vinyl, stall);
            }

            PointLight("Stall Light", new Vector3(-7.4f, 2.4f, z), new Color(1f, 0.7f, 0.4f), 2.2f, 6f, stall, fog: 0.8f);
            Text(sign, new Vector3(-6.12f, 2.55f, z), Vector3.UnitX, 0.32f, color, stall);

            var smoke = new GameObject("Smoke");
            smoke.Transform.Position = new Vector3(-8f, 1.4f, z);
            smoke.Transform.Parent = stall;
            smoke.AddComponent<ParticleSystem>(p =>
            {
                p.Rate = 14f;
                p.Lifetime = new FloatRange(2f, 3.2f);
                p.Speed = new FloatRange(0.5f, 0.9f);
                p.Spread = 18f;
                p.Size = new FloatRange(0.35f, 0.6f);
                p.EndSize = 3.5f;
                p.StartColor = new Color(0.55f, 0.55f, 0.6f, 0.22f);
                p.EndColor = new Color(0.4f, 0.4f, 0.45f, 0f);
                p.Blend = ParticleBlend.Alpha;
                p.Area = new Vector3(0.8f, 0f, 1.6f);
            });
        }

        private static void VendingMachines(Transform parent)
        {
            Transform group = Group("Vending Machines", parent).Transform;
            var bodies = new MeshBuilder();
            var fronts = new MeshBuilder();
            for (int i = 0; i < 3; i++)
            {
                float z = 43f + i * 1.15f;
                bodies.Span(new Vector3(8.15f, 0.15f, z - 0.55f), new Vector3(8.95f, 2.05f, z + 0.55f));
                fronts.Panel(new Vector3(8.14f, 1.25f, z), -Vector3.UnitX, Vector3.UnitY, 0.9f, 1.5f);
            }

            Add("Machines", bodies, Lit(new Color(0.75f, 0.1f, 0.12f), 0.35f), group, solid: true);
            Add("Fronts", fronts, Picture(_vending, Color.White), group, shadows: false);
            PointLight("Machine Glow", new Vector3(7.4f, 1.3f, 44.15f), new Color(0.7f, 0.85f, 1f), 1.5f, 4.5f, group, fog: 0.8f);
        }

        /// <summary>Steam rising from a grate in the road, thickening the fog round it.</summary>
        private static void Steam(Transform parent, Vector3 at)
        {
            var grate = new MeshBuilder();
            grate.Box(at + new Vector3(0f, 0.01f, 0f), new Vector3(1f, 0.03f, 1f));
            GameObject steam = Add("Steam Grate", grate, _darkMetal, parent, shadows: false);

            var rising = new GameObject("Steam");
            rising.Transform.Position = at + new Vector3(0f, 0.1f, 0f);
            rising.Transform.Parent = steam.Transform;
            rising.AddComponent<ParticleSystem>(p =>
            {
                p.Rate = 10f;
                p.Lifetime = new FloatRange(2.5f, 4f);
                p.Speed = new FloatRange(0.7f, 1.3f);
                p.Spread = 12f;
                p.Size = new FloatRange(0.5f, 0.8f);
                p.EndSize = 4f;
                p.StartColor = new Color(0.6f, 0.62f, 0.68f, 0.25f);
                p.EndColor = new Color(0.5f, 0.5f, 0.55f, 0f);
                p.Blend = ParticleBlend.Alpha;
                p.Area = new Vector3(0.7f, 0f, 0.7f);
            });

            var mist = new GameObject("Mist");
            mist.Transform.Position = at + new Vector3(0f, 2f, 0f);
            mist.Transform.Scale = new Vector3(4f, 5f, 4f);
            mist.Transform.Parent = steam.Transform;
            mist.AddComponent<FogVolume>(f => { f.Density = 0.12f; f.Softness = 1f; });
        }

        /// <summary>Crates, bins and pipes along the walls, so they don't look swept.</summary>
        private static void Clutter(Transform parent)
        {
            var junk = new MeshBuilder();
            var pipes = new MeshBuilder();
            for (int i = 0; i < 22; i++)
            {
                float side = i % 2 == 0 ? -1f : 1f;
                float z = Range(-90f, 64f);
                if (side > 0f && z > 3f && z < 31f) continue;   // the night market's front
                if (z > -38f && z < -26f) continue;             // the cross street
                var size = new Vector3(Range(0.5f, 1.1f), Range(0.4f, 1f), Range(0.5f, 1.2f));
                junk.Box(new Vector3(side * (8.85f - size.X * 0.5f), 0.15f + size.Y * 0.5f, z), size, Range(-8f, 8f));
            }

            // Pipes running up the walls in the gaps between buildings.
            foreach ((float x, float z) in new[] { (-9.05f, 19f), (-9.05f, 25f), (9.05f, 31f), (9.05f, 2f), (9.05f, -27.5f), (-9.05f, -27.5f) })
            {
                pipes.Beam(new Vector3(x, 0.15f, z), new Vector3(x, 26f, z), 0.18f);
                pipes.Beam(new Vector3(x, 0.15f, z + 0.35f), new Vector3(x, 18f, z + 0.35f), 0.1f);
            }

            Add("Crates", junk, _darkWood, parent, solid: true);
            Add("Pipes", pipes, _metal, parent);
        }

        /// <summary>Rain over the open street: streaks falling the length of it, faint against the dark and bright through the lights.</summary>
        private static void Rain(Transform parent)
        {
            var rain = new GameObject("Rain");
            rain.Transform.Position = new Vector3(0f, 22f, -40f);
            rain.Transform.Parent = parent;
            rain.AddComponent<GpuParticleSystem>(p =>
            {
                p.Rate = 4500f;
                p.MaxParticles = 6500;
                p.Area = new Vector3(19f, 0f, 210f);
                p.Direction = -Vector3.UnitY;
                p.Spread = 3f;
                p.Speed = new FloatRange(17f, 21f);
                p.Lifetime = new FloatRange(1.1f, 1.3f);
                p.Size = 0.022f;
                p.EndSize = 1f;
                p.Stretch = 0.035f;
                p.StartColor = new Color(0.7f, 0.78f, 1f, 0.35f);
                p.EndColor = new Color(0.7f, 0.78f, 1f, 0.35f);
                p.Blend = ParticleBlend.Additive;
            });
        }

        /// <summary>Cars flying along the street above the signs, headlights cutting through the fog.</summary>
        private static void FlyingCars(Transform group)
        {
            (Vector3 Start, float Direction, float Speed, float Offset, Color Glow)[] cars =
            [
                (new Vector3(-2.5f, 24f, -170f), 1f, 16f, 0f, _cyan),
                (new Vector3(3f, 30f, 140f), -1f, 21f, 90f, _pink),
                (new Vector3(-1f, 41f, 140f), -1f, 13f, 200f, _orange),
                (new Vector3(1.5f, 35f, -170f), 1f, 24f, 140f, _purple)
            ];

            foreach ((Vector3 start, float direction, float speed, float offset, Color glow) in cars)
            {
                var car = new GameObject("Flying Car");
                car.Transform.Position = start;
                car.Transform.Rotation = Facing(new Vector3(0f, 0f, -direction));
                car.Transform.Parent = group;
                car.AddComponent<Flyer>(f => { f.Speed = speed; f.Distance = 310f; f.Offset = offset; });

                // Built facing -Z, its front, as an object faces before it's turned.
                var body = new MeshBuilder();
                body.Box(Vector3.Zero, new Vector3(2.2f, 0.7f, 4.6f));
                body.Box(new Vector3(0f, 0.55f, 0.3f), new Vector3(1.6f, 0.45f, 2.2f));
                body.Box(new Vector3(1.25f, -0.1f, 0f), new Vector3(0.4f, 0.4f, 3.4f));
                body.Box(new Vector3(-1.25f, -0.1f, 0f), new Vector3(0.4f, 0.4f, 3.4f));
                AddAt("Body", Vector3.Zero, body, _darkMetal, car.Transform);

                var tail = new MeshBuilder();
                tail.Box(new Vector3(0f, 0.05f, 2.31f), new Vector3(1.9f, 0.1f, 0.05f));
                AddAt("Tail Lights", Vector3.Zero, tail, Glow(_red, 10f), car.Transform, shadows: false);

                var head = new MeshBuilder();
                head.Box(new Vector3(0.7f, 0.05f, -2.31f), new Vector3(0.45f, 0.1f, 0.05f));
                head.Box(new Vector3(-0.7f, 0.05f, -2.31f), new Vector3(0.45f, 0.1f, 0.05f));
                AddAt("Headlights", Vector3.Zero, head, Glow(new Color(0.9f, 0.95f, 1f), 14f), car.Transform, shadows: false);

                var under = new MeshBuilder();
                under.Box(new Vector3(0f, -0.36f, 0f), new Vector3(1.6f, 0.03f, 3.6f));
                AddAt("Underglow", Vector3.Zero, under, Glow(glow, 6f), car.Transform, shadows: false);

                SpotLight("Headlight Beam", new Vector3(0f, 0f, -2.4f), new Vector3(0f, -0.18f, -1f), new Color(0.85f, 0.9f, 1f), 9f, 28f, 26f, car.Transform, fog: 2.5f);
            }
        }

        // ---------------------------------------------------------------- The city beyond

        /// <summary>Towers further off on every side, fading into the fog, their windows lit here and there.</summary>
        private static void Skyline()
        {
            Transform group = Group("Skyline").Transform;
            var shells = new MeshBuilder();
            var windows = new MeshBuilder();
            var beacons = new MeshBuilder();

            for (float x = -150f; x <= 150f; x += 26f)
            {
                for (float z = -330f; z <= 190f; z += 28f)
                {
                    // Leave the streets and the blocks along them clear.
                    if (MathF.Abs(x) < 70f && z > -170f && z < 100f) continue;
                    if (_random.NextSingle() < 0.25f) continue;

                    float w = Range(12f, 22f), d = Range(12f, 24f);
                    float near = MathF.Min(MathF.Abs(x), MathF.Abs(z + 40f));
                    float h = Range(35f, 90f) + (near > 120f ? Range(20f, 90f) : 0f);
                    var c = new Vector3(x + Range(-3f, 3f), 0f, z + Range(-3f, 3f));
                    shells.Span(c - new Vector3(w * 0.5f, 0f, d * 0.5f), c + new Vector3(w * 0.5f, h, d * 0.5f));
                    if (h > 100f) beacons.Box(c + new Vector3(0f, h + 1.5f, 0f), new Vector3(0.8f, 0.8f, 0.8f));

                    // Only lit windows: from this far off the dark ones are just wall.
                    Vector3 toCity = Vector3.Normalize(new Vector3(-c.X, 0f, -40f - c.Z));
                    (Vector3 Corner, Vector3 Along, Vector3 Normal, float Width)[] faces =
                    [
                        (c + new Vector3(-w * 0.5f, 0f, -d * 0.5f), Vector3.UnitZ, -Vector3.UnitX, d),
                        (c + new Vector3(w * 0.5f, 0f, -d * 0.5f), Vector3.UnitZ, Vector3.UnitX, d),
                        (c + new Vector3(-w * 0.5f, 0f, -d * 0.5f), Vector3.UnitX, -Vector3.UnitZ, w),
                        (c + new Vector3(-w * 0.5f, 0f, d * 0.5f), Vector3.UnitX, Vector3.UnitZ, w)
                    ];
                    foreach ((Vector3 corner, Vector3 along, Vector3 normal, float width) in faces)
                    {
                        if (Vector3.Dot(normal, toCity) < -0.2f) continue;
                        for (float y = 4f; y + 3f < h; y += 4f)
                        {
                            for (float a = 2f; a + 1.5f <= width; a += 3.2f)
                            {
                                if (_random.NextSingle() > 0.15f) continue;
                                (Vector2 lo, Vector2 hi) = WindowPicture(_random.Next(_litColors));
                                windows.Panel(corner + along * a + new Vector3(0f, y + 1.2f, 0f) + normal * 0.05f, normal, Vector3.UnitY, 2.2f, 2.4f, lo, hi);
                            }
                        }
                    }
                }
            }

            Add("Towers", shells, _darkWall, group);
            Add("Windows", windows, _windows, group, shadows: false);
            Add("Warning Lights", beacons, Glow(_red, 12f), group, shadows: false).AddComponent<Flicker>(f => { f.Style = FlickerStyle.Blink; f.Seconds = 2f; f.Low = 0f; });

            // A giant sign on a far tower, glowing through the haze down the street.
            GlyphSign("Far Tower Sign", 6, new Vector3(-30f, 70f, -230f), Vector3.UnitX, Vector3.UnitY, _pink, 5f, group);
        }

        private static void Sky()
        {
            // The moon: dim and blue, high enough to shine down between the towers in shafts. It shows more in the fog than on
            // the ground, for the shafts.
            var moon = new GameObject("Moon");
            moon.Transform.EulerAngles = new Vector3(-55f, 200f, 0f);
            moon.AddComponent<Light>(light =>
            {
                light.Type = LightType.Directional;
                light.Color = new Color(0.55f, 0.65f, 1f);
                light.Intensity = 0.22f;
                light.FogStrength = 2.5f;
            });
        }

        private static void Player()
        {
            var player = new GameObject("Player");
            player.Transform.Position = new Vector3(0f, 0f, 58f);
            player.AddComponent<CharacterController>();

            var head = new GameObject("Head");
            head.Transform.Parent = player.Transform;
            head.Transform.Position = new Vector3(0f, 1.65f, 0f);
            head.AddComponent<Camera>(camera =>
            {
                camera.FieldOfView = 72f;
                camera.ClearColor = new Color(0.02f, 0.02f, 0.04f);
            });

            player.AddComponent<Walker>(walker => walker.Head = head);
            player.AddComponent<CityHud>();
        }
    }
}
