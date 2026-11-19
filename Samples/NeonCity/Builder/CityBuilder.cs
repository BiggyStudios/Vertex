using System.Numerics;

using Vertex;
using Vertex.Physics;
using Vertex.Rendering;
using Vertex.Serialization;

namespace NeonCity.Builder
{
    /// <summary>
    /// Makes the city's scene and textures with the engine's own API: a rainy street at night between tall buildings,
    /// lit by neon signs, street lamps and windows through volumetric fog, with a night market to walk into. The same
    /// random numbers every time, so it comes out the same.
    /// </summary>
    internal static partial class CityBuilder
    {
        private static Random _random = new(11);
        private static string _assets = "";

        public static void Build(string assets)
        {
            _assets = assets;
            _random = new Random(11);
            Directory.CreateDirectory(Path.Combine(assets, "Scenes"));
            Directory.CreateDirectory(Path.Combine(assets, "Textures"));
            Application.AssetFolders = [assets];

            var scene = new Scene();
            Scene.Active = scene;

            MakeTextures();
            MakeMaterials();
            SetLighting(scene.Lighting);

            Streets();
            Buildings();
            NightMarket();
            StreetLife();
            Skyline();
            Sky();
            Player();

            string path = Path.Combine(assets, "Scenes", "Neon City.scene");
            SceneFile.Save(scene, path, assets);
            Console.WriteLine($"Wrote {path} ({new FileInfo(path).Length / 1024} KB, {scene.GameObjects.Count} objects).");
        }

        // ---------------------------------------------------------------- The night

        private static void SetLighting(Lighting lighting)
        {
            // A dark sky, glowing purple low down with the city's light.
            lighting.Sky = SkyStyle.Gradient;
            lighting.SkyColor = new Color(0.012f, 0.012f, 0.035f);
            lighting.HorizonColor = new Color(0.2f, 0.07f, 0.24f);
            lighting.GroundColor = new Color(0.035f, 0.035f, 0.05f);
            lighting.CloudCover = 0f;
            lighting.AmbientIntensity = 0.45f;
            lighting.FogDensity = 0.006f;
            lighting.Exposure = 1.25f;
            lighting.Bloom = 1.3f;
            lighting.AmbientOcclusion = 1f;
            lighting.Vignette = 0.35f;
            lighting.ShadowDistance = 90f;

            lighting.VolumetricFog = true;
            lighting.VolumetricDensity = 0.03f;
            lighting.VolumetricColor = new Color(0.86f, 0.88f, 1f);
            lighting.VolumetricHeight = 16f;
            lighting.VolumetricFalloff = 0.03f;
            lighting.VolumetricNoise = 0.55f;
            lighting.VolumetricWind = new Vector3(0.5f, 0.05f, -0.3f);
            lighting.VolumetricForwardScattering = 0.55f;
            lighting.VolumetricAmbient = 0.5f;
            lighting.VolumetricDistance = 100f;
        }

        // ---------------------------------------------------------------- Making things

        private static GameObject Group(string name, Transform? parent = null)
        {
            var group = new GameObject(name);
            group.Transform.Parent = parent;
            return group;
        }

        /// <summary>A mesh made from <paramref name="mesh"/>, in the world as built; solid to walk into if <paramref name="solid"/>.</summary>
        private static GameObject Add(string name, MeshBuilder mesh, Material material, Transform? parent, bool solid = false, bool shadows = true)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Parent = parent;
            if (mesh.IsEmpty) return gameObject;

            Mesh built = mesh.Build();
            gameObject.AddComponent<MeshRenderer>(renderer =>
            {
                renderer.Mesh = built;
                renderer.Material = material;
                renderer.CastShadows = shadows;
            });
            if (solid) gameObject.AddComponent<MeshCollider>().Mesh = built;
            return gameObject;
        }

        /// <summary>An object at <paramref name="position"/> showing <paramref name="mesh"/>, built round its own origin.</summary>
        private static GameObject AddAt(string name, Vector3 position, MeshBuilder mesh, Material material, Transform? parent, bool shadows = true)
        {
            GameObject gameObject = Add(name, mesh, material, null, shadows: shadows);
            gameObject.Transform.Position = position;
            gameObject.Transform.Parent = parent;
            return gameObject;
        }

        /// <summary>A box nobody sees, to keep the walker inside the city.</summary>
        private static void Wall(Vector3 min, Vector3 max, Transform parent)
        {
            var wall = new GameObject("Invisible Wall");
            wall.Transform.Position = (min + max) * 0.5f;
            wall.Transform.Parent = parent;
            wall.AddComponent<BoxCollider>().Size = max - min;
        }

        private static Light PointLight(string name, Vector3 at, Color color, float intensity, float range, Transform parent, float fog = 1f)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Position = at;
            gameObject.Transform.Parent = parent;
            return gameObject.AddComponent<Light>(light =>
            {
                light.Type = LightType.Point;
                light.Color = color;
                light.Intensity = intensity;
                light.Range = range;
                light.FogStrength = fog;
                light.CastShadows = false;
            });
        }

        private static Light SpotLight(string name, Vector3 at, Vector3 direction, Color color, float intensity, float range, float angle, Transform parent, float fog = 1f)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Position = at;
            gameObject.Transform.Rotation = Facing(direction);
            gameObject.Transform.Parent = parent;
            return gameObject.AddComponent<Light>(light =>
            {
                light.Type = LightType.Spot;
                light.Color = color;
                light.Intensity = intensity;
                light.Range = range;
                light.SpotAngle = angle;
                light.SpotSoftness = 0.5f;
                light.FogStrength = fog;
                light.CastShadows = false;
            });
        }

        /// <summary>The turn that points an object's forward (-Z) along <paramref name="forward"/>.</summary>
        private static Quaternion Facing(Vector3 forward)
        {
            Vector3 up = MathF.Abs(Vector3.Normalize(forward).Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, Vector3.Normalize(forward), up)));
        }

        /// <summary>Words on a wall, readable from in front of it, which is the way <paramref name="facing"/> points.</summary>
        private static WorldText Text(string text, Vector3 at, Vector3 facing, float size, Color color, Transform parent)
        {
            var gameObject = new GameObject("Text");
            gameObject.Transform.Position = at;
            gameObject.Transform.Rotation = Facing(-facing);
            gameObject.Transform.Parent = parent;
            return gameObject.AddComponent<WorldText>(words =>
            {
                words.Text = text;
                words.Size = size;
                words.Color = color;
            });
        }

        private static float Range(float min, float max) => min + (max - min) * _random.NextSingle();

        private static T Pick<T>(IReadOnlyList<T> from) => from[_random.Next(from.Count)];

        // ---------------------------------------------------------------- Neon

        /// <summary>
        /// A sign of neon tubes spelling <paramref name="text"/>, across (or down, if <paramref name="vertical"/>) a dark
        /// panel facing the way <paramref name="right"/> × <paramref name="up"/> points, with a light of its colour in front.
        /// Two-sided signs stick out from a wall and read from both ways along the street.
        /// </summary>
        private static GameObject NeonSign(string name, string text, Vector3 centre, Vector3 right, Vector3 up, Color color, float letterHeight,
            Transform parent, bool vertical = false, bool twoSided = false, bool panel = true, bool frame = true, float light = 2.5f,
            FlickerStyle? flicker = null, float glow = 5f)
        {
            float s = letterHeight / NeonFont.Height;
            var strokes = new List<Vector2[]>();
            float width, height;
            if (vertical)
            {
                float step = letterHeight * 1.35f;
                width = NeonFont.Width * s;
                height = text.Length * step - (step - letterHeight);
                for (int i = 0; i < text.Length; i++)
                {
                    var origin = new Vector2(-width * 0.5f, height * 0.5f - letterHeight - i * step);
                    foreach (Vector2[] stroke in NeonFont.Strokes(text[i])) strokes.Add(stroke.Select(p => origin + p * s).ToArray());
                }
            }
            else
            {
                width = NeonFont.LineWidth(text) * s;
                height = letterHeight;
                for (int i = 0; i < text.Length; i++)
                {
                    var origin = new Vector2(-width * 0.5f + i * NeonFont.Advance * s, -letterHeight * 0.5f);
                    foreach (Vector2[] stroke in NeonFont.Strokes(text[i])) strokes.Add(stroke.Select(p => origin + p * s).ToArray());
                }
            }

            return NeonStrokes(name, strokes, width, height, centre, right, up, color, letterHeight * 0.065f, parent, twoSided, panel, frame, light, flicker, glow);
        }

        /// <summary>A neon sign of lines given in metres across and up from its middle: see <see cref="NeonSign"/>.</summary>
        private static GameObject NeonStrokes(string name, List<Vector2[]> strokes, float width, float height, Vector3 centre, Vector3 right, Vector3 up,
            Color color, float tube, Transform parent, bool twoSided, bool panel, bool frame, float light, FlickerStyle? flicker, float glow)
        {
            right = Vector3.Normalize(right);
            up = Vector3.Normalize(up);
            Vector3 normal = Vector3.Cross(right, up);
            tube = MathF.Max(tube, 0.03f);
            float pad = MathF.Min(height, width) * 0.25f + 0.12f;
            float depth = 0.14f;

            // Built round the light, which sits a little in front of the sign.
            Vector3 lightAt = twoSided ? Vector3.Zero : normal * 0.35f;
            var tubes = new MeshBuilder();
            void Side(Vector3 sideRight, Vector3 sideNormal)
            {
                Vector3 face = sideNormal * (depth * 0.5f + tube) - lightAt;
                foreach (Vector2[] stroke in strokes)
                {
                    for (int i = 0; i + 1 < stroke.Length; i++)
                    {
                        Vector3 a = face + sideRight * stroke[i].X + up * stroke[i].Y;
                        Vector3 b = face + sideRight * stroke[i + 1].X + up * stroke[i + 1].Y;
                        tubes.Beam(a, b, tube, sideNormal);
                    }
                }

                if (!frame) return;

                // A tube round the edge of the panel.
                float w = width * 0.5f + pad * 0.7f, h = height * 0.5f + pad * 0.7f;
                Vector3[] corners = [face - sideRight * w - up * h, face + sideRight * w - up * h, face + sideRight * w + up * h, face - sideRight * w + up * h];
                for (int i = 0; i < 4; i++) tubes.Beam(corners[i], corners[(i + 1) % 4], tube * 0.8f, sideNormal);
            }

            // The other side mirrors the letters so they read the right way round from there too.
            Side(right, normal);
            if (twoSided) Side(-right, -normal);

            GameObject sign = AddAt(name, centre + lightAt, tubes, Glow(color, glow), parent, shadows: false);
            sign.AddComponent<Light>(l =>
            {
                l.Type = LightType.Point;
                l.Color = color;
                l.Intensity = light;
                l.Range = MathF.Max(5f, MathF.Max(width, height) * 1.1f);
                l.CastShadows = false;
            });
            if (flicker is { } style) sign.AddComponent<Flicker>(f => f.Style = style);

            if (panel)
            {
                var back = new MeshBuilder();
                back.Box(-lightAt, right, up, normal, new Vector3(width + pad * 2f, height + pad * 2f, depth));
                AddAt("Panel", Vector3.Zero, back, _signPanel, sign.Transform);
            }

            return sign;
        }

        /// <summary>A column of made-up letters, like the signs in a language the city speaks: a few random lines on a 3 by 3 grid each.</summary>
        private static GameObject GlyphSign(string name, int count, Vector3 centre, Vector3 right, Vector3 up, Color color, float cell, Transform parent, bool twoSided = false)
        {
            var strokes = new List<Vector2[]>();
            float step = cell * 1.3f;
            float height = count * step - (step - cell);
            for (int i = 0; i < count; i++)
            {
                var origin = new Vector2(-cell * 0.5f, height * 0.5f - cell - i * step);
                Vector2 Point(int x, int y) => origin + new Vector2(x, y) * (cell * 0.5f);

                // A frame line or two, and some strokes across, down and slanting.
                int lines = 3 + _random.Next(3);
                for (int l = 0; l < lines; l++)
                {
                    int kind = _random.Next(4);
                    int a = _random.Next(3), b = _random.Next(3);
                    strokes.Add(kind switch
                    {
                        0 => [Point(0, a), Point(2, a)],
                        1 => [Point(a, 0), Point(a, 2)],
                        2 => [Point(a, b), Point(Math.Min(a + 1, 2), Math.Max(b - 1, 0))],
                        _ => [Point(0, 2), Point(2, 2), Point(2, a)]
                    });
                }
            }

            return NeonStrokes(name, strokes, cell, height, centre, right, up, color, cell * 0.07f, parent, twoSided, panel: true, frame: true, light: 2f, flicker: null, glow: 4.5f);
        }
    }
}
