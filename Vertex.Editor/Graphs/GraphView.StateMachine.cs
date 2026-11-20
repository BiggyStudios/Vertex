using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Animation;
using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Graphs;
using Vertex.Input;
using Vertex.Scripting;

namespace Vertex.Editor.Graphs
{
    /// <summary>
    /// An animation graph's State Machine, opened from its node: its states as boxes and its transitions as arrows
    /// between them, on a grid that pans and zooms like the graph's.
    /// <list type="bullet">
    /// <item>Drag animations in from the Assets panel to make states (onto a state, it plays that instead).</item>
    /// <item>Drag from a state's dot to another state to join them with a transition; from Entry to choose where it
    /// starts; from Any State for a transition taken from whichever state it's in.</item>
    /// <item>Click a state or an arrow to change it on the right: a state's animation, an arrow's conditions and fade.</item>
    /// <item>Right-click for a new state; Delete, F and Escape (back to the graph) as in the graph.</item>
    /// </list>
    /// </summary>
    public sealed partial class GraphView
    {
        private const float _stateWidth = 176f, _stateHeight = 52f;
        private static readonly Vector2 _pillSize = new(120f, 32f);

        /// <summary>Where an arrow's being drawn from: a state's name, Any State, or Entry.</summary>
        private const string _fromEntry = "\0entry";

        // Moving states (and the Entry and Any State boxes): where each was, and the graph before.
        private readonly Dictionary<string, Vector2> _stateOrigins = new();
        private Vector2 _stateDragFrom;
        private string? _stateDragBefore;
        private bool _stateMoved;

        // An arrow being drawn out of a state, to the one it's let go on (or, asked for from a menu, clicked); the frame it started.
        private string? _arrowFrom;
        private bool _arrowDragged;
        private int _arrowFrame;

        private void StartArrow(string from, bool dragged)
        {
            _arrowFrom = from;
            _arrowDragged = dragged;
            _arrowFrame = Ui.Frame;
        }

        // Whether the graph's parameters show over the states (shut to start with, out of their way).
        private bool _showMachineParameters;

        // Panning, and a right press that doesn't move: the menu.
        private bool _machinePanning, _machineRightDown;
        private Vector2 _machineRightAt;

        // Where things were drawn this frame, in the view's own units: for tests.
        private Rect _machineRect;
        private readonly Dictionary<string, Rect> _stateRects = new();

        internal Vector2? StateOnScreen(GraphDocument doc, string state) =>
            _stateRects.TryGetValue(state, out Rect rect) ? _machineRect.Position + (rect.Centre - doc.MachineOffset) * doc.MachineZoom : null;

        internal Vector2? StateDotOnScreen(GraphDocument doc, string state) =>
            _stateRects.TryGetValue(state, out Rect rect) ? _machineRect.Position + (Dot(rect) - doc.MachineOffset) * doc.MachineZoom : null;

        /// <summary>Shows a State Machine node's states in place of the graph.</summary>
        internal static void OpenStateMachine(GraphDocument doc, int node)
        {
            doc.OpenMachine = node;
            doc.MachineFramed = false;
            doc.SelectedStates.Clear();
            doc.SelectedTransition = null;
        }

        private static void CloseStateMachine(GraphDocument doc)
        {
            doc.OpenMachine = null;
            doc.SelectedStates.Clear();
            doc.SelectedTransition = null;
        }

        private static Vector2 Dot(Rect state) => new(state.Right, state.Centre.Y);

        /// <summary>Changes the state machine with undo: a copy's changed, then kept on the node.</summary>
        private void EditMachine(GraphDocument doc, GraphNode node, string name, Action<AnimationStateMachine> change, object? mergeKey = null)
        {
            AnimationStateMachine machine = AnimationStateMachine.Of(node).Copy();
            doc.Edit(_context.History, name, () =>
            {
                change(machine);
                node.Values[AnimationStateMachine.ValueName] = machine.ToJson();
            }, mergeKey);
        }

        // ---------------------------------------------------------------- Drawing

        private void DrawStateMachine(Rect rect, GraphDocument doc, GraphNode node)
        {
            Graph graph = doc.Graph;
            AnimationStateMachine machine = AnimationStateMachine.Of(node);
            _machineRect = rect;
            _stateRects.Clear();
            foreach (AnimationState state in machine.States) _stateRects[state.Name] = new Rect(state.Position.X, state.Position.Y, _stateWidth, _stateHeight);
            doc.SelectedStates.RemoveWhere(name => machine.State(name) == null);
            if (doc.SelectedTransition is int picked && picked >= machine.Transitions.Count) doc.SelectedTransition = null;

            int id = Ui.Id(RuntimeHelpers.GetHashCode(doc), "machineView");
            Interaction background = Ui.Interact(id, rect, rightClick: true);
            bool over = rect.Contains(Ui.Mouse) && Vertex.UI.Canvas.InClip(Ui.Mouse) && !Menu.IsAnyOpen && !Popup.AnyOpen && !Dialog.IsOpen;
            if (Ui.MousePressed() || Ui.MousePressed(MouseButton.Right) || Ui.MousePressed(MouseButton.Middle))
            {
                if (!Menu.IsAnyOpen && !Popup.AnyOpen) _focused = over;
            }

            if (!doc.MachineFramed && rect.Width > 50f)
            {
                doc.MachineFramed = true;
                FrameMachine(doc, rect, machine);
            }

            PanAndZoomMachine(rect, doc, node, over, background, id);

            Ui.PushView(rect, doc.MachineOffset, doc.MachineZoom);
            DrawGrid(new Rect(doc.MachineOffset.X, doc.MachineOffset.Y, rect.Width / doc.MachineZoom, rect.Height / doc.MachineZoom), doc.MachineZoom);

            Rect entry = new(machine.EntryPosition.X, machine.EntryPosition.Y, _pillSize.X, _pillSize.Y);
            Rect any = new(machine.AnyPosition.X, machine.AnyPosition.Y, _pillSize.X, _pillSize.Y);

            int? hoveredArrow = DrawTransitions(doc, machine, entry, any, Ui.HotId == id);
            if (background.Pressed)
            {
                if (_arrowFrom != null) _arrowFrom = null;
                else if (hoveredArrow is int arrow)
                {
                    doc.SelectedStates.Clear();
                    doc.SelectedTransition = arrow;
                }
                else if (!Ctrl && !Shift)
                {
                    doc.SelectedStates.Clear();
                    doc.SelectedTransition = null;
                }
            }

            string playing = AnimationStateMachine.PlayingState(graph, node.Id) ?? "";
            DrawPill(doc, node, entry, "Entry", Theme.Hex(0x3E9E6B), _fromEntry, "Where it starts: drag from its dot to a state to start there");
            DrawPill(doc, node, any, "Any State", Theme.Hex(0x5B6CC4), AnimationStateMachine.AnyState,
                "Transitions from here can be taken whichever state it's in (a jump, a fall, getting hit): drag from its dot to a state");
            foreach (AnimationState state in machine.States) DrawState(doc, graph, node, machine, state, state.Name == playing);

            UpdateStateDrag(doc, node);
            UpdateArrow(doc, node, machine);
            Ui.PopView();

            HandleStateDrop(rect, doc, node, machine);
            DrawMachineToolbar(rect, doc, node);
            if (_showMachineParameters) DrawVariables(rect, doc);
            DrawMachineInspector(rect, doc, graph, node, machine);

            const string hint = "Drag animations in: new states  ·  drag from a state's dot to another: a transition  ·  right-click: add a state  ·  Esc: back to the graph";
            Ui.Text(new Rect(rect.X + 12f, rect.Bottom - 24f, rect.Width - 24f, 18f), hint, Theme.TextFaint, size: Theme.SmallTextSize);

            if (_focused && !Ui.KeyboardTaken && !Menu.IsAnyOpen && !Popup.AnyOpen && !Dialog.IsOpen) HandleMachineKeys(rect, doc, node, machine);
        }

        private void FrameMachine(GraphDocument doc, Rect rect, AnimationStateMachine machine)
        {
            var rects = machine.States.Select(s => new Rect(s.Position.X, s.Position.Y, _stateWidth, _stateHeight)).ToList();
            rects.Add(new Rect(machine.EntryPosition.X, machine.EntryPosition.Y, _pillSize.X, _pillSize.Y));
            rects.Add(new Rect(machine.AnyPosition.X, machine.AnyPosition.Y, _pillSize.X, _pillSize.Y));
            float left = rects.Min(r => r.X), top = rects.Min(r => r.Y), right = rects.Max(r => r.Right), bottom = rects.Max(r => r.Bottom);
            var size = new Vector2(right - left, bottom - top);
            // Big enough to read; what doesn't fit is scrolled to.
            doc.MachineZoom = Math.Clamp(MathF.Min((rect.Width - 160f) / MathF.Max(size.X, 1f), (rect.Height - 160f) / MathF.Max(size.Y, 1f)), 0.8f, 1f);
            float middle = rect.Width * 0.5f;
            doc.MachineOffset = new Vector2(left + size.X * 0.5f, top + size.Y * 0.5f) - new Vector2(middle, rect.Height * 0.5f) / doc.MachineZoom;
        }

        private void PanAndZoomMachine(Rect rect, GraphDocument doc, GraphNode node, bool over, Interaction background, int viewId)
        {
            if (over && InputSystem.ScrollDelta != 0f && Ui.ActiveId == 0)
            {
                Vector2 at = Ui.Mouse - rect.Position;
                Vector2 point = doc.MachineOffset + at / doc.MachineZoom;
                doc.MachineZoom = Math.Clamp(doc.MachineZoom * MathF.Pow(1.15f, InputSystem.ScrollDelta), _minZoom, _maxZoom);
                doc.MachineOffset = point - at / doc.MachineZoom;
            }

            if (over && Ui.MousePressed(MouseButton.Middle)) _machinePanning = true;
            if (over && background.RightClicked)
            {
                _machineRightDown = true;
                _machineRightAt = Ui.Mouse;
            }

            if (_machineRightDown && Vector2.Distance(Ui.Mouse, _machineRightAt) > 4f) _machinePanning = true;
            if (_machinePanning)
            {
                doc.MachineOffset -= Ui.MouseDelta / doc.MachineZoom;
                Ui.Cursor = CursorShape.Hand;
                if (!Ui.MouseHeld(MouseButton.Middle) && !Ui.MouseHeld(MouseButton.Right)) _machinePanning = false;
            }

            if (_machineRightDown && !Ui.MouseHeld(MouseButton.Right))
            {
                if (Vector2.Distance(Ui.Mouse, _machineRightAt) <= 4f)
                {
                    Vector2 at = doc.MachineOffset + (Ui.Mouse - rect.Position) / doc.MachineZoom - new Vector2(_stateWidth, _stateHeight) * 0.5f;
                    Menu.Open(Ui.Id(viewId, "menu"), Ui.Mouse, [new MenuItem("Add State", () => AddState(doc, node, "State", "", at)) { Icon = Icons.Plus }]);
                }

                _machineRightDown = false;
                _machinePanning = false;
            }
        }

        /// <summary>A new state, picked, with an animation if it's given one.</summary>
        private void AddState(GraphDocument doc, GraphNode node, string name, string animation, Vector2 at)
        {
            string chosen = AnimationStateMachine.Of(node).UniqueName(name);
            EditMachine(doc, node, "Add State", machine =>
            {
                machine.States.Add(new AnimationState { Name = chosen, Animation = animation, Position = at });
                if (machine.States.Count == 1) machine.Entry = chosen;
            });
            doc.SelectedStates.Clear();
            doc.SelectedStates.Add(chosen);
            doc.SelectedTransition = null;
        }

        /// <summary>The Entry and Any State boxes: moved like states, with a dot to drag arrows out of.</summary>
        private void DrawPill(GraphDocument doc, GraphNode node, Rect rect, string label, Color color, string from, string tooltip)
        {
            int id = Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(doc), "machinePill"), label);
            Interaction interaction = Ui.Interact(id, rect);
            Ui.Shadow(rect, rect.Height * 0.5f, 10f, 0.5f);
            Ui.Rect(rect, color.WithAlpha(interaction.Hovered ? 1f : 0.88f), rect.Height * 0.5f);
            Ui.Text(rect.Shrink(14f, 0f), label, Color.White, align: 0.5f, bold: true);
            Ui.Tooltip(id, tooltip);
            if (interaction.Pressed) BeginStateDrag(doc, node, [from]);

            DrawArrowDot(doc, rect, from, color);
        }

        private void DrawState(GraphDocument doc, Graph graph, GraphNode node, AnimationStateMachine machine, AnimationState state, bool playing)
        {
            Rect r = _stateRects[state.Name];
            int id = Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(doc), "machineState"), state.Name);
            Interaction interaction = Ui.Interact(id, r, rightClick: true);
            bool selected = doc.SelectedStates.Contains(state.Name);
            bool entry = machine.EntryIndex >= 0 && machine.States[machine.EntryIndex] == state;
            bool linked = graph.LinksTo(node.Id, state.Name).Any();

            if (playing) Ui.Outline(r.Grow(4f), Theme.Hex(0x5FC9B5), 12f, 3f);
            Ui.Shadow(r, 8f, 16f, 0.7f);
            Ui.Rect(r, Theme.Hex(0x2A2D33), 8f);
            // A band down the left: green for where it starts, else the animation graph's.
            Ui.Rect(new Rect(r.X, r.Y, 6f, r.Height), entry ? Theme.Hex(0x3E9E6B) : Theme.Hex(0x43A385).WithAlpha(0.5f), 3f);
            bool arrowTarget = _arrowFrom != null && _arrowFrom != state.Name && interaction.Hovered;
            Ui.Outline(r, arrowTarget ? Theme.Accent : selected ? Theme.AccentHover : new Color(0f, 0f, 0f, 0.5f), 8f, selected || arrowTarget ? 2f : 1f);

            Ui.Text(new Rect(r.X + 16f, r.Y + 6f, r.Width - 32f, 22f), state.Name, Theme.Text, bold: true);
            string plays = linked ? "What's linked into it" : state.Animation.Length > 0 ? AnimationStateMachine.AnimationName(state.Animation) : "Nothing to play yet";
            Ui.Text(new Rect(r.X + 16f, r.Y + 27f, r.Width - 32f, 18f), plays, linked || state.Animation.Length > 0 ? Theme.TextDim : Theme.Warning, size: Theme.SmallTextSize);

            Ui.Tooltip(id, linked
                ? $"Plays what's linked into its input on the State Machine node. Drag from its dot to join it to another state"
                : "Drag from its dot to join it to another state. Drag an animation onto it for it to play that");

            if (interaction.Pressed && _arrowFrom == null)
            {
                if (Ctrl || Shift)
                {
                    if (!doc.SelectedStates.Remove(state.Name)) doc.SelectedStates.Add(state.Name);
                }
                else if (!selected)
                {
                    doc.SelectedStates.Clear();
                    doc.SelectedStates.Add(state.Name);
                }

                doc.SelectedTransition = null;
                BeginStateDrag(doc, node, doc.SelectedStates.ToList());
            }

            if (interaction.RightClicked)
            {
                doc.SelectedStates.Clear();
                doc.SelectedStates.Add(state.Name);
                doc.SelectedTransition = null;
                string name = state.Name;
                Menu.Open(Ui.Id(id, "menu"), Ui.Mouse,
                [
                    new MenuItem("Make Transition", () => StartArrow(name, dragged: false)),
                    new MenuItem("Start Here", () => EditMachine(doc, node, "Set Entry State", m => m.Entry = name)) { Enabled = !entry },
                    MenuItem.Separator,
                    new MenuItem("Delete State", () => DeleteStates(doc, node, [name])) { Shortcut = "Del", Icon = Icons.Trash }
                ]);
            }

            DrawArrowDot(doc, r, state.Name, Theme.Hex(0x43A385));
        }

        /// <summary>The dot on the right of a box, to drag a transition out of.</summary>
        private void DrawArrowDot(GraphDocument doc, Rect box, string from, Color color)
        {
            Vector2 at = Dot(box);
            int id = Ui.Id(Ui.Id(RuntimeHelpers.GetHashCode(doc), "machineDot"), from);
            Interaction interaction = Ui.Interact(id, new Rect(at.X - 9f, at.Y - 9f, 18f, 18f));
            bool lit = interaction.Hovered || _arrowFrom == from;
            Ui.Circle(at, lit ? 7f : 5.5f, Theme.Hex(0x16171A));
            Ui.Circle(at, lit ? 5.5f : 4f, lit ? Theme.AccentHover : color);
            Ui.Tooltip(id, from == _fromEntry ? "Drag to the state it starts in" : "Drag to another state to make a transition to it");
            if (interaction.Pressed) StartArrow(from, dragged: true);
        }

        /// <summary>The transitions as arrows, and Entry's to where it starts. The one under the mouse (if the view's) is returned.</summary>
        private int? DrawTransitions(GraphDocument doc, AnimationStateMachine machine, Rect entry, Rect any, bool canHover)
        {
            Rect? RectOf(string name) => name == AnimationStateMachine.AnyState ? any : _stateRects.TryGetValue(name, out Rect r) ? r : null;

            // Entry's arrow: to the state it starts in.
            if (machine.EntryIndex >= 0 && _stateRects.TryGetValue(machine.States[machine.EntryIndex].Name, out Rect first))
            {
                (Vector2 a, Vector2 b) = Between(entry, first, 0f);
                Arrow(a, b, Theme.Hex(0x3E9E6B).WithAlpha(0.8f), 2f);
            }

            int? hovered = null;
            float best = 7f / MathF.Min(doc.MachineZoom, 1f);
            var ends = new List<(int Index, Vector2 A, Vector2 B)>();
            for (int i = 0; i < machine.Transitions.Count; i++)
            {
                StateTransition transition = machine.Transitions[i];
                if (RectOf(transition.From) is not { } from || RectOf(transition.To) is not { } to || transition.From == transition.To) continue;

                // Side by side when there are arrows both ways.
                bool both = machine.Transitions.Any(t => t.From == transition.To && t.To == transition.From);
                (Vector2 a, Vector2 b) = Between(from, to, both ? 7f : 0f);
                ends.Add((i, a, b));
                if (canHover && _arrowFrom == null && !_stateMoved && _stateDragBefore == null)
                {
                    float distance = SegmentDistance(Ui.Mouse, a, b);
                    if (distance < best)
                    {
                        best = distance;
                        hovered = i;
                    }
                }
            }

            foreach ((int index, Vector2 a, Vector2 b) in ends)
            {
                StateTransition transition = machine.Transitions[index];
                bool picked = doc.SelectedTransition == index;
                bool problem = transition.Conditions.Count == 0 && !transition.AtEnd;
                Color color = picked ? Theme.AccentHover : hovered == index ? Theme.Text : problem ? Theme.Warning : Theme.TextDim;
                Arrow(a, b, color, picked || hovered == index ? 2.6f : 1.8f);
            }

            return hovered;
        }

        /// <summary>Where an arrow from one box to another starts and ends: on their edges, along the line between their middles, moved aside by <paramref name="aside"/>.</summary>
        private static (Vector2 From, Vector2 To) Between(Rect from, Rect to, float aside)
        {
            Vector2 a = from.Centre, b = to.Centre;
            Vector2 direction = b - a;
            if (direction.LengthSquared() < 1e-4f) return (a, b);
            direction = Vector2.Normalize(direction);
            var side = new Vector2(-direction.Y, direction.X) * aside;
            return (Edge(from, a + side, direction) , Edge(to, b + side, -direction));
        }

        /// <summary>Where a line from inside <paramref name="box"/> at <paramref name="start"/>, going along <paramref name="direction"/>, leaves it.</summary>
        private static Vector2 Edge(Rect box, Vector2 start, Vector2 direction)
        {
            float t = float.MaxValue;
            if (direction.X > 1e-5f) t = MathF.Min(t, (box.Right - start.X) / direction.X);
            if (direction.X < -1e-5f) t = MathF.Min(t, (box.X - start.X) / direction.X);
            if (direction.Y > 1e-5f) t = MathF.Min(t, (box.Bottom - start.Y) / direction.Y);
            if (direction.Y < -1e-5f) t = MathF.Min(t, (box.Y - start.Y) / direction.Y);
            return t == float.MaxValue ? start : start + direction * MathF.Max(t, 0f);
        }

        /// <summary>A line with an arrowhead halfway along it.</summary>
        private static void Arrow(Vector2 from, Vector2 to, Color color, float thickness)
        {
            Ui.Line(from, to, color, thickness);
            Vector2 along = to - from;
            if (along.LengthSquared() < 1f) return;
            along = Vector2.Normalize(along);
            var across = new Vector2(-along.Y, along.X);
            Vector2 tip = (from + to) * 0.5f + along * 6f;
            Vertex.UI.Canvas.DrawTriangle(tip, tip - along * 12f + across * 6.5f, tip - along * 12f - across * 6.5f, Ui.Fade(color));
        }

        // ---------------------------------------------------------------- Moving and joining

        private void BeginStateDrag(GraphDocument doc, GraphNode node, List<string> names)
        {
            AnimationStateMachine machine = AnimationStateMachine.Of(node);
            _stateOrigins.Clear();
            foreach (string name in names)
            {
                if (name == _fromEntry) _stateOrigins[name] = machine.EntryPosition;
                else if (name == AnimationStateMachine.AnyState) _stateOrigins[name] = machine.AnyPosition;
                else if (machine.State(name) is { } state) _stateOrigins[name] = state.Position;
            }

            _stateDragFrom = Ui.Mouse;
            _stateDragBefore = doc.Graph.ToJson();
            _stateMoved = false;
        }

        private void UpdateStateDrag(GraphDocument doc, GraphNode node)
        {
            if (_stateDragBefore == null) return;
            if (!Ui.MouseHeld())
            {
                if (_stateMoved) doc.Record(_context.History, _stateOrigins.Count == 1 ? "Move State" : "Move States", _stateDragBefore);
                _stateDragBefore = null;
                _stateMoved = false;
                return;
            }

            Vector2 moved = Ui.Mouse - _stateDragFrom;
            if (!_stateMoved && moved.Length() < 3f) return;
            _stateMoved = true;

            // Moved in place, without undo while dragging: let go, it's one change.
            AnimationStateMachine machine = AnimationStateMachine.Of(node).Copy();
            foreach ((string name, Vector2 was) in _stateOrigins)
            {
                Vector2 at = was + moved;
                if (name == _fromEntry) machine.EntryPosition = at;
                else if (name == AnimationStateMachine.AnyState) machine.AnyPosition = at;
                else if (machine.State(name) is { } state) state.Position = at;
            }

            machine.Store(doc.Graph, node);
        }

        /// <summary>An arrow being drawn: it follows the mouse, and let go (or clicked) on a state, it's a transition to it.</summary>
        private void UpdateArrow(GraphDocument doc, GraphNode node, AnimationStateMachine machine)
        {
            if (_arrowFrom is not { } from) return;
            Rect start = from == _fromEntry ? new Rect(machine.EntryPosition.X, machine.EntryPosition.Y, _pillSize.X, _pillSize.Y)
                : from == AnimationStateMachine.AnyState ? new Rect(machine.AnyPosition.X, machine.AnyPosition.Y, _pillSize.X, _pillSize.Y)
                : _stateRects.TryGetValue(from, out Rect r) ? r : default;
            if (start.Width <= 0f)
            {
                _arrowFrom = null;
                return;
            }

            string? target = _stateRects.FirstOrDefault(s => s.Key != from && s.Value.Contains(Ui.Mouse)).Key;
            Vector2 end = target != null ? Edge(_stateRects[target], _stateRects[target].Centre, Vector2.Normalize(Dot(start) - _stateRects[target].Centre)) : Ui.Mouse;
            Arrow(Dot(start), end, Theme.Accent, 2.2f);

            // Dragged out of a dot, it ends when let go; asked for from the menu, at the next click.
            if (_arrowFrame == Ui.Frame) return;
            bool ends = _arrowDragged ? !Ui.MouseHeld() : Ui.MousePressed();
            if (Ui.MousePressed(MouseButton.Right) || InputSystem.GetKeyDown(Key.Escape))
            {
                InputSystem.Consume(Key.Escape);
                _arrowFrom = null;
                return;
            }

            if (!ends) return;
            _arrowFrom = null;
            if (target == null) return;

            if (from == _fromEntry)
            {
                EditMachine(doc, node, "Set Entry State", m => m.Entry = target);
                return;
            }

            int existing = machine.Transitions.FindIndex(t => t.From == from && t.To == target);
            if (existing >= 0)
            {
                doc.SelectedTransition = existing;
                doc.SelectedStates.Clear();
                return;
            }

            int index = machine.Transitions.Count;
            EditMachine(doc, node, "Add Transition", m =>
            {
                var transition = new StateTransition { From = from, To = target };
                // It starts with a condition on the first parameter, ready to change.
                if (doc.Graph.Variables.FirstOrDefault() is { } parameter) transition.Conditions.Add(NewCondition(parameter));
                m.Transitions.Add(transition);
            });
            doc.SelectedTransition = index;
            doc.SelectedStates.Clear();
        }

        private static TransitionCondition NewCondition(GraphVariable parameter) => parameter.Type == PortType.Number
            ? new TransitionCondition { Parameter = parameter.Name, Is = Comparison.Greater, Value = 0.1f }
            : new TransitionCondition { Parameter = parameter.Name, Is = Comparison.Equal, Value = 1f };

        /// <summary>Animations dragged in from the assets: onto a state, it plays that; onto empty space, a new state playing it.</summary>
        private void HandleStateDrop(Rect rect, GraphDocument doc, GraphNode node, AnimationStateMachine machine)
        {
            if (!DragDrop.Dragging || DragDrop.Payload is not AssetEntry { Kind: AssetKind.Animation } animation || !rect.Contains(Ui.Mouse)) return;

            Vector2 at = doc.MachineOffset + (Ui.Mouse - rect.Position) / doc.MachineZoom;
            string? onto = _stateRects.FirstOrDefault(s => s.Value.Contains(at)).Key;
            if (onto != null)
            {
                Rect box = _stateRects[onto];
                Ui.Outline(new Rect(rect.Position + (box.Position - doc.MachineOffset) * doc.MachineZoom, box.Size * doc.MachineZoom).Grow(4f), Theme.Accent, 10f * doc.MachineZoom, 2f);
            }

            if (!DragDrop.Dropped) return;
            string file = Vertex.Assets.RelativePath(animation.Path);
            if (onto != null)
            {
                EditMachine(doc, node, "Change State's Animation", m => m.State(onto)!.Animation = file);
                doc.SelectedStates.Clear();
                doc.SelectedStates.Add(onto);
                doc.SelectedTransition = null;
            }
            else
            {
                AddState(doc, node, animation.NameWithoutExtension, file, at - new Vector2(_stateWidth, _stateHeight) * 0.5f);
            }
        }

        private void DeleteStates(GraphDocument doc, GraphNode node, List<string> names)
        {
            if (names.Count == 0) return;
            EditMachine(doc, node, names.Count == 1 ? "Delete State" : "Delete States", machine =>
            {
                foreach (string name in names)
                {
                    machine.States.RemoveAll(s => s.Name == name);
                    machine.Transitions.RemoveAll(t => t.From == name || t.To == name);
                    doc.Graph.Links.RemoveAll(l => l.ToNode == node.Id && l.ToPort == name);
                }
            });
            doc.SelectedStates.Clear();
            doc.SelectedTransition = null;
        }

        private void HandleMachineKeys(Rect rect, GraphDocument doc, GraphNode node, AnimationStateMachine machine)
        {
            bool Pressed(Key key)
            {
                if (!InputSystem.GetKeyDown(key)) return false;
                InputSystem.Consume(key);
                return true;
            }

            if (Pressed(Key.Delete) || Pressed(Key.Backspace))
            {
                if (doc.SelectedTransition is int index)
                {
                    EditMachine(doc, node, "Delete Transition", m => m.Transitions.RemoveAt(index));
                    doc.SelectedTransition = null;
                }
                else
                {
                    DeleteStates(doc, node, doc.SelectedStates.ToList());
                }
            }

            if (Pressed(Key.F)) FrameMachine(doc, rect, machine);
            if (Pressed(Key.Escape)) CloseStateMachine(doc);
        }

        // ---------------------------------------------------------------- Over the view

        private void DrawMachineToolbar(Rect rect, GraphDocument doc, GraphNode node)
        {
            var bar = new Rect(rect.X + 10f, rect.Y + 10f, MathF.Min(rect.Width - 20f, 470f), 36f);
            Ui.Block(Ui.Id("graph.toolbar"), bar);
            Ui.Shadow(bar, 8f, 14f, 0.5f);
            Ui.Rect(bar, Theme.Panel.WithAlpha(0.94f), 8f);
            Ui.Outline(bar, Theme.Border, 8f);

            // Back to the graph: its name, then the state machine's.
            var back = new Rect(bar.X + 4f, bar.Y + 4f, 28f, 28f);
            if (Widgets.IconButton(Ui.Id("machine.back"), back, (c, s, col) => Icons.Chevron(c, s, col, 180f), "Back to the graph (Esc)")) CloseStateMachine(doc);
            float x = back.Right + 6f;
            float y = bar.Y + (bar.Height - Ui.LineHeight()) * 0.5f;
            var graphName = new Rect(x, bar.Y, Ui.TextWidth(doc.Title) + 4f, bar.Height);
            Interaction crumb = Ui.Interact(Ui.Id("machine.crumb"), graphName);
            x += Ui.Text(doc.Title, new Vector2(x, y), crumb.Hovered ? Theme.Text : Theme.TextDim) + 8f;
            if (crumb.Clicked) CloseStateMachine(doc);
            x += Ui.Text("›", new Vector2(x, y), Theme.TextFaint) + 8f;
            Icons.States(new Vector2(x + 8f, bar.Centre.Y), 18f, Theme.Hex(0x43A385));
            x += 22f;
            Ui.Text("State Machine", new Vector2(x, bar.Y + (bar.Height - Ui.LineHeight(bold: true)) * 0.5f), Theme.Text, bold: true);

            var parameters = new Rect(bar.Right - 98f - 34f, bar.Y + 4f, 94f, 28f);
            if (Widgets.Button(Ui.Id("graph.variables"), parameters, "Parameters", _showMachineParameters ? ButtonStyle.Primary : ButtonStyle.Subtle, "Show or hide the graph's parameters"))
            {
                _showMachineParameters = !_showMachineParameters;
            }

            if (Widgets.IconButton(Ui.Id("machine.frame"), new Rect(bar.Right - 32f, bar.Y + 4f, 28f, 28f), Icons.Scale, "Frame the states (F)"))
            {
                FrameMachine(doc, rect, AnimationStateMachine.Of(node));
            }
        }

        /// <summary>What's picked, to change: a state's animation and speed, or a transition's conditions and fade.</summary>
        private void DrawMachineInspector(Rect rect, GraphDocument doc, Graph graph, GraphNode node, AnimationStateMachine machine)
        {
            AnimationState? state = doc.SelectedStates.Count == 1 ? machine.State(doc.SelectedStates.First()) : null;
            StateTransition? transition = doc.SelectedTransition is int index && index < machine.Transitions.Count ? machine.Transitions[index] : null;
            if (state == null && transition == null) return;

            float height = state != null ? 236f : 196f + transition!.Conditions.Count * 32f;
            var panel = new Rect(rect.Right - 310f, rect.Y + 10f, 300f, MathF.Min(height, rect.Height - 50f));
            if (panel.X < rect.X + 10f) return;
            Ui.Block(Ui.Id("machine.inspector"), panel);
            Ui.Shadow(panel, 8f, 14f, 0.5f);
            Ui.Rect(panel, Theme.Panel.WithAlpha(0.97f), 8f);
            Ui.Outline(panel, Theme.Border, 8f);
            Ui.PushClip(panel);
            if (state != null) DrawStateInspector(panel, doc, graph, node, machine, state);
            else DrawTransitionInspector(panel, doc, graph, node, machine, transition!, doc.SelectedTransition!.Value);
            Ui.PopClip();
        }

        private static Rect Row(Rect panel, ref float y, float height = 26f)
        {
            var row = new Rect(panel.X + 12f, y, panel.Width - 24f, height);
            y += height + 6f;
            return row;
        }

        private static void FieldLabel(Rect row, string text) => Ui.Text(row.TakeLeft(84f), text, Theme.TextDim, size: Theme.SmallTextSize);

        private void DrawStateInspector(Rect panel, GraphDocument doc, Graph graph, GraphNode node, AnimationStateMachine machine, AnimationState state)
        {
            float y = panel.Y + 10f;
            int id = Ui.Id(Ui.Id("machine.state"), state.Name);
            string old = state.Name;
            Ui.Text(Row(panel, ref y, 20f), "State", Theme.TextFaint, size: Theme.SmallTextSize, bold: true);

            Rect nameRow = Row(panel, ref y);
            FieldLabel(nameRow, "Name");
            string name = state.Name;
            if (TextInput.Draw(Ui.Id("machine.stateName"), nameRow.CutLeft(84f), ref name) && name.Trim().Length > 0 && name.Trim() != old)
            {
                string renamed = machine.UniqueName(name.Trim());
                EditMachine(doc, node, "Rename State", m =>
                {
                    m.State(old)!.Name = renamed;
                    if (m.Entry == old) m.Entry = renamed;
                    foreach (StateTransition t in m.Transitions)
                    {
                        if (t.From == old) t.From = renamed;
                        if (t.To == old) t.To = renamed;
                    }

                    // What's linked into its input stays linked.
                    for (int i = 0; i < graph.Links.Count; i++)
                    {
                        if (graph.Links[i].ToNode == node.Id && graph.Links[i].ToPort == old) graph.Links[i] = graph.Links[i] with { ToPort = renamed };
                    }
                });
                doc.SelectedStates.Clear();
                doc.SelectedStates.Add(renamed);
                return;
            }

            bool linked = graph.LinksTo(node.Id, state.Name).Any();
            Rect animationRow = Row(panel, ref y);
            FieldLabel(animationRow, "Animation");
            _files.AssetField(Ui.Id(id, "animation"), animationRow.CutLeft(84f), AssetKind.Animation, PortType.Animation.Convert(state.Animation), canWrite: true,
                path => EditMachine(doc, node, "Change State's Animation", m => m.State(old)!.Animation = path != null ? Vertex.Assets.RelativePath(path) : ""));

            Rect speedRow = Row(panel, ref y);
            FieldLabel(speedRow, "Speed");
            float speed = state.Speed;
            if (NumberField.Float(Ui.Id(id, "speed"), speedRow.CutLeft(84f).TakeLeft(80f), ref speed, speed: 0.01f))
            {
                EditMachine(doc, node, "Change State's Speed", m => m.State(old)!.Speed = speed, ("stateSpeed", doc, node.Id, old));
            }

            Rect note = Row(panel, ref y, 34f);
            string text = linked ? "It plays what's linked into its input on the State Machine node, not its animation."
                : "Link a blend into its input on the State Machine node to play that instead (walking and running by speed).";
            DrawWrapped(note, text);

            bool entry = machine.EntryIndex >= 0 && machine.States[machine.EntryIndex] == state;
            Rect entryRow = Row(panel, ref y);
            if (entry) Ui.Text(entryRow, "It starts in this state.", Theme.TextFaint, size: Theme.SmallTextSize);
            else if (Widgets.Button(Ui.Id(id, "entry"), entryRow.TakeLeft(130f), "Start Here", ButtonStyle.Subtle, "Make this the state it starts in"))
            {
                EditMachine(doc, node, "Set Entry State", m => m.Entry = old);
            }

            Rect deleteRow = Row(panel, ref y);
            if (Widgets.Button(Ui.Id(id, "delete"), deleteRow.TakeLeft(130f), "Delete State", ButtonStyle.Subtle, icon: Icons.Trash)) DeleteStates(doc, node, [old]);
        }

        private void DrawTransitionInspector(Rect panel, GraphDocument doc, Graph graph, GraphNode node, AnimationStateMachine machine, StateTransition transition, int index)
        {
            float y = panel.Y + 10f;
            int id = Ui.Id(Ui.Id("machine.transition"), index);
            Ui.Text(Row(panel, ref y, 20f), "Transition", Theme.TextFaint, size: Theme.SmallTextSize, bold: true);
            string from = transition.From == AnimationStateMachine.AnyState ? "Any State" : transition.From;
            // From, an arrow, to.
            Rect title = Row(panel, ref y, 22f);
            float textY = title.Y + (title.Height - Ui.LineHeight(bold: true)) * 0.5f;
            float x = title.X + Ui.Text(from, new Vector2(title.X, textY), Theme.Text, bold: true) + 10f;
            Arrow(new Vector2(x, title.Centre.Y), new Vector2(x + 26f, title.Centre.Y), Theme.TextDim, 1.6f);
            Ui.Text(transition.To, new Vector2(x + 36f, textY), Theme.Text, bold: true);

            Rect fadeRow = Row(panel, ref y);
            FieldLabel(fadeRow, "Fade");
            float fade = transition.Fade;
            if (NumberField.Float(Ui.Id(id, "fade"), fadeRow.CutLeft(84f).TakeLeft(80f), ref fade, speed: 0.01f, format: "0.00", min: 0f))
            {
                EditMachine(doc, node, "Change Fade", m => m.Transitions[index].Fade = MathF.Max(0f, fade), ("transitionFade", doc, node.Id, index));
            }

            Ui.Text(fadeRow.CutLeft(172f), "seconds", Theme.TextFaint, size: Theme.SmallTextSize);

            Rect endRow = Row(panel, ref y);
            bool atEnd = transition.AtEnd;
            if (Widgets.Checkbox(Ui.Id(id, "atEnd"), endRow.TakeLeft(18f), ref atEnd, $"Waits until {from}'s animation has played through, ending as the fade does"))
            {
                EditMachine(doc, node, atEnd ? "Wait for the End" : "Don't Wait for the End", m => m.Transitions[index].AtEnd = atEnd);
            }

            Ui.Text(endRow.CutLeft(26f), "Only once the animation's done", Theme.TextDim, size: Theme.SmallTextSize);

            Ui.Text(Row(panel, ref y, 18f), "When", Theme.TextFaint, size: Theme.SmallTextSize, bold: true);
            for (int c = 0; c < transition.Conditions.Count; c++) DrawCondition(Row(panel, ref y), doc, graph, node, index, c, transition.Conditions[c]);

            if (transition.Conditions.Count == 0)
            {
                string text = transition.AtEnd ? "It goes as soon as the animation's done." : "With nothing here it's never taken: add a condition, or wait for the end.";
                Ui.Text(Row(panel, ref y, 18f), text, transition.AtEnd ? Theme.TextFaint : Theme.Warning, size: Theme.SmallTextSize);
            }

            Rect buttons = Row(panel, ref y);
            if (graph.Variables.Count == 0)
            {
                Ui.Text(buttons, "Add parameters to test (Parameters, top left).", Theme.TextFaint, size: Theme.SmallTextSize);
            }
            else if (Widgets.Button(Ui.Id(id, "addCondition"), buttons.TakeLeft(130f), "Add Condition", ButtonStyle.Subtle, icon: Icons.Plus))
            {
                GraphVariable parameter = graph.Variables[0];
                EditMachine(doc, node, "Add Condition", m => m.Transitions[index].Conditions.Add(NewCondition(parameter)));
            }

            if (Widgets.Button(Ui.Id(id, "delete"), buttons.CutLeft(138f).TakeLeft(130f), "Delete", ButtonStyle.Subtle, "Delete the transition", Icons.Trash))
            {
                EditMachine(doc, node, "Delete Transition", m => m.Transitions.RemoveAt(index));
                doc.SelectedTransition = null;
            }
        }

        private static readonly string[] _comparisons = ["is more than", "is at least", "is less than", "is at most", "is", "isn't"];
        private static readonly Comparison[] _comparisonValues = [Comparison.Greater, Comparison.GreaterOrEqual, Comparison.Less, Comparison.LessOrEqual, Comparison.Equal, Comparison.NotEqual];

        /// <summary>One condition: which parameter, then how it's tested (a number compared, true or false, or a trigger set).</summary>
        private void DrawCondition(Rect row, GraphDocument doc, Graph graph, GraphNode node, int transition, int index, TransitionCondition condition)
        {
            int id = Ui.Id(Ui.Id(Ui.Id("machine.condition"), transition), index);
            List<string> names = graph.Variables.Select(v => v.Name).ToList();
            bool missing = !names.Contains(condition.Parameter);
            if (missing) names.Add(condition.Parameter);
            if (Widgets.Dropdown(Ui.Id(id, "parameter"), row.TakeLeft(96f), names, names.IndexOf(condition.Parameter), missing ? "There's no parameter of that name" : null) is int picked
                && graph.Variable(names[picked]) is { } chosen)
            {
                EditMachine(doc, node, "Change Condition", m => m.Transitions[transition].Conditions[index] = NewCondition(chosen));
            }

            Rect rest = row.CutLeft(102f).CutRight(28f);
            PortType type = graph.Variable(condition.Parameter)?.Type ?? PortType.Number;
            void Change(Action<TransitionCondition> change) => EditMachine(doc, node, "Change Condition", m => change(m.Transitions[transition].Conditions[index]), ("condition", doc, node.Id, transition, index));

            if (type == AnimationNodes.Trigger)
            {
                Ui.Text(rest.Shrink(4f, 0f), "is set", Theme.TextDim, size: Theme.SmallTextSize);
            }
            else if (type == PortType.Boolean)
            {
                bool wanted = condition.Is == Comparison.Equal ? condition.Value >= 0.5f : condition.Value < 0.5f;
                if (Widgets.Dropdown(Ui.Id(id, "bool"), rest, ["is true", "is false"], wanted ? 0 : 1) is int which)
                {
                    Change(c =>
                    {
                        c.Is = Comparison.Equal;
                        c.Value = which == 0 ? 1f : 0f;
                    });
                }
            }
            else
            {
                int at = Array.IndexOf(_comparisonValues, condition.Is);
                if (Widgets.Dropdown(Ui.Id(id, "is"), rest.TakeLeft(96f), _comparisons, at) is int compared) Change(c => c.Is = _comparisonValues[compared]);
                float value = condition.Value;
                if (NumberField.Float(Ui.Id(id, "value"), rest.CutLeft(100f), ref value, speed: 0.01f)) Change(c => c.Value = value);
            }

            if (Widgets.IconButton(Ui.Id(id, "remove"), row.TakeRight(24f), Icons.Close, "Take this condition away"))
            {
                EditMachine(doc, node, "Remove Condition", m => m.Transitions[transition].Conditions.RemoveAt(index));
            }
        }

        /// <summary>Text broken into lines to fit <paramref name="rect"/>'s width.</summary>
        private static void DrawWrapped(Rect rect, string text)
        {
            float y = rect.Y;
            var line = new System.Text.StringBuilder();
            foreach (string word in text.Split(' '))
            {
                string tried = line.Length == 0 ? word : line + " " + word;
                if (Ui.TextWidth(tried, Theme.SmallTextSize) > rect.Width && line.Length > 0)
                {
                    Ui.Text(line.ToString(), new Vector2(rect.X, y), Theme.TextFaint, Theme.SmallTextSize);
                    y += 16f;
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(tried);
                }
            }

            if (line.Length > 0) Ui.Text(line.ToString(), new Vector2(rect.X, y), Theme.TextFaint, Theme.SmallTextSize);
        }
    }
}
