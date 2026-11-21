using System.Numerics;
using System.Runtime.CompilerServices;

using Vertex.Editor.Gui;
using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.Editor.SceneEditing
{
    /// <summary>
    /// Colliders drawn as green wireframes in the scene view, so you can see where things are solid. Each is drawn the
    /// size and shape the physics world makes it, the object's scale included (a sphere takes its largest axis).
    /// </summary>
    internal static class ColliderShapes
    {
        public static readonly Color Solid = Theme.Hex(0x8CEB7A);

        // Triggers are paler: you walk through them.
        public static readonly Color Trigger = Theme.Hex(0xC8F5BE);

        private const int _ringSegments = 48;

        // Meshes with more edges than this are shown as the box round them: their wireframe would hide everything.
        private const int _maxMeshEdges = 6000;

        private sealed class MeshEdges
        {
            public bool Made;
            public int Version;
            public int VertexCount;
            public Vector3[] Ends = [];
        }

        private static readonly ConditionalWeakTable<Mesh, MeshEdges> _meshEdges = new();

        /// <summary>
        /// The colliders the physics world uses on <paramref name="gameObject"/>: its first enabled one (the rest are
        /// left out, as there), and a character controller.
        /// </summary>
        public static IEnumerable<Collider> Used(GameObject gameObject)
        {
            bool body = false;
            foreach (Component component in gameObject.Components)
            {
                if (component is CharacterController { Enabled: true } controller) yield return controller;
                else if (!body && component is Collider { Enabled: true } collider)
                {
                    body = true;
                    yield return collider;
                }
            }
        }

        /// <summary>Draws the colliders of <paramref name="gameObject"/>, at <paramref name="alpha"/>.</summary>
        public static void Draw(in ViewProjection view, GameObject gameObject, float alpha)
        {
            var lines = new List<(Vector3, Vector3)>();
            foreach (Collider collider in Used(gameObject))
            {
                lines.Clear();
                Outline(collider, lines, view.CameraPosition);
                Color color = (collider.IsTrigger ? Trigger : Solid).WithAlpha((collider.IsTrigger ? 0.7f : 1f) * alpha);
                foreach ((Vector3 from, Vector3 to) in lines) view.Line(from, to, color, 1.25f);
            }
        }

        /// <summary>
        /// The edges of <paramref name="collider"/>'s shape in the world, added to <paramref name="lines"/>. A sphere also
        /// gets its outline as seen from <paramref name="eye"/>, so it reads as round from anywhere.
        /// </summary>
        public static void Outline(Collider collider, List<(Vector3 From, Vector3 To)> lines, Vector3 eye)
        {
            Transform transform = collider.Transform;
            Vector3 size = Vector3.Max(Vector3.Abs(transform.WorldScale), new Vector3(0.001f));
            Quaternion rotation = transform.WorldRotation;
            Vector3 origin = transform.WorldPosition + Vector3.Transform(collider.Center * size, rotation);
            Vector3 right = Vector3.Transform(Vector3.UnitX, rotation), up = Vector3.Transform(Vector3.UnitY, rotation), back = Vector3.Transform(Vector3.UnitZ, rotation);

            switch (collider)
            {
                // A character stands upright on its feet (the object's position), whatever its scale or turn.
                case CharacterController controller:
                {
                    float radius = MathF.Max(controller.Radius, 0.01f);
                    float height = MathF.Max(controller.Height, radius * 2f);
                    Capsule(lines, transform.WorldPosition + new Vector3(0f, height * 0.5f, 0f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, radius, height * 0.5f - radius);
                    break;
                }

                case BoxCollider box:
                    Box(lines, origin, right, up, back, box.Size * size * 0.5f);
                    break;

                case BoxCollider2D box:
                    Box(lines, origin, right, up, back, new Vector3(box.Size * new Vector2(size.X, size.Y), box.Depth * size.Z) * 0.5f);
                    break;

                case SphereCollider sphere:
                    Sphere(lines, origin, right, up, back, sphere.Radius * MathF.Max(size.X, MathF.Max(size.Y, size.Z)), eye);
                    break;

                case CapsuleCollider capsule:
                {
                    float radius = capsule.Radius * MathF.Max(size.X, size.Z);
                    Capsule(lines, origin, right, up, back, radius, capsule.Height * size.Y * 0.5f - radius);
                    break;
                }

                case CapsuleCollider2D capsule:
                {
                    float radius = capsule.Radius * size.X;
                    Capsule(lines, origin, right, up, back, radius, capsule.Height * size.Y * 0.5f - radius);
                    break;
                }

                // A disc lying in X-Y, Depth deep along Z.
                case CircleCollider2D circle:
                {
                    float radius = circle.Radius * MathF.Max(size.X, size.Y);
                    Vector3 half = back * MathF.Max(circle.Depth * size.Z * 0.5f, 0.01f);
                    Ring(lines, origin + half, right, up, radius);
                    Ring(lines, origin - half, right, up, radius);
                    foreach (Vector3 side in (ReadOnlySpan<Vector3>)[right, -right, up, -up]) lines.Add((origin + half + side * radius, origin - half + side * radius));
                    break;
                }

                case ConvexCollider { Mesh: { } mesh }:
                    MeshLines(lines, mesh, origin, rotation, size);
                    break;

                case MeshCollider { Mesh: { } mesh }:
                    MeshLines(lines, mesh, origin, rotation, size);
                    break;
            }
        }

        private static void Box(List<(Vector3, Vector3)> lines, Vector3 centre, Vector3 right, Vector3 up, Vector3 back, Vector3 half)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                corners[i] = centre + right * (i % 2 == 0 ? -half.X : half.X) + up * (i / 2 % 2 == 0 ? -half.Y : half.Y) + back * (i / 4 == 0 ? -half.Z : half.Z);
            }

            // Corners that differ in one axis.
            for (int a = 0; a < 8; a++)
            {
                foreach (int bit in (ReadOnlySpan<int>)[1, 2, 4])
                {
                    if ((a & bit) == 0) lines.Add((corners[a], corners[a | bit]));
                }
            }
        }

        /// <summary>Part of a circle round <paramref name="centre"/> in the plane of <paramref name="a"/> and <paramref name="b"/>, from angle <paramref name="from"/> to <paramref name="to"/>.</summary>
        private static void Arc(List<(Vector3, Vector3)> lines, Vector3 centre, Vector3 a, Vector3 b, float radius, float from, float to)
        {
            int segments = Math.Max(4, (int)MathF.Ceiling(_ringSegments * MathF.Abs(to - from) / MathF.Tau));
            Vector3 last = centre + (a * MathF.Cos(from) + b * MathF.Sin(from)) * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = from + (to - from) * i / segments;
                Vector3 next = centre + (a * MathF.Cos(angle) + b * MathF.Sin(angle)) * radius;
                lines.Add((last, next));
                last = next;
            }
        }

        private static void Ring(List<(Vector3, Vector3)> lines, Vector3 centre, Vector3 a, Vector3 b, float radius) => Arc(lines, centre, a, b, radius, 0f, MathF.Tau);

        internal static void Sphere(List<(Vector3, Vector3)> lines, Vector3 centre, Vector3 right, Vector3 up, Vector3 back, float radius, Vector3 eye)
        {
            Ring(lines, centre, right, back, radius);
            Ring(lines, centre, right, up, radius);
            Ring(lines, centre, back, up, radius);

            // Its edge as seen from the eye: a circle a little nearer than the centre, a little smaller than the sphere.
            Vector3 toEye = eye - centre;
            float distance = toEye.Length();
            if (distance <= radius * 1.001f) return;

            Vector3 towards = toEye / distance;
            Vector3 a = Vector3.Normalize(Vector3.Cross(towards, MathF.Abs(towards.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
            Vector3 b = Vector3.Cross(towards, a);
            float along = radius * radius / distance;
            Ring(lines, centre + towards * along, a, b, MathF.Sqrt(radius * radius - along * along));
        }

        /// <summary>A capsule standing along <paramref name="up"/>: rings round the ends of its middle, joined, and capped by half circles.</summary>
        private static void Capsule(List<(Vector3, Vector3)> lines, Vector3 centre, Vector3 right, Vector3 up, Vector3 back, float radius, float halfMiddle)
        {
            // Too short for a middle: it's a sphere, as in the physics world.
            if (halfMiddle <= 0.001f)
            {
                Ring(lines, centre, right, back, radius);
                Ring(lines, centre, right, up, radius);
                Ring(lines, centre, back, up, radius);
                return;
            }

            Vector3 top = centre + up * halfMiddle, bottom = centre - up * halfMiddle;
            Ring(lines, top, right, back, radius);
            Ring(lines, bottom, right, back, radius);
            foreach (Vector3 side in (ReadOnlySpan<Vector3>)[right, -right, back, -back]) lines.Add((top + side * radius, bottom + side * radius));

            Arc(lines, top, right, up, radius, 0f, MathF.PI);
            Arc(lines, top, back, up, radius, 0f, MathF.PI);
            Arc(lines, bottom, right, up, radius, MathF.PI, MathF.Tau);
            Arc(lines, bottom, back, up, radius, MathF.PI, MathF.Tau);
        }

        private static void MeshLines(List<(Vector3, Vector3)> lines, Mesh mesh, Vector3 origin, Quaternion rotation, Vector3 size)
        {
            Vector3 Place(Vector3 local) => origin + Vector3.Transform(local * size, rotation);

            Vector3[] ends = EdgesOf(mesh);
            if (ends.Length == 0)
            {
                // Too many edges to show: the box round it.
                Box(lines, Place(mesh.BoundsCentre), Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation), Vector3.Transform(Vector3.UnitZ, rotation),
                    mesh.BoundsExtents * size);
                return;
            }

            for (int i = 0; i < ends.Length; i += 2) lines.Add((Place(ends[i]), Place(ends[i + 1])));
        }

        /// <summary>
        /// Each edge of the mesh's triangles once (pairs of ends, in the mesh's own space), however its vertices are split
        /// for normals or UVs; none if there are too many to show. Kept until the mesh changes.
        /// </summary>
        internal static Vector3[] EdgesOf(Mesh mesh)
        {
            MeshEdges cached = _meshEdges.GetOrCreateValue(mesh);
            if (cached.Made && cached.Version == mesh.Version && cached.VertexCount == mesh.Vertices.Length) return cached.Ends;

            // Vertices in the same place are the same corner.
            var corners = new Dictionary<Vector3, int>();
            var cornerOf = new int[mesh.Vertices.Length];
            for (int i = 0; i < cornerOf.Length; i++)
            {
                Vector3 position = mesh.Vertices[i].Position;
                if (!corners.TryGetValue(position, out int corner)) corners[position] = corner = corners.Count;
                cornerOf[i] = corner;
            }

            var seen = new HashSet<long>();
            var ends = new List<Vector3>();
            uint[] indices = mesh.Indices;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    uint a = indices[t + e], b = indices[t + (e + 1) % 3];
                    int ca = cornerOf[a], cb = cornerOf[b];
                    if (ca == cb || !seen.Add(Math.Min(ca, cb) * (long)int.MaxValue + Math.Max(ca, cb))) continue;

                    ends.Add(mesh.Vertices[a].Position);
                    ends.Add(mesh.Vertices[b].Position);
                }

                if (ends.Count > _maxMeshEdges * 2)
                {
                    ends.Clear();
                    break;
                }
            }

            cached.Made = true;
            cached.Version = mesh.Version;
            cached.VertexCount = mesh.Vertices.Length;
            cached.Ends = [.. ends];
            return cached.Ends;
        }
    }
}
