using System.Numerics;

namespace Vertex.Rendering.Baking
{
    /// <summary>Where a ray hit a triangle: which, how far along, where on it (barycentric u and v), and whether from behind.</summary>
    internal readonly record struct RayHit(int Triangle, float Distance, float U, float V, bool Backface);

    /// <summary>
    /// Triangles to send rays at, for baking light: sorted into a tree of boxes (a bounding volume hierarchy), so a ray
    /// only tests the few triangles in the boxes it passes through. Built once; safe to trace from many threads at once.
    /// </summary>
    internal sealed class TriangleTree
    {
        private struct Node
        {
            public Vector3 Min;
            public int LeftOrFirst;   // a branch's first child (the second follows it), or a leaf's first triangle
            public Vector3 Max;
            public int Count;         // triangles in a leaf; 0 for a branch
        }

        private const int _bins = 12;
        private const int _leafSize = 4;

        private readonly Vector3[] _a;
        private readonly Vector3[] _ab;
        private readonly Vector3[] _ac;
        private readonly Vector3[] _facing;
        private readonly int[] _ids;
        private Node[] _nodes;
        private int _nodeCount;

        public int Count => _ids.Length;

        /// <summary>
        /// Builds the tree. Each triangle's front is the side <paramref name="facing"/> points to (its corners' normals
        /// averaged), whichever way round its corners go.
        /// </summary>
        public TriangleTree(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C)> triangles, IReadOnlyList<Vector3> facing)
        {
            int count = triangles.Count;
            _a = new Vector3[count];
            _ab = new Vector3[count];
            _ac = new Vector3[count];
            _facing = new Vector3[count];
            _ids = new int[count];
            _nodes = new Node[Math.Max(2 * count, 1)];

            var centres = new Vector3[count];
            var mins = new Vector3[count];
            var maxs = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = triangles[i];
                mins[i] = Vector3.Min(a, Vector3.Min(b, c));
                maxs[i] = Vector3.Max(a, Vector3.Max(b, c));
                centres[i] = (mins[i] + maxs[i]) * 0.5f;
                _ids[i] = i;
            }

            _nodeCount = 1;
            if (count > 0) Split(0, 0, count, centres, mins, maxs);

            // The triangles in tree order, so a leaf's are next to each other.
            for (int i = 0; i < count; i++)
            {
                (Vector3 a, Vector3 b, Vector3 c) = triangles[_ids[i]];
                _a[i] = a;
                _ab[i] = b - a;
                _ac[i] = c - a;
                Vector3 normal = Vector3.Cross(_ab[i], _ac[i]);
                _facing[i] = Vector3.Dot(normal, facing[_ids[i]]) < 0f ? -normal : normal;
            }
        }

        private void Split(int node, int first, int count, Vector3[] centres, Vector3[] mins, Vector3[] maxs)
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue), centreMin = new(float.MaxValue), centreMax = new(float.MinValue);
            for (int i = first; i < first + count; i++)
            {
                int t = _ids[i];
                min = Vector3.Min(min, mins[t]);
                max = Vector3.Max(max, maxs[t]);
                centreMin = Vector3.Min(centreMin, centres[t]);
                centreMax = Vector3.Max(centreMax, centres[t]);
            }

            _nodes[node].Min = min;
            _nodes[node].Max = max;

            if (count <= _leafSize || !BestSplit(first, count, centreMin, centreMax, centres, mins, maxs, out int axis, out float position))
            {
                _nodes[node].LeftOrFirst = first;
                _nodes[node].Count = count;
                return;
            }

            // Those on each side of the split, the near side first.
            int i0 = first, j = first + count - 1;
            while (i0 <= j)
            {
                if (Axis(centres[_ids[i0]], axis) < position)
                {
                    i0++;
                }
                else
                {
                    (_ids[i0], _ids[j]) = (_ids[j], _ids[i0]);
                    j--;
                }
            }

            int leftCount = i0 - first;
            if (leftCount == 0 || leftCount == count)
            {
                _nodes[node].LeftOrFirst = first;
                _nodes[node].Count = count;
                return;
            }

            int left = _nodeCount;
            _nodeCount += 2;
            _nodes[node].LeftOrFirst = left;
            _nodes[node].Count = 0;
            Split(left, first, leftCount, centres, mins, maxs);
            Split(left + 1, i0, count - leftCount, centres, mins, maxs);
        }

        /// <summary>
        /// Where to split a node to make tracing quickest: in bins along each axis, the place that leaves the least
        /// surface area times triangles on the two sides (the surface area heuristic). False if not splitting is better.
        /// </summary>
        private bool BestSplit(int first, int count, Vector3 centreMin, Vector3 centreMax, Vector3[] centres, Vector3[] mins, Vector3[] maxs, out int bestAxis, out float bestPosition)
        {
            bestAxis = -1;
            bestPosition = 0f;
            float bestCost = float.MaxValue;
            Span<int> binCounts = stackalloc int[_bins];
            Span<Vector3> binMin = stackalloc Vector3[_bins];
            Span<Vector3> binMax = stackalloc Vector3[_bins];
            Span<float> leftArea = stackalloc float[_bins];
            Span<int> leftCount = stackalloc int[_bins];

            for (int axis = 0; axis < 3; axis++)
            {
                float low = Axis(centreMin, axis), high = Axis(centreMax, axis);
                if (high - low < 1e-6f) continue;

                binCounts.Clear();
                binMin.Fill(new Vector3(float.MaxValue));
                binMax.Fill(new Vector3(float.MinValue));
                float scale = _bins / (high - low);
                for (int i = first; i < first + count; i++)
                {
                    int t = _ids[i];
                    int bin = Math.Min((int)((Axis(centres[t], axis) - low) * scale), _bins - 1);
                    binCounts[bin]++;
                    binMin[bin] = Vector3.Min(binMin[bin], mins[t]);
                    binMax[bin] = Vector3.Max(binMax[bin], maxs[t]);
                }

                Vector3 runMin = new(float.MaxValue), runMax = new(float.MinValue);
                int run = 0;
                for (int b = 0; b < _bins - 1; b++)
                {
                    run += binCounts[b];
                    runMin = Vector3.Min(runMin, binMin[b]);
                    runMax = Vector3.Max(runMax, binMax[b]);
                    leftCount[b] = run;
                    leftArea[b] = run > 0 ? Area(runMin, runMax) : 0f;
                }

                runMin = new Vector3(float.MaxValue);
                runMax = new Vector3(float.MinValue);
                run = 0;
                for (int b = _bins - 1; b > 0; b--)
                {
                    run += binCounts[b];
                    runMin = Vector3.Min(runMin, binMin[b]);
                    runMax = Vector3.Max(runMax, binMax[b]);
                    if (leftCount[b - 1] == 0 || run == 0) continue;
                    float cost = leftCount[b - 1] * leftArea[b - 1] + run * Area(runMin, runMax);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestAxis = axis;
                        bestPosition = low + b / scale;
                    }
                }
            }

            return bestAxis >= 0;
        }

        private static float Area(Vector3 min, Vector3 max)
        {
            Vector3 d = max - min;
            return d.X * d.Y + d.Y * d.Z + d.Z * d.X;
        }

        private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        /// <summary>The nearest triangle a ray from <paramref name="origin"/> along <paramref name="direction"/> (of length 1) hits within <paramref name="maxDistance"/>.</summary>
        public bool Intersect(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) => Trace(origin, direction, maxDistance, anyHit: false, out hit);

        /// <summary>Whether anything is in the way of a ray within <paramref name="maxDistance"/>: quicker, as any hit will do.</summary>
        public bool Occluded(Vector3 origin, Vector3 direction, float maxDistance) => Trace(origin, direction, maxDistance, anyHit: true, out _);

        private bool Trace(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out RayHit hit)
        {
            hit = default;
            if (_ids.Length == 0) return false;

            var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
            float nearest = maxDistance;
            int found = -1;
            float foundU = 0f, foundV = 0f;

            Span<int> stack = stackalloc int[128];
            int top = 0;
            stack[top++] = 0;
            while (top > 0)
            {
                ref Node node = ref _nodes[stack[--top]];
                if (!HitsBox(origin, inverse, node.Min, node.Max, nearest, out _)) continue;

                if (node.Count > 0)
                {
                    for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                    {
                        if (!HitsTriangle(i, origin, direction, nearest, out float t, out float u, out float v)) continue;
                        nearest = t;
                        found = i;
                        foundU = u;
                        foundV = v;
                        if (anyHit)
                        {
                            hit = new RayHit(_ids[i], t, u, v, false);
                            return true;
                        }
                    }

                    continue;
                }

                // The nearer child is looked at first, so the far one's often skipped.
                int left = node.LeftOrFirst;
                bool leftHit = HitsBox(origin, inverse, _nodes[left].Min, _nodes[left].Max, nearest, out float leftNear);
                bool rightHit = HitsBox(origin, inverse, _nodes[left + 1].Min, _nodes[left + 1].Max, nearest, out float rightNear);
                if (leftHit && rightHit)
                {
                    if (leftNear <= rightNear)
                    {
                        stack[top++] = left + 1;
                        stack[top++] = left;
                    }
                    else
                    {
                        stack[top++] = left;
                        stack[top++] = left + 1;
                    }
                }
                else if (leftHit)
                {
                    stack[top++] = left;
                }
                else if (rightHit)
                {
                    stack[top++] = left + 1;
                }
            }

            if (found < 0) return false;
            hit = new RayHit(_ids[found], nearest, foundU, foundV, Vector3.Dot(direction, _facing[found]) > 0f);
            return true;
        }

        private static bool HitsBox(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float within, out float near)
        {
            Vector3 t0 = (min - origin) * inverse;
            Vector3 t1 = (max - origin) * inverse;
            Vector3 tMin = Vector3.Min(t0, t1);
            Vector3 tMax = Vector3.Max(t0, t1);
            near = MathF.Max(MathF.Max(tMin.X, tMin.Y), MathF.Max(tMin.Z, 0f));
            float far = MathF.Min(MathF.Min(tMax.X, tMax.Y), MathF.Min(tMax.Z, within));
            return near <= far;
        }

        // Möller and Trumbore's: either side counts.
        private bool HitsTriangle(int i, Vector3 origin, Vector3 direction, float within, out float t, out float u, out float v)
        {
            t = u = v = 0f;
            Vector3 p = Vector3.Cross(direction, _ac[i]);
            float determinant = Vector3.Dot(_ab[i], p);
            if (MathF.Abs(determinant) < 1e-12f) return false;

            float inverse = 1f / determinant;
            Vector3 s = origin - _a[i];
            u = Vector3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(s, _ab[i]);
            v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;

            t = Vector3.Dot(_ac[i], q) * inverse;
            return t > 1e-5f && t < within;
        }
    }
}
