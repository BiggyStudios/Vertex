using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Editing
{
    /// <summary>The things the editor can make from nothing: an empty object, simple shapes, lights and a camera.</summary>
    public static class CreateMenu
    {
        /// <summary>
        /// The menu of them, each made under <paramref name="parent"/> (or at the top level). With <paramref name="flat"/>,
        /// the UI pieces are listed in it rather than in a menu of their own: for when this is already a submenu.
        /// </summary>
        public static List<MenuItem> Items(EditorContext context, Transform? parent = null, bool flat = false)
        {
            List<MenuItem> items = AllItems(context, parent);
            if (!flat) return items;

            var flattened = new List<MenuItem>();
            foreach (MenuItem item in items)
            {
                if (item.Children == null) flattened.Add(item);
                else flattened.AddRange(item.Children.Where(child => !child.IsSeparator).Select(child => child with { Label = $"{item.Label} {child.Label}" }));
            }

            return flattened;
        }

        private static List<MenuItem> AllItems(EditorContext context, Transform? parent)
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
                new MenuItem("Camera", () => Make("Camera", gameObject => gameObject.AddComponent<Camera>())) { Icon = Icons.Camera },
                MenuItem.Separator,
                new MenuItem("UI")
                {
                    Icon = Icons.UI,
                    Children =
                    [
                        new MenuItem("Canvas", () => context.Create("Canvas", gameObject => gameObject.AddComponent<UICanvas>())) { Icon = Icons.UI },
                        MenuItem.Separator,
                        new MenuItem("Panel", () => MakeUI(context, "Panel", new Vector2(320f, 220f), gameObject => gameObject.AddComponent<UIPanel>())) { Icon = Icons.UI },
                        new MenuItem("Text", () => MakeUI(context, "Text", new Vector2(240f, 50f), gameObject => gameObject.AddComponent<UIText>())) { Icon = Icons.UI },
                        new MenuItem("Image", () => MakeUI(context, "Image", new Vector2(120f, 120f), gameObject => gameObject.AddComponent<UIImage>())) { Icon = Icons.Image },
                        new MenuItem("Button", () => MakeUI(context, "Button", new Vector2(200f, 50f), gameObject => gameObject.AddComponent<UIButton>())) { Icon = Icons.UI }
                    ]
                }
            ];
        }

        /// <summary>Raised when a piece of UI is made, so the view can show the game's screen, where UI is arranged.</summary>
        public static event Action? MadeUI;

        /// <summary>
        /// Makes a piece of UI: inside the selected piece of UI (or canvas) if there is one, otherwise in the first canvas,
        /// making one if the scene has none.
        /// </summary>
        public static GameObject MakeUI(EditorContext context, string name, Vector2 size, Action<GameObject> build)
        {
            Transform? parent = null;
            for (Transform? t = context.Selection.Object?.Transform; t != null && parent == null; t = t.Parent)
            {
                if (t.GameObject.GetComponent<UIRect>() != null || t.GameObject.GetComponent<UICanvas>() != null) parent = t;
            }

            if (parent == null)
            {
                foreach (GameObject candidate in context.Scene.GameObjects)
                {
                    if (candidate.GetComponent<UICanvas>() != null && context.InScene(candidate))
                    {
                        parent = candidate.Transform;
                        break;
                    }
                }
            }

            var changes = new List<IUndoable>();
            if (parent == null)
            {
                var canvas = new GameObject("Canvas", context.Scene);
                canvas.AddComponent<UICanvas>();
                context.Scene.ApplyChanges();
                changes.Add(new CreateObject(canvas));
                parent = canvas.Transform;
            }

            var made = new GameObject(name, context.Scene);
            UIRect rect = made.AddComponent<UIRect>();
            rect.Size = size;
            build(made);
            made.Transform.Parent = parent;
            context.Scene.ApplyChanges();
            changes.Add(new CreateObject(made));

            context.History.Record(changes.Count == 1 ? changes[0] : new ChangeGroup($"Create {name}", changes));
            context.Selection.Select(made);
            MadeUI?.Invoke();
            return made;
        }
    }
}
