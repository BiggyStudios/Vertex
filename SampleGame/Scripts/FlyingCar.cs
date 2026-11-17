using System.Numerics;

using Vertex;
using Vertex.Rendering;

namespace SampleGame
{
    /// <summary>
    /// A car flying along a straight lane through the city, high over the maps, and back to the start of the lane once
    /// it's off the far end, for ever. It bobs a little as it goes. Only backdrop: nothing hits it. See <see cref="Traffic"/>.
    /// </summary>
    public class FlyingCar : Component
    {
        /// <summary>Where the lane starts and ends.</summary>
        public Vector3 From { get; set; }
        public Vector3 To { get; set; }

        /// <summary>Metres a second.</summary>
        public float Speed { get; set; } = 50f;

        /// <summary>How far along the lane it is, from 0 to 1.</summary>
        public float Along { get; set; }

        private float _bobPhase;

        protected override void Start()
        {
            _bobPhase = Along * 40f;
            Vector3 direction = To - From;
            Transform.EulerAngles = new Vector3(0f, MathHelper.ToDegrees(MathF.Atan2(-direction.X, -direction.Z)), 0f);
            Place();
        }

        protected override void Update()
        {
            float length = Vector3.Distance(From, To);
            Along = (Along + Speed * Time.DeltaTime / length) % 1f;
            Place();
        }

        private void Place()
        {
            float bob = MathF.Sin(Time.TotalTime * 1.3f + _bobPhase) * 0.6f;
            Transform.Position = Vector3.Lerp(From, To, Along) + new Vector3(0f, bob, 0f);
        }
    }

    /// <summary>
    /// The sky traffic over the city: lanes of flying cars criss-crossing high above the maps, both ways along each,
    /// with white headlights, red tail lights and a neon glow underneath.
    /// </summary>
    public static class Traffic
    {
        private static readonly Color[] _underglow = [new(1f, 0.2f, 0.6f), new(0.1f, 0.9f, 1f), new(0.65f, 0.3f, 1f), new(1f, 0.75f, 0.2f)];
        private static readonly Color[] _paint = [new(0.12f, 0.12f, 0.15f), new(0.25f, 0.05f, 0.1f), new(0.05f, 0.1f, 0.22f), new(0.6f, 0.6f, 0.65f), new(0.7f, 0.45f, 0.1f)];

        public static void Build()
        {
            var random = new Random(1982);

            // Each lane runs the length of the city down the middle of one of its streets, so it never goes through a
            // tower, flown both ways a little apart. Those that pass over the maps are high enough to clear everything in
            // them, the sky platforms and grapple points included; the rest fly lower, down among the towers.
            Vector2 min = CityBackdrop.Min, max = CityBackdrop.Max;
            (Vector3 From, Vector3 To) EastWest(int street, float height) => (new(min.X, height, CityBackdrop.StreetZ(street)), new(max.X, height, CityBackdrop.StreetZ(street)));
            (Vector3 From, Vector3 To) NorthSouth(int street, float height) => (new(CityBackdrop.StreetX(street), height, min.Y), new(CityBackdrop.StreetX(street), height, max.Y));
            (Vector3 From, Vector3 To)[] lanes =
            [
                // Over the maps.
                EastWest(16, 110f), EastWest(21, 135f), EastWest(26, 120f),
                NorthSouth(15, 125f), NorthSouth(19, 145f), NorthSouth(10, 115f),
                // Down among the towers, away from the maps.
                EastWest(10, 60f), EastWest(30, 45f), EastWest(6, 85f),
                NorthSouth(22, 55f), NorthSouth(6, 75f), NorthSouth(25, 35f)
            ];

            var traffic = new GameObject("Traffic");
            foreach ((Vector3 from, Vector3 to) in lanes)
            {
                Vector3 across = Vector3.Normalize(Vector3.Cross(to - from, Vector3.UnitY)) * 7f;
                foreach (bool back in (ReadOnlySpan<bool>)[false, true])
                {
                    float speed = 45f + random.NextSingle() * 30f;
                    const int cars = 5;
                    for (int i = 0; i < cars; i++)
                    {
                        Vector3 start = back ? to - across : from + across;
                        Vector3 end = back ? from - across : to + across;
                        FlyingCar car = AddCar(traffic, random);
                        car.From = start;
                        car.To = end;
                        car.Speed = speed * (0.9f + random.NextSingle() * 0.2f);
                        car.Along = (i + random.NextSingle() * 0.6f) / cars;
                    }
                }
            }
        }

        // Every car is built from the same four meshes, and shares its materials with the others in its colours, so the
        // renderer draws them in long runs that each set up a material and mesh once (see Renderer).
        private static Mesh? _body, _headlights, _tailLight, _underglowMesh;
        private static Material[] _paints = [];
        private static Material[] _glows = [];
        private static Material? _headlightMaterial, _tailLightMaterial;

        /// <summary>
        /// One car, its forward along -Z: a sleek body with its cabin on top, headlights in front, tail lights behind,
        /// and neon underneath. Four parts, so it's four draws.
        /// </summary>
        private static FlyingCar AddCar(GameObject parent, Random random)
        {
            if (_body == null) CreateParts();

            var car = new GameObject("Flying Car");
            car.Transform.Parent = parent.Transform;
            // A bit bigger than a real car, so it reads from far below.
            car.Transform.Scale = new Vector3(1.5f);

            Part(car, "Body", _body!, _paints[random.Next(_paints.Length)]);
            Part(car, "Headlights", _headlights!, _headlightMaterial!);
            Part(car, "Tail Lights", _tailLight!, _tailLightMaterial!);
            Part(car, "Underglow", _underglowMesh!, _glows[random.Next(_glows.Length)]);

            return car.AddComponent<FlyingCar>();
        }

        private static void CreateParts()
        {
            var body = new CityBackdrop.MeshBuilder();
            body.AddBox(Vector3.Zero, new Vector3(2.2f, 0.8f, 5f), Vector2.Zero);
            body.AddBox(new Vector3(0f, 0.65f, 0.3f), new Vector3(1.8f, 0.6f, 2.4f), Vector2.Zero);
            _body = body.ToMesh();

            var headlights = new CityBackdrop.MeshBuilder();
            headlights.AddBox(new Vector3(-0.7f, 0.05f, -2.52f), new Vector3(0.5f, 0.2f, 0.06f), Vector2.Zero);
            headlights.AddBox(new Vector3(0.7f, 0.05f, -2.52f), new Vector3(0.5f, 0.2f, 0.06f), Vector2.Zero);
            _headlights = headlights.ToMesh();

            var tail = new CityBackdrop.MeshBuilder();
            tail.AddBox(new Vector3(0f, 0.1f, 2.52f), new Vector3(1.9f, 0.18f, 0.06f), Vector2.Zero);
            _tailLight = tail.ToMesh();

            var underglow = new CityBackdrop.MeshBuilder();
            underglow.AddBox(new Vector3(0f, -0.42f, 0f), new Vector3(1.8f, 0.05f, 4.2f), Vector2.Zero);
            _underglowMesh = underglow.ToMesh();

            _paints = _paint.Select(color => new Material { Color = color, Specular = 0.6f }).ToArray();
            _glows = _underglow.Select(color => Glowing(color, 6f)).ToArray();
            _headlightMaterial = Glowing(Color.White, 7f);
            _tailLightMaterial = Glowing(new Color(1f, 0.08f, 0.1f), 7f);
        }

        private static Material Glowing(Color color, float strength) =>
            new() { Shading = ShadingModel.Unlit, Color = color, Emission = color, EmissionStrength = strength };

        private static void Part(GameObject car, string name, Mesh mesh, Material material)
        {
            var part = new GameObject(name);
            part.Transform.Parent = car.Transform;
            MeshRenderer renderer = part.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material = material;
            renderer.CastShadows = false;
        }
    }
}
