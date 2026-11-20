using System.Numerics;

namespace Vertex.Rendering.Baking
{
    /// <summary>
    /// A static mesh laid out flat for its lightmap, as it stands in the world: cut into pieces (charts) that each lie
    /// nearly flat, each piece laid out as seen from straight on, in world units, so every part of it gets the same
    /// detail. Corners are split where pieces meet, as each piece needs its own place for them.
    /// </summary>
    internal sealed class LightmapUnwrap
    {
        /// <summary>A piece: its triangles (of <see cref="Indices"/>, by first index / 3) and how big it is laid flat, in world units.</summary>
        public sealed record Chart(List<int> Triangles, Vector2 Size);

        /// <summary>Which corner of the original mesh each corner is.</summary>
        public required int[] SourceVertices { get; init; }

        public required uint[] Indices { get; init; }

        /// <summary>Each corner's place in its piece laid flat, in world units from the piece's corner.</summary>
        public required Vector2[] Flat { get; init; }

        /// <summary>Which piece each corner's in.</summary>
        public required int[] VertexChart { get; init; }

        /// <summary>Each corner in the world, and which way its surface faces there.</summary>
        public required Vector3[] Positions { get; init; }

        public required Vector3[] Normals { get; init; }

        public required List<Chart> Charts { get; init; }

        /// <summary>How near to facing the same way a piece's triangles must be (the cosine of the most they can differ by).</summary>
        private const float _sameWay = 0.7f;

        public static LightmapUnwrap Of(Mesh mesh, Matrix4x4 world)
        {
            MeshVertex[] vertices = mesh.Vertices;
            uint[] indices = mesh.Indices;
            int triangles = indices.Length / 3;

            // Normals turn with the object but stay square to its surface however it's stretched.
            var m = new Matrix4x4(world.M11, world.M12, world.M13, 0f, world.M21, world.M22, world.M23, 0f, world.M31, world.M32, world.M33, 0f, 0f, 0f, 0f, 1f);
            Matrix4x4.Invert(m, out Matrix4x4 inverse);
            Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);

            var positions = new Vector3[vertices.Length];
            var normals = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                positions[i] = Vector3.Transform(vertices[i].Position, world);
                Vector3 n = Vector3.TransformNormal(vertices[i].Normal, normalMatrix);
                normals[i] = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
            }

            // Each triangle's way and size, facing the way its corners' normals do.
            var facing = new Vector3[triangles];
            var areas = new float[triangles];
            for (int t = 0; t < triangles; t++)
            {
                Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float length = cross.Length();
                areas[t] = length * 0.5f;
                Vector3 average = normals[indices[t * 3]] + normals[indices[t * 3 + 1]] + normals[indices[t * 3 + 2]];
                facing[t] = length > 1e-12f ? cross / length : (average.LengthSquared() > 1e-12f ? Vector3.Normalize(average) : Vector3.UnitY);
                if (Vector3.Dot(facing[t], average) < 0f) facing[t] = -facing[t];
            }

            // Which triangles share an edge, by where their corners are (meshes keep separate corners where shading's sharp).
            var welded = new Dictionary<(int, int, int), int>();
            var weld = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = positions[i] * 10000f;
                var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                if (!welded.TryGetValue(key, out int id)) welded[key] = id = welded.Count;
                weld[i] = id;
            }

            var edges = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < triangles; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = weld[indices[t * 3 + e]], b = weld[indices[t * 3 + (e + 1) % 3]];
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!edges.TryGetValue(key, out List<int>? list)) edges[key] = list = new List<int>(2);
                    list.Add(t);
                }
            }

            // Pieces grown from the biggest triangle left, across shared edges, while they face nearly the same way.
            int[] order = Enumerable.Range(0, triangles).OrderByDescending(t => areas[t]).ThenBy(t => t).ToArray();
            var chartOf = new int[triangles];
            Array.Fill(chartOf, -1);
            var pieces = new List<(List<int> Triangles, Vector3 Normal)>();
            var queue = new Queue<int>();
            foreach (int seed in order)
            {
                if (chartOf[seed] >= 0) continue;
                int chart = pieces.Count;
                var members = new List<int>();
                Vector3 normal = facing[seed];
                pieces.Add((members, normal));
                chartOf[seed] = chart;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    members.Add(t);
                    for (int e = 0; e < 3; e++)
                    {
                        int a = weld[indices[t * 3 + e]], b = weld[indices[t * 3 + (e + 1) % 3]];
                        foreach (int next in edges[(Math.Min(a, b), Math.Max(a, b))])
                        {
                            if (chartOf[next] >= 0) continue;
                            if (areas[next] > 1e-10f && Vector3.Dot(facing[next], normal) < _sameWay) continue;
                            chartOf[next] = chart;
                            queue.Enqueue(next);
                        }
                    }
                }
            }

            // Each piece laid flat as seen from the way it faces, turned to fit in the smallest box, from that box's corner.
            var sourceVertices = new List<int>();
            var flat = new List<Vector2>();
            var vertexChart = new List<int>();
            var newIndices = new uint[indices.Length];
            var charts = new List<Chart>();
            var cornerOf = new Dictionary<int, int>();
            for (int c = 0; c < pieces.Count; c++)
            {
                (List<int> members, Vector3 normal) = pieces[c];
                Vector3 across = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
                Vector3 up = Vector3.Cross(normal, across);

                cornerOf.Clear();
                var points = new List<Vector2>();
                int firstCorner = sourceVertices.Count;
                foreach (int t in members)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int source = (int)indices[t * 3 + k];
                        if (!cornerOf.TryGetValue(source, out int corner))
                        {
                            corner = sourceVertices.Count;
                            cornerOf[source] = corner;
                            sourceVertices.Add(source);
                            vertexChart.Add(c);
                            points.Add(new Vector2(Vector3.Dot(positions[source], across), Vector3.Dot(positions[source], up)));
                        }

                        newIndices[t * 3 + k] = (uint)corner;
                    }
                }

                // The turn that fits it in the smallest box.
                float bestArea = float.MaxValue, bestAngle = 0f;
                for (int step = 0; step < 18; step++)
                {
                    float angle = step * (MathF.PI / 36f);
                    (Vector2 min, Vector2 max) = Box(points, angle);
                    Vector2 size = max - min;
                    float area = size.X * size.Y + (size.X + size.Y) * 1e-3f;
                    if (area < bestArea - 1e-9f)
                    {
                        bestArea = area;
                        bestAngle = angle;
                    }
                }

                (Vector2 lowest, Vector2 highest) = Box(points, bestAngle);
                float cos = MathF.Cos(bestAngle), sin = MathF.Sin(bestAngle);
                foreach (Vector2 p in points) flat.Add(new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos) - lowest);

                // Taller than wide stands on its side, so the pieces pack in rows more tightly.
                Vector2 extent = highest - lowest;
                if (extent.Y > extent.X)
                {
                    for (int i = firstCorner; i < flat.Count; i++) flat[i] = new Vector2(flat[i].Y, flat[i].X);
                    extent = new Vector2(extent.Y, extent.X);
                }

                charts.Add(new Chart(members, extent));
            }

            return new LightmapUnwrap
            {
                SourceVertices = sourceVertices.ToArray(),
                Indices = newIndices,
                Flat = flat.ToArray(),
                VertexChart = vertexChart.ToArray(),
                Positions = sourceVertices.Select(v => positions[v]).ToArray(),
                Normals = sourceVertices.Select(v => normals[v]).ToArray(),
                Charts = charts
            };
        }

        private static (Vector2 Min, Vector2 Max) Box(List<Vector2> points, float angle)
        {
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
            Vector2 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector2 p in points)
            {
                var turned = new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
                min = Vector2.Min(min, turned);
                max = Vector2.Max(max, turned);
            }

            return (min, max);
        }
    }
}
