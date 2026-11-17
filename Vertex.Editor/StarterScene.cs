using System.Numerics;

using Vertex.Rendering;

namespace Vertex.Editor
{
    /// <summary>What the editor opens with, until it can open saved scenes: a sun, a floor, and a few shapes to try things on.</summary>
    public static class StarterScene
    {
        public static void Build(EditorContext context)
        {
            Scene scene = context.Scene;

            var sun = new GameObject("Sun", scene);
            sun.AddComponent<Light>().Intensity = 1.1f;
            sun.Transform.EulerAngles = new Vector3(-48f, 35f, 0f);

            var floor = new GameObject("Floor", scene);
            floor.Transform.Scale = new Vector3(40f, 1f, 40f);
            floor.AddComponent<MeshRenderer>().Mesh = Primitives.Plane;
            floor.GetComponent<MeshRenderer>()!.Material = new Material(new Color(0.52f, 0.54f, 0.56f)) { Roughness = 0.85f };

            var shapes = new GameObject("Shapes", scene);

            GameObject Shape(string name, Mesh mesh, Vector3 position, Color color, float roughness = 0.5f, float metallic = 0f)
            {
                var gameObject = new GameObject(name, scene);
                gameObject.Transform.Parent = shapes.Transform;
                gameObject.Transform.Position = position;
                var renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.Mesh = mesh;
                renderer.Material = new Material(color) { Roughness = roughness, Metallic = metallic };
                return gameObject;
            }

            Shape("Cube", Primitives.Cube, new Vector3(-2f, 0.5f, 0f), Color.FromBytes(226, 109, 90));
            GameObject sphere = Shape("Sphere", Primitives.Sphere, new Vector3(0.5f, 0.5f, 1.5f), Color.FromBytes(84, 150, 230), roughness: 0.2f);
            sphere.Transform.Scale = new Vector3(1f);
            GameObject tower = Shape("Tower", Primitives.Cube, new Vector3(2.5f, 1.5f, -1.5f), Color.FromBytes(230, 190, 90), roughness: 0.35f, metallic: 1f);
            tower.Transform.Scale = new Vector3(1f, 3f, 1f);
            tower.Transform.EulerAngles = new Vector3(0f, 25f, 0f);

            var lamp = new GameObject("Lamp", scene);
            lamp.Transform.Position = new Vector3(-0.5f, 2.2f, -2f);
            Light light = lamp.AddComponent<Light>();
            light.Type = LightType.Point;
            light.Color = Color.FromBytes(255, 170, 90);
            light.Intensity = 3f;
            light.Range = 6f;
            light.CastShadows = false;

            var camera = new GameObject("Main Camera", scene);
            camera.Transform.Position = new Vector3(0f, 3f, 9f);
            camera.Transform.EulerAngles = new Vector3(-15f, 0f, 0f);
            camera.AddComponent<Camera>();

            context.SceneName = "Starter Scene";
        }
    }
}
