using System.IO.Compression;
using System.Numerics;

using Vertex.Rendering;

namespace Vertex.AI
{
    /// <summary>Whether <see cref="NavMesh.FindPath"/> found a way.</summary>
    public enum NavMeshPathStatus
    {
        /// <summary>There's no way at all: the start isn't near the navmesh.</summary>
        None,

        /// <summary>All the way to where it was asked to go.</summary>
        Complete,

        /// <summary>As near as can be got: where it was asked to go can't be reached from where it starts.</summary>
        Partial
    }

    /// <summary>A way across a navmesh: the corners to walk between, in order, the first where it starts.</summary>
    public sealed class NavMeshPath
    {
        public List<Vector3> Corners { get; } = new();

        public NavMeshPathStatus Status { get; internal set; }

        /// <summary>How far it is along the corners.</summary>
        public float Length
        {
            get
            {
                float length = 0f;
                for (int i = 1; i < Corners.Count; i++) length += Vector3.Distance(Corners[i - 1], Corners[i]);
                return length;
            }
        }

        public void Clear()
        {
            Corners.Clear();
            Status = NavMeshPathStatus.None;
        }
    }

    /// <summary>
    /// Where characters can walk in a scene, and the ways across it: the ground, floors, stairs and ramps that are flat
    /// enough, with room above for a character and kept a character's width from walls and drops. Made by
    /// <see cref="Build"/> (or baked in the editor, View › Navigation) from the scene's static meshes and colliders, and
    /// used by <see cref="NavMeshAgent"/>s to find their way.
    /// <para>
    /// It's a grid of small squares seen from above (more than one where floors are stacked), each at the height of the
    /// ground there and joined to the squares beside it that a character can step to. The squares are put together into
    /// rectangles, so a path is found from rectangle to rectangle, then pulled tight round the corners it has to go round.
    /// </para>
    /// </summary>
    public sealed class NavMesh
    {
        public const string Extension = ".navmesh";

        private const int FormatVersion = 1;
        private static ReadOnlySpan<byte> Magic => "VXNAV"u8;

        // Which way each of a cell's four links goes: -x, +x, -z, +z.
        internal const int Left = 0, Right = 1, Back = 2, Forward = 3;
        internal static int Opposite(int direction) => direction ^ 1;

        // ---------------------------------------------------------------- The grid

        /// <summary>Where the grid starts, its lowest corner along x and z.</summary>
        internal Vector2 Origin;

        internal float CellSize;
        internal int Width, Depth;

        /// <summary>Each column's first cell; a column's cells run to the next column's first.</summary>
        internal int[] ColumnStart = [0];

        /// <summary>Each cell's column (x + z × <see cref="Width"/>), and the height of the ground on it.</summary>
        internal int[] CellColumn = [];
        internal float[] Heights = [];

        /// <summary>Four to a cell (see <see cref="Left"/>...): the cell a character steps to that way, or -1 for none.</summary>
        internal int[] Links = [];

        /// <summary>Which rectangle each cell is in.</summary>
        internal int[] CellArea = [];

        internal struct Area
        {
            public int X0, Z0, X1, Z1;
            public int Island;
            public int FirstPortal, Portals;
        }

        internal struct Portal
        {
            public int To;
            public int Side;
            public Vector2 A, B;
        }

        internal Area[] Areas = [];
        internal Portal[] Portals = [];

        /// <summary>The size of character it was made for.</summary>
        public float AgentRadius { get; internal set; }
        public float AgentHeight { get; internal set; }
        public float StepHeight { get; internal set; }

        /// <summary>The file it was loaded from, if it was.</summary>
        public string? Source { get; internal set; }

        /// <summary>How many squares of ground it has.</summary>
        public int CellCount => Heights.Length;

        /// <summary>How many rectangles they're put together into.</summary>
        public int AreaCount => Areas.Length;

        /// <summary>How much ground can be walked on, in square units.</summary>
        public float WalkableArea => Heights.Length * CellSize * CellSize;

        public bool IsEmpty => Heights.Length == 0;

        /// <summary>
        /// The most rectangles one search for a path looks at. Past that, the way found goes only as near as it got (a
        /// <see cref="NavMeshPathStatus.Partial"/> path), and the agent finds the rest once it's nearer. It keeps a crowd
        /// on a huge map from each searching all of it. Most levels never reach it.
        /// </summary>
        public int SearchLimit { get; set; } = 3000;

        /// <summary>How many rectangles the last search looked at: for tests.</summary>
        internal int LastSearched { get; private set; }

        /// <summary>
        /// Makes the navmesh of <paramref name="scene"/> as it is now, for the size of character in
        /// <paramref name="settings"/> (the scene's own <see cref="Scene.Navigation"/> if null). For a level built as the
        /// game runs: give it to the scene (<c>scene.Navigation.NavMesh = NavMesh.Build(scene)</c>) for its agents to use.
        /// </summary>
        public static NavMesh Build(Scene scene, NavigationSettings? settings = null) => NavMeshBuilder.Prepare(scene, settings).Build();

        /// <summary>
        /// The navmesh agents in <paramref name="scene"/> walk on: the one given it or baked for it, or if there's none,
        /// one made from the scene as it is now (and said in the console, as baking it saves the wait).
        /// </summary>
        public static NavMesh For(Scene scene)
        {
            if (scene.Navigation.NavMesh is { } navMesh) return navMesh;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            NavMesh made = Build(scene);
            scene.Navigation.NavMesh = made;
            Log.Info($"The scene has no baked navmesh, so one was made as the game started ({timer.Elapsed.TotalMilliseconds:0} ms): bake it in the editor, under View › Navigation, to save the wait");
            return made;
        }

        // ---------------------------------------------------------------- Cells

        internal int CellX(int cell) => CellColumn[cell] % Width;
        internal int CellZ(int cell) => CellColumn[cell] / Width;

        internal Vector2 CellMin(int cell) => Origin + new Vector2(CellX(cell), CellZ(cell)) * CellSize;

        internal Vector3 CellCentre(int cell)
        {
            Vector2 min = CellMin(cell);
            return new Vector3(min.X + CellSize * 0.5f, Heights[cell], min.Y + CellSize * 0.5f);
        }

        internal int Link(int cell, int direction) => Links[cell * 4 + direction];

        /// <summary>The column <paramref name="x"/>, <paramref name="z"/> is in, or -1 off the grid.</summary>
        private int ColumnAt(float x, float z)
        {
            int cx = (int)MathF.Floor((x - Origin.X) / CellSize), cz = (int)MathF.Floor((z - Origin.Y) / CellSize);
            return cx < 0 || cz < 0 || cx >= Width || cz >= Depth ? -1 : cx + cz * Width;
        }

        /// <summary>The cell under <paramref name="point"/> nearest its height, within <paramref name="maxVertical"/>; -1 if there's none.</summary>
        internal int CellAt(Vector3 point, float maxVertical)
        {
            int column = ColumnAt(point.X, point.Z);
            if (column < 0) return -1;

            int best = -1;
            float bestGap = maxVertical;
            for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
            {
                float gap = MathF.Abs(Heights[cell] - point.Y);
                if (gap > bestGap) continue;
                bestGap = gap;
                best = cell;
            }

            return best;
        }

        /// <summary>
        /// The cell nearest <paramref name="point"/>: no further away along the ground than <paramref name="radius"/>,
        /// and up or down by no more than <paramref name="maxVertical"/>. -1 if there's none.
        /// </summary>
        /// <param name="island">Only cells of this island of connected ground, or any if -1.</param>
        internal int NearestCell(Vector3 point, float radius, float maxVertical, int island = -1)
        {
            if (IsEmpty) return -1;
            int direct = CellAt(point, MathF.Min(maxVertical, AgentHeight));
            if (direct >= 0 && (island < 0 || Areas[CellArea[direct]].Island == island)) return direct;

            int reach = (int)MathF.Ceiling(radius / CellSize);
            int cx = (int)MathF.Floor((point.X - Origin.X) / CellSize), cz = (int)MathF.Floor((point.Z - Origin.Y) / CellSize);
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int z = Math.Max(cz - reach, 0); z <= Math.Min(cz + reach, Depth - 1); z++)
            {
                for (int x = Math.Max(cx - reach, 0); x <= Math.Min(cx + reach, Width - 1); x++)
                {
                    int column = x + z * Width;
                    for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
                    {
                        float up = MathF.Abs(Heights[cell] - point.Y);
                        if (up > maxVertical) continue;
                        if (island >= 0 && Areas[CellArea[cell]].Island != island) continue;
                        Vector2 along = ClosestOnCell(cell, new Vector2(point.X, point.Z)) - new Vector2(point.X, point.Z);
                        float flat = along.Length();
                        if (flat > radius) continue;
                        float distance = flat * flat + up * up;
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        best = cell;
                    }
                }
            }

            return best;
        }

        private Vector2 ClosestOnCell(int cell, Vector2 point)
        {
            Vector2 min = CellMin(cell);
            return Vector2.Clamp(point, min, min + new Vector2(CellSize));
        }

        private Vector3 OnCell(int cell, Vector2 point)
        {
            Vector2 at = ClosestOnCell(cell, point);
            return new Vector3(at.X, Heights[cell], at.Y);
        }

        /// <summary>
        /// The same, but a hair inside the cell: a way that starts or ends exactly on the edge between two rectangles
        /// (along that edge) confuses the pulling tight, which can then send it back along the edge first.
        /// </summary>
        private Vector3 OnCellInside(int cell, Vector2 point)
        {
            Vector2 min = CellMin(cell), inset = new(CellSize * 0.02f);
            Vector2 at = Vector2.Clamp(point, min + inset, min + new Vector2(CellSize) - inset);
            return new Vector3(at.X, Heights[cell], at.Y);
        }

        // ---------------------------------------------------------------- Asking it things

        /// <summary>
        /// The nearest point on the navmesh to <paramref name="point"/>, no further than <paramref name="maxDistance"/>
        /// from it: where to put something so it can walk. False if there's nowhere that near.
        /// </summary>
        public bool SamplePosition(Vector3 point, float maxDistance, out Vector3 onMesh)
        {
            int cell = NearestCell(point, maxDistance, maxDistance);
            onMesh = cell >= 0 ? OnCell(cell, new Vector2(point.X, point.Z)) : point;
            return cell >= 0;
        }

        /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> (both on or near the navmesh) can be walked between.</summary>
        public bool Connected(Vector3 a, Vector3 b)
        {
            int from = NearestCell(a, 2f, AgentHeight), to = NearestCell(b, 2f, AgentHeight);
            return from >= 0 && to >= 0 && Areas[CellArea[from]].Island == Areas[CellArea[to]].Island;
        }

        /// <summary>
        /// A random point on the navmesh within <paramref name="radius"/> of <paramref name="around"/> that can be walked to
        /// from there: somewhere to wander to. False if none was found.
        /// </summary>
        public bool RandomPoint(Vector3 around, float radius, out Vector3 point, Random? random = null)
        {
            random ??= Random.Shared;
            point = around;
            int start = NearestCell(around, MathF.Max(radius, 2f), AgentHeight * 2f);
            if (start < 0) return false;
            int island = Areas[CellArea[start]].Island;

            for (int attempt = 0; attempt < 30; attempt++)
            {
                float angle = random.NextSingle() * MathF.Tau, distance = MathF.Sqrt(random.NextSingle()) * radius;
                var at = new Vector3(around.X + MathF.Cos(angle) * distance, around.Y, around.Z + MathF.Sin(angle) * distance);

                // Any floor in that column, near the height it started at.
                int column = ColumnAt(at.X, at.Z);
                if (column < 0) continue;
                int best = -1;
                float bestGap = radius + AgentHeight;
                for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
                {
                    float gap = MathF.Abs(Heights[cell] - around.Y);
                    if (gap >= bestGap || Areas[CellArea[cell]].Island != island) continue;
                    bestGap = gap;
                    best = cell;
                }

                if (best < 0) continue;
                point = new Vector3(at.X, Heights[best], at.Z);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Moves from <paramref name="from"/> towards <paramref name="to"/> along the navmesh, sliding along its edges rather
        /// than off it, and returns where it ends up, at the height of the ground there.
        /// </summary>
        public Vector3 MoveAlong(Vector3 from, Vector3 to)
        {
            int cell = NearestCell(from, 2f, AgentHeight);
            if (cell < 0) return to;
            return Slide(ref cell, OnCell(cell, new Vector2(from.X, from.Z)), to);
        }

        /// <summary>
        /// Whether something's in the way of walking straight from <paramref name="from"/> to <paramref name="to"/>: the
        /// edge of the navmesh, where <paramref name="hit"/> is. False if the way's clear.
        /// </summary>
        public bool Raycast(Vector3 from, Vector3 to, out Vector3 hit)
        {
            int cell = NearestCell(from, 2f, AgentHeight);
            hit = from;
            if (cell < 0) return true;
            var start = new Vector2(from.X, from.Z);
            bool blocked = Walk(ref cell, ref start, new Vector2(to.X, to.Z) - start, slide: false);
            hit = new Vector3(start.X, Heights[cell], start.Y);
            return blocked;
        }

        /// <summary>
        /// Moves along the navmesh from <paramref name="from"/> (on cell <paramref name="cell"/>) towards
        /// <paramref name="to"/>, sliding along edges, and returns where it got to; <paramref name="cell"/> becomes the cell
        /// it's on.
        /// </summary>
        internal Vector3 Slide(ref int cell, Vector3 from, Vector3 to)
        {
            var at = new Vector2(from.X, from.Z);
            Walk(ref cell, ref at, new Vector2(to.X - from.X, to.Z - from.Z), slide: true);
            return new Vector3(at.X, Heights[cell], at.Y);
        }

        /// <summary>
        /// Steps from cell to cell along <paramref name="move"/>. At an edge, it slides along it (or stops, without
        /// <paramref name="slide"/>). True if it met an edge.
        /// </summary>
        private bool Walk(ref int cell, ref Vector2 at, Vector2 move, bool slide)
        {
            const float inside = 1e-4f;
            bool blocked = false;
            for (int step = 0; step < 4096; step++)
            {
                // Kept in its cell: a point on the line between two can round to just outside the one it's in.
                Vector2 min = CellMin(cell), max = min + new Vector2(CellSize);
                at = Vector2.Clamp(at, min, max);
                Vector2 target = at + move;
                if (target.X >= min.X - 1e-5f && target.X <= max.X + 1e-5f && target.Y >= min.Y - 1e-5f && target.Y <= max.Y + 1e-5f)
                {
                    at = target;
                    return blocked;
                }

                // Where it leaves this cell: across a side along x, or along z.
                float tx = move.X > 0f ? (max.X - at.X) / move.X : move.X < 0f ? (min.X - at.X) / move.X : float.MaxValue;
                float tz = move.Y > 0f ? (max.Y - at.Y) / move.Y : move.Y < 0f ? (min.Y - at.Y) / move.Y : float.MaxValue;
                bool alongX = tx <= tz;
                float t = Math.Clamp(alongX ? tx : tz, 0f, 1f);
                at += move * t;
                move *= 1f - t;
                int direction = alongX ? (move.X > 0f ? Right : Left) : (move.Y > 0f ? Forward : Back);

                int next = Link(cell, direction);
                if (next >= 0)
                {
                    cell = next;
                    continue;
                }

                // The edge: kept just inside, and only the movement along it goes on.
                blocked = true;
                if (alongX)
                {
                    at.X = direction == Right ? max.X - inside : min.X + inside;
                    move.X = 0f;
                }
                else
                {
                    at.Y = direction == Forward ? max.Y - inside : min.Y + inside;
                    move.Y = 0f;
                }

                if (!slide || move.LengthSquared() < 1e-12f) return true;
            }

            return blocked;
        }

        /// <summary>The ground's height at <paramref name="point"/> (its x and z), on the floor nearest its y; false off the navmesh.</summary>
        public bool HeightAt(Vector3 point, out float height)
        {
            int cell = CellAt(point, AgentHeight);
            height = cell >= 0 ? Heights[cell] : point.Y;
            return cell >= 0;
        }

        // ---------------------------------------------------------------- Paths

        // Kept between searches, to save making them each time. Searches happen on the thread the game runs on.
        private float[] _cost = [];
        private int[] _came = [];
        private int[] _cameBy = [];
        private int[] _searched = [];
        private Vector2[] _entered = [];
        private int _search;
        private readonly PriorityQueue<int, float> _open = new();
        private readonly List<int> _corridor = new();
        private readonly List<(Vector2 Left, Vector2 Right)> _portals = new();
        private readonly List<Vector2> _pulled = new();
        private readonly object _searching = new();

        /// <summary>
        /// Finds the shortest way from <paramref name="from"/> to <paramref name="to"/> into <paramref name="path"/>. If
        /// <paramref name="to"/> can't be reached, the way is to the nearest point that can (and its
        /// <see cref="NavMeshPath.Status"/> says <see cref="NavMeshPathStatus.Partial"/>). False if there's no way at
        /// all: <paramref name="from"/> isn't near the navmesh.
        /// </summary>
        public bool FindPath(Vector3 from, Vector3 to, NavMeshPath path)
        {
            path.Clear();
            int start = NearestCell(from, 2f, AgentHeight);
            if (start < 0) return false;

            int startIsland = Areas[CellArea[start]].Island;
            int end = NearestCell(to, 4f, AgentHeight * 2f);

            // A goal on ground that can't be walked to from here (on a car roof, over a fence) is gone to from beside it,
            // if there's ground of this island near: otherwise the search would go through the whole island looking.
            bool besideGoal = false;
            if (end < 0 || Areas[CellArea[end]].Island != startIsland)
            {
                int beside = NearestCell(to, 6f, AgentHeight * 4f, startIsland);
                if (beside >= 0)
                {
                    end = beside;
                    besideGoal = true;
                }
            }

            lock (_searching)
            {
                int startArea = CellArea[start];
                Vector3 begin = OnCellInside(start, new Vector2(from.X, from.Z));
                Vector2 goal = new(to.X, to.Z);
                int goalArea = end >= 0 && Areas[CellArea[end]].Island == Areas[startArea].Island ? CellArea[end] : -1;

                int reached = Search(startArea, new Vector2(begin.X, begin.Z), goalArea, goal);
                Vector3 finish;
                if (reached == goalArea && end >= 0)
                {
                    finish = OnCell(end, goal);
                    path.Status = besideGoal ? NavMeshPathStatus.Partial : NavMeshPathStatus.Complete;
                }
                else
                {
                    // As near as can be got, in the nearest rectangle reached.
                    finish = NearestIn(reached, goal, to.Y);
                    path.Status = NavMeshPathStatus.Partial;
                }

                _corridor.Clear();
                for (int area = reached; area != startArea; area = _came[area]) _corridor.Add(area);
                _corridor.Add(startArea);
                _corridor.Reverse();
                Pull(begin, finish, path.Corners);
            }

            return true;
        }

        /// <summary>
        /// A* from rectangle to rectangle, through the middles of the portals between them. Returns the goal, or if it
        /// can't be got to, the rectangle that came nearest it.
        /// </summary>
        private int Search(int start, Vector2 startPoint, int goal, Vector2 goalPoint)
        {
            int count = Areas.Length;
            if (_cost.Length < count)
            {
                _cost = new float[count];
                _came = new int[count];
                _cameBy = new int[count];
                _searched = new int[count];
                _entered = new Vector2[count];
            }

            // A new number for each search marks what this one's seen, without clearing everything.
            if (++_search == int.MaxValue)
            {
                Array.Clear(_searched);
                _search = 1;
            }

            _open.Clear();
            _cost[start] = 0f;
            _came[start] = -1;
            _cameBy[start] = -1;
            _entered[start] = startPoint;
            _searched[start] = _search;
            _open.Enqueue(start, Vector2.Distance(startPoint, goalPoint));

            int nearest = start;
            float nearestLeft = DistanceTo(start, goalPoint);
            int looked = 0;
            LastSearched = 0;
            while (_open.TryDequeue(out int area, out float priority))
            {
                if (area == goal) return goal;
                if (priority > _cost[area] + Vector2.Distance(_entered[area], goalPoint) + 1e-3f) continue;

                // Too far to look this time: the way goes as near as it's got, and on from there next time.
                if (++looked > SearchLimit) break;
                LastSearched = looked;

                float left = DistanceTo(area, goalPoint);
                if (left < nearestLeft)
                {
                    nearestLeft = left;
                    nearest = area;
                }

                Area here = Areas[area];
                for (int p = here.FirstPortal; p < here.FirstPortal + here.Portals; p++)
                {
                    Portal portal = Portals[p];
                    Vector2 through = ClosestOnSegment(portal.A, portal.B, _entered[area]);
                    float cost = _cost[area] + Vector2.Distance(_entered[area], through);
                    int next = portal.To;
                    if (_searched[next] == _search && cost >= _cost[next]) continue;

                    _searched[next] = _search;
                    _cost[next] = cost;
                    _came[next] = area;
                    _cameBy[next] = p;
                    _entered[next] = through;
                    _open.Enqueue(next, cost + Vector2.Distance(through, goalPoint));
                }
            }

            return goal >= 0 && _searched[goal] == _search ? goal : nearest;
        }

        private float DistanceTo(int area, Vector2 point)
        {
            Area a = Areas[area];
            Vector2 min = Origin + new Vector2(a.X0, a.Z0) * CellSize, max = Origin + new Vector2(a.X1, a.Z1) * CellSize;
            return Vector2.Distance(point, Vector2.Clamp(point, min, max));
        }

        private static Vector2 ClosestOnSegment(Vector2 a, Vector2 b, Vector2 point)
        {
            Vector2 along = b - a;
            float t = Math.Clamp(Vector2.Dot(point - a, along) / MathF.Max(along.LengthSquared(), 1e-9f), 0.1f, 0.9f);
            return a + along * t;
        }

        /// <summary>The point of rectangle <paramref name="area"/> nearest <paramref name="point"/>, on its ground.</summary>
        private Vector3 NearestIn(int area, Vector2 point, float nearY)
        {
            Area a = Areas[area];
            Vector2 min = Origin + new Vector2(a.X0, a.Z0) * CellSize, max = Origin + new Vector2(a.X1, a.Z1) * CellSize;
            Vector2 at = Vector2.Clamp(point, min + new Vector2(1e-3f), max - new Vector2(1e-3f));
            int column = ColumnAt(at.X, at.Y);
            for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
            {
                if (CellArea[cell] == area) return new Vector3(at.X, Heights[cell], at.Y);
            }

            return new Vector3(at.X, nearY, at.Y);
        }

        /// <summary>
        /// Pulls the way through <see cref="_corridor"/> tight, as a string would be (the "funnel"): it only turns at the
        /// corners it has to go round.
        /// </summary>
        private void Pull(Vector3 begin, Vector3 finish, List<Vector3> corners)
        {
            var start = new Vector2(begin.X, begin.Z);
            var end = new Vector2(finish.X, finish.Z);

            // Each portal's two ends, as left and right going through it.
            _portals.Clear();
            _portals.Add((start, start));
            for (int i = 1; i < _corridor.Count; i++)
            {
                Portal portal = Portals[_cameBy[_corridor[i]]];
                Vector2 way = portal.Side switch { Right => Vector2.UnitX, Left => -Vector2.UnitX, Forward => Vector2.UnitY, _ => -Vector2.UnitY };
                Vector2 middle = (portal.A + portal.B) * 0.5f;
                bool aLeft = Cross(way, portal.A - middle) > 0f;
                _portals.Add(aLeft ? (portal.A, portal.B) : (portal.B, portal.A));
            }

            _portals.Add((end, end));

            _pulled.Clear();
            _pulled.Add(start);
            Vector2 apex = start, left = start, right = start;
            int apexIndex = 0, leftIndex = 0, rightIndex = 0;
            for (int i = 1; i < _portals.Count; i++)
            {
                (Vector2 nextLeft, Vector2 nextRight) = _portals[i];

                // The right side narrows the funnel, unless it crosses the left: then the left's a corner.
                if (Cross(right - apex, nextRight - apex) >= 0f)
                {
                    if (apex == right || Cross(left - apex, nextRight - apex) < 0f)
                    {
                        right = nextRight;
                        rightIndex = i;
                    }
                    else
                    {
                        _pulled.Add(left);
                        apex = left;
                        apexIndex = leftIndex;
                        right = left = apex;
                        rightIndex = leftIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }

                if (Cross(left - apex, nextLeft - apex) <= 0f)
                {
                    if (apex == left || Cross(right - apex, nextLeft - apex) > 0f)
                    {
                        left = nextLeft;
                        leftIndex = i;
                    }
                    else
                    {
                        _pulled.Add(right);
                        apex = right;
                        apexIndex = rightIndex;
                        right = left = apex;
                        rightIndex = leftIndex = apexIndex;
                        i = apexIndex;
                    }
                }
            }

            if (Vector2.DistanceSquared(_pulled[^1], end) > 1e-8f) _pulled.Add(end);

            // Each corner at the height of the ground there, on the floor nearest the way so far.
            corners.Add(begin);
            float y = begin.Y;
            for (int i = 1; i < _pulled.Count - 1; i++)
            {
                // A corner where the last one is (the start, on the corner of a rectangle) is left out.
                if (Vector2.DistanceSquared(_pulled[i], new Vector2(corners[^1].X, corners[^1].Z)) < 1e-4f) continue;
                y = GroundNear(_pulled[i], y);
                corners.Add(new Vector3(_pulled[i].X, y, _pulled[i].Y));
            }

            if (_pulled.Count > 1) corners.Add(finish);

            // Corners it needn't turn at: where it can walk straight from the one before to the one after. Pulling tight
            // only straightens the way within the rectangles the search went through, which aren't always the ones the
            // straight line crosses.
            for (int i = 1; i < corners.Count - 1;)
            {
                if (!Raycast(corners[i - 1], corners[i + 1], out _)) corners.RemoveAt(i);
                else i++;
            }
        }

        /// <summary>The height of the ground at a corner (on the edge between cells), on the floor nearest <paramref name="nearY"/>.</summary>
        private float GroundNear(Vector2 at, float nearY)
        {
            float best = nearY, bestGap = float.MaxValue;
            float nudge = CellSize * 0.25f;
            foreach (Vector2 offset in (ReadOnlySpan<Vector2>)[new(-nudge, -nudge), new(nudge, -nudge), new(-nudge, nudge), new(nudge, nudge)])
            {
                int column = ColumnAt(at.X + offset.X, at.Y + offset.Y);
                if (column < 0) continue;
                for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
                {
                    float gap = MathF.Abs(Heights[cell] - nearY);
                    if (gap >= bestGap) continue;
                    bestGap = gap;
                    best = Heights[cell];
                }
            }

            return best;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        // ---------------------------------------------------------------- Putting it together

        /// <summary>
        /// Works out the portals between rectangles (where a cell on one's edge steps to a cell of another) and which
        /// rectangles can be walked between (islands), from the cells, their links and their rectangles.
        /// </summary>
        internal void Finish()
        {
            var portals = new List<Portal>();
            for (int index = 0; index < Areas.Length; index++)
            {
                ref Area area = ref Areas[index];
                area.FirstPortal = portals.Count;

                // Each side: the cells along it, and where they step out to.
                for (int side = 0; side < 4; side++)
                {
                    bool alongZ = side is Left or Right;
                    int length = alongZ ? area.Z1 - area.Z0 : area.X1 - area.X0;
                    int runTo = -1, runStart = 0;
                    for (int i = 0; i <= length; i++)
                    {
                        int to = -1;
                        if (i < length)
                        {
                            int x = side switch { Left => area.X0, Right => area.X1 - 1, _ => area.X0 + i };
                            int z = side switch { Back => area.Z0, Forward => area.Z1 - 1, _ => area.Z0 + i };
                            int cell = AreaCellAt(index, x, z);
                            int next = cell >= 0 ? Link(cell, side) : -1;
                            to = next >= 0 ? CellArea[next] : -1;
                        }

                        if (to == runTo) continue;
                        if (runTo >= 0) portals.Add(MakePortal(area, side, runStart, i, runTo));
                        runTo = to;
                        runStart = i;
                    }
                }

                area.Portals = portals.Count - area.FirstPortal;
            }

            Portals = portals.ToArray();

            // Islands: rectangles joined by portals, one way or another.
            for (int i = 0; i < Areas.Length; i++) Areas[i].Island = -1;
            int island = 0;
            var stack = new Stack<int>();
            for (int i = 0; i < Areas.Length; i++)
            {
                if (Areas[i].Island >= 0) continue;
                Areas[i].Island = island;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    Area area = Areas[stack.Pop()];
                    for (int p = area.FirstPortal; p < area.FirstPortal + area.Portals; p++)
                    {
                        int to = Portals[p].To;
                        if (Areas[to].Island >= 0) continue;
                        Areas[to].Island = island;
                        stack.Push(to);
                    }
                }

                island++;
            }
        }

        private Portal MakePortal(in Area area, int side, int from, int to, int other)
        {
            float x0 = Origin.X + area.X0 * CellSize, x1 = Origin.X + area.X1 * CellSize;
            float z0 = Origin.Y + area.Z0 * CellSize, z1 = Origin.Y + area.Z1 * CellSize;
            (Vector2 a, Vector2 b) = side switch
            {
                Left => (new Vector2(x0, z0 + from * CellSize), new Vector2(x0, z0 + to * CellSize)),
                Right => (new Vector2(x1, z0 + from * CellSize), new Vector2(x1, z0 + to * CellSize)),
                Back => (new Vector2(x0 + from * CellSize, z0), new Vector2(x0 + to * CellSize, z0)),
                _ => (new Vector2(x0 + from * CellSize, z1), new Vector2(x0 + to * CellSize, z1))
            };
            return new Portal { To = other, Side = side, A = a, B = b };
        }

        /// <summary>Rectangle <paramref name="area"/>'s cell in column <paramref name="x"/>, <paramref name="z"/>.</summary>
        private int AreaCellAt(int area, int x, int z)
        {
            int column = x + z * Width;
            for (int cell = ColumnStart[column]; cell < ColumnStart[column + 1]; cell++)
            {
                if (CellArea[cell] == area) return cell;
            }

            return -1;
        }

        // ---------------------------------------------------------------- Showing it

        /// <summary>
        /// The navmesh as quads to draw over the scene (see <see cref="RenderView.Overlay"/>): its ground in
        /// <paramref name="fill"/>, a little above the surface, and its edges in <paramref name="edge"/>.
        /// </summary>
        public List<WorldQuad> Quads(Color fill, Color edge)
        {
            const float lift = 0.04f;
            const int block = 4;
            var quads = new List<WorldQuad>();
            var up = new Vector3(0f, lift, 0f);

            // How high and low each rectangle's ground goes: a flat one is drawn as one quad.
            var lowest = new float[Areas.Length];
            var highest = new float[Areas.Length];
            Array.Fill(lowest, float.MaxValue);
            Array.Fill(highest, float.MinValue);
            for (int cell = 0; cell < Heights.Length; cell++)
            {
                int area = CellArea[cell];
                lowest[area] = MathF.Min(lowest[area], Heights[cell]);
                highest[area] = MathF.Max(highest[area], Heights[cell]);
            }

            // The rest in pieces of up to 4 × 4 cells, their corners at the height of the cells there, so they follow
            // steps and slopes.
            for (int index = 0; index < Areas.Length; index++)
            {
                Area area = Areas[index];
                if (highest[index] - lowest[index] < 0.01f)
                {
                    float y = lowest[index] + lift;
                    float x0 = Origin.X + area.X0 * CellSize, x1 = Origin.X + area.X1 * CellSize, z0 = Origin.Y + area.Z0 * CellSize, z1 = Origin.Y + area.Z1 * CellSize;
                    quads.Add(new WorldQuad(null, new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0), new Vector3(x0, y, z0), Vector2.Zero, Vector2.One, fill));
                    continue;
                }

                for (int z = area.Z0; z < area.Z1; z += block)
                {
                    for (int x = area.X0; x < area.X1; x += block)
                    {
                        int xEnd = Math.Min(x + block, area.X1), zEnd = Math.Min(z + block, area.Z1);
                        Vector3 Corner(int cx, int cz, int px, int pz)
                        {
                            int cell = AreaCellAt(index, cx, cz);
                            return new Vector3(Origin.X + px * CellSize, cell >= 0 ? Heights[cell] : 0f, Origin.Y + pz * CellSize) + up;
                        }

                        quads.Add(new WorldQuad(null, Corner(x, zEnd - 1, x, zEnd), Corner(xEnd - 1, zEnd - 1, xEnd, zEnd), Corner(xEnd - 1, z, xEnd, z),
                            Corner(x, z, x, z), Vector2.Zero, Vector2.One, fill));
                    }
                }
            }

            // A strip along each edge (cell sides with nothing to step to), joined into one along a straight run at the
            // same height.
            var edges = new List<(int Side, int Line, int Height, int From)>();
            for (int cell = 0; cell < Heights.Length; cell++)
            {
                int x = CellX(cell), z = CellZ(cell), height = (int)MathF.Round(Heights[cell] * 100f);
                for (int side = 0; side < 4; side++)
                {
                    if (Link(cell, side) >= 0) continue;
                    edges.Add(side switch
                    {
                        Left => (side, x, height, z),
                        Right => (side, x + 1, height, z),
                        Back => (side, z, height, x),
                        _ => (side, z + 1, height, x)
                    });
                }
            }

            edges.Sort();
            float width = MathF.Min(CellSize * 0.35f, 0.06f);
            for (int i = 0; i < edges.Count;)
            {
                (int side, int line, int height, int from) = edges[i];
                int to = from + 1;
                for (i++; i < edges.Count && edges[i] == (side, line, height, to); i++) to++;

                float y = height / 100f + lift * 1.5f;
                float across = (side is Left or Right ? Origin.X : Origin.Y) + line * CellSize;
                float start = (side is Left or Right ? Origin.Y : Origin.X) + from * CellSize, end = start + (to - from) * CellSize;
                float inward = side is Left or Back ? width : -width;
                Vector3 Point(float along, float off) => side is Left or Right ? new Vector3(across + off, y, along) : new Vector3(along, y, across + off);
                quads.Add(new WorldQuad(null, Point(start, 0f), Point(end, 0f), Point(end, inward), Point(start, inward), Vector2.Zero, Vector2.One, edge));
            }

            return quads;
        }

        // ---------------------------------------------------------------- Files

        public void Save(string path)
        {
            string temporary = path + ".saving";
            using (FileStream file = File.Create(temporary)) Write(file);
            File.Move(temporary, path, overwrite: true);
        }

        public static NavMesh Load(string path)
        {
            using Stream file = AssetFiles.OpenRead(path);
            NavMesh navMesh = Read(file);
            navMesh.Source = Path.GetFullPath(path);
            return navMesh;
        }

        public void Write(Stream stream)
        {
            stream.Write(Magic);
            using var writer = new BinaryWriter(new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true));
            writer.Write(FormatVersion);
            writer.Write(AgentRadius);
            writer.Write(AgentHeight);
            writer.Write(StepHeight);
            writer.Write(Origin.X);
            writer.Write(Origin.Y);
            writer.Write(CellSize);
            writer.Write(Width);
            writer.Write(Depth);
            writer.Write(Heights.Length);
            writer.Write(Areas.Length);

            // How many cells each column has, then each cell.
            for (int column = 0; column < Width * Depth; column++) writer.Write((byte)(ColumnStart[column + 1] - ColumnStart[column]));
            for (int cell = 0; cell < Heights.Length; cell++)
            {
                writer.Write(Heights[cell]);
                writer.Write(CellArea[cell]);
                for (int side = 0; side < 4; side++) writer.Write(Link(cell, side));
            }

            foreach (Area area in Areas)
            {
                writer.Write(area.X0);
                writer.Write(area.Z0);
                writer.Write(area.X1);
                writer.Write(area.Z1);
            }
        }

        public static NavMesh Read(Stream stream)
        {
            Span<byte> magic = stackalloc byte[Magic.Length];
            stream.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("It isn't a navmesh file.");

            using var reader = new BinaryReader(new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true));
            int version = reader.ReadInt32();
            if (version != FormatVersion) throw new InvalidDataException($"It's a navmesh file of a kind this version of Vertex doesn't know ({version}).");

            var navMesh = new NavMesh
            {
                AgentRadius = reader.ReadSingle(),
                AgentHeight = reader.ReadSingle(),
                StepHeight = reader.ReadSingle(),
                Origin = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                CellSize = reader.ReadSingle(),
                Width = reader.ReadInt32(),
                Depth = reader.ReadInt32()
            };

            int cells = reader.ReadInt32(), areas = reader.ReadInt32();
            if (navMesh.Width < 0 || navMesh.Depth < 0 || (long)navMesh.Width * navMesh.Depth > 50_000_000 || cells < 0 || areas < 0 || navMesh.CellSize <= 0f)
            {
                throw new InvalidDataException("The navmesh file is damaged.");
            }

            int columns = navMesh.Width * navMesh.Depth;
            navMesh.ColumnStart = new int[columns + 1];
            navMesh.CellColumn = new int[cells];
            int at = 0;
            for (int column = 0; column < columns; column++)
            {
                navMesh.ColumnStart[column] = at;
                int count = reader.ReadByte();
                for (int i = 0; i < count && at < cells; i++) navMesh.CellColumn[at++] = column;
            }

            navMesh.ColumnStart[columns] = at;
            if (at != cells) throw new InvalidDataException("The navmesh file is damaged.");

            navMesh.Heights = new float[cells];
            navMesh.CellArea = new int[cells];
            navMesh.Links = new int[cells * 4];
            for (int cell = 0; cell < cells; cell++)
            {
                navMesh.Heights[cell] = reader.ReadSingle();
                navMesh.CellArea[cell] = reader.ReadInt32();
                for (int side = 0; side < 4; side++) navMesh.Links[cell * 4 + side] = reader.ReadInt32();
            }

            navMesh.Areas = new Area[areas];
            for (int i = 0; i < areas; i++)
            {
                navMesh.Areas[i] = new Area { X0 = reader.ReadInt32(), Z0 = reader.ReadInt32(), X1 = reader.ReadInt32(), Z1 = reader.ReadInt32() };
            }

            // Anything pointing outside what's there means the file's been damaged.
            foreach (int link in navMesh.Links)
            {
                if (link < -1 || link >= cells) throw new InvalidDataException("The navmesh file is damaged.");
            }

            foreach (int area in navMesh.CellArea)
            {
                if (area < 0 || area >= areas) throw new InvalidDataException("The navmesh file is damaged.");
            }

            foreach (Area area in navMesh.Areas)
            {
                if (area.X0 < 0 || area.Z0 < 0 || area.X1 > navMesh.Width || area.Z1 > navMesh.Depth || area.X0 >= area.X1 || area.Z0 >= area.Z1)
                {
                    throw new InvalidDataException("The navmesh file is damaged.");
                }
            }

            navMesh.Finish();
            return navMesh;
        }
    }
}
