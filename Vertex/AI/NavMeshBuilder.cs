using System.Numerics;

using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex.AI
{
    /// <summary>
    /// Makes a scene's <see cref="NavMesh"/>. <see cref="Prepare"/> copies the shapes of what's walked on and round from
    /// the scene, on the thread that uses it: the meshes of static Mesh Renderers (see <see cref="MeshRenderer.Static"/>)
    /// and the colliders of objects without a Rigidbody (scenery). <see cref="Build"/> can then go on any thread.
    /// <para>
    /// It's built as Recast builds one: the shapes are filled into a grid of columns seen from above, each holding the
    /// spans of height that are solid; the tops of spans flat enough, with room above for a character, are the cells
    /// that can be stood on, joined to the cells beside them that can be stepped to. Cells nearer an edge than a
    /// character's radius are taken away, so the middle of a character can go anywhere that's left; then the cells are
    /// put together into rectangles to find paths across.
    /// </para>
    /// </summary>
    public sealed class NavMeshBuilder
    {
        private readonly List<Vector3> _vertices = new();
        private readonly List<int> _indices = new();
        private readonly List<bool> _walkable = new();

        private float _radius, _height, _step, _slope, _cellSize, _minArea;

        /// <summary>How many things the navmesh is made from.</summary>
        public int Objects { get; private set; }

        /// <summary>How many triangles they have between them.</summary>
        public int Triangles => _walkable.Count;

        private NavMeshBuilder() {}

        /// <summary>
        /// Copies what <paramref name="scene"/>'s navmesh is made from, for a character of the size in
        /// <paramref name="settings"/> (the scene's own <see cref="Scene.Navigation"/> if null).
        /// </summary>
        public static NavMeshBuilder Prepare(Scene scene, NavigationSettings? settings = null)
        {
            settings ??= scene.Navigation;
            scene.ApplyChanges();
            var builder = new NavMeshBuilder
            {
                _radius = MathF.Max(settings.AgentRadius, 0f),
                _height = MathF.Max(settings.AgentHeight, 0.1f),
                _step = MathF.Max(settings.StepHeight, 0f),
                _slope = Math.Clamp(settings.MaxSlope, 0f, 89f),
                _cellSize = Math.Clamp(settings.CellSize, 0.02f, 4f),
                _minArea = MathF.Max(settings.MinArea, 0f)
            };

            var taken = new HashSet<GameObject>();
            foreach (MeshRenderer renderer in scene.FindComponents<MeshRenderer>())
            {
                if (!Takes(renderer)) continue;
                builder.AddMesh(renderer.Mesh!, renderer.Transform.WorldMatrix);
                taken.Add(renderer.GameObject);
            }

            foreach (Collider collider in scene.FindComponents<Collider>())
            {
                if (!Takes(collider)) continue;
                Matrix4x4 world = collider.Transform.WorldMatrix;
                switch (collider)
                {
                    case BoxCollider box:
                        builder.AddBox(box.Center, box.Size * 0.5f, world);
                        break;
                    case SphereCollider sphere:
                        builder.AddCapsule(sphere.Center, sphere.Radius, 0f, world);
                        break;
                    case CapsuleCollider capsule:
                        builder.AddCapsule(capsule.Center, capsule.Radius, MathF.Max(capsule.Height - capsule.Radius * 2f, 0f), world);
                        break;
                    case MeshCollider { Mesh: { } mesh }:
                        builder.AddMesh(mesh, world);
                        break;
                    case ConvexCollider { Mesh: { } mesh }:
                        builder.AddMesh(mesh, world);
                        break;
                    default:
                        continue;
                }

                taken.Add(collider.GameObject);
            }

            builder.Objects = taken.Count;
            return builder;
        }

        /// <summary>How many of the scene's objects its navmesh is made from, without copying their shapes: for an editor to show.</summary>
        public static int CountObjects(Scene scene)
        {
            var taken = new HashSet<GameObject>();
            foreach (MeshRenderer renderer in scene.FindComponents<MeshRenderer>())
            {
                if (Takes(renderer)) taken.Add(renderer.GameObject);
            }

            foreach (Collider collider in scene.FindComponents<Collider>())
            {
                if (Takes(collider)) taken.Add(collider.GameObject);
            }

            return taken.Count;
        }

        private static bool Takes(MeshRenderer renderer) => renderer.Static && renderer is not SkinnedMeshRenderer && renderer.Mesh != null && !Moves(renderer.GameObject);

        private static bool Takes(Collider collider) => !collider.IsTrigger && !Moves(collider.GameObject) &&
            collider is BoxCollider or SphereCollider or CapsuleCollider or MeshCollider { Mesh: not null } or ConvexCollider { Mesh: not null };

        /// <summary>What moves isn't part of it: things physics moves, and characters (and what's under them: a character's body).</summary>
        private static bool Moves(GameObject gameObject)
        {
            for (Transform? at = gameObject.Transform; at != null; at = at.Parent)
            {
                GameObject owner = at.GameObject;
                if (owner.GetComponent<Rigidbody>() != null || owner.GetComponent<NavMeshAgent>() != null || owner.GetComponent<CharacterController>() != null) return true;
            }

            return false;
        }

        private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 facing)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            if (length < 1e-9f) return;
            normal /= length;
            if (Vector3.Dot(normal, facing) < 0f) normal = -normal;

            int first = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _indices.Add(first);
            _indices.Add(first + 1);
            _indices.Add(first + 2);
            _walkable.Add(normal.Y >= MathF.Cos(_slope * MathF.PI / 180f) - 1e-4f);
        }

        // A mesh's triangles face the way its corners' normals do, whichever way round they're wound.
        private void AddMesh(Mesh mesh, Matrix4x4 world)
        {
            MeshVertex[] vertices = mesh.Vertices;
            uint[] indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                if (indices[i] >= vertices.Length || indices[i + 1] >= vertices.Length || indices[i + 2] >= vertices.Length) continue;
                MeshVertex a = vertices[indices[i]], b = vertices[indices[i + 1]], c = vertices[indices[i + 2]];
                Vector3 facing = Vector3.TransformNormal(a.Normal + b.Normal + c.Normal, world);
                if (facing.LengthSquared() < 1e-12f) facing = Vector3.Cross(Vector3.Transform(b.Position, world) - Vector3.Transform(a.Position, world),
                    Vector3.Transform(c.Position, world) - Vector3.Transform(a.Position, world));
                AddTriangle(Vector3.Transform(a.Position, world), Vector3.Transform(b.Position, world), Vector3.Transform(c.Position, world), facing);
            }
        }

        private void AddBox(Vector3 centre, Vector3 half, Matrix4x4 world)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var local = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
                corners[i] = Vector3.Transform(centre + local, world);
            }

            Vector3 middle = Vector3.Transform(centre, world);

            // Each face as two triangles, facing out from the middle.
            ReadOnlySpan<int> faces = [0, 1, 3, 2, 4, 5, 7, 6, 0, 1, 5, 4, 2, 3, 7, 6, 0, 2, 6, 4, 1, 3, 7, 5];
            for (int f = 0; f < faces.Length; f += 4)
            {
                Vector3 a = corners[faces[f]], b = corners[faces[f + 1]], c = corners[faces[f + 2]], d = corners[faces[f + 3]];
                Vector3 facing = (a + b + c + d) * 0.25f - middle;
                AddTriangle(a, b, c, facing);
                AddTriangle(a, c, d, facing);
            }
        }

        /// <summary>A capsule standing along y (a sphere with no <paramref name="straight"/>), as rings of corners.</summary>
        private void AddCapsule(Vector3 centre, float radius, float straight, Matrix4x4 world)
        {
            const int around = 12, rings = 6;
            var points = new Vector3[(rings + 1) * around];
            var middles = new Vector3[rings + 1];
            for (int ring = 0; ring <= rings; ring++)
            {
                float angle = MathF.PI * ring / rings - MathF.PI * 0.5f;
                float y = MathF.Sin(angle) * radius + (ring < rings / 2 ? -straight * 0.5f : ring > rings / 2 ? straight * 0.5f : 0f);
                float width = MathF.Cos(angle) * radius;
                middles[ring] = centre + new Vector3(0f, y, 0f);
                for (int i = 0; i < around; i++)
                {
                    float turn = MathF.Tau * i / around;
                    points[ring * around + i] = Vector3.Transform(centre + new Vector3(MathF.Cos(turn) * width, y, MathF.Sin(turn) * width), world);
                }
            }

            Vector3 middle = Vector3.Transform(centre, world);
            for (int ring = 0; ring < rings; ring++)
            {
                for (int i = 0; i < around; i++)
                {
                    int next = (i + 1) % around;
                    Vector3 a = points[ring * around + i], b = points[ring * around + next];
                    Vector3 c = points[(ring + 1) * around + next], d = points[(ring + 1) * around + i];
                    Vector3 facing = (a + b + c + d) * 0.25f - middle;
                    AddTriangle(a, b, c, facing);
                    AddTriangle(a, c, d, facing);
                }
            }
        }

        // ---------------------------------------------------------------- Building

        // The solid spans of height in each column, as linked lists, each the next one up.
        private int[] _spanMin = [], _spanMax = [], _spanNext = [];
        private bool[] _spanWalkable = [];
        private int _spans, _freeSpan = -1;
        private int[] _head = [];

        /// <summary>Makes the navmesh. Can go on any thread; <paramref name="cancel"/> stops it.</summary>
        public NavMesh Build(CancellationToken cancel = default)
        {
            float cs = _cellSize, ch = Math.Clamp(cs * 0.25f, 0.02f, 0.05f);
            var navMesh = new NavMesh { AgentRadius = _radius, AgentHeight = _height, StepHeight = _step, CellSize = cs };
            if (_vertices.Count == 0)
            {
                navMesh.Finish();
                return navMesh;
            }

            // The grid covers everything, with a cell round it so edges there count as edges.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector3 v in _vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            min -= new Vector3(cs, 0f, cs);
            max += new Vector3(cs, 0f, cs);
            int width = (int)MathF.Ceiling((max.X - min.X) / cs), depth = (int)MathF.Ceiling((max.Z - min.Z) / cs);
            if ((long)width * depth > 25_000_000)
            {
                throw new InvalidOperationException($"The scene is too big for a navmesh of squares this small ({width} × {depth}): make the Cell Size bigger.");
            }

            float top = max.Y + _height + 1f;
            int levels = (int)MathF.Ceiling((top - min.Y) / ch);
            navMesh.Origin = new Vector2(min.X, min.Z);
            navMesh.Width = width;
            navMesh.Depth = depth;

            // 1. Fill the shapes into the columns.
            _head = new int[width * depth];
            Array.Fill(_head, -1);
            _spans = 0;
            _freeSpan = -1;
            int climb = (int)MathF.Floor(_step / ch);
            for (int t = 0; t < _walkable.Count; t++)
            {
                if ((t & 1023) == 0) cancel.ThrowIfCancellationRequested();
                Rasterize(_vertices[_indices[t * 3]], _vertices[_indices[t * 3 + 1]], _vertices[_indices[t * 3 + 2]], _walkable[t], min, cs, ch, width, depth, levels);
            }

            // 2. Low things a character steps over (kerbs, the edges of steps) can be walked on; what has too little room
            //    above it can't.
            int clearance = (int)MathF.Ceiling(_height / ch);
            for (int column = 0; column < _head.Length; column++)
            {
                bool belowWalkable = false;
                int belowTop = 0;
                for (int s = _head[column]; s >= 0; s = _spanNext[s])
                {
                    bool was = _spanWalkable[s];
                    if (!was && belowWalkable && _spanMax[s] - belowTop <= climb) _spanWalkable[s] = true;
                    belowWalkable = was;
                    belowTop = _spanMax[s];
                }

                for (int s = _head[column]; s >= 0; s = _spanNext[s])
                {
                    int ceiling = _spanNext[s] >= 0 ? _spanMin[_spanNext[s]] : int.MaxValue;
                    if ((long)ceiling - _spanMax[s] < clearance) _spanWalkable[s] = false;
                }
            }

            cancel.ThrowIfCancellationRequested();

            // 3. The cells: tops of walkable spans, with how much room is above.
            var columnStart = new int[width * depth + 1];
            var floors = new List<int>();
            var ceilings = new List<int>();
            var cellColumn = new List<int>();
            for (int column = 0; column < _head.Length; column++)
            {
                columnStart[column] = floors.Count;
                int inColumn = 0;
                for (int s = _head[column]; s >= 0; s = _spanNext[s])
                {
                    if (!_spanWalkable[s] || inColumn == 255) continue;
                    floors.Add(_spanMax[s]);
                    ceilings.Add(_spanNext[s] >= 0 ? _spanMin[_spanNext[s]] : int.MaxValue);
                    cellColumn.Add(column);
                    inColumn++;
                }
            }

            columnStart[^1] = floors.Count;
            int cells = floors.Count;

            // 4. Each cell's link to the cell beside it a character can step to, each way.
            var links = new int[cells * 4];
            Array.Fill(links, -1);
            for (int cell = 0; cell < cells; cell++)
            {
                int x = cellColumn[cell] % width, z = cellColumn[cell] / width;
                for (int side = 0; side < 4; side++)
                {
                    int nx = x + (side == NavMesh.Right ? 1 : side == NavMesh.Left ? -1 : 0);
                    int nz = z + (side == NavMesh.Forward ? 1 : side == NavMesh.Back ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= width || nz >= depth) continue;
                    int column = nx + nz * width, best = -1, bestStep = int.MaxValue;
                    for (int other = columnStart[column]; other < columnStart[column + 1]; other++)
                    {
                        int step = Math.Abs(floors[other] - floors[cell]);
                        long room = (long)Math.Min(ceilings[cell], ceilings[other]) - Math.Max(floors[cell], floors[other]);
                        if (step > climb || room < clearance || step >= bestStep) continue;
                        best = other;
                        bestStep = step;
                    }

                    links[cell * 4 + side] = best;
                }
            }

            // Only links both ways count, so stepping there and back always gets back.
            for (int cell = 0; cell < cells; cell++)
            {
                for (int side = 0; side < 4; side++)
                {
                    int other = links[cell * 4 + side];
                    if (other >= 0 && links[other * 4 + NavMesh.Opposite(side)] != cell) links[cell * 4 + side] = -1;
                }
            }

            cancel.ThrowIfCancellationRequested();

            // 5. Away from the edges by the radius: how far each cell is from one, in halves of a cell (diagonals 1.5).
            var distance = new int[cells];
            for (int cell = 0; cell < cells; cell++)
            {
                bool edge = false;
                for (int side = 0; side < 4; side++) edge |= links[cell * 4 + side] < 0;
                distance[cell] = edge ? 0 : int.MaxValue / 2;
            }

            int L(int cell, int side) => cell < 0 ? -1 : links[cell * 4 + side];
            void Nearer(int cell, int other, int cost)
            {
                if (other >= 0 && distance[other] + cost < distance[cell]) distance[cell] = distance[other] + cost;
            }

            for (int cell = 0; cell < cells; cell++)
            {
                Nearer(cell, L(cell, NavMesh.Left), 2);
                Nearer(cell, L(L(cell, NavMesh.Left), NavMesh.Back), 3);
                Nearer(cell, L(cell, NavMesh.Back), 2);
                Nearer(cell, L(L(cell, NavMesh.Back), NavMesh.Right), 3);
            }

            for (int cell = cells - 1; cell >= 0; cell--)
            {
                Nearer(cell, L(cell, NavMesh.Right), 2);
                Nearer(cell, L(L(cell, NavMesh.Right), NavMesh.Forward), 3);
                Nearer(cell, L(cell, NavMesh.Forward), 2);
                Nearer(cell, L(L(cell, NavMesh.Forward), NavMesh.Left), 3);
            }

            int keepFrom = (int)MathF.Ceiling(_radius / cs - 1e-3f) * 2;
            var kept = new bool[cells];
            for (int cell = 0; cell < cells; cell++) kept[cell] = distance[cell] >= keepFrom;

            // 6. Patches too small to matter go (the tops of crates).
            int smallest = (int)MathF.Ceiling(_minArea / (cs * cs));
            var island = new int[cells];
            Array.Fill(island, -1);
            var stack = new Stack<int>();
            var members = new List<int>();
            for (int cell = 0; cell < cells; cell++)
            {
                if (!kept[cell] || island[cell] >= 0) continue;
                members.Clear();
                island[cell] = cell;
                stack.Push(cell);
                while (stack.Count > 0)
                {
                    int at = stack.Pop();
                    members.Add(at);
                    for (int side = 0; side < 4; side++)
                    {
                        int next = links[at * 4 + side];
                        if (next < 0 || !kept[next] || island[next] >= 0) continue;
                        island[next] = cell;
                        stack.Push(next);
                    }
                }

                if (members.Count < smallest)
                {
                    foreach (int member in members) kept[member] = false;
                }
            }

            cancel.ThrowIfCancellationRequested();

            // 7. What's kept, numbered again, in column order.
            var renumber = new int[cells];
            int keptCount = 0;
            for (int cell = 0; cell < cells; cell++) renumber[cell] = kept[cell] ? keptCount++ : -1;

            navMesh.ColumnStart = new int[width * depth + 1];
            navMesh.CellColumn = new int[keptCount];
            navMesh.Heights = new float[keptCount];
            navMesh.Links = new int[keptCount * 4];
            int written = 0;
            for (int column = 0; column < width * depth; column++)
            {
                navMesh.ColumnStart[column] = written;
                for (int cell = columnStart[column]; cell < columnStart[column + 1]; cell++)
                {
                    if (!kept[cell]) continue;
                    navMesh.CellColumn[written] = column;
                    navMesh.Heights[written] = min.Y + floors[cell] * ch;
                    for (int side = 0; side < 4; side++)
                    {
                        int other = links[cell * 4 + side];
                        navMesh.Links[written * 4 + side] = other >= 0 ? renumber[other] : -1;
                    }

                    written++;
                }
            }

            navMesh.ColumnStart[^1] = written;

            // 8. Rectangles of cells, each as big as it'll grow (up to a side of 24 cells), to find paths across.
            MakeAreas(navMesh);
            navMesh.Finish();
            return navMesh;
        }

        private static void MakeAreas(NavMesh navMesh)
        {
            const int biggest = 24;
            int cells = navMesh.Heights.Length;
            var area = new int[cells];
            Array.Fill(area, -1);
            var areas = new List<NavMesh.Area>();
            var row = new List<int>();
            var rows = new List<int[]>();

            for (int cell = 0; cell < cells; cell++)
            {
                if (area[cell] >= 0) continue;

                // As far along x as it goes...
                row.Clear();
                row.Add(cell);
                for (int at = cell; row.Count < biggest;)
                {
                    int next = navMesh.Link(at, NavMesh.Right);
                    if (next < 0 || area[next] >= 0) break;
                    row.Add(next);
                    at = next;
                }

                // ...then as far along z as whole rows go, each joined across.
                rows.Clear();
                rows.Add(row.ToArray());
                while (rows.Count < biggest)
                {
                    int[] last = rows[^1];
                    var next = new int[last.Length];
                    bool fits = true;
                    for (int i = 0; i < last.Length && fits; i++)
                    {
                        next[i] = navMesh.Link(last[i], NavMesh.Forward);
                        fits = next[i] >= 0 && area[next[i]] < 0 && (i == 0 || navMesh.Link(next[i - 1], NavMesh.Right) == next[i]);
                    }

                    if (!fits) break;
                    rows.Add(next);
                }

                int index = areas.Count;
                foreach (int[] cellsInRow in rows)
                {
                    foreach (int member in cellsInRow) area[member] = index;
                }

                int x = navMesh.CellX(cell), z = navMesh.CellZ(cell);
                areas.Add(new NavMesh.Area { X0 = x, Z0 = z, X1 = x + row.Count, Z1 = z + rows.Count });
            }

            navMesh.CellArea = area;
            navMesh.Areas = areas.ToArray();
        }

        // ---------------------------------------------------------------- Filling in a triangle

        private void Rasterize(Vector3 a, Vector3 b, Vector3 c, bool walkable, Vector3 min, float cs, float ch, int width, int depth, int levels)
        {
            Vector3 low = Vector3.Min(a, Vector3.Min(b, c)), high = Vector3.Max(a, Vector3.Max(b, c));
            int z0 = Math.Clamp((int)MathF.Floor((low.Z - min.Z) / cs), 0, depth - 1), z1 = Math.Clamp((int)MathF.Floor((high.Z - min.Z) / cs), 0, depth - 1);

            Span<Vector3> triangle = [a, b, c];
            Span<Vector3> rowA = stackalloc Vector3[12], rowB = stackalloc Vector3[12];
            Span<Vector3> cellA = stackalloc Vector3[12], cellB = stackalloc Vector3[12];
            for (int z = z0; z <= z1; z++)
            {
                // The part of it in this row of columns...
                float zMin = min.Z + z * cs;
                int n = Clip(triangle, rowA, 2, zMin, keepAbove: true);
                n = Clip(rowA[..n], rowB, 2, zMin + cs, keepAbove: false);
                if (n < 3) continue;
                Span<Vector3> inRow = rowB[..n];

                float xLow = float.MaxValue, xHigh = float.MinValue;
                foreach (Vector3 v in inRow)
                {
                    xLow = MathF.Min(xLow, v.X);
                    xHigh = MathF.Max(xHigh, v.X);
                }

                int x0 = Math.Clamp((int)MathF.Floor((xLow - min.X) / cs), 0, width - 1), x1 = Math.Clamp((int)MathF.Floor((xHigh - min.X) / cs), 0, width - 1);
                for (int x = x0; x <= x1; x++)
                {
                    // ...and in each column of it: the heights it covers there.
                    float xMin = min.X + x * cs;
                    int m = Clip(inRow, cellA, 0, xMin, keepAbove: true);
                    m = Clip(cellA[..m], cellB, 0, xMin + cs, keepAbove: false);
                    if (m < 3) continue;

                    float yLow = float.MaxValue, yHigh = float.MinValue;
                    for (int i = 0; i < m; i++)
                    {
                        yLow = MathF.Min(yLow, cellB[i].Y);
                        yHigh = MathF.Max(yHigh, cellB[i].Y);
                    }

                    // A little leeway, so a floor at a whole number of steps isn't rounded up a step; a flat piece is a
                    // step thick below its top, so its top stays where it is.
                    int top = Math.Clamp((int)MathF.Ceiling((yHigh - min.Y) / ch - 1e-3f), 1, levels);
                    int bottom = Math.Clamp((int)MathF.Floor((yLow - min.Y) / ch + 1e-3f), 0, top - 1);
                    AddSpan(x + z * width, bottom, top, walkable);
                }
            }
        }

        /// <summary>The part of polygon <paramref name="input"/> on one side of a plane across axis <paramref name="axis"/> (0 x, 2 z).</summary>
        private static int Clip(ReadOnlySpan<Vector3> input, Span<Vector3> output, int axis, float at, bool keepAbove)
        {
            int count = 0;
            for (int i = 0; i < input.Length; i++)
            {
                Vector3 a = input[i], b = input[(i + 1) % input.Length];
                float da = (axis == 0 ? a.X : a.Z) - at, db = (axis == 0 ? b.X : b.Z) - at;
                if (!keepAbove)
                {
                    da = -da;
                    db = -db;
                }

                bool aIn = da >= 0f, bIn = db >= 0f;
                if (aIn && count < output.Length) output[count++] = a;
                if (aIn != bIn && count < output.Length) output[count++] = Vector3.Lerp(a, b, da / (da - db));
            }

            return count;
        }

        /// <summary>
        /// Adds a solid span to a column, joined with any it touches. Where two tops are near enough (a step's height of
        /// each other) either being walkable makes the top walkable; otherwise the higher top says.
        /// </summary>
        private void AddSpan(int column, int bottom, int top, bool walkable)
        {
            int previous = -1, current = _head[column];
            while (current >= 0)
            {
                if (_spanMin[current] > top) break;
                if (_spanMax[current] < bottom)
                {
                    previous = current;
                    current = _spanNext[current];
                    continue;
                }

                // Overlapping: one span of the two.
                if (Math.Abs(_spanMax[current] - top) <= 1) walkable |= _spanWalkable[current];
                else if (_spanMax[current] > top) walkable = _spanWalkable[current];
                bottom = Math.Min(bottom, _spanMin[current]);
                top = Math.Max(top, _spanMax[current]);

                int next = _spanNext[current];
                FreeSpan(current);
                if (previous >= 0) _spanNext[previous] = next;
                else _head[column] = next;
                current = next;
            }

            int span = NewSpan();
            _spanMin[span] = bottom;
            _spanMax[span] = top;
            _spanWalkable[span] = walkable;
            if (previous >= 0)
            {
                _spanNext[span] = _spanNext[previous];
                _spanNext[previous] = span;
            }
            else
            {
                _spanNext[span] = _head[column];
                _head[column] = span;
            }
        }

        private int NewSpan()
        {
            if (_freeSpan >= 0)
            {
                int free = _freeSpan;
                _freeSpan = _spanNext[free];
                return free;
            }

            if (_spans == _spanMin.Length)
            {
                int size = Math.Max(1024, _spans * 2);
                Array.Resize(ref _spanMin, size);
                Array.Resize(ref _spanMax, size);
                Array.Resize(ref _spanNext, size);
                Array.Resize(ref _spanWalkable, size);
            }

            return _spans++;
        }

        private void FreeSpan(int span)
        {
            _spanNext[span] = _freeSpan;
            _freeSpan = span;
        }
    }
}
