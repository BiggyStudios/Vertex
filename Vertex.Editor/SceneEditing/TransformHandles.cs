using System.Numerics;

using Vertex.Editor.Editing;
using Vertex.Editor.Gui;
using Vertex.Input;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>Whether the handles line up with the world's axes or the object's own.</summary>
    public enum HandleSpace
    {
        World,
        Local
    }

    /// <summary>
    /// The handles drawn on the selected object to move, turn and scale it with the mouse: arrows along each axis and
    /// squares for each plane to move it, rings to turn it, and boxes to scale it. Hold Ctrl to snap (half metres,
    /// 15 degrees, tenths of its size). A drag is one undo.
    /// </summary>
    public sealed class TransformHandles
    {
        private enum Part
        {
            None,
            X,
            Y,
            Z,
            PlaneYZ,
            PlaneXZ,
            PlaneXY,

            /// <summary>Moving in the view's plane, turning about the view's axis, or scaling evenly.</summary>
            Centre
        }

        private Part _hover;
        private Part _dragging;

        // Where the drag started.
        private (Vector3, Quaternion, Vector3) _startPose;
        private Vector3 _startWorldPosition;
        private Quaternion _startWorldRotation;
        private Vector3 _startHit;
        private Vector3 _lastAngleVector;
        private float _angle;
        private Vector2 _startMouse;
        private Vector3 _dragAxis;

        // The other selected objects being dragged along, and where each started.
        private readonly List<(Transform Transform, Vector3 WorldPosition, Quaternion WorldRotation, (Vector3, Quaternion, Vector3) Pose)> _others = new();

        private const float _handleLength = 90f;
        private const float _hitDistance = 8f;

        private static readonly Color _highlight = new(1f, 0.82f, 0.3f);

        public bool Dragging => _dragging != Part.None;
        public bool Hovered => _hover != Part.None;

        public HandleSpace Space { get; set; } = HandleSpace.World;

        /// <summary>
        /// Draws the handles for <paramref name="transform"/> and lets the mouse drag them. Only looks at the mouse when
        /// <paramref name="mouseFree"/> (nothing else on screen has it) or a drag is under way. Returns true when it took
        /// a press, so the view doesn't also select whatever's under the mouse.
        /// </summary>
        /// <param name="others">Other selected objects, moved, turned and scaled along with it: turned about its position, each scaled about its own.</param>
        public bool Draw(Transform transform, TransformTool tool, in ViewProjection view, UndoHistory history, SnapSettings snap, bool mouseFree,
            IReadOnlyList<Transform>? others = null)
        {
            Vector3 origin = transform.WorldPosition;
            if (view.ToScreen(origin) is not Vector2 centre) return false;

            float length = _handleLength * view.MetresPerUnit(origin);
            Quaternion rotation = tool == TransformTool.Scale || Space == HandleSpace.Local ? transform.WorldRotation : Quaternion.Identity;
            Vector3 xAxis = Vector3.Transform(Vector3.UnitX, rotation);
            Vector3 yAxis = Vector3.Transform(Vector3.UnitY, rotation);
            Vector3 zAxis = Vector3.Transform(Vector3.UnitZ, rotation);
            Vector3 Axis(Part part) => part switch
            {
                Part.X or Part.PlaneYZ => xAxis,
                Part.Y or Part.PlaneXZ => yAxis,
                _ => zAxis
            };

            bool tookPress = false;

            if (_dragging == Part.None)
            {
                _hover = mouseFree ? HitTest(tool, view, origin, centre, length, xAxis, yAxis, zAxis) : Part.None;
                if (_hover != Part.None && Ui.MousePressed())
                {
                    BeginDrag(transform, tool, view, origin, _hover, Axis(_hover), others ?? []);
                    tookPress = true;
                }
            }
            else if (Ui.MouseHeld())
            {
                Drag(transform, tool, view, length, history, snap);
            }
            else
            {
                _dragging = Part.None;
                history.EndMerge();
            }

            switch (tool)
            {
                case TransformTool.Move: DrawMove(view, origin, centre, length, xAxis, yAxis, zAxis); break;
                case TransformTool.Rotate: DrawRotate(view, origin, centre, length, xAxis, yAxis, zAxis); break;
                case TransformTool.Scale: DrawScale(view, origin, centre, length, xAxis, yAxis, zAxis); break;
            }

            return tookPress;
        }

        // ---------------------------------------------------------------- Dragging

        private void BeginDrag(Transform transform, TransformTool tool, in ViewProjection view, Vector3 origin, Part part, Vector3 axis, IReadOnlyList<Transform> others)
        {
            _dragging = part;
            _dragAxis = axis;
            _startPose = SetPose.Of(transform);
            _startWorldPosition = origin;
            _startWorldRotation = transform.WorldRotation;
            _others.Clear();
            foreach (Transform other in others) _others.Add((other, other.WorldPosition, other.WorldRotation, SetPose.Of(other)));
            _startMouse = Ui.Mouse;
            _angle = 0f;

            (Vector3 rayOrigin, Vector3 rayDirection) = view.Ray(Ui.Mouse);
            _startHit = HitPoint(tool, part, view, origin, axis, rayOrigin, rayDirection) ?? origin;
            _lastAngleVector = _startHit - origin;
        }

        private void Drag(Transform transform, TransformTool tool, in ViewProjection view, float length, UndoHistory history, SnapSettings settings)
        {
            // Snapping as set, or the opposite while Ctrl is held.
            bool snap = settings.Enabled ^ (InputSystem.GetKey(Key.ControlLeft) || InputSystem.GetKey(Key.ControlRight));
            (Vector3 rayOrigin, Vector3 rayDirection) = view.Ray(Ui.Mouse);
            Vector3 origin = _startWorldPosition;
            string name;

            switch (tool)
            {
                case TransformTool.Move:
                {
                    if (HitPoint(tool, _dragging, view, origin, _dragAxis, rayOrigin, rayDirection) is not Vector3 hit) return;
                    Vector3 position = origin + (hit - _startHit);
                    if (snap) position = SnapPosition(position, origin, settings.Move);
                    transform.WorldPosition = position;
                    name = $"Move {transform.GameObject.Name}";
                    break;
                }

                case TransformTool.Rotate:
                {
                    Vector3 axis = _dragging == Part.Centre ? -view.CameraForward : _dragAxis;
                    if (PlaneHit(origin, axis, rayOrigin, rayDirection) is Vector3 hit && MathF.Abs(Vector3.Dot(rayDirection, axis)) > 0.08f)
                    {
                        Vector3 current = hit - origin;
                        _angle += SignedAngle(_lastAngleVector, current, axis);
                        _lastAngleVector = current;
                    }
                    else
                    {
                        // The ring is edge-on: turn by how far the mouse moves instead.
                        _angle += Ui.MouseDelta.X * 0.01f;
                    }

                    float angle = snap ? Snap(_angle, MathF.Max(settings.Rotate, 0.1f) * MathF.PI / 180f) : _angle;
                    transform.WorldRotation = Quaternion.Normalize(Quaternion.Concatenate(_startWorldRotation, Quaternion.CreateFromAxisAngle(axis, angle)));
                    name = $"Rotate {transform.GameObject.Name}";
                    break;
                }

                default:
                {
                    Vector3 scale = _startPose.Item3;
                    if (_dragging == Part.Centre)
                    {
                        float factor = MathF.Max(1f + (Ui.Mouse.X - _startMouse.X) / 120f, 0.01f);
                        if (snap) factor = MathF.Max(Snap(factor, MathF.Max(settings.Scale, 0.001f)), MathF.Max(settings.Scale, 0.001f));
                        scale *= factor;
                    }
                    else if (HitPoint(TransformTool.Move, _dragging, view, origin, _dragAxis, rayOrigin, rayDirection) is Vector3 hit)
                    {
                        float along = Vector3.Dot(hit - _startHit, _dragAxis);
                        float factor = MathF.Max(1f + along / length, 0.01f);
                        if (snap) factor = MathF.Max(Snap(factor, MathF.Max(settings.Scale, 0.001f)), MathF.Max(settings.Scale, 0.001f));
                        if (_dragging == Part.X) scale.X *= factor;
                        if (_dragging == Part.Y) scale.Y *= factor;
                        if (_dragging == Part.Z) scale.Z *= factor;
                    }

                    transform.Scale = scale;
                    name = $"Scale {transform.GameObject.Name}";
                    break;
                }
            }

            if (_others.Count == 0)
            {
                history.Record(new SetPose(name, transform, _startPose, SetPose.Of(transform)), ("pose", transform));
                return;
            }

            // The others follow: moved as far, turned as much about this one's position, scaled by as much each.
            Vector3 moved = transform.WorldPosition - _startWorldPosition;
            Quaternion turned = Quaternion.Normalize(Quaternion.Concatenate(Quaternion.Inverse(_startWorldRotation), transform.WorldRotation));
            Vector3 startScale = _startPose.Item3, nowScale = transform.Scale;
            var scaled = new Vector3(Ratio(nowScale.X, startScale.X), Ratio(nowScale.Y, startScale.Y), Ratio(nowScale.Z, startScale.Z));

            var changes = new List<IUndoable> { new SetPose(name, transform, _startPose, SetPose.Of(transform)) };
            foreach ((Transform other, Vector3 position, Quaternion rotation, (Vector3, Quaternion, Vector3) pose) in _others)
            {
                switch (tool)
                {
                    case TransformTool.Move:
                        other.WorldPosition = position + moved;
                        break;
                    case TransformTool.Rotate:
                        other.WorldPosition = _startWorldPosition + Vector3.Transform(position - _startWorldPosition, turned);
                        other.WorldRotation = Quaternion.Normalize(Quaternion.Concatenate(rotation, turned));
                        break;
                    default:
                        other.Scale = pose.Item3 * scaled;
                        break;
                }

                changes.Add(new SetPose(name, other, pose, SetPose.Of(other)));
            }

            string many = $"{name.Split(' ')[0]} {_others.Count + 1} Objects";
            history.Record(new ChangeGroup(many, changes), ("pose", transform, _others.Count));
        }

        private static float Ratio(float now, float start) => MathF.Abs(start) > 1e-6f ? now / start : 1f;

        /// <summary>Where the mouse ray meets the handle being dragged: the closest point on an axis, or where it crosses a plane.</summary>
        private static Vector3? HitPoint(TransformTool tool, Part part, in ViewProjection view, Vector3 origin, Vector3 axis, Vector3 rayOrigin, Vector3 rayDirection)
        {
            if (tool == TransformTool.Rotate) return PlaneHit(origin, part == Part.Centre ? -view.CameraForward : axis, rayOrigin, rayDirection);

            return part switch
            {
                Part.X or Part.Y or Part.Z => ClosestOnLine(origin, axis, rayOrigin, rayDirection),
                Part.PlaneYZ or Part.PlaneXZ or Part.PlaneXY => PlaneHit(origin, axis, rayOrigin, rayDirection),
                _ => PlaneHit(origin, view.CameraForward, rayOrigin, rayDirection)
            };
        }

        /// <summary>The point on the line through <paramref name="point"/> along <paramref name="axis"/> closest to the ray.</summary>
        public static Vector3? ClosestOnLine(Vector3 point, Vector3 axis, Vector3 rayOrigin, Vector3 rayDirection)
        {
            float b = Vector3.Dot(axis, rayDirection);
            float denominator = 1f - b * b;
            if (denominator < 1e-4f) return null;

            Vector3 w = point - rayOrigin;
            float s = (b * Vector3.Dot(rayDirection, w) - Vector3.Dot(axis, w)) / denominator;
            return point + axis * s;
        }

        /// <summary>Where the ray crosses the plane through <paramref name="point"/> facing <paramref name="normal"/>.</summary>
        public static Vector3? PlaneHit(Vector3 point, Vector3 normal, Vector3 rayOrigin, Vector3 rayDirection)
        {
            float facing = Vector3.Dot(normal, rayDirection);
            if (MathF.Abs(facing) < 1e-5f) return null;

            float t = Vector3.Dot(point - rayOrigin, normal) / facing;
            return t > 0f ? rayOrigin + rayDirection * t : null;
        }

        /// <summary>
        /// Where a dragged position snaps to: with the world's axes, onto the grid of <paramref name="step"/>-sized cells (only
        /// along the axes being dragged); with the object's own, moved in whole steps from where it started.
        /// </summary>
        private Vector3 SnapPosition(Vector3 position, Vector3 start, float step)
        {
            step = MathF.Max(step, 0.001f);
            if (Space == HandleSpace.Local)
            {
                Vector3 moved = position - start;
                if (_dragging is Part.X or Part.Y or Part.Z) return start + _dragAxis * Snap(Vector3.Dot(moved, _dragAxis), step);
                return start + new Vector3(Snap(moved.X, step), Snap(moved.Y, step), Snap(moved.Z, step));
            }

            bool x = _dragging is Part.X or Part.PlaneXY or Part.PlaneXZ or Part.Centre;
            bool y = _dragging is Part.Y or Part.PlaneXY or Part.PlaneYZ or Part.Centre;
            bool z = _dragging is Part.Z or Part.PlaneXZ or Part.PlaneYZ or Part.Centre;
            return new Vector3(x ? Snap(position.X, step) : start.X, y ? Snap(position.Y, step) : start.Y, z ? Snap(position.Z, step) : start.Z);
        }

        private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis) =>
            MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), axis), Vector3.Dot(from, to));

        private static float Snap(float value, float step) => MathF.Round(value / step) * step;

        // ---------------------------------------------------------------- Hit testing

        private Part HitTest(TransformTool tool, in ViewProjection view, Vector3 origin, Vector2 centre, float length, Vector3 x, Vector3 y, Vector3 z)
        {
            Vector2 mouse = Ui.Mouse;

            if (tool == TransformTool.Rotate)
            {
                Part best = Part.None;
                float bestDistance = _hitDistance;
                foreach ((Part part, Vector3 axis) in new[] { (Part.X, x), (Part.Y, y), (Part.Z, z) })
                {
                    float distance = RingDistance(view, origin, axis, length, mouse, frontOnly: true);
                    if (distance < bestDistance)
                    {
                        best = part;
                        bestDistance = distance;
                    }
                }

                if (best != Part.None) return best;
                float outer = Vector2.Distance(mouse, centre) - _handleLength * 1.18f;
                return MathF.Abs(outer) < _hitDistance ? Part.Centre : Part.None;
            }

            if (Vector2.Distance(mouse, centre) < 9f) return Part.Centre;

            if (tool == TransformTool.Move)
            {
                foreach ((Part part, Vector3 a, Vector3 b) in new[] { (Part.PlaneXY, x, y), (Part.PlaneXZ, x, z), (Part.PlaneYZ, y, z) })
                {
                    if (PlaneSquare(view, origin, a, b, length) is { } corners && InQuad(mouse, corners)) return part;
                }
            }

            foreach ((Part part, Vector3 axis) in new[] { (Part.X, x), (Part.Y, y), (Part.Z, z) })
            {
                if (view.ToScreen(origin + axis * length) is not Vector2 end || Vector2.Distance(centre, end) < 12f) continue;
                if (SegmentDistance(mouse, centre, end) < _hitDistance) return part;
            }

            return Part.None;
        }

        private static float SegmentDistance(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 along = b - a;
            float t = Math.Clamp(Vector2.Dot(point - a, along) / MathF.Max(along.LengthSquared(), 1e-6f), 0f, 1f);
            return Vector2.Distance(point, a + along * t);
        }

        private static float RingDistance(in ViewProjection view, Vector3 origin, Vector3 axis, float radius, Vector2 mouse, bool frontOnly)
        {
            float best = float.MaxValue;
            Vector3? previous = null;
            foreach (Vector3 point in RingPoints(origin, axis, radius))
            {
                if (previous is Vector3 last && (!frontOnly || Facing(view, origin, (last + point) * 0.5f)) &&
                    view.ToScreen(last) is Vector2 a && view.ToScreen(point) is Vector2 b)
                {
                    best = MathF.Min(best, SegmentDistance(mouse, a, b));
                }

                previous = point;
            }

            return best;
        }

        // Whether a point on a ring is on the near side of the object, towards the camera.
        private static bool Facing(in ViewProjection view, Vector3 origin, Vector3 point) => Vector3.Dot(point - origin, view.CameraPosition - origin) >= -0.0001f;

        private static IEnumerable<Vector3> RingPoints(Vector3 origin, Vector3 axis, float radius, int segments = 64)
        {
            Vector3 u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            Vector3 v = Vector3.Cross(axis, u);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * MathF.PI * 2f / segments;
                yield return origin + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            }
        }

        private static Vector2[]? PlaneSquare(in ViewProjection view, Vector3 origin, Vector3 a, Vector3 b, float length)
        {
            float near = length * 0.22f, far = length * 0.42f;
            Vector3[] corners = [origin + a * near + b * near, origin + a * far + b * near, origin + a * far + b * far, origin + a * near + b * far];
            var screen = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                if (view.ToScreen(corners[i]) is not Vector2 point) return null;
                screen[i] = point;
            }

            return screen;
        }

        private static bool InQuad(Vector2 p, Vector2[] q) => InTriangle(p, q[0], q[1], q[2]) || InTriangle(p, q[0], q[2], q[3]);

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            static float Side(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f, positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        // ---------------------------------------------------------------- Drawing

        private Color PartColor(Part part, Color color)
        {
            if (_dragging != Part.None) return _dragging == part ? _highlight : color.WithAlpha(0.25f);
            return _hover == part ? _highlight : color;
        }

        private void DrawMove(in ViewProjection view, Vector3 origin, Vector2 centre, float length, Vector3 x, Vector3 y, Vector3 z)
        {
            foreach ((Part part, Vector3 a, Vector3 b, Color color) in new[]
                     {
                         (Part.PlaneXY, x, y, Theme.AxisZ), (Part.PlaneXZ, x, z, Theme.AxisY), (Part.PlaneYZ, y, z, Theme.AxisX)
                     })
            {
                if (PlaneSquare(view, origin, a, b, length) is not { } q) continue;
                Color fill = PartColor(part, color).WithAlpha(_hover == part || _dragging == part ? 0.55f : 0.28f * (_dragging == Part.None ? 1f : 0.4f));
                Vertex.UI.Canvas.DrawTriangle(q[0], q[1], q[2], Ui.Fade(fill));
                Vertex.UI.Canvas.DrawTriangle(q[0], q[2], q[3], Ui.Fade(fill));
            }

            foreach ((Part part, Vector3 axis, Color color) in new[] { (Part.X, x, Theme.AxisX), (Part.Y, y, Theme.AxisY), (Part.Z, z, Theme.AxisZ) })
            {
                if (view.ToScreen(origin + axis * length) is not Vector2 tip || Vector2.Distance(centre, tip) < 12f) continue;

                Color c = PartColor(part, color);
                Vector2 direction = Vector2.Normalize(tip - centre);
                Vector2 side = new Vector2(-direction.Y, direction.X) * 5.5f;
                Vector2 headBase = tip - direction * 15f;
                Ui.Line(centre, headBase, c, 2.5f);
                Vertex.UI.Canvas.DrawTriangle(tip, headBase + side, headBase - side, Ui.Fade(c));
            }

            Color centreColor = PartColor(Part.Centre, Color.White);
            Vertex.UI.Canvas.DrawCircleOutline(centre, 6f, Ui.Fade(centreColor), 2f);
        }

        private void DrawRotate(in ViewProjection view, Vector3 origin, Vector2 centre, float length, Vector3 x, Vector3 y, Vector3 z)
        {
            Vertex.UI.Canvas.DrawCircleOutline(centre, _handleLength * 1.18f + 1f, Ui.Fade(PartColor(Part.Centre, Color.White.WithAlpha(0.6f))), 2f);

            foreach ((Part part, Vector3 axis, Color color) in new[] { (Part.X, x, Theme.AxisX), (Part.Y, y, Theme.AxisY), (Part.Z, z, Theme.AxisZ) })
            {
                Color c = PartColor(part, color);
                Vector3? previous = null;
                foreach (Vector3 point in RingPoints(origin, axis, length))
                {
                    if (previous is Vector3 last)
                    {
                        // The far half of each ring is faint, so it's clear which way round it is.
                        bool front = Facing(view, origin, (last + point) * 0.5f);
                        view.Line(last, point, front ? c : c.WithAlpha(c.A * 0.22f), front ? 2.5f : 1.5f);
                    }

                    previous = point;
                }
            }

            if (_dragging != Part.None)
            {
                // How far it's turned so far.
                float degrees = _angle * 180f / MathF.PI;
                string text = $"{degrees:0.#}°";
                var label = new Rect(Ui.Mouse.X + 16f, Ui.Mouse.Y - 28f, Ui.TextWidth(text, bold: true) + 14f, 22f);
                Ui.Rect(label, Theme.Popup.WithAlpha(0.92f), 5f);
                Ui.Text(label, text, _highlight, align: 0.5f, bold: true);
            }
        }

        private void DrawScale(in ViewProjection view, Vector3 origin, Vector2 centre, float length, Vector3 x, Vector3 y, Vector3 z)
        {
            foreach ((Part part, Vector3 axis, Color color) in new[] { (Part.X, x, Theme.AxisX), (Part.Y, y, Theme.AxisY), (Part.Z, z, Theme.AxisZ) })
            {
                if (view.ToScreen(origin + axis * length) is not Vector2 end || Vector2.Distance(centre, end) < 12f) continue;
                Color c = PartColor(part, color);
                Ui.Line(centre, end, c, 2.5f);
                Ui.Rect(new Rect(end.X - 5.5f, end.Y - 5.5f, 11f, 11f), c, 2f);
            }

            Ui.Rect(new Rect(centre.X - 6.5f, centre.Y - 6.5f, 13f, 13f), PartColor(Part.Centre, Color.White), 2.5f);
        }
    }
}
