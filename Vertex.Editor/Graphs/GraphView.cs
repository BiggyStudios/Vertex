using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;

namespace Vertex.Editor.Graphs
{
    /// <summary>
    /// Shows a graph to edit, for any kind of graph (visual scripts now; shaders, audio and animation later): nodes as
    /// cards with their inputs on the left and outputs on the right, links as curves between them, on a grid that pans
    /// and zooms.
    /// <list type="bullet">
    /// <item>Right-click (or Space) on empty space: add a node, searched for by name.</item>
    /// <item>Drag from a port to another to link them; let go on empty space to add a node already linked. Drag a
    /// linked input away to unlink it.</item>
    /// <item>Click or drag a box to select nodes (Ctrl or Shift adds); drag them to move them.</item>
    /// <item>Delete, Ctrl+D (duplicate), Ctrl+C and Ctrl+V, Ctrl+A, F (frame), all undoable.</item>
    /// <item>Middle or right drag to pan, the wheel to zoom.</item>
    /// </list>
    /// While the game runs, nodes light up as they run, and one that went wrong is outlined red.
    /// </summary>
    public sealed class GraphView
    {
        private readonly EditorContext _context;

        private const float _header = 28f;
        private const float _row = 24f;
        private const float _minZoom = 0.25f, _maxZoom = 2f;

        private sealed class Layout
        {
            public required GraphNode Node;
            public required NodeType Type;
            public Rect Rect;
            public readonly Dictionary<string, Vector2> Inputs = new();
            public readonly Dictionary<string, Vector2> Outputs = new();
        }

        private GraphDocument? _doc;
        private readonly Dictionary<int, Layout> _layouts = new();
        private bool _focused;

        // Moving nodes: where the drag started, where each was, and the graph before.
        private bool _dragging;
        private bool _pressedNode;
        private Vector2 _dragFrom;
        private readonly Dictionary<int, Vector2> _dragOrigins = new();
        private string? _dragBefore;

        // Selecting with a box: where it started, in the graph.
        private Vector2? _boxFrom;

        // A link being dragged out of a port: which, and whether from an output. An input's link picked up to move it is
        // kept, to take away when it's let go.
        private (int Node, string Port, bool FromOutput)? _wire;
        private GraphLink? _pickedUp;

        // Panning with the middle or right button; a right press that doesn't move opens the add menu.
        private bool _panning;
        private bool _rightDown;
        private Vector2 _rightAt;

        // Adding a node: where it goes, and the link it'll be joined to, if it was asked for by letting go of one.
        private string _search = "";
        private int _highlighted;
        private Vector2 _addAt;
        private (int Node, string Port, bool FromOutput)? _addLinked;

        // A key setting waiting for a key to be pressed.
        private int _capturingKey;

        private bool _showVariables = true;

        public GraphView(EditorContext context)
        {
            _context = context;
        }

        private static bool Ctrl => InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight);
        private static bool Shift => InputSystem.GetKey(Key.ShiftLeft) || InputSystem.GetKey(Key.ShiftRight);

        // ---------------------------------------------------------------- Drawing

        public void Draw(Rect rect, GraphDocument doc)
        {
            if (_doc != doc)
            {
                _doc = doc;
                _dragging = _pressedNode = _panning = _rightDown = false;
                _wire = null;
                _boxFrom = null;
                _layouts.Clear();
            }

            Graph graph = doc.Graph;
            Ui.Rect(rect, Theme.Hex(0x16171A));
            if (graph.Library is not { } library)
            {
                Ui.Text(rect, $"Graphs of kind \"{graph.Kind}\" can't be edited yet", Theme.TextDim, align: 0.5f);
                return;
            }

            int id = Ui.Id(RuntimeHelpers.GetHashCode(doc), "graphView");
            Interaction background = Ui.Interact(id, rect, rightClick: true);
            bool over = rect.Contains(Ui.Mouse) && Vertex.UI.Canvas.InClip(Ui.Mouse) && !Menu.IsAnyOpen && !Popup.AnyOpen && !Dialog.IsOpen;
            if (Ui.MousePressed() || Ui.MousePressed(MouseButton.Right) || Ui.MousePressed(MouseButton.Middle))
            {
                if (!Menu.IsAnyOpen && !Popup.AnyOpen) _focused = over;
            }

            _rect = rect;
            Lay(graph, library);
            if (!doc.Framed && rect.Width > 50f)
            {
                doc.Framed = true;
                Frame(doc, rect, selectedOnly: false);
            }

            PanAndZoom(rect, doc, over, background, id);

            Ui.PushView(rect, doc.Offset, doc.Zoom);
            DrawGrid(new Rect(doc.Offset.X, doc.Offset.Y, rect.Width / doc.Zoom, rect.Height / doc.Zoom), doc.Zoom);

            // A press on empty space: a box to select with (what was selected stays only with Ctrl or Shift).
            if (background.Pressed)
            {
                if (!Ctrl && !Shift) doc.Selected.Clear();
                _boxFrom = Ui.Mouse;
            }

            DrawLinks(graph, library);
            DrawNodes(id, doc, library);
            UpdateWire(doc, library, id);
            UpdateNodeDrag(doc);
            UpdateBox(doc);
            Ui.PopView();

            DrawToolbar(rect, doc, library);
            if (_showVariables) DrawVariables(rect, doc);
            if (graph.Nodes.Count == 0)
            {
                Ui.Text(new Rect(rect.X, rect.Centre.Y - 10f, rect.Width, 20f), "Right-click or press Space to add a node", Theme.TextFaint, align: 0.5f);
            }

            DrawAddPopup(id, doc, library);
            if (_focused && !Ui.KeyboardTaken && !Menu.IsAnyOpen && !Popup.AnyOpen && !Dialog.IsOpen) HandleKeys(rect, doc, id);
        }

        /// <summary>Works out where every node and port is, and how big each node is.</summary>
        private void Lay(Graph graph, NodeLibrary library)
        {
            _layouts.Clear();
            foreach (GraphNode node in graph.Nodes)
            {
                if (library.Find(node.Type) is not { } type) continue;

                float left = 0f, right = 0f;
                int rows = Math.Max(Math.Max(type.Inputs.Count, type.Outputs.Count), 1);
                for (int i = 0; i < rows; i++)
                {
                    float l = 0f, r = 0f;
                    if (i < type.Inputs.Count)
                    {
                        Port port = type.Inputs[i];
                        l = 14f + LabelWidth(port);
                        if (ShowsEditor(graph, node, port)) l += 6f + EditorWidth(port);
                    }

                    if (i < type.Outputs.Count) r = LabelWidth(type.Outputs[i]) + 14f;
                    left = MathF.Max(left, l);
                    right = MathF.Max(right, r);
                }

                float width = MathF.Max(MathF.Max(150f, Ui.TextWidth(type.Title, Theme.TextSize, bold: true) + 40f), left + right + 18f);
                var layout = new Layout
                {
                    Node = node,
                    Type = type,
                    Rect = new Rect(node.Position.X, node.Position.Y, width, _header + rows * _row + 8f)
                };

                for (int i = 0; i < type.Inputs.Count; i++) layout.Inputs[type.Inputs[i].Name] = new Vector2(layout.Rect.X, RowCentre(layout.Rect, i));
                for (int i = 0; i < type.Outputs.Count; i++) layout.Outputs[type.Outputs[i].Name] = new Vector2(layout.Rect.Right, RowCentre(layout.Rect, i));
                _layouts[node.Id] = layout;
            }
        }

        private Rect _rect;

        /// <summary>Where a port was drawn last frame, in ordinary UI units: for tests to drag between.</summary>
        internal Vector2? PortOnScreen(GraphDocument doc, int node, string port, bool output)
        {
            if (!_layouts.TryGetValue(node, out Layout? layout)) return null;
            if (!(output ? layout.Outputs : layout.Inputs).TryGetValue(port, out Vector2 at)) return null;
            return _rect.Position + (at - doc.Offset) * doc.Zoom;
        }

        private static float RowCentre(Rect node, int row) => node.Y + _header + 4f + row * _row + _row * 0.5f;

        // Flow ports called In and Then go without saying.
        private static string Label(Port port) => port.Type.IsFlow && port.Name is "In" or "Then" ? "" : port.Name;

        private static float LabelWidth(Port port) => Label(port) is { Length: > 0 } label ? Ui.TextWidth(label, Theme.SmallTextSize) : 0f;

        private static bool ShowsEditor(Graph graph, GraphNode node, Port port) =>
            !port.Type.IsFlow && EditorWidth(port) > 0f && (!port.Linkable || !graph.LinksTo(node.Id, port.Name).Any());

        private static float EditorWidth(Port port)
        {
            if (port.IsVariableName) return 110f;
            if (port.Type == PortType.Vector) return 150f;
            if (port.Type == PortType.Number) return 56f;
            if (port.Type == PortType.Boolean) return 18f;
            if (port.Type == PortType.Text || port.Type == PortType.Any) return 110f;
            if (port.Type.ValueType is { IsEnum: true }) return 100f;
            if (port.Type == PortType.Object) return 30f;
            return 0f;
        }

        private static void DrawGrid(Rect visible, float zoom)
        {
            float thin = 1f / zoom;
            void Lines(float step, Color color)
            {
                for (float x = MathF.Floor(visible.X / step) * step; x < visible.Right; x += step) Ui.Rect(new Rect(x, visible.Y, thin, visible.Height), color);
                for (float y = MathF.Floor(visible.Y / step) * step; y < visible.Bottom; y += step) Ui.Rect(new Rect(visible.X, y, visible.Width, thin), color);
            }

            if (zoom > 0.5f) Lines(24f, new Color(1f, 1f, 1f, 0.025f));
            Lines(120f, new Color(1f, 1f, 1f, 0.05f));
        }

        // ---------------------------------------------------------------- Links

        private void DrawLinks(Graph graph, NodeLibrary library)
        {
            double now = Graph.Clock;
            foreach (GraphLink link in graph.Links)
            {
                if (!_layouts.TryGetValue(link.FromNode, out Layout? from) || !_layouts.TryGetValue(link.ToNode, out Layout? to)) continue;
                if (!from.Outputs.TryGetValue(link.FromPort, out Vector2 a) || !to.Inputs.TryGetValue(link.ToPort, out Vector2 b)) continue;
                if (_pickedUp == link) continue;

                PortType type = from.Type.Output(link.FromPort)!.Type;
                Color color = type.Color;

                // Flow that's just gone along it shows.
                if (type.IsFlow && graph.LastRan(link.ToNode) is double ran && now - ran < 0.5)
                {
                    color = Color.Lerp(color, Theme.Hex(0x5FC9B5), (float)(1.0 - (now - ran) / 0.5));
                }

                Wire(a, b, color, type.IsFlow ? 2.6f : 2f);
            }
        }

        private static void Wire(Vector2 from, Vector2 to, Color color, float thickness)
        {
            float reach = MathF.Max(MathF.Abs(to.X - from.X) * 0.5f, 40f);
            Vector2 c1 = from + new Vector2(reach, 0f), c2 = to - new Vector2(reach, 0f);
            Span<Vector2> points = stackalloc Vector2[28];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (points.Length - 1f), u = 1f - t;
                points[i] = u * u * u * from + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * to;
            }

            Vertex.UI.Canvas.DrawPolyline(points, thickness, Ui.Fade(color));
        }

        // ---------------------------------------------------------------- Nodes

        private void DrawNodes(int viewId, GraphDocument doc, NodeLibrary library)
        {
            // Selected ones on top.
            foreach (Layout layout in _layouts.Values.OrderBy(l => doc.Selected.Contains(l.Node.Id)))
            {
                DrawNode(viewId, doc, library, layout);
            }
        }

        private void DrawNode(int viewId, GraphDocument doc, NodeLibrary library, Layout layout)
        {
            Graph graph = doc.Graph;
            GraphNode node = layout.Node;
            NodeType type = layout.Type;
            Rect r = layout.Rect;
            int nodeId = Ui.Id(viewId, node.Id);
            bool selected = doc.Selected.Contains(node.Id);

            // Running: a glow that fades; gone wrong: red.
            string? error = graph.Error(node.Id);
            if (graph.LastRan(node.Id) is double ran)
            {
                float glow = 1f - Math.Clamp((float)(Graph.Clock - ran) / 0.6f, 0f, 1f);
                if (glow > 0.01f) Ui.Outline(r.Grow(3f), Theme.Hex(0x5FC9B5).WithAlpha(glow), 10f, 3f);
            }

            Ui.Shadow(r, 8f, 16f, 0.7f);
            Ui.Rect(r, Theme.Hex(0x25272C), 8f);

            Color category = library.CategoryColor(type.Category);
            var header = new Rect(r.X, r.Y, r.Width, _header);
            Ui.Rect(header, category.WithAlpha(0.85f), 8f);
            Ui.Rect(new Rect(r.X, r.Y + _header - 8f, r.Width, 8f), category.WithAlpha(0.85f));
            Ui.Text(new Rect(r.X + 10f, r.Y, r.Width - 20f, _header), type.Title, Color.White, bold: true);

            Ui.Outline(r, error != null ? Theme.Danger : selected ? Theme.AccentHover : new Color(0f, 0f, 0f, 0.5f), 8f, error != null || selected ? 2f : 1f);

            Interaction body = Ui.Interact(nodeId, r, rightClick: true);
            Ui.Tooltip(nodeId, error != null ? $"Went wrong: {error}" : type.Description);
            if (body.Pressed)
            {
                if (Ctrl || Shift)
                {
                    if (!doc.Selected.Remove(node.Id)) doc.Selected.Add(node.Id);
                }
                else if (!selected)
                {
                    doc.Selected.Clear();
                    doc.Selected.Add(node.Id);
                }

                // A drag of whatever's selected, if it moves.
                _pressedNode = doc.Selected.Contains(node.Id);
                _dragFrom = Ui.Mouse;
                _dragOrigins.Clear();
                foreach (int selectedId in doc.Selected)
                {
                    if (graph.Node(selectedId) is { } moving) _dragOrigins[selectedId] = moving.Position;
                }

                _dragBefore = graph.ToJson();
            }

            if (body.RightClicked)
            {
                if (!selected)
                {
                    doc.Selected.Clear();
                    doc.Selected.Add(node.Id);
                }

                Menu.Open(Ui.Id(nodeId, "menu"), Ui.Mouse, NodeMenu(doc));
            }

            for (int i = 0; i < type.Inputs.Count; i++) DrawInput(nodeId, doc, layout, type.Inputs[i], i);
            for (int i = 0; i < type.Outputs.Count; i++) DrawOutput(nodeId, doc, layout, type.Outputs[i], i);
        }

        private void DrawInput(int nodeId, GraphDocument doc, Layout layout, Port port, int row)
        {
            Graph graph = doc.Graph;
            GraphNode node = layout.Node;
            float centre = RowCentre(layout.Rect, row);
            bool linked = graph.LinksTo(node.Id, port.Name).Any();

            if (port.Linkable) DrawPort(nodeId, doc, layout, port, new Vector2(layout.Rect.X, centre), output: false, linked);

            float x = layout.Rect.X + 12f;
            string label = Label(port);
            if (label.Length > 0)
            {
                Ui.Text(new Rect(x, centre - _row * 0.5f, LabelWidth(port) + 4f, _row), label, Theme.TextDim, size: Theme.SmallTextSize);
                x += LabelWidth(port) + 6f;
            }

            if (ShowsEditor(graph, node, port))
            {
                var field = new Rect(x, centre - (_row - 6f) * 0.5f, EditorWidth(port), _row - 6f);
                DrawEditor(Ui.Id(nodeId, "value." + port.Name), doc, node, port, field);
            }
        }

        private void DrawOutput(int nodeId, GraphDocument doc, Layout layout, Port port, int row)
        {
            float centre = RowCentre(layout.Rect, row);
            bool linked = doc.Graph.LinksFrom(layout.Node.Id, port.Name).Any();
            DrawPort(nodeId, doc, layout, port, new Vector2(layout.Rect.Right, centre), output: true, linked);

            string label = Label(port);
            if (label.Length > 0)
            {
                Ui.Text(new Rect(layout.Rect.X, centre - _row * 0.5f, layout.Rect.Width - 12f, _row), label, Theme.TextDim, align: 1f, size: Theme.SmallTextSize);
            }
        }

        /// <summary>A port's dot (an arrow for flow): drag from it to link. While a link's being dragged, ports it can't join fade.</summary>
        private void DrawPort(int nodeId, GraphDocument doc, Layout layout, Port port, Vector2 at, bool output, bool linked)
        {
            int portId = Ui.Id(nodeId, (output ? "out." : "in.") + port.Name);
            Interaction interaction = Ui.Interact(portId, new Rect(at.X - 9f, at.Y - 9f, 18f, 18f));

            bool usable = _wire is not { } wire || CanJoin(doc.Graph, wire, layout.Node.Id, port.Name, output);
            Color color = port.Type.Color.WithAlpha(usable ? 1f : 0.2f);
            float size = interaction.Hovered && _wire == null || _wire != null && usable && Vector2.Distance(Ui.Mouse, at) < 12f ? 6.5f : 5f;

            if (port.Type.IsFlow)
            {
                Vertex.UI.Canvas.DrawTriangle(at + new Vector2(-size * 0.7f, -size), at + new Vector2(size, 0f), at + new Vector2(-size * 0.7f, size), Ui.Fade(color));
                if (!linked) Vertex.UI.Canvas.DrawTriangle(at + new Vector2(-size * 0.3f, -size * 0.45f), at + new Vector2(size * 0.4f, 0f), at + new Vector2(-size * 0.3f, size * 0.45f), Ui.Fade(Theme.Hex(0x25272C)));
            }
            else
            {
                Ui.Circle(at, size, color);
                if (!linked) Ui.Circle(at, size - 2f, Theme.Hex(0x25272C));
            }

            Ui.Tooltip(portId, port.Tooltip ?? (port.Type.IsFlow ? null : port.Type.Name));
            if (!interaction.Pressed) return;

            // Pressed: a link starts from here, or (an input already linked) the link's picked up from its other end.
            if (!output && doc.Graph.LinksTo(layout.Node.Id, port.Name).FirstOrDefault() is { } existing && !port.Type.IsFlow)
            {
                _pickedUp = existing;
                _wire = (existing.FromNode, existing.FromPort, true);
            }
            else
            {
                _pickedUp = null;
                _wire = (layout.Node.Id, port.Name, output);
            }
        }

        /// <summary>Whether the link being dragged can end at port <paramref name="port"/> of node <paramref name="node"/>.</summary>
        private static bool CanJoin(Graph graph, (int Node, string Port, bool FromOutput) wire, int node, string port, bool output)
        {
            if (wire.FromOutput == output) return false;
            return wire.FromOutput ? graph.CanLink(wire.Node, wire.Port, node, port) : graph.CanLink(node, port, wire.Node, wire.Port);
        }

        // ---------------------------------------------------------------- Values set on nodes

        private void DrawEditor(int id, GraphDocument doc, GraphNode node, Port port, Rect field)
        {
            object? raw = node.Values.TryGetValue(port.Name, out object? set) ? set : port.DefaultValue;
            void Set(object? value) => doc.Edit(_context.History, $"Change {port.Name}", () => node.Values[port.Name] = value, ("graphValue", doc, node.Id, port.Name));

            if (port.IsVariableName)
            {
                List<string> names = doc.Graph.Variables.Select(v => v.Name).ToList();
                if (names.Count == 0)
                {
                    Ui.Text(field, "No variables", Theme.TextFaint, size: Theme.SmallTextSize);
                    return;
                }

                if (Widgets.Dropdown(id, field, names, names.IndexOf(raw as string ?? "")) is int picked) Set(names[picked]);
            }
            else if (port.Type == PortType.Number)
            {
                float number = (float)PortType.Number.Convert(raw)!;
                if (NumberField.Float(id, field, ref number)) Set(number);
            }
            else if (port.Type == PortType.Boolean)
            {
                bool on = (bool)PortType.Boolean.Convert(raw)!;
                if (Widgets.Checkbox(id, field, ref on)) Set(on);
            }
            else if (port.Type == PortType.Vector)
            {
                var vector = (Vector3)PortType.Vector.Convert(raw)!;
                if (NumberField.Vector3(id, field, ref vector)) Set(vector);
            }
            else if (port.Type == PortType.Text || port.Type == PortType.Any)
            {
                string text = raw == null ? "" : PortType.Describe(raw);
                if (TextInput.Draw(id, field, ref text)) Set(text);
            }
            else if (port.Type.ValueType == typeof(Key))
            {
                KeyField(id, field, (Key)port.Type.Convert(raw)!, Set);
            }
            else if (port.Type.ValueType is { IsEnum: true } enumType)
            {
                Array values = Enum.GetValues(enumType);
                object current = port.Type.Convert(raw)!;
                string[] names = values.Cast<object>().Select(v => Inspection.ComponentCatalog.SplitWords(v.ToString()!)).ToArray();
                if (Widgets.Dropdown(id, field, names, Array.IndexOf(values, current)) is int picked) Set(values.GetValue(picked));
            }
            else if (port.Type == PortType.Object)
            {
                Ui.Text(field, "Self", Theme.TextFaint, size: Theme.SmallTextSize);
            }
        }

        /// <summary>A key setting: click it, then press the key.</summary>
        private void KeyField(int id, Rect field, Key key, Action<object?> set)
        {
            Interaction interaction = Ui.Interact(id, field);
            bool capturing = _capturingKey == id;
            if (interaction.Clicked) _capturingKey = capturing ? 0 : id;

            Ui.Rect(field, capturing ? Theme.ControlHover : interaction.Hovered ? Theme.Control : Theme.Field, Theme.ControlRadius);
            if (capturing) Ui.Outline(field, Theme.Accent, Theme.ControlRadius, 1.5f);
            Ui.Text(field.Shrink(6f, 0f), capturing ? "Press a key…" : Inspection.ComponentCatalog.SplitWords(key.ToString()), capturing ? Theme.AccentText : Theme.Text,
                size: Theme.SmallTextSize);
            Ui.Tooltip(id, capturing ? null : "Click, then press the key");
            if (!capturing) return;

            if (InputSystem.GetKeyDown(Key.Escape))
            {
                InputSystem.Consume(Key.Escape);
                _capturingKey = 0;
                return;
            }

            foreach (Key candidate in Enum.GetValues<Key>())
            {
                if (candidate == Key.Unknown || !InputSystem.GetKeyDown(candidate)) continue;
                InputSystem.Consume(candidate);
                _capturingKey = 0;
                set(candidate);
                return;
            }

            if (Ui.MousePressed() && !interaction.Hovered) _capturingKey = 0;
        }

        // ---------------------------------------------------------------- Dragging

        private void UpdateNodeDrag(GraphDocument doc)
        {
            if (_pressedNode && !_dragging && Ui.MouseHeld() && Vector2.Distance(Ui.Mouse, _dragFrom) * doc.Zoom > 4f) _dragging = true;

            if (_dragging && Ui.MouseHeld())
            {
                Vector2 moved = Ui.Mouse - _dragFrom;
                foreach ((int nodeId, Vector2 origin) in _dragOrigins)
                {
                    if (doc.Graph.Node(nodeId) is { } node) node.Position = origin + moved;
                }

                Ui.Cursor = CursorShape.Hand;
            }

            if (Ui.MouseHeld()) return;
            if (_dragging && _dragBefore != null) doc.Record(_context.History, _dragOrigins.Count == 1 ? "Move Node" : "Move Nodes", _dragBefore);
            _dragging = _pressedNode = false;
            _dragBefore = null;
        }

        private void UpdateBox(GraphDocument doc)
        {
            if (_boxFrom is not Vector2 from) return;

            Vector2 min = Vector2.Min(from, Ui.Mouse), max = Vector2.Max(from, Ui.Mouse);
            var box = new Rect(min.X, min.Y, max.X - min.X, max.Y - min.Y);
            if (box.Width + box.Height > 4f / doc.Zoom)
            {
                Ui.Rect(box, Theme.Accent.WithAlpha(0.12f));
                Ui.Outline(box, Theme.Accent.WithAlpha(0.8f), 0f, 1f / doc.Zoom);
            }

            if (Ui.MouseHeld()) return;
            if (box.Width + box.Height > 4f / doc.Zoom)
            {
                foreach (Layout layout in _layouts.Values)
                {
                    if (layout.Rect.Overlaps(box)) doc.Selected.Add(layout.Node.Id);
                }
            }

            _boxFrom = null;
        }

        private void UpdateWire(GraphDocument doc, NodeLibrary library, int viewId)
        {
            if (_wire is not { } wire) return;
            Graph graph = doc.Graph;
            if (!_layouts.TryGetValue(wire.Node, out Layout? start))
            {
                _wire = null;
                return;
            }

            Vector2 from = wire.FromOutput ? start.Outputs.GetValueOrDefault(wire.Port) : start.Inputs.GetValueOrDefault(wire.Port);
            PortType type = (wire.FromOutput ? start.Type.Output(wire.Port) : start.Type.Input(wire.Port))?.Type ?? PortType.Any;

            // The port it would join: the nearest that can take it, near enough.
            (int Node, string Port, Vector2 At)? target = null;
            float best = 16f;
            foreach (Layout layout in _layouts.Values)
            {
                foreach ((string port, Vector2 at) in wire.FromOutput ? layout.Inputs : layout.Outputs)
                {
                    float distance = Vector2.Distance(at, Ui.Mouse);
                    if (distance < best && CanJoin(graph, wire, layout.Node.Id, port, !wire.FromOutput))
                    {
                        best = distance;
                        target = (layout.Node.Id, port, at);
                    }
                }
            }

            Vector2 end = target?.At ?? Ui.Mouse;
            if (wire.FromOutput) Wire(from, end, type.Color, 2.4f);
            else Wire(end, from, type.Color, 2.4f);

            if (Ui.MouseHeld()) return;

            GraphLink? pickedUp = _pickedUp;
            _wire = null;
            _pickedUp = null;

            if (target is { } joined)
            {
                doc.Edit(_context.History, "Link", () =>
                {
                    if (pickedUp != null) graph.Unlink(pickedUp);
                    if (wire.FromOutput) graph.Link(wire.Node, wire.Port, joined.Node, joined.Port);
                    else graph.Link(joined.Node, joined.Port, wire.Node, wire.Port);
                });
                return;
            }

            // Let go on nothing: a picked-up link is taken away, and the add menu offers what could go on the end.
            if (pickedUp != null) doc.Edit(_context.History, "Unlink", () => graph.Unlink(pickedUp));
            OpenAdd(viewId, Ui.Mouse, Ui.Mouse, wire);
        }

        private void PanAndZoom(Rect rect, GraphDocument doc, bool over, Interaction background, int viewId)
        {
            // The wheel zooms about the mouse.
            if (over && InputSystem.ScrollDelta != 0f && Ui.ActiveId == 0)
            {
                Vector2 at = Ui.Mouse - rect.Position;
                Vector2 point = doc.Offset + at / doc.Zoom;
                doc.Zoom = Math.Clamp(doc.Zoom * MathF.Pow(1.15f, InputSystem.ScrollDelta), _minZoom, _maxZoom);
                doc.Offset = point - at / doc.Zoom;
            }

            if (over && Ui.MousePressed(MouseButton.Middle)) _panning = true;
            if (over && background.RightClicked)
            {
                _rightDown = true;
                _rightAt = Ui.Mouse;
            }

            if (_rightDown && Vector2.Distance(Ui.Mouse, _rightAt) > 4f) _panning = true;

            if (_panning)
            {
                doc.Offset -= Ui.MouseDelta / doc.Zoom;
                Ui.Cursor = CursorShape.Hand;
                if (!Ui.MouseHeld(MouseButton.Middle) && !Ui.MouseHeld(MouseButton.Right)) _panning = false;
            }

            // Right-clicked without dragging: the add menu, there.
            if (_rightDown && !Ui.MouseHeld(MouseButton.Right))
            {
                if (Vector2.Distance(Ui.Mouse, _rightAt) <= 4f) OpenAdd(viewId, doc.Offset + (Ui.Mouse - rect.Position) / doc.Zoom, Ui.Mouse, null);
                _rightDown = false;
                _panning = false;
            }
        }

        /// <summary>Scrolls and zooms to show every node (or the selected ones), in the middle of the view.</summary>
        private void Frame(GraphDocument doc, Rect rect, bool selectedOnly)
        {
            List<Rect> rects = _layouts.Values.Where(l => !selectedOnly || doc.Selected.Contains(l.Node.Id)).Select(l => l.Rect).ToList();
            if (rects.Count == 0)
            {
                if (selectedOnly) Frame(doc, rect, selectedOnly: false);
                else doc.Offset = -rect.Size * 0.5f / doc.Zoom;
                return;
            }

            float left = rects.Min(r => r.X), top = rects.Min(r => r.Y), right = rects.Max(r => r.Right), bottom = rects.Max(r => r.Bottom);
            var size = new Vector2(right - left, bottom - top);
            doc.Zoom = Math.Clamp(MathF.Min((rect.Width - 120f) / MathF.Max(size.X, 1f), (rect.Height - 160f) / MathF.Max(size.Y, 1f)), 0.75f, 1f);
            doc.Offset = new Vector2(left + size.X * 0.5f, top + size.Y * 0.5f) - rect.Size * 0.5f / doc.Zoom;
        }

        // ---------------------------------------------------------------- Adding nodes

        private int _addPopupId;

        /// <summary>Opens the list of nodes to add one at <paramref name="at"/> in the graph, the popup at <paramref name="anchor"/> (in the units of wherever it's asked from).</summary>
        private void OpenAdd(int viewId, Vector2 at, Vector2 anchor, (int Node, string Port, bool FromOutput)? linked)
        {
            _addPopupId = Ui.Id(viewId, "add");
            _addAt = at;
            _addLinked = linked;
            _search = "";
            _highlighted = 0;
            Popup.Open(_addPopupId, new Rect(anchor.X, anchor.Y, 0f, 0f));
            TextInput.Focus(Ui.Id(_addPopupId, "search"), selectAll: true);
        }

        private void DrawAddPopup(int viewId, GraphDocument doc, NodeLibrary library)
        {
            int popupId = Ui.Id(viewId, "add");
            Popup.Show(popupId, new Vector2(300f, 380f), inner => AddList(popupId, inner, doc, library));
        }

        /// <summary>Whether a node of <paramref name="type"/> has a port the link being let go of could join.</summary>
        private static Port? JoinablePort(NodeType type, PortType linkType, bool fromOutput) => fromOutput
            ? type.Inputs.FirstOrDefault(p => p.Linkable && linkType.CanLinkTo(p.Type))
            : type.Outputs.FirstOrDefault(p => p.Type.CanLinkTo(linkType));

        private void AddList(int popupId, Rect rect, GraphDocument doc, NodeLibrary library)
        {
            int searchId = Ui.Id(popupId, "search");
            bool enter = InputSystem.GetKeyDown(Key.Enter) || InputSystem.GetKeyDown(Key.KeypadEnter);
            if (enter) InputSystem.Consume(Key.Enter);
            Ui.KeepFocus(searchId);
            if (TextInput.Draw(searchId, rect.TakeTop(28f), ref _search, "Search nodes", live: true, icon: Icons.Search)) _highlighted = 0;

            // With a link let go of, only what could go on its end.
            PortType? linkType = null;
            if (_addLinked is { } linked && doc.Graph.Node(linked.Node) is { } linkedNode && library.Find(linkedNode.Type) is { } linkedType)
            {
                linkType = (linked.FromOutput ? linkedType.Output(linked.Port) : linkedType.Input(linked.Port))?.Type;
            }

            string search = _search.Trim();
            // Found by its name, or by the start of a word of its category or keywords ("plus" finds Add); names first.
            static bool WordStarts(string words, string search) =>
                words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(search, StringComparison.OrdinalIgnoreCase));
            static int Rank(NodeType t, string search) =>
                search.Length == 0 ? 0
                : t.Title.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 0
                : t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ? 1
                : WordStarts(t.Category + " " + t.Keywords, search) ? 2
                : 3;

            List<NodeType> matches = library.All
                .Where(t => Rank(t, search) < 3)
                .Where(t => linkType == null || JoinablePort(t, linkType, _addLinked!.Value.FromOutput) != null)
                .OrderBy(t => Rank(t, search))
                .ToList();

            if (InputSystem.GetKeyDown(Key.Down)) _highlighted = Math.Min(_highlighted + 1, matches.Count - 1);
            if (InputSystem.GetKeyDown(Key.Up)) _highlighted = Math.Max(_highlighted - 1, 0);

            Rect list = rect.CutTop(34f);
            int scrollId = Ui.Id(popupId, "list");
            var scroll = ScrollView.Begin(scrollId, list);
            float y = scroll.Top;
            string? category = null;
            const float rowHeight = 26f;

            for (int i = 0; i < matches.Count; i++)
            {
                NodeType type = matches[i];
                if (search.Length == 0 && type.Category != category)
                {
                    category = type.Category;
                    Ui.Text(new Rect(list.X + 6f, y + 4f, list.Width, 20f), category.ToUpperInvariant(), Theme.TextFaint, size: 10.5f, bold: true);
                    y += 24f;
                }

                var row = new Rect(list.X, y, list.Width - 8f, rowHeight);
                int rowId = Ui.Id(popupId, type.Id);
                Interaction interaction = Ui.Interact(rowId, row);
                if (interaction.Hovered && Ui.MouseMovedLately) _highlighted = i;

                bool lit = i == _highlighted;
                if (lit)
                {
                    Ui.Rect(row, Theme.Accent.WithAlpha(0.85f), 4f);
                    ScrollView.Reveal(scrollId, y - scroll.Top - 24f, y - scroll.Top + rowHeight, list.Height);
                }

                Ui.Circle(new Vector2(row.X + 12f, row.Centre.Y), 4f, library.CategoryColor(type.Category));
                Ui.Text(row.CutLeft(24f), type.Title, lit ? Color.White : Theme.Text);
                if (search.Length > 0) Ui.Text(row.Shrink(10f, 0f), type.Category, lit ? Color.White.WithAlpha(0.7f) : Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);
                Ui.Tooltip(rowId, type.Description);

                if (interaction.Clicked || enter && lit)
                {
                    Add(doc, library, type);
                    Popup.Close();
                    Ui.FocusId = 0;
                }

                y += rowHeight;
            }

            if (matches.Count == 0) Ui.Text(new Rect(list.X, list.Y + 10f, list.Width, 20f), "No nodes match", Theme.TextFaint, align: 0.5f);
            scroll.End(y - scroll.Top);
        }

        private void Add(GraphDocument doc, NodeLibrary library, NodeType type)
        {
            Graph graph = doc.Graph;
            (int Node, string Port, bool FromOutput)? linked = _addLinked;
            doc.Edit(_context.History, $"Add {type.Title}", () =>
            {
                // To the right of an output it's linked from, to the left of an input; otherwise where it was asked for.
                Vector2 at = _addAt;
                if (linked is { FromOutput: false }) at -= new Vector2(190f, 0f);
                GraphNode node = graph.AddNode(type.Id, at - new Vector2(0f, _header * 0.5f));
                doc.Selected.Clear();
                doc.Selected.Add(node.Id);

                if (linked is not { } link || graph.Node(link.Node) is not { } other || library.Find(other.Type) is not { } otherType) return;
                PortType? linkType = (link.FromOutput ? otherType.Output(link.Port) : otherType.Input(link.Port))?.Type;
                if (linkType == null || JoinablePort(type, linkType, link.FromOutput) is not { } port) return;

                if (link.FromOutput) graph.Link(link.Node, link.Port, node.Id, port.Name);
                else graph.Link(node.Id, port.Name, link.Node, link.Port);
            });
        }

        // ---------------------------------------------------------------- Menus and keys

        private List<MenuItem> NodeMenu(GraphDocument doc) =>
        [
            new MenuItem("Duplicate", () => Duplicate(doc)) { Shortcut = "Ctrl+D" },
            new MenuItem("Copy", () => Copy(doc)) { Shortcut = "Ctrl+C" },
            new MenuItem("Unlink All", () => doc.Edit(_context.History, "Unlink", () =>
                doc.Graph.Links.RemoveAll(l => doc.Selected.Contains(l.FromNode) || doc.Selected.Contains(l.ToNode)))),
            MenuItem.Separator,
            new MenuItem("Delete", () => Delete(doc)) { Shortcut = "Del", Icon = Icons.Trash }
        ];

        private void HandleKeys(Rect rect, GraphDocument doc, int viewId)
        {
            bool Pressed(Key key)
            {
                if (!InputSystem.GetKeyDown(key)) return false;
                InputSystem.Consume(key);
                return true;
            }

            Vector2 mouse = doc.Offset + (Ui.Mouse - rect.Position) / doc.Zoom;

            if (Pressed(Key.Delete) || Pressed(Key.Backspace)) Delete(doc);
            if (Ctrl)
            {
                if (Pressed(Key.D)) Duplicate(doc);
                if (Pressed(Key.C)) Copy(doc);
                if (Pressed(Key.V)) Paste(doc, mouse);
                if (Pressed(Key.A))
                {
                    doc.Selected.Clear();
                    doc.Selected.UnionWith(doc.Graph.Nodes.Select(n => n.Id));
                }

                return;
            }

            if (Pressed(Key.F)) Frame(doc, rect, selectedOnly: doc.Selected.Count > 0);
            if (Pressed(Key.Space) && rect.Contains(Ui.Mouse)) OpenAdd(viewId, mouse, Ui.Mouse, null);
        }

        private void Delete(GraphDocument doc)
        {
            if (doc.Selected.Count == 0) return;
            List<int> gone = doc.Selected.ToList();
            doc.Edit(_context.History, gone.Count == 1 ? "Delete Node" : $"Delete {gone.Count} Nodes", () =>
            {
                foreach (int id in gone) doc.Graph.RemoveNode(id);
            });
            doc.Selected.Clear();
        }

        /// <summary>The selected nodes, with the links between them, as a graph of their own in JSON: for copying.</summary>
        private static string? SelectedAsJson(GraphDocument doc)
        {
            if (doc.Selected.Count == 0) return null;
            var copy = new Graph(doc.Graph.Kind);
            var ids = new Dictionary<int, int>();
            foreach (GraphNode node in doc.Graph.Nodes.Where(n => doc.Selected.Contains(n.Id)))
            {
                GraphNode copied = copy.AddNode(node.Type, node.Position);
                foreach ((string name, object? value) in node.Values) copied.Values[name] = value;
                ids[node.Id] = copied.Id;
            }

            foreach (GraphLink link in doc.Graph.Links)
            {
                if (ids.TryGetValue(link.FromNode, out int from) && ids.TryGetValue(link.ToNode, out int to)) copy.Links.Add(link with { FromNode = from, ToNode = to });
            }

            return copy.ToJson();
        }

        /// <summary>Puts a graph's nodes (copied ones) into the document with their top-left at <paramref name="at"/>, selected.</summary>
        private void Insert(GraphDocument doc, string json, Vector2 at, string name)
        {
            Graph pasted;
            try
            {
                pasted = Graph.FromJson(json);
            }
            catch (Exception)
            {
                return;
            }

            if (pasted.Kind != doc.Graph.Kind || pasted.Nodes.Count == 0) return;

            var corner = new Vector2(pasted.Nodes.Min(n => n.Position.X), pasted.Nodes.Min(n => n.Position.Y));
            doc.Edit(_context.History, name, () =>
            {
                var ids = new Dictionary<int, int>();
                doc.Selected.Clear();
                foreach (GraphNode node in pasted.Nodes)
                {
                    GraphNode added = doc.Graph.AddNode(node.Type, at + node.Position - corner);
                    foreach ((string key, object? value) in node.Values) added.Values[key] = value;
                    ids[node.Id] = added.Id;
                    doc.Selected.Add(added.Id);
                }

                foreach (GraphLink link in pasted.Links)
                {
                    if (ids.TryGetValue(link.FromNode, out int from) && ids.TryGetValue(link.ToNode, out int to)) doc.Graph.Links.Add(link with { FromNode = from, ToNode = to });
                }
            });
        }

        private void Duplicate(GraphDocument doc)
        {
            if (SelectedAsJson(doc) is not { } json) return;
            var corner = new Vector2(doc.Graph.Nodes.Where(n => doc.Selected.Contains(n.Id)).Min(n => n.Position.X),
                doc.Graph.Nodes.Where(n => doc.Selected.Contains(n.Id)).Min(n => n.Position.Y));
            Insert(doc, json, corner + new Vector2(30f, 30f), "Duplicate");
        }

        private static void Copy(GraphDocument doc)
        {
            if (SelectedAsJson(doc) is { } json) InputSystem.ClipboardText = json;
        }

        private void Paste(GraphDocument doc, Vector2 at)
        {
            string text = InputSystem.ClipboardText;
            if (text.TrimStart().StartsWith('{')) Insert(doc, text, at, "Paste");
        }

        // ---------------------------------------------------------------- Over the graph

        private void DrawToolbar(Rect rect, GraphDocument doc, NodeLibrary library)
        {
            var bar = new Rect(rect.X + 10f, rect.Y + 10f, MathF.Min(rect.Width - 20f, 470f), 36f);
            Ui.Block(Ui.Id("graph.toolbar"), bar);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.94f), 8f);
            Ui.Outline(bar, Theme.Border, 8f);

            Icons.Graph(new Vector2(bar.X + 18f, bar.Centre.Y), 18f, Theme.Hex(0x5FC9B5));
            float x = bar.X + 34f;
            x += Ui.Text(doc.Title, new Vector2(x, bar.Y + (bar.Height - Ui.LineHeight(bold: true)) * 0.5f), Theme.Text, bold: true) + 10f;
            Ui.Text(new Rect(x, bar.Y, 200f, bar.Height), $"{library.DisplayName}  ·  {doc.Graph.Nodes.Count} nodes", Theme.TextFaint, size: Theme.SmallTextSize);

            var variables = new Rect(bar.Right - 98f - 34f, bar.Y + 4f, 94f, 28f);
            if (Widgets.Button(Ui.Id("graph.variables"), variables, "Variables", _showVariables ? ButtonStyle.Primary : ButtonStyle.Subtle, "Show or hide the graph's variables"))
            {
                _showVariables = !_showVariables;
            }

            if (Widgets.IconButton(Ui.Id("graph.frame"), new Rect(bar.Right - 32f, bar.Y + 4f, 28f, 28f), Icons.Scale, "Frame the nodes (F)"))
            {
                Frame(doc, rect, selectedOnly: doc.Selected.Count > 0);
            }

            const string hint = "Right-click or Space: add  ·  drag from a port: link  ·  middle-drag: pan  ·  wheel: zoom";
            Ui.Text(new Rect(rect.X + 12f, rect.Bottom - 24f, rect.Width - 24f, 18f), hint, Theme.TextFaint, size: Theme.SmallTextSize);
        }

        /// <summary>The graph's variables: a name, a type and a starting value each, with a way to add more.</summary>
        private void DrawVariables(Rect rect, GraphDocument doc)
        {
            Graph graph = doc.Graph;
            const float rowHeight = 28f;
            float height = 38f + graph.Variables.Count * rowHeight + 36f;
            var panel = new Rect(rect.X + 10f, rect.Y + 54f, MathF.Min(330f, rect.Width - 20f), height);
            if (panel.Bottom > rect.Bottom - 30f) return;

            Ui.Block(Ui.Id("graph.variablesPanel"), panel);
            Ui.Shadow(panel, 8f, 14f, 0.5f);
            Ui.Rect(panel, Theme.Panel.WithAlpha(0.96f), 8f);
            Ui.Outline(panel, Theme.Border, 8f);
            Ui.Text(new Rect(panel.X + 12f, panel.Y + 6f, panel.Width, 24f), "Variables", Theme.Text, bold: true);
            Ui.Text(new Rect(panel.X, panel.Y + 6f, panel.Width - 12f, 24f), "kept per object while it runs", Theme.TextFaint, align: 1f, size: Theme.SmallTextSize);

            float y = panel.Y + 36f;
            string[] typeNames = PortType.VariableTypes.Select(t => t.Name).ToArray();
            for (int i = 0; i < graph.Variables.Count; i++)
            {
                GraphVariable variable = graph.Variables[i];
                int id = Ui.Id(Ui.Id("graph.variable"), i);
                var row = new Rect(panel.X + 8f, y, panel.Width - 16f, rowHeight - 4f);

                string name = variable.Name;
                if (TextInput.Draw(Ui.Id(id, "name"), row.TakeLeft(100f), ref name) && name.Trim().Length > 0 && graph.Variable(name.Trim()) == null)
                {
                    string old = variable.Name, renamed = name.Trim();
                    doc.Edit(_context.History, "Rename Variable", () =>
                    {
                        variable.Name = renamed;

                        // Nodes using it follow.
                        foreach (GraphNode node in graph.Nodes)
                        {
                            if (node.Values.TryGetValue("Variable", out object? used) && used as string == old) node.Values["Variable"] = renamed;
                        }
                    });
                }

                Rect typeRect = row.CutLeft(104f).TakeLeft(76f);
                int typeIndex = Array.IndexOf(PortType.VariableTypes.ToArray(), variable.Type);
                if (Widgets.Dropdown(Ui.Id(id, "type"), typeRect, typeNames, typeIndex) is int picked && picked != typeIndex)
                {
                    PortType type = PortType.VariableTypes[picked];
                    doc.Edit(_context.History, "Change Variable Type", () =>
                    {
                        variable.Type = type;
                        variable.Value = type.Convert(variable.Value);
                    });
                }

                Rect valueRect = row.CutLeft(184f).CutRight(26f);
                DrawVariableValue(Ui.Id(id, "value"), doc, variable, valueRect);

                if (Widgets.IconButton(Ui.Id(id, "delete"), row.TakeRight(24f), Icons.Close, "Delete the variable"))
                {
                    doc.Edit(_context.History, "Delete Variable", () => graph.Variables.Remove(variable));
                }

                y += rowHeight;
            }

            var add = new Rect(panel.X + 8f, y + 2f, panel.Width - 16f, 26f);
            if (Widgets.Button(Ui.Id("graph.addVariable"), add, "Add Variable", ButtonStyle.Subtle, icon: Icons.Plus))
            {
                string name = "Variable";
                for (int n = 2; graph.Variable(name) != null; n++) name = $"Variable {n}";
                doc.Edit(_context.History, "Add Variable", () => graph.Variables.Add(new GraphVariable(name, PortType.Number)));
            }
        }

        private void DrawVariableValue(int id, GraphDocument doc, GraphVariable variable, Rect field)
        {
            void Set(object? value) => doc.Edit(_context.History, "Change Variable", () => variable.Value = value, ("graphVariable", doc, variable));

            if (variable.Type == PortType.Number)
            {
                float number = (float)PortType.Number.Convert(variable.Value)!;
                if (NumberField.Float(id, field, ref number)) Set(number);
            }
            else if (variable.Type == PortType.Boolean)
            {
                bool on = (bool)PortType.Boolean.Convert(variable.Value)!;
                if (Widgets.Checkbox(id, field.TakeLeft(field.Height), ref on)) Set(on);
            }
            else if (variable.Type == PortType.Text)
            {
                string text = (string)PortType.Text.Convert(variable.Value)!;
                if (TextInput.Draw(id, field, ref text)) Set(text);
            }
            else if (variable.Type == PortType.Vector)
            {
                var vector = (Vector3)PortType.Vector.Convert(variable.Value)!;
                if (NumberField.Vector3(id, field, ref vector)) Set(vector);
            }
            else
            {
                Ui.Text(field.Shrink(4f, 0f), "None at first", Theme.TextFaint, size: Theme.SmallTextSize);
            }
        }
    }
}
