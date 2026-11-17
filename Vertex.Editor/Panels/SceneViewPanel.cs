using System.Numerics;

using Vertex.Editor.Gui;
using Vertex.Editor.SceneEditing;
using Vertex.Input;
using Vertex.Rendering;
using Vertex.UI;

namespace Vertex.Editor.Panels
{
    /// <summary>
    /// The scene, seen through the editor's own camera, in the middle of the window. Click to select, drag the handles to
    /// move, turn and scale; see <see cref="EditorCamera"/> for getting around. Lights and cameras, which have nothing to
    /// see, show as icons that can be clicked.
    /// </summary>
    public sealed class SceneViewPanel
    {
        private readonly EditorContext _context;
        private readonly TransformHandles _handles = new();
        private RenderTexture? _texture;

        private bool _looking, _panning, _orbiting;
        private bool _pickPending;
        private Vector2 _pressedAt;
        private float _speedChangedAt = -10f;

        public EditorCamera Camera { get; }

        /// <summary>Whether the right mouse is held to look and fly, so WASD belong to the camera, not shortcuts.</summary>
        public bool Looking => _looking;

        /// <summary>The picture for the renderer to draw this frame, or null while the view has no room.</summary>
        public RenderView? View => _texture != null ? new RenderView(Camera.Camera, _texture, ShowGrid) : null;

        /// <summary>Whether the grid on the ground shows. Toggled from the view's toolbar.</summary>
        public bool ShowGrid { get; set; } = true;

        public SceneViewPanel(EditorContext context)
        {
            _context = context;

            // The camera lives in a scene of the editor's own, which is never run or drawn by itself.
            Camera = new EditorCamera(new Scene());
        }

        public void Draw(Rect rect)
        {
            Ui.Rect(rect, Theme.Background, Theme.PanelRadius);
            Rect viewport = rect;

            // Drawn into a texture the size of the view, in real pixels.
            int width = (int)MathF.Round(viewport.Width * Canvas.Scale), height = (int)MathF.Round(viewport.Height * Canvas.Scale);
            if (width > 0 && height > 0)
            {
                if (_texture == null) _texture = new RenderTexture(width, height);
                else _texture.Resize(width, height);
            }

            int id = Ui.Id("sceneView");
            Interaction interaction = Ui.Interact(id, viewport);
            bool hovered = interaction.Hovered || Ui.HotId == id;

            HandleCamera(id, viewport, hovered);
            Camera.Update(Ui.DeltaTime);
            _context.SpawnPoint = Round(Camera.Pivot, 0.5f);

            // This frame's picture: the renderer draws the views before the 2D layer.
            if (_texture != null) Canvas.DrawImage(_texture, viewport.Position, viewport.Size, Vector2.Zero, Vector2.One, Color.White, Theme.PanelRadius);
            Ui.Outline(rect, Theme.Border, Theme.PanelRadius);

            var view = new ViewProjection(Camera, viewport);
            Ui.PushClip(viewport);

            GameObject? selected = _context.Selection.Object;
            if (selected != null && !_context.InScene(selected)) selected = null;
            if (selected != null) DrawSelectionOutline(view, selected);

            GameObject? clickedIcon = DrawObjectIcons(view, hovered && !_looking);

            bool mouseFree = hovered && !_looking && !_panning && !_orbiting && !Alt;
            bool handleTookPress = selected != null && _handles.Draw(selected.Transform, _context.Tool, view, _context.History, mouseFree);

            // A click (pressed and let go without dragging) selects what's under it.
            if (mouseFree && Ui.MousePressed() && !handleTookPress)
            {
                _pickPending = true;
                _pressedAt = Ui.Mouse;
            }

            if (_pickPending && Ui.MouseReleased())
            {
                _pickPending = false;
                if (Vector2.Distance(_pressedAt, Ui.Mouse) < 5f) Pick(view, clickedIcon);
            }

            if (!Ui.MouseHeld() && !Ui.MouseReleased()) _pickPending = false;

            Ui.PopClip();

            DrawToolbar(viewport);
            DrawAxes(viewport);
            DrawSpeed(viewport);
        }

        private static bool Alt => InputSystem.GetKey(Key.AltLeft) || InputSystem.GetKey(Key.AltRight);

        private static Vector3 Round(Vector3 v, float step) =>
            new(MathF.Round(v.X / step) * step, MathF.Round(v.Y / step) * step, MathF.Round(v.Z / step) * step);

        // ---------------------------------------------------------------- Getting around

        private void HandleCamera(int id, Rect viewport, bool hovered)
        {
            const float lookSpeed = 0.16f;

            // Right mouse: look around and fly.
            if (hovered && Ui.MousePressed(MouseButton.Right) && !Menu.IsAnyOpen)
            {
                _looking = true;
                Ui.ActiveId = id;
                InputSystem.CursorLocked = true;
            }

            if (_looking)
            {
                if (!Ui.MouseHeld(MouseButton.Right))
                {
                    _looking = false;
                    InputSystem.CursorLocked = false;
                    Camera.EndLook();
                }
                else
                {
                    Ui.ActiveId = id;
                    if (Alt) Camera.Zoom(-Ui.MouseDelta.Y * 0.02f);
                    else Camera.Look(-Ui.MouseDelta.X * lookSpeed, -Ui.MouseDelta.Y * lookSpeed);

                    var direction = new Vector3(
                        (Held(Key.D) ? 1f : 0f) - (Held(Key.A) ? 1f : 0f),
                        (Held(Key.E) ? 1f : 0f) - (Held(Key.Q) ? 1f : 0f),
                        (Held(Key.S) ? 1f : 0f) - (Held(Key.W) ? 1f : 0f));
                    Camera.Fly(direction, Ui.DeltaTime, Held(Key.ShiftLeft) || Held(Key.ShiftRight));

                    if (InputSystem.ScrollDelta != 0f)
                    {
                        Camera.FlySpeed = Math.Clamp(Camera.FlySpeed * MathF.Pow(1.25f, InputSystem.ScrollDelta), 0.5f, 500f);
                        _speedChangedAt = Ui.Time;
                    }
                }
            }

            // Middle mouse: pan.
            if (hovered && Ui.MousePressed(MouseButton.Middle)) _panning = true;
            if (_panning)
            {
                if (!Ui.MouseHeld(MouseButton.Middle)) _panning = false;
                else
                {
                    Ui.ActiveId = id;
                    Camera.Pan(Ui.MouseDelta, viewport.Height);
                }
            }

            // Alt and left mouse: orbit.
            if (hovered && Alt && Ui.MousePressed()) _orbiting = true;
            if (_orbiting)
            {
                if (!Ui.MouseHeld()) _orbiting = false;
                else
                {
                    Ui.ActiveId = id;
                    Camera.Orbit(-Ui.MouseDelta.X * 0.3f, -Ui.MouseDelta.Y * 0.3f);
                }
            }

            // The wheel: zoom.
            if (hovered && !_looking && InputSystem.ScrollDelta != 0f) Camera.Zoom(InputSystem.ScrollDelta);
        }

        private static bool Held(Key key) => InputSystem.GetKey(key);

        /// <summary>Flies to frame the selected object: everything it and its children draw.</summary>
        public void FrameSelection()
        {
            if (_context.Selection.Object is not { } selected || !_context.InScene(selected)) return;

            (Vector3 Min, Vector3 Max)? bounds = Bounds(selected);
            if (bounds is (Vector3 min, Vector3 max)) Camera.Frame((min + max) * 0.5f, Vector3.Distance(min, max) * 0.5f);
            else Camera.Frame(selected.Transform.WorldPosition, 1f);
        }

        private static (Vector3 Min, Vector3 Max)? Bounds(GameObject gameObject)
        {
            (Vector3, Vector3)? bounds = null;
            if (gameObject.GetComponent<MeshRenderer>() is { Mesh: { } mesh })
            {
                Culling.WorldBounds(mesh, gameObject.Transform.WorldMatrix, out Vector3 centre, out Vector3 extents);
                bounds = (centre - extents, centre + extents);
            }

            foreach (Transform child in gameObject.Transform.Children)
            {
                if (Bounds(child.GameObject) is not (Vector3 childMin, Vector3 childMax)) continue;
                bounds = bounds is (Vector3 min, Vector3 max) ? (Vector3.Min(min, childMin), Vector3.Max(max, childMax)) : (childMin, childMax);
            }

            return bounds;
        }

        // ---------------------------------------------------------------- Selecting

        private void Pick(in ViewProjection view, GameObject? clickedIcon)
        {
            if (clickedIcon != null)
            {
                _context.Selection.Select(clickedIcon);
                return;
            }

            (Vector3 origin, Vector3 direction) = view.Ray(Ui.Mouse);
            if (Picking.Pick(_context.Scene, origin, direction) is (GameObject hit, _))
            {
                // Clicking a part of what's selected again picks the part; otherwise the top of what was clicked
                // isn't assumed: the object hit is selected.
                _context.Selection.Select(hit);
            }
            else
            {
                _context.Selection.Clear();
            }
        }

        private static void DrawSelectionOutline(ViewProjection view, GameObject selected)
        {
            void Box(GameObject gameObject, Color color)
            {
                if (gameObject.GetComponent<MeshRenderer>() is { Mesh: { } mesh })
                {
                    Matrix4x4 world = gameObject.Transform.WorldMatrix;
                    Vector3 c = mesh.BoundsCentre, e = mesh.BoundsExtents;
                    var corners = new Vector3[8];
                    for (int i = 0; i < 8; i++)
                    {
                        var local = new Vector3(i % 2 == 0 ? -e.X : e.X, i / 2 % 2 == 0 ? -e.Y : e.Y, i / 4 == 0 ? -e.Z : e.Z);
                        corners[i] = Vector3.Transform(c + local, world);
                    }

                    // The twelve edges: corners that differ in one axis.
                    for (int a = 0; a < 8; a++)
                    {
                        foreach (int bit in (ReadOnlySpan<int>)[1, 2, 4])
                        {
                            if ((a & bit) == 0) view.Line(corners[a], corners[a | bit], color, 1.5f);
                        }
                    }
                }

                foreach (Transform child in gameObject.Transform.Children) Box(child.GameObject, color.WithAlpha(0.4f));
            }

            float pulse = 0.75f + 0.25f * MathF.Sin(Ui.Time * 3f);
            Box(selected, Theme.Accent.WithAlpha(pulse));
        }

        /// <summary>Icons for lights and cameras. Returns the one under the mouse, if any.</summary>
        private GameObject? DrawObjectIcons(in ViewProjection view, bool mouseFree)
        {
            GameObject? under = null;
            foreach (GameObject gameObject in _context.Scene.GameObjects)
            {
                if (!gameObject.ActiveInHierarchy) continue;
                Light? light = gameObject.GetComponent<Light>();
                Camera? camera = gameObject.GetComponent<Camera>();
                if (light == null && camera == null) continue;

                Vector3 position = gameObject.Transform.WorldPosition;
                if (view.ToScreen(position) is not Vector2 at) continue;

                bool selected = _context.Selection.Object == gameObject;
                bool hovered = mouseFree && Vector2.Distance(at, Ui.Mouse) < 13f;
                if (hovered) under = gameObject;

                Color tint = selected ? Theme.AccentText : light != null ? Color.Lerp(light.Color, Color.White, 0.4f) : Theme.Text;

                // Which way a sun shines.
                if (light is { Type: LightType.Directional })
                {
                    float metres = view.MetresPerUnit(position);
                    view.Line(position, position + gameObject.Transform.Forward * 46f * metres, tint.WithAlpha(0.7f), 1.5f);
                }

                if (camera != null && selected) DrawFrustum(view, camera);

                Ui.Circle(at, hovered || selected ? 13f : 12f, Theme.Popup.WithAlpha(0.9f));
                Vertex.UI.Canvas.DrawCircleOutline(at, hovered || selected ? 13f : 12f, Ui.Fade(selected ? Theme.Accent : Theme.BorderStrong), 1.5f);
                if (light != null) Icons.Light(at, 18f, tint);
                else Icons.Camera(at, 18f, tint);
            }

            return under;
        }

        private static void DrawFrustum(in ViewProjection view, Camera camera)
        {
            Transform t = camera.Transform;
            float depth = MathF.Min(camera.FarPlane, 4f);
            float halfHeight = MathF.Tan(camera.FieldOfView * MathF.PI / 360f) * depth;
            float halfWidth = halfHeight * 16f / 9f;
            Vector3 eye = t.WorldPosition;
            Vector3 centre = eye + t.Forward * depth;
            Vector3[] corners =
            [
                centre + t.Right * halfWidth + t.Up * halfHeight, centre - t.Right * halfWidth + t.Up * halfHeight,
                centre - t.Right * halfWidth - t.Up * halfHeight, centre + t.Right * halfWidth - t.Up * halfHeight
            ];

            Color color = Color.White.WithAlpha(0.55f);
            for (int i = 0; i < 4; i++)
            {
                view.Line(eye, corners[i], color, 1f);
                view.Line(corners[i], corners[(i + 1) % 4], color, 1f);
            }
        }

        // ---------------------------------------------------------------- Overlays

        private void DrawToolbar(Rect viewport)
        {
            // Move, turn and scale, and world or local axes, floating in the corner.
            var bar = new Rect(viewport.X + 10f, viewport.Y + 10f, 3f * 30f + 8f + 62f + 9f + 30f, 36f);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.92f), 8f);
            Ui.Outline(bar, Theme.Border, 8f);
            Ui.Block(Ui.Id("sceneView.toolbar"), bar);

            (TransformTool Tool, Icon Icon, string Tip)[] tools =
            [
                (TransformTool.Move, Icons.Move, "Move (W)"),
                (TransformTool.Rotate, Icons.Rotate, "Rotate (E)"),
                (TransformTool.Scale, Icons.Scale, "Scale (R)")
            ];

            int selectedIndex = Array.FindIndex(tools, tool => tool.Tool == _context.Tool);
            float slide = Ui.Smooth(Ui.Id("sceneView.toolHighlight"), selectedIndex * 30f, 22f);
            Ui.Rect(new Rect(bar.X + 4f + slide, bar.Y + 4f, 28f, 28f), Theme.Accent.WithAlpha(0.9f), 6f);

            for (int i = 0; i < tools.Length; i++)
            {
                var button = new Rect(bar.X + 4f + i * 30f, bar.Y + 4f, 28f, 28f);
                bool on = i == selectedIndex;
                if (Widgets.IconButton(Ui.Id("sceneView.tool" + i), button, tools[i].Icon, tools[i].Tip, color: on ? Color.White : null)) _context.Tool = tools[i].Tool;
            }

            Ui.Rect(new Rect(bar.X + 4f + 3f * 30f + 3f, bar.Y + 9f, 1f, 18f), Theme.Border);

            var space = new Rect(bar.X + 4f + 3f * 30f + 8f, bar.Y + 4f, 58f, 28f);
            bool local = _handles.Space == HandleSpace.Local;
            string tip = _context.Tool == TransformTool.Scale ? "Scaling always uses the object's own axes" : local
                ? "Handles follow the object's own axes: click for the world's"
                : "Handles follow the world's axes: click for the object's own";
            if (Widgets.Button(Ui.Id("sceneView.space"), space, local ? "Local" : "World", ButtonStyle.Subtle, tip))
            {
                _handles.Space = local ? HandleSpace.World : HandleSpace.Local;
            }

            Ui.Rect(new Rect(space.Right + 4f, bar.Y + 9f, 1f, 18f), Theme.Border);
            var grid = new Rect(space.Right + 9f, bar.Y + 4f, 28f, 28f);
            if (Widgets.IconButton(Ui.Id("sceneView.grid"), grid, Icons.Grid, ShowGrid ? "Hide the grid" : "Show the grid", selected: ShowGrid)) ShowGrid = !ShowGrid;
        }

        private void DrawAxes(Rect viewport)
        {
            // Which way the world's axes point from here. Click one to look along it.
            var centre = new Vector2(viewport.Right - 52f, viewport.Y + 52f);
            const float radius = 30f;
            int id = Ui.Id("sceneView.axes");
            var area = new Rect(centre.X - 44f, centre.Y - 44f, 88f, 88f);
            Interaction areaInteraction = Ui.Interact(id, area);
            float glow = Ui.Smooth(id, areaInteraction.Hovered ? 1f : 0f, 14f);
            if (glow > 0.01f) Ui.Circle(centre, 42f, Theme.Panel.WithAlpha(0.6f * glow));

            Transform camera = Camera.Camera.Transform;
            Vector3 right = camera.Right, up = camera.Up, forward = camera.Forward;

            var ends = new List<(Vector3 Axis, Color Color, string Label, float Depth)>();
            foreach ((Vector3 axis, Color color, string label) in new[] { (Vector3.UnitX, Theme.AxisX, "X"), (Vector3.UnitY, Theme.AxisY, "Y"), (Vector3.UnitZ, Theme.AxisZ, "Z") })
            {
                ends.Add((axis, color, label, Vector3.Dot(axis, forward)));
                ends.Add((-axis, color, "", Vector3.Dot(-axis, forward)));
            }

            // Furthest first, so nearer ends are drawn over them.
            ends.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            Vector3? clicked = null;
            foreach ((Vector3 axis, Color color, string label, float _) in ends)
            {
                Vector2 at = centre + new Vector2(Vector3.Dot(axis, right), -Vector3.Dot(axis, up)) * radius;
                bool positive = label.Length > 0;
                float size = positive ? 9f : 6.5f;
                Interaction interaction = Ui.Interact(Ui.Id(id, label + axis), new Rect(at.X - size, at.Y - size, size * 2f, size * 2f));
                if (interaction.Clicked) clicked = axis;

                Color shown = interaction.Hovered ? Color.Lerp(color, Color.White, 0.35f) : color;
                if (positive)
                {
                    Ui.Line(centre, at, shown, 2f);
                    Ui.Circle(at, size, shown);
                    Ui.Text(new Rect(at.X - size, at.Y - size, size * 2f, size * 2f), label, Theme.Background, align: 0.5f, size: 10.5f, bold: true);
                }
                else
                {
                    Ui.Circle(at, size, shown.WithAlpha(0.35f));
                    Vertex.UI.Canvas.DrawCircleOutline(at, size, Ui.Fade(shown.WithAlpha(0.8f)), 1.3f);
                }

                Ui.Tooltip(Ui.Id(id, label + axis), positive ? $"Look along -{label}" : null);
            }

            // Looking from that axis's end, back towards the middle.
            if (clicked is Vector3 from) Camera.LookAlong(-from);
        }

        private void DrawSpeed(Rect viewport)
        {
            float shown = 1f - Math.Clamp((Ui.Time - _speedChangedAt - 0.9f) / 0.35f, 0f, 1f);
            if (shown <= 0f) return;

            string text = $"Fly speed  {Camera.FlySpeed:0.#} m/s";
            float width = Ui.TextWidth(text) + 24f;
            var chip = new Rect(viewport.Centre.X - width * 0.5f, viewport.Bottom - 46f, width, 28f);
            Ui.Opacity = shown;
            Ui.Rect(chip, Theme.Panel.WithAlpha(0.9f), 14f);
            Ui.Text(chip, text, Theme.Text, align: 0.5f);
            Ui.Opacity = 1f;
        }
    }
}
