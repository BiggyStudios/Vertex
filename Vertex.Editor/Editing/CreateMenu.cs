using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Rendering;

namespace Vertex.Editor.Editing
{
    /// <summary>The things the editor can make from nothing: an empty object, simple shapes, lights and a camera.</summary>
    public static class CreateMenu
    {
        /// <summary>The menu of them, each made under <paramref name="parent"/> (or at the top level) at <paramref name="at"/>.</summary>
        public static List<MenuItem> Items(EditorContext context, Transform? parent = null)
        {
            Vector3 at = parent == null ? context.SpawnPoint : Vector3.Zero;

            GameObject Make(string name, Action<GameObject>? build = null) => context.Create(name, gameObject =>
            {
                gameObject.Transform.Position = at;
                build?.Invoke(gameObject);
            }, parent);

            void Shape(string name, Mesh mesh, Vector3? scale = null) => Make(name, gameObject =>
            {
                gameObject.AddComponent<MeshRenderer>().Mesh = mesh;
                if (scale is Vector3 s) gameObject.Transform.Scale = s;
            });

            return
            [
                new MenuItem("Empty", () => Make("Empty")) { Icon = Icons.Empty },
                MenuItem.Separator,
                new MenuItem("Cube", () => Shape("Cube", Primitives.Cube)) { Icon = Icons.Cube },
                new MenuItem("Sphere", () => Shape("Sphere", Primitives.Sphere)) { Icon = Icons.Cube },
                new MenuItem("Plane", () => Shape("Plane", Primitives.Plane, new Vector3(10f, 1f, 10f))) { Icon = Icons.Cube },
                MenuItem.Separator,
                new MenuItem("Sun", () => Make("Sun", gameObject =>
                {
                    gameObject.AddComponent<Light>();
                    gameObject.Transform.EulerAngles = new Vector3(-50f, 30f, 0f);
                })) { Icon = Icons.Light },
                new MenuItem("Point Light", () => Make("Point Light", gameObject =>
                {
                    Light light = gameObject.AddComponent<Light>();
                    light.Type = LightType.Point;
                    light.Intensity = 2f;
                    light.CastShadows = false;
                })) { Icon = Icons.Light },
                new MenuItem("Camera", () => Make("Camera", gameObject => gameObject.AddComponent<Camera>())) { Icon = Icons.Camera }
            ];
        }
    }
}
