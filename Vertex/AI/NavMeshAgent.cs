using System.Numerics;

using Vertex.Physics;

namespace Vertex.AI
{
    /// <summary>
    /// Walks its object round the scene's <see cref="NavMesh"/>: give it somewhere to go (<see cref="SetDestination"/>)
    /// and it finds the way, round walls and up stairs, speeds up, turns to face where it's going, slows to a stop when
    /// it gets there, and keeps out of other agents' way. A behaviour tree's Move To, Wander and Patrol drive it.
    /// <para>
    /// On its own it moves the object itself, along the navmesh's ground. With a <see cref="CharacterController"/> on the
    /// same object it moves that instead (with gravity), so the character's blocked and pushed like any other.
    /// </para>
    /// </summary>
    public sealed class NavMeshAgent : Component
    {
        /// <summary>The fastest it walks, in units a second.</summary>
        [Tooltip("The fastest it walks, in units a second")]
        public float Speed { get; set; } = 3.5f;

        /// <summary>How quickly it gets up to speed and slows down, in units a second, each second.</summary>
        [Tooltip("How quickly it gets up to speed and slows down")]
        public float Acceleration { get; set; } = 12f;

        /// <summary>How fast it turns to face where it's going, in degrees a second. 0 leaves its turning to you.</summary>
        [Tooltip("How fast it turns to face where it's going, in degrees a second; 0 leaves its turning to you")]
        public float TurnSpeed { get; set; } = 540f;

        /// <summary>How near where it's going it stops.</summary>
        [Tooltip("How near where it's going it stops")]
        public float StoppingDistance { get; set; } = 0.1f;

        /// <summary>How far it keeps from other agents, from its middle.</summary>
        [Tooltip("How far it keeps from other agents, from its middle")]
        public float Radius { get; set; } = 0.4f;

        /// <summary>Whether it steps round other agents rather than through them.</summary>
        [Tooltip("Steps round other agents rather than through them")]
        public bool AvoidOthers { get; set; } = true;

        private readonly NavMeshPath _path = new();
        private int _corner;
        private int _cell = -1;
        private NavMesh? _navMesh;
        private Vector3 _velocity;
        private float _fall;
        private bool _registered;
        private bool _toldNoNavMesh;
        private float _repathAt;

        // Every agent moving, to keep apart: those of scenes not being run drop out, and join again when they move.
        private static readonly List<NavMeshAgent> _agents = new();

        /// <summary>Where it's been told to go: the last <see cref="SetDestination"/>.</summary>
        [HideInInspector]
        public Vector3 Destination { get; private set; }

        /// <summary>Whether it's on its way somewhere.</summary>
        [HideInInspector]
        public bool HasPath => _path.Corners.Count > 1;

        /// <summary>Whether it got to where it was last told to go (or as near as it could), and stopped.</summary>
        [HideInInspector]
        public bool Arrived { get; private set; }

        /// <summary>Whether the way it's on goes all the way, or only as near as can be got.</summary>
        [HideInInspector]
        public NavMeshPathStatus PathStatus => _path.Status;

        /// <summary>The corners of the way it's on, from where it started.</summary>
        [HideInInspector]
        public IReadOnlyList<Vector3> Corners => _path.Corners;

        /// <summary>How fast and which way it's moving, in units a second.</summary>
        [HideInInspector]
        public Vector3 Velocity => _velocity;

        /// <summary>How far it still has to go along its way; 0 with none.</summary>
        [HideInInspector]
        public float RemainingDistance
        {
            get
            {
                if (!HasPath) return 0f;
                Vector3 at = Transform.WorldPosition;
                float left = Flat(_path.Corners[_corner] - at).Length();
                for (int i = _corner + 1; i < _path.Corners.Count; i++) left += Vector3.Distance(_path.Corners[i - 1], _path.Corners[i]);
                return left;
            }
        }

        /// <summary>The navmesh it walks on: the scene's (made for it if it has none baked). Null until it's started.</summary>
        [HideInInspector]
        public NavMesh? NavMesh => _navMesh;

        /// <summary>
        /// Sets off for <paramref name="point"/>, the shortest way. If it can't be got to, it goes as near as it can. False
        /// if there's no way at all (the agent isn't on the navmesh).
        /// </summary>
        public bool SetDestination(Vector3 point)
        {
            Destination = point;
            Arrived = false;
            if (FindNavMesh() is not { } navMesh) return false;

            if (!navMesh.FindPath(Transform.WorldPosition, point, _path))
            {
                _path.Clear();
                return false;
            }

            _corner = Math.Min(1, _path.Corners.Count - 1);
            return true;
        }

        /// <summary>Stops where it is (slowing down as it does), and forgets where it was going.</summary>
        public void Stop()
        {
            _path.Clear();
            _corner = 0;
        }

        /// <summary>Puts it straight at <paramref name="point"/> (on the navmesh, if it's near), without walking there; it stops.</summary>
        public void Warp(Vector3 point)
        {
            Stop();
            _velocity = Vector3.Zero;
            if (FindNavMesh() is { } navMesh)
            {
                _cell = navMesh.NearestCell(point, 2f, navMesh.AgentHeight);
                if (_cell >= 0) point = new Vector3(point.X, navMesh.Heights[_cell], point.Z);
            }

            Transform.WorldPosition = point;
        }

        private NavMesh? FindNavMesh()
        {
            if (GameObject?.Scene is not { } scene) return null;
            NavMesh navMesh = NavMesh.For(scene);
            if (navMesh != _navMesh)
            {
                _navMesh = navMesh;
                _cell = -1;
            }

            if (navMesh.IsEmpty && !_toldNoNavMesh)
            {
                _toldNoNavMesh = true;
                Log.Warning($"{GameObject.Name} has nowhere to walk: the scene's navmesh is empty. Tick Static on what's walked on, or give it a collider, and bake it (View › Navigation)");
            }

            return navMesh;
        }

        protected internal override void Start() => FindNavMesh();

        protected internal override void Update()
        {
            if (GetComponent<CharacterController>() == null) Move(Time.DeltaTime, null);
        }

        protected internal override void FixedUpdate()
        {
            if (GetComponent<CharacterController>() is { } controller) Move(Time.FixedDeltaTime, controller);
        }

        protected internal override void OnDestroy()
        {
            _agents.Remove(this);
            _registered = false;
        }

        private static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

        private void Move(float deltaTime, CharacterController? controller)
        {
            if (deltaTime <= 0f || FindNavMesh() is not { } navMesh || navMesh.IsEmpty) return;
            if (!_registered)
            {
                _agents.Add(this);
                _registered = true;
            }

            Vector3 at = Transform.WorldPosition;
            if (_cell < 0 || MathF.Abs(navMesh.Heights[_cell] - at.Y) > navMesh.AgentHeight)
            {
                _cell = navMesh.NearestCell(at, 2f, navMesh.AgentHeight);
                if (_cell < 0) return;
            }

            Vector3 wanted = Steer(navMesh, at);
            if (AvoidOthers) wanted = KeepApart(at, wanted);

            // Up to speed, or down to a stop, no faster than it accelerates.
            Vector3 change = wanted - _velocity;
            float most = MathF.Max(Acceleration, 0.01f) * deltaTime;
            _velocity = change.Length() <= most ? wanted : _velocity + Vector3.Normalize(change) * most;

            if (controller != null)
            {
                _fall = controller.IsGrounded ? -2f : _fall - 9.81f * deltaTime;
                controller.Velocity = new Vector3(_velocity.X, _fall, _velocity.Z);
                controller.Move(deltaTime);
                _cell = navMesh.NearestCell(Transform.WorldPosition, 2f, navMesh.AgentHeight);
            }
            else if (_velocity.LengthSquared() > 1e-8f)
            {
                // Along the ground, sliding along the navmesh's edges, and up and down steps smoothly.
                Vector3 to = navMesh.Slide(ref _cell, at, at + _velocity * deltaTime);
                float y = at.Y + (to.Y - at.Y) * MathF.Min(deltaTime * 20f, 1f);
                if (MathF.Abs(to.Y - at.Y) > navMesh.StepHeight * 2f) y = to.Y;
                Transform.WorldPosition = new Vector3(to.X, y, to.Z);
            }

            // It turns to face where it walks; shoved by others while standing (a crowd round something fighting), it
            // keeps facing as it was. Turning with the shove had big ones swinging round to face away mid-attack.
            if (HasPath) Turn(deltaTime);
        }

        /// <summary>The velocity that takes it along its way: towards the next corner, slowing to stop at the last.</summary>
        private Vector3 Steer(NavMesh navMesh, Vector3 at)
        {
            if (!HasPath) return Vector3.Zero;
            List<Vector3> corners = _path.Corners;

            // On past corners it's reached (or nearly: it needn't touch each one).
            while (_corner < corners.Count - 1 && Flat(corners[_corner] - at).Length() < MathF.Max(0.15f, Speed * 0.05f)) _corner++;

            // Pushed well off its way: a new one from here. Not more than twice a second, though: in a packed crowd they
            // push each other off all the time, and on its way back it heads for the corner it was going to anyway.
            Vector3 from = corners[Math.Max(_corner - 1, 0)], target = corners[_corner];
            if (Time.TotalTime >= _repathAt && DistanceToSegment(Flat(at), Flat(from), Flat(target)) > 1f && navMesh.FindPath(at, Destination, _path))
            {
                _repathAt = Time.TotalTime + 0.5f;
                _corner = Math.Min(1, corners.Count - 1);
                target = corners[_corner];
            }

            float left = RemainingDistance;
            if (left <= MathF.Max(StoppingDistance, 0.02f))
            {
                Arrived = true;
                Stop();
                return Vector3.Zero;
            }

            Vector3 way = Flat(target - at);
            if (way.LengthSquared() < 1e-8f) return Vector3.Zero;

            // Slows down in time to stop where it's going.
            float speed = MathF.Min(Speed, MathF.Sqrt(2f * MathF.Max(Acceleration, 0.01f) * MathF.Max(left - StoppingDistance, 0f)) + 0.05f);
            return Vector3.Normalize(way) * speed;
        }

        /// <summary>
        /// <paramref name="wanted"/>, turned to step round other agents ahead of it (each keeps to its own right, so two
        /// meeting pass), and pushed away from any it's nearer than both their radii, harder the nearer. Only agents in
        /// the squares round it are looked at (see <see cref="Regrid"/>), so a crowd of hundreds costs little more each.
        /// </summary>
        private Vector3 KeepApart(Vector3 at, Vector3 wanted)
        {
            Scene scene = GameObject.Scene;
            if (_gridTime != Time.UnscaledTotalTime || _gridScene != scene) Regrid(scene);

            float speed = wanted.Length();
            Vector3 heading = speed > 1e-4f ? wanted / speed : Vector3.Zero;
            var right = new Vector3(-heading.Z, 0f, heading.X);
            Vector3 push = Vector3.Zero, aside = Vector3.Zero;

            // Far enough to find any it could steer round (the biggest agents are about a unit across).
            float lookAhead = Radius + 1.2f + MathF.Max(speed, 0.5f) * 1.2f + 0.4f;
            int reach = (int)MathF.Ceiling(lookAhead / GridSquare);
            int cx = (int)MathF.Floor(at.X / GridSquare), cz = (int)MathF.Floor(at.Z / GridSquare);
            for (int x = cx - reach; x <= cx + reach; x++)
            {
                for (int z = cz - reach; z <= cz + reach; z++)
                {
                    if (!_grid.TryGetValue((x, z), out List<NavMeshAgent>? near)) continue;
                    foreach (NavMeshAgent other in near)
                    {
                        if (other == this) continue;
                        Vector3 away = Flat(at - other.Transform.WorldPosition);
                        float apart = away.Length(), room = Radius + other.Radius + 0.2f;
                        if (apart < 1e-4f) away = new Vector3(MathF.Cos(GetHashCode()), 0f, MathF.Sin(GetHashCode()));
                        else away /= apart;

                        // Ahead and coming near (within a second or so): steer to the side it isn't on.
                        float ahead = -Vector3.Dot(away, heading);
                        float within = room + MathF.Max(speed, 0.5f) * 1.2f;
                        if (speed > 1e-4f && ahead > 0.2f && apart < within)
                        {
                            float side = Vector3.Dot(away, right);
                            float sign = MathF.Abs(side) < 0.15f ? 1f : MathF.Sign(side);
                            aside += right * sign * ahead * (1f - apart / within);
                        }

                        if (apart < room) push += away * (1f - apart / room) * MathF.Max(Speed, 1f);
                    }
                }
            }

            if (aside != Vector3.Zero) wanted = Vector3.Normalize(heading + aside * 1.5f) * speed;
            return wanted + push;
        }

        // The moving agents of the scene running, by square, made again once a frame.
        private const float GridSquare = 2.5f;
        private static readonly Dictionary<(int, int), List<NavMeshAgent>> _grid = new();
        private static float _gridTime = float.NaN;
        private static Scene? _gridScene;

        /// <summary>Puts every moving agent of <paramref name="scene"/> into its square, and lets go of those that are gone.</summary>
        private static void Regrid(Scene scene)
        {
            _gridTime = Time.UnscaledTotalTime;
            _gridScene = scene;
            foreach (List<NavMeshAgent> square in _grid.Values) square.Clear();
            for (int i = _agents.Count - 1; i >= 0; i--)
            {
                NavMeshAgent agent = _agents[i];
                if (agent.GameObject == null || agent.GameObject.IsDestroyed)
                {
                    _agents.RemoveAt(i);
                    agent._registered = false;
                    continue;
                }

                if (agent.GameObject.Scene != scene || !agent.Enabled || !agent.GameObject.ActiveInHierarchy) continue;
                Vector3 at = agent.Transform.WorldPosition;
                var key = ((int)MathF.Floor(at.X / GridSquare), (int)MathF.Floor(at.Z / GridSquare));
                if (!_grid.TryGetValue(key, out List<NavMeshAgent>? square))
                {
                    square = new List<NavMeshAgent>();
                    _grid[key] = square;
                }

                square.Add(agent);
            }
        }

        private void Turn(float deltaTime)
        {
            if (TurnSpeed <= 0f) return;
            Vector3 facing = Flat(_velocity);
            if (facing.LengthSquared() < 0.04f) return;

            float yaw = MathF.Atan2(-facing.X, -facing.Z);
            Quaternion target = Quaternion.CreateFromYawPitchRoll(yaw, 0f, 0f);
            Quaternion now = Transform.WorldRotation;
            float angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(now, target)), 0f, 1f)) * 180f / MathF.PI;
            float step = TurnSpeed * deltaTime;
            Transform.WorldRotation = angle <= step ? target : Quaternion.Slerp(now, target, step / angle);
        }

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 along = b - a;
            float t = Math.Clamp(Vector3.Dot(point - a, along) / MathF.Max(along.LengthSquared(), 1e-9f), 0f, 1f);
            return Vector3.Distance(point, a + along * t);
        }
    }
}
