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
                new MenuItem("Spot Light", () => Make("Spot Light", gameObject =>
                {
                    // A few units up, shining down at a slant.
                    Light light = gameObject.AddComponent<Light>();
                    light.Type = LightType.Spot;
                    light.Intensity = 3f;
                    light.Range = 15f;
                    light.CastShadows = false;
                    gameObject.Transform.Position += new Vector3(0f, 4f, 0f);
                    gameObject.Transform.EulerAngles = new Vector3(-70f, 0f, 0f);
                })) { Icon = Icons.Light },
                new MenuItem("Camera", () => Make("Camera", gameObject => gameObject.AddComponent<Camera>())) { Icon = Icons.Camera },
                new MenuItem("Fog Volume", () => Make("Fog Volume", gameObject =>
                {
                    gameObject.AddComponent<FogVolume>();
                    gameObject.Transform.Scale = new Vector3(10f, 4f, 10f);
                })) { Icon = Icons.Fog },
                MenuItem.Separator,
                new MenuItem("Sprite", () => Make("Sprite", gameObject => gameObject.AddComponent<SpriteRenderer>())) { Icon = Icons.Sprite },
                new MenuItem("World Text", () => Make("World Text", gameObject => gameObject.AddComponent<WorldText>())) { Icon = Icons.WorldText },
                new MenuItem("2D Camera", () => Make("2D Camera", gameObject =>
                {
                    // Looking along -Z at the X-Y plane, without perspective, ten units back.
                    Camera camera = gameObject.AddComponent<Camera>();
                    camera.Projection = CameraProjection.Orthographic;
                    camera.ClearColor = new Color(0.16f, 0.17f, 0.2f);
                    gameObject.Transform.Position = gameObject.Transform.Position with { Z = 10f };
                })) { Icon = Icons.Camera },
                MenuItem.Separator,
                new MenuItem("UI")
                {
                    Icon = Icons.UI,
                    Children =
                    [
                        new MenuItem("Canvas", () => context.Create("Canvas", gameObject => gameObject.AddComponent<UICanvas>())) { Icon = Icons.UI },
                        MenuItem.Separator,
                        new MenuItem("Panel", () => MakeUI(context, "Panel", new Vector2(320f, 220f), gameObject => gameObject.AddComponent<UIPanel>())) { Icon = Icons.Panel },
                        new MenuItem("Text", () => MakeUI(context, "Text", new Vector2(240f, 50f), gameObject => gameObject.AddComponent<UIText>())) { Icon = Icons.Letter },
                        new MenuItem("Image", () => MakeUI(context, "Image", new Vector2(120f, 120f), gameObject => gameObject.AddComponent<UIImage>())) { Icon = Icons.Image },
                        MenuItem.Separator,
                        new MenuItem("Button", () => MakeUI(context, "Button", new Vector2(200f, 50f), gameObject => gameObject.AddComponent<UIButton>())) { Icon = Icons.Button },
                        new MenuItem("Toggle", () => MakeUI(context, "Toggle", new Vector2(200f, 32f), gameObject => gameObject.AddComponent<UIToggle>())) { Icon = Icons.Toggle },
                        new MenuItem("Slider", () => MakeUI(context, "Slider", new Vector2(240f, 28f), gameObject => gameObject.AddComponent<UISlider>())) { Icon = Icons.Slider },
                        new MenuItem("Progress Bar", () => MakeUI(context, "Progress Bar", new Vector2(240f, 20f), gameObject => gameObject.AddComponent<UIProgressBar>())) { Icon = Icons.ProgressBar },
                        new MenuItem("Input Field", () => MakeUI(context, "Input Field", new Vector2(260f, 40f), gameObject => gameObject.AddComponent<UIInputField>())) { Icon = Icons.TextBox },
                        MenuItem.Separator,
                        new MenuItem("Vertical Layout", () => MakeUI(context, "Vertical Layout", new Vector2(240f, 300f), gameObject => gameObject.AddComponent<UIVerticalLayout>())) { Icon = Icons.Column },
                        new MenuItem("Horizontal Layout", () => MakeUI(context, "Horizontal Layout", new Vector2(400f, 60f), gameObject => gameObject.AddComponent<UIHorizontalLayout>())) { Icon = Icons.Row },
                        new MenuItem("Grid Layout", () => MakeUI(context, "Grid Layout", new Vector2(360f, 360f), gameObject => gameObject.AddComponent<UIGridLayout>())) { Icon = Icons.GridLayout },
                        new MenuItem("Mask", () => MakeUI(context, "Mask", new Vector2(300f, 200f), gameObject => gameObject.AddComponent<UIMask>())) { Icon = Icons.Mask }
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
