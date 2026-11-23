using System.Numerics;
using System.Runtime.InteropServices;

using JoltPhysicsSharp;

namespace Vertex.Physics
{
    /// <summary>
    /// A scene's physics simulation, powered by Jolt Physics. Each step it creates, updates and removes Jolt bodies
    /// to match the scene's colliders and rigidbodies, simulates, writes the results back to the Transforms,
    /// then sends collision events. Between steps it interpolates Transforms for smooth drawing.
    /// Nothing outside this class talks to Jolt.
    /// </summary>
    public sealed class PhysicsWorld
    {
        // Static bodies only need testing against moving ones, never against each other.
        private static readonly ObjectLayer _staticLayer = new(0);
        private static readonly ObjectLayer _movingLayer = new(1);

        // Collides with nothing: the body joints hold on to when they hold on to the world (see WorldAnchor).
        private static readonly ObjectLayer _anchorLayer = new(2);

        private static bool _foundationInitialised;

        private readonly Scene _scene;
        private readonly ObjectLayerPairFilterTable _objectLayerPairs;
        private readonly BroadPhaseLayerInterfaceTable _broadPhaseLayers;
        private readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;
        private readonly PhysicsSystem _system;
        private readonly JobSystemThreadPool _jobSystem;

        // What character movement collides with: everything except the moving character's own body.
        private readonly IgnoreOneBody _characterBodies = new();

        // What raycasts hit: everything solid (not triggers), optionally except one collider.
        private readonly IgnoreOneBody _raycastBodies = new() { SkipTriggers = true };
        private readonly AnyBroadPhaseLayer _anyBroadPhaseLayer = new();
        private readonly AnyObjectLayer _anyObjectLayer = new();
        private readonly AcceptAllShapes _allShapes = new();

        private readonly Dictionary<Collider, Body> _bodies = new();
        // Everything contacts can be reported for, by Jolt body ID: collider bodies and characters' inner bodies.
        private readonly Dictionary<uint, PhysicsObject> _bodiesById = new();
        private readonly Dictionary<CharacterController, Character> _characters = new();
        private readonly List<Collider> _colliders = new();
        private readonly List<Body> _toRemove = new();
        private readonly List<Character> _charactersToRemove = new();
        private int _stepIndex;

        // Jolt reports contacts from its worker threads during the step, so they're queued
        // and handled on the main thread afterwards, where game code can safely run.
        private readonly object _contactLock = new();
        private List<ContactEvent> _pendingContacts = new();
        private List<ContactEvent> _processingContacts = new();
        private readonly Dictionary<(uint, uint), Contact> _activeContacts = new();
        // Contacts Jolt dropped because the bodies fell asleep touching. Value: steps since one of them woke up.
        private readonly Dictionary<(uint, uint), int> _sleepingContacts = new();
        private readonly List<(uint, uint)> _endedKeys = new();
        private readonly List<Contact> _endedByRemoval = new();

        public Vector3 Gravity
        {
            get => _system.Gravity;
            set => _system.Gravity = value;
        }

        internal PhysicsWorld(Scene scene)
        {
            _scene = scene;

            if (!_foundationInitialised)
            {
                if (!Foundation.Init(false)) throw new InvalidOperationException("Failed to initialise Jolt Physics.");
                _foundationInitialised = true;
            }

            _objectLayerPairs = new ObjectLayerPairFilterTable(3);
            _objectLayerPairs.EnableCollision(_staticLayer, _movingLayer);
            _objectLayerPairs.EnableCollision(_movingLayer, _movingLayer);

            _broadPhaseLayers = new BroadPhaseLayerInterfaceTable(3, 2);
            _broadPhaseLayers.MapObjectToBroadPhaseLayer(_staticLayer, new BroadPhaseLayer(0));
            _broadPhaseLayers.MapObjectToBroadPhaseLayer(_movingLayer, new BroadPhaseLayer(1));
            _broadPhaseLayers.MapObjectToBroadPhaseLayer(_anchorLayer, new BroadPhaseLayer(0));

            _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayers, 2, _objectLayerPairs, 3);

            _system = new PhysicsSystem(new PhysicsSystemSettings
            {
                MaxBodies = 16384,
                MaxBodyPairs = 65536,
                MaxContactConstraints = 16384,
                ObjectLayerPairFilter = _objectLayerPairs,
                BroadPhaseLayerInterface = _broadPhaseLayers,
                ObjectVsBroadPhaseLayerFilter = _objectVsBroadPhase
            });

            _system.OnContactAdded += OnContactAdded;
            _system.OnContactPersisted += OnContactPersisted;
            _system.OnContactRemoved += OnContactRemoved;
            _system.OnContactValidate += OnContactValidate;

            _jobSystem = new JobSystemThreadPool();
        }

        /// <summary>
        /// Finds the closest solid collider along a ray. Triggers are ignored, and so is <paramref name="ignore"/>
        /// (e.g. the player's own CharacterController when shooting). Colliders added this frame are found from the next step.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit, Collider? ignore = null)
        {
            hit = default;
            if (direction == Vector3.Zero || maxDistance <= 0f) return false;

            Vector3 normalized = Vector3.Normalize(direction);
            _raycastBodies.Ignored = FindBodyId(ignore);

            // Jolt's rays carry their length in the direction, and report hits as a fraction of it.
            var ray = new Ray(origin, normalized * maxDistance);
            if (!_system.NarrowPhaseQuery.CastRay(ray, out RayCastResult result, _anyBroadPhaseLayer, _anyObjectLayer, _raycastBodies)) return false;
            if (!_bodiesById.TryGetValue(result.BodyID.ID, out PhysicsObject? target)) return false;

            float distance = result.Fraction * maxDistance;
            Vector3 point = origin + normalized * distance;
            hit = new RaycastHit(target.Collider, point, distance, SurfaceNormal(result, point, -normalized));
            return true;
        }

        /// <summary>
        /// Finds the closest solid collider along a ray that <paramref name="accept"/> says yes to: the rest are seen
        /// through, as if they weren't there (a game's shots going past its own hit boxes to find the wall behind).
        /// Triggers are ignored as always.
        /// </summary>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit, Func<Collider, bool> accept)
        {
            _raycastBodies.Accept = id => !_bodiesById.TryGetValue(id, out PhysicsObject? body) || accept(body.Collider);
            try
            {
                return Raycast(origin, direction, maxDistance, out hit);
            }
            finally
            {
                _raycastBodies.Accept = null;
            }
        }

        // The ball SphereCast moves, kept while casts ask for the same size.
        private SphereShape? _castSphere;
        private readonly List<ShapeCastResult> _castResults = new();
        private readonly ShapeCastSettings _castSettings = new() { BackFaceModeTriangles = BackFaceMode.IgnoreBackFaces, BackFaceModeConvex = BackFaceMode.IgnoreBackFaces };

        /// <summary>
        /// Finds the first solid collider a ball of <paramref name="radius"/> would touch moving from <paramref name="origin"/>
        /// along <paramref name="direction"/>: a raycast with thickness, for a camera that mustn't poke into walls. Triggers
        /// and <paramref name="ignore"/> are left out, as for <see cref="Raycast(Vector3, Vector3, float, out RaycastHit, Collider?)"/>.
        /// <see cref="RaycastHit.Distance"/> is how far the ball's centre got before touching, and <see cref="RaycastHit.Point"/>
        /// where it touched. A ball that starts out touching something hits it at 0.
        /// </summary>
        public bool SphereCast(Vector3 origin, float radius, Vector3 direction, float maxDistance, out RaycastHit hit, Collider? ignore = null)
        {
            if (radius <= 0f) return Raycast(origin, direction, maxDistance, out hit, ignore);
            hit = default;
            if (direction == Vector3.Zero || maxDistance <= 0f) return false;

            if (_castSphere == null || _castSphere.Radius != radius)
            {
                _castSphere?.Dispose();
                _castSphere = new SphereShape(radius);
            }

            Vector3 normalized = Vector3.Normalize(direction);
            _raycastBodies.Ignored = FindBodyId(ignore);
            // Jolt takes the matrix the other way round to System.Numerics (the move in its last column, not its last row).
            Matrix4x4 start = Matrix4x4.Transpose(Matrix4x4.CreateTranslation(origin));
            Vector3 motion = normalized * maxDistance;
            Vector3 baseOffset = Vector3.Zero;
            _castResults.Clear();
            if (!_system.NarrowPhaseQuery.CastShape(_castSphere, in start, in motion, _castSettings, in baseOffset, CollisionCollectorType.ClosestHit, _castResults,
                    _anyBroadPhaseLayer, _anyObjectLayer, _raycastBodies, _allShapes) || _castResults.Count == 0) return false;

            ShapeCastResult result = _castResults[0];
            foreach (ShapeCastResult other in _castResults) if (other.Fraction < result.Fraction) result = other;
            if (!_bodiesById.TryGetValue(result.BodyID2.ID, out PhysicsObject? target)) return false;

            // The axis points from the ball into what it touched.
            Vector3 normal = result.PenetrationAxis.LengthSquared() > 1e-12f ? -Vector3.Normalize(result.PenetrationAxis) : -normalized;
            hit = new RaycastHit(target.Collider, result.ContactPointOn2, result.Fraction * maxDistance, normal);
            return true;
        }

        /// <summary>Finds the closest solid collider along a line across the X-Y plane (at Z 0): a 2D game's raycast.</summary>
        public bool Raycast(Vector2 origin, Vector2 direction, float maxDistance, out RaycastHit hit, Collider? ignore = null) =>
            Raycast(new Vector3(origin, 0f), new Vector3(direction, 0f), maxDistance, out hit, ignore);

        /// <summary>The way the surface a ray hit faces at <paramref name="point"/>, or <paramref name="fallback"/> if Jolt can't say.</summary>
        private Vector3 SurfaceNormal(RayCastResult result, Vector3 point, Vector3 fallback)
        {
            BodyLockInterface locks = _system.BodyLockInterface;
            locks.LockRead(result.BodyID, out BodyLockRead bodyLock);
            try
            {
                if (!bodyLock.Succeeded || bodyLock.Body is not { } body) return fallback;

                Vector3 normal = body.GetWorldSpaceSurfaceNormal(new SubShapeID(result.subShapeID2), point);
                return normal.LengthSquared() > 0.5f ? normal : fallback;
            }
            finally
            {
                locks.UnlockRead(bodyLock);
            }
        }

        private BodyID FindBodyId(Collider? collider)
        {
            if (collider is CharacterController controller && _characters.TryGetValue(controller, out Character? character)) return character.Id;
            if (collider != null && _bodies.TryGetValue(collider, out Body? body)) return body.Id;
            return new BodyID(BodyID.InvalidBodyID);
        }

        internal void Step(float deltaTime)
        {
            SyncBodies(deltaTime);
            SyncJoints();

            PhysicsUpdateError error = _system.Update(deltaTime, 1, _jobSystem);
            if (error != PhysicsUpdateError.None) Log.Warning($"Physics step failed: {error}");

            ReadBack();
            ReadJoints(deltaTime);
            SendContacts();
            SendBrokenJoints();
        }

        // ---- Interpolation -----------------------------------------------------------------------------------------

        /// <summary>
        /// Puts interpolated objects back at their real simulated position before a frame's physics steps,
        /// so FixedUpdate and the simulation never see the in-between position that was drawn.
        /// </summary>
        internal void RestorePoses()
        {
            foreach (Body body in _bodies.Values)
            {
                if (body.Motion == MotionType.Dynamic && body.TransformUnchanged()) body.WriteTransform(body.Position, body.Rotation);
            }

            foreach (Character character in _characters.Values)
            {
                if (character.TransformUnchanged()) character.WriteTransform(character.Position);
            }
        }

        /// <summary>
        /// Draws interpolated objects between their last two physics positions.
        /// <paramref name="alpha"/> is how far the current frame is between those two steps, from 0 to 1.
        /// </summary>
        internal void Interpolate(float alpha)
        {
            foreach (Body body in _bodies.Values)
            {
                if (body.Motion != MotionType.Dynamic || body.Rigidbody?.Interpolate != true || !body.TransformUnchanged()) continue;

                body.WriteTransform(
                    Vector3.Lerp(body.PreviousPosition, body.Position, alpha),
                    Quaternion.Slerp(body.PreviousRotation, body.Rotation, alpha));
            }

            foreach (Character character in _characters.Values)
            {
                if (character.TransformUnchanged()) character.WriteTransform(Vector3.Lerp(character.PreviousPosition, character.Position, alpha));
            }
        }

        // ---- Bodies ------------------------------------------------------------------------------------------------

        /// <summary>Makes Jolt's bodies match the scene: adds new colliders, rebuilds changed ones, removes gone ones.</summary>
        private void SyncBodies(float deltaTime)
        {
            _stepIndex++;

            _colliders.Clear();
            _colliders.AddRange(_scene.FindComponents<Collider>());

            foreach (Collider collider in _colliders)
            {
                if (FirstEnabledCollider(collider.GameObject) != collider) continue;
                if (collider is ConvexCollider { Mesh: null } or MeshCollider { Mesh: null }) continue;

                Rigidbody? rigidbody = collider.GetComponent<Rigidbody>();
                // Jolt can only collide a mesh's triangles with things that move, so it never moves itself.
                if (rigidbody is { Enabled: false } || collider is MeshCollider) rigidbody = null;

                Vector3 scale = collider.Transform.WorldScale;

                if (_bodies.TryGetValue(collider, out Body? body) && !body.Matches(rigidbody, scale))
                {
                    RemoveBody(body);
                    body = null;
                }

                body ??= CreateBody(collider, rigidbody, scale);
                body.LastSeenStep = _stepIndex;
                PushToJolt(body, deltaTime);
            }

            // Anything not seen was destroyed, disabled, or lost its collider.
            foreach (Body body in _bodies.Values)
            {
                if (body.LastSeenStep != _stepIndex) _toRemove.Add(body);
            }

            foreach (Body body in _toRemove) RemoveBody(body);
            _toRemove.Clear();

            foreach (CharacterController controller in _scene.FindComponents<CharacterController>())
            {
                // Made before it first moves, and kept where its Transform is when something else moves it: another
                // machine's player, moved by the network, is still there to be shot and bumped into.
                if (!_characters.TryGetValue(controller, out Character? character)) character = CreateCharacter(controller);
                else if (!character.TransformUnchanged()) Teleport(character);
                character.LastSeenStep = _stepIndex;
            }

            foreach (Character character in _characters.Values)
            {
                if (character.LastSeenStep != _stepIndex) _charactersToRemove.Add(character);
            }

            foreach (Character character in _charactersToRemove) RemoveCharacter(character);
            _charactersToRemove.Clear();
        }

        /// <summary>Sends changes made by game code (moved Transforms, velocities, forces) to Jolt.</summary>
        private void PushToJolt(Body body, float deltaTime)
        {
            BodyInterface bodies = _system.BodyInterface;
            Transform transform = body.Collider.Transform;
            Vector3 position = transform.WorldPosition;
            Quaternion rotation = transform.WorldRotation;
            bool moved = !body.TransformUnchanged();

            switch (body.Motion)
            {
                case MotionType.Static:
                    if (moved) bodies.SetPositionAndRotation(body.Id, position, rotation, Activation.DontActivate);
                    break;

                case MotionType.Kinematic:
                    // Kinematic bodies follow their Transform; Jolt works out the velocity needed to get there.
                    bodies.MoveKinematic(body.Id, position, rotation, deltaTime);
                    break;

                case MotionType.Dynamic:
                    // Game code set the Transform directly: teleport the body there.
                    if (moved) bodies.SetPositionAndRotation(body.Id, position, rotation, Activation.Activate);
                    break;
            }

            // Static and kinematic bodies always go where their Transform says; a teleported body doesn't interpolate.
            if (moved || body.Motion != MotionType.Dynamic) body.Teleported(transform, position, rotation);

            Rigidbody? rigidbody = body.Rigidbody;
            if (rigidbody == null) return;

            if (body.Motion == MotionType.Dynamic)
            {
                if (rigidbody.VelocityChanged) bodies.SetLinearVelocity(body.Id, rigidbody.CurrentVelocity);
                if (rigidbody.AngularVelocityChanged) bodies.SetAngularVelocity(body.Id, rigidbody.CurrentAngularVelocity);
                if (rigidbody.PendingForce != Vector3.Zero) bodies.AddForce(body.Id, rigidbody.PendingForce);
                if (rigidbody.PendingImpulse != Vector3.Zero) bodies.AddImpulse(body.Id, rigidbody.PendingImpulse);
                if (rigidbody.PendingTorque != Vector3.Zero) bodies.AddTorque(body.Id, rigidbody.PendingTorque);
            }

            // Kinematic bodies ignore forces, so these are dropped rather than saved up.
            rigidbody.VelocityChanged = false;
            rigidbody.AngularVelocityChanged = false;
            rigidbody.PendingForce = Vector3.Zero;
            rigidbody.PendingImpulse = Vector3.Zero;
            rigidbody.PendingTorque = Vector3.Zero;
        }

        /// <summary>Copies the simulated positions and velocities back to the Transforms and Rigidbodies.</summary>
        private void ReadBack()
        {
            BodyInterface bodies = _system.BodyInterface;

            foreach (Body body in _bodies.Values)
            {
                if (body.Rigidbody == null) continue;

                if (!bodies.IsActive(body.Id))
                {
                    // Asleep: nothing moved, so there's nothing to interpolate between.
                    body.PreviousPosition = body.Position;
                    body.PreviousRotation = body.Rotation;
                    continue;
                }

                body.Rigidbody.CurrentVelocity = bodies.GetLinearVelocity(body.Id);
                body.Rigidbody.CurrentAngularVelocity = bodies.GetAngularVelocity(body.Id);

                if (body.Motion != MotionType.Dynamic) continue;

                body.PreviousPosition = body.Position;
                body.PreviousRotation = body.Rotation;
                body.Position = bodies.GetPosition(body.Id);
                body.Rotation = bodies.GetRotation(body.Id);
                body.WriteTransform(body.Position, body.Rotation);
            }
        }

        private Body CreateBody(Collider collider, Rigidbody? rigidbody, Vector3 scale)
        {
            // Every shape made for it, inner ones first, so they can all be let go of when the body goes.
            var shapes = new List<Shape>(2);
            Shape shape = CreateShape(collider, scale, shapes);

            if (collider.Center != Vector3.Zero)
            {
                shape = new RotatedTranslatedShape(collider.Center * scale, Quaternion.Identity, shape);
                shapes.Add(shape);
            }

            // Locked along and about every axis, it can't move at all: it stays where it's put, like a kinematic body.
            bool locked = rigidbody != null && (rigidbody.LockPosition | rigidbody.AlwaysLockedPosition) == Axes.All && (rigidbody.LockRotation | rigidbody.AlwaysLockedRotation) == Axes.All;
            MotionType motion = rigidbody == null ? MotionType.Static
                : rigidbody.IsKinematic || locked ? MotionType.Kinematic
                : MotionType.Dynamic;

            Transform transform = collider.Transform;
            Vector3 position = transform.WorldPosition;
            Quaternion rotation = transform.WorldRotation;

            using var settings = new BodyCreationSettings(shape, position, rotation, motion,
                motion == MotionType.Static ? _staticLayer : _movingLayer)
            {
                Friction = collider.Friction,
                Restitution = collider.Bounciness,
                IsSensor = collider.IsTrigger
            };

            if (rigidbody != null)
            {
                // Jolt works out how the mass is spread through the shape; we only give it the total.
                settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
                settings.MassPropertiesOverride = new MassProperties { Mass = rigidbody.Mass };
                settings.GravityFactor = rigidbody.UseGravity ? 1f : 0f;
                settings.LinearDamping = rigidbody.Drag;
                settings.AngularDamping = rigidbody.AngularDrag;
                settings.MotionQuality = rigidbody.ContinuousCollision ? MotionQuality.LinearCast : MotionQuality.Discrete;
                settings.LinearVelocity = rigidbody.CurrentVelocity;
                settings.AngularVelocity = rigidbody.CurrentAngularVelocity;
                if (motion == MotionType.Dynamic)
                {
                    settings.AllowedDOFs = AllowedMovement(rigidbody.LockPosition | rigidbody.AlwaysLockedPosition, rigidbody.LockRotation | rigidbody.AlwaysLockedRotation);
                }

                // Lets kinematic bodies (and triggers) report touching static colliders, e.g. a player hitting a wall.
                settings.CollideKinematicVsNonDynamic = rigidbody.IsKinematic;

                // Already applied through the creation settings above.
                rigidbody.VelocityChanged = false;
                rigidbody.AngularVelocityChanged = false;
            }

            BodyID id = _system.BodyInterface.CreateAndAddBody(settings,
                motion == MotionType.Static ? Activation.DontActivate : Activation.Activate);

            var body = new Body(collider, rigidbody, id, motion, shapes, scale);
            body.Teleported(transform, position, rotation);

            _bodies[collider] = body;
            _bodiesById[id.ID] = body;
            return body;
        }

        /// <summary>Jolt's name for the ways a body is free to move: every axis but the locked ones.</summary>
        private static AllowedDOFs AllowedMovement(Axes lockPosition, Axes lockRotation)
        {
            AllowedDOFs allowed = 0;
            if ((lockPosition & Axes.X) == 0) allowed |= AllowedDOFs.TranslationX;
            if ((lockPosition & Axes.Y) == 0) allowed |= AllowedDOFs.TranslationY;
            if ((lockPosition & Axes.Z) == 0) allowed |= AllowedDOFs.TranslationZ;
            if ((lockRotation & Axes.X) == 0) allowed |= AllowedDOFs.RotationX;
            if ((lockRotation & Axes.Y) == 0) allowed |= AllowedDOFs.RotationY;
            if ((lockRotation & Axes.Z) == 0) allowed |= AllowedDOFs.RotationZ;
            return allowed;
        }

        /// <summary>The collider's shape, at its object's scale. Every shape made for it goes into <paramref name="made"/>, the one returned last.</summary>
        private static Shape CreateShape(Collider collider, Vector3 scale, List<Shape> made)
        {
            Shape shape = CreateShape(collider, scale, out Shape? inner);
            if (inner != null) made.Add(inner);
            made.Add(shape);
            return shape;
        }

        /// <param name="inner">A shape the returned one is built round, if it is (a 2D circle's cylinder, turned to lie along Z).</param>
        private static Shape CreateShape(Collider collider, Vector3 scale, out Shape? inner)
        {
            // Guards against a zero scale, which Jolt can't build a shape from.
            Vector3 size = Vector3.Max(Vector3.Abs(scale), new Vector3(0.001f));
            inner = null;

            switch (collider)
            {
                case BoxCollider box:
                    return Box(box.Size * size * 0.5f);

                // 2D shapes lie in the X-Y plane and go Depth deep along Z, so things a little nearer or further still meet.
                case BoxCollider2D box:
                    return Box(new Vector3(box.Size * new Vector2(size.X, size.Y), box.Depth * size.Z) * 0.5f);

                case CircleCollider2D circle:
                {
                    // Jolt's cylinders stand along Y: turned a quarter about X, it lies along Z.
                    float radius = circle.Radius * MathF.Max(size.X, size.Y);
                    float halfDepth = MathF.Max(circle.Depth * size.Z * 0.5f, 0.01f);
                    inner = new CylinderShape(halfDepth, radius);
                    return new RotatedTranslatedShape(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f), inner);
                }

                case CapsuleCollider2D capsule:
                {
                    float radius = capsule.Radius * size.X;
                    float halfCylinder = capsule.Height * size.Y * 0.5f - radius;
                    return halfCylinder > 0.001f ? new CapsuleShape(halfCylinder, radius) : new SphereShape(radius);
                }

                case SphereCollider sphere:
                    return new SphereShape(sphere.Radius * MathF.Max(size.X, MathF.Max(size.Y, size.Z)));

                case CapsuleCollider capsule:
                {
                    float radius = capsule.Radius * MathF.Max(size.X, size.Z);
                    float halfCylinder = capsule.Height * size.Y * 0.5f - radius;
                    return halfCylinder > 0.001f ? new CapsuleShape(halfCylinder, radius) : new SphereShape(radius);
                }

                case ConvexCollider { Mesh: { } mesh }:
                {
                    var points = new Vector3[mesh.Vertices.Length];
                    for (int i = 0; i < points.Length; i++) points[i] = mesh.Vertices[i].Position * size;

                    using var settings = new ConvexHullShapeSettings(points, Foundation.DefaultConvexRadius);
                    return settings.Create();
                }

                case MeshCollider { Mesh: { } mesh }:
                    return CreateMeshShape(mesh, size);

                default:
                    throw new NotSupportedException($"{collider.GetType().Name} is not supported by the physics world.");
            }
        }

        private static Shape Box(Vector3 halfExtents)
        {
            // Jolt rounds box corners slightly for speed and stability; the rounding can't exceed the box.
            float convexRadius = MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));
            return new BoxShape(halfExtents, convexRadius);
        }

        /// <summary>
        /// A shape of the mesh's triangles. Corners in the same place are joined into one first: meshes keep separate
        /// copies where faces meet (for sharp shading), and Jolt needs to see that triangles share an edge to slide
        /// smoothly over it rather than catching on it.
        /// </summary>
        private static Shape CreateMeshShape(Rendering.Mesh mesh, Vector3 scale)
        {
            var points = new List<Vector3>();
            var pointIndices = new Dictionary<(int, int, int), int>();
            var remap = new int[mesh.Vertices.Length];
            for (int i = 0; i < remap.Length; i++)
            {
                Vector3 position = mesh.Vertices[i].Position * scale;
                var key = ((int)MathF.Round(position.X * 1000f), (int)MathF.Round(position.Y * 1000f), (int)MathF.Round(position.Z * 1000f));
                if (!pointIndices.TryGetValue(key, out int index))
                {
                    index = points.Count;
                    points.Add(position);
                    pointIndices[key] = index;
                }
                remap[i] = index;
            }

            var triangles = new List<IndexedTriangle>(mesh.Indices.Length / 3);
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = remap[mesh.Indices[i]], b = remap[mesh.Indices[i + 1]], c = remap[mesh.Indices[i + 2]];
                if (a == b || b == c || a == c) continue;
                triangles.Add(new IndexedTriangle(a, b, c, 0, 0));
            }

            using var settings = new MeshShapeSettings(CollectionsMarshal.AsSpan(points), CollectionsMarshal.AsSpan(triangles));
            return settings.Create();
        }

        private void RemoveBody(Body body)
        {
            // Jolt mustn't be left with a joint holding a body that's gone.
            RemoveJointsOn(body.Id);
            _system.BodyInterface.RemoveAndDestroyBody(body.Id);
            body.DisposeShapes();

            _bodies.Remove(body.Collider);
            EndContacts(body);
        }

        private void EndContacts(PhysicsObject removed)
        {
            _bodiesById.Remove(removed.Id.ID);

            // Jolt reports these contacts as ended after the next step, but by then the body is gone, so end them here.
            // The exit callbacks wait until after the step, so game code never runs in the middle of syncing.
            foreach (KeyValuePair<(uint, uint), Contact> entry in _activeContacts)
            {
                if (entry.Value.A == removed || entry.Value.B == removed) _endedKeys.Add(entry.Key);
            }

            foreach ((uint, uint) key in _endedKeys)
            {
                _sleepingContacts.Remove(key);
                if (_activeContacts.Remove(key, out Contact contact)) _endedByRemoval.Add(contact);
            }

            _endedKeys.Clear();
        }

        private bool IsAwake(PhysicsObject body) => body.Motion != MotionType.Static && _system.BodyInterface.IsActive(body.Id);

        private static Collider? FirstEnabledCollider(GameObject gameObject)
        {
            foreach (Component component in gameObject.Components)
            {
                // Character controllers are colliders too, but they're simulated separately, not as bodies.
                if (component is Collider { Enabled: true } collider and not CharacterController) return collider;
            }

            return null;
        }

        // ---- Joints ------------------------------------------------------------------------------------------------

        private readonly Dictionary<Joint, JointRecord> _joints = new();
        private readonly List<JointRecord> _jointsToRemove = new();
        private readonly List<Joint> _broken = new();
        private BodyID? _worldAnchor;
        private Shape? _worldAnchorShape;

        // Joined pairs that mustn't collide (Jolt body IDs, smaller first). Read from Jolt's worker threads during the
        // step, so it's only ever swapped for a new set between steps, never changed.
        private volatile HashSet<(uint, uint)> _unjoinedPairs = new();

        /// <summary>
        /// A body that never moves and touches nothing, for joints to hold on to when they hold on to the world: Jolt
        /// joins two bodies, so a joint to "the world" is a joint to this.
        /// </summary>
        private BodyID WorldAnchor()
        {
            if (_worldAnchor is { } existing) return existing;

            _worldAnchorShape = new SphereShape(0.01f);
            using var settings = new BodyCreationSettings(_worldAnchorShape, Vector3.Zero, Quaternion.Identity, MotionType.Static, _anchorLayer) { IsSensor = true };
            BodyID id = _system.BodyInterface.CreateAndAddBody(settings, Activation.DontActivate);
            _worldAnchor = id;
            return id;
        }

        private Body? BodyOf(GameObject gameObject) =>
            FirstEnabledCollider(gameObject) is { } collider && _bodies.TryGetValue(collider, out Body? body) ? body : null;

        /// <summary>Makes Jolt's joints match the scene's: new ones made, changed ones made again, gone ones removed.</summary>
        private void SyncJoints()
        {
            bool pairsChanged = false;

            foreach (Joint joint in _scene.FindComponents<Joint>())
            {
                if (joint.IsBroken) continue;

                // Both ends need a body: its own object's, and the connected one's (or the world's).
                Body? own = BodyOf(joint.GameObject);
                if (own == null) continue;

                BodyID other;
                if (joint.ConnectedBody is { } connected)
                {
                    if (connected.GameObject.IsDestroyed || BodyOf(connected.GameObject) is not { } connectedBody || connectedBody == own) continue;
                    other = connectedBody.Id;
                }
                else
                {
                    other = WorldAnchor();
                }

                if (_joints.TryGetValue(joint, out JointRecord? record) &&
                    (record.Version != joint.Version || record.Body1.ID != other.ID || record.Body2.ID != own.Id.ID))
                {
                    RemoveJoint(record);
                    pairsChanged = true;
                    record = null;
                }

                if (record == null)
                {
                    record = CreateJoint(joint, other, own);
                    if (record == null) continue;
                    pairsChanged = true;
                }

                record.LastSeenStep = _stepIndex;
                DriveMotor(record);
            }

            // Anything not seen was destroyed, switched off, broken, or lost a body.
            foreach (JointRecord record in _joints.Values)
            {
                if (record.LastSeenStep != _stepIndex) _jointsToRemove.Add(record);
            }

            foreach (JointRecord record in _jointsToRemove) RemoveJoint(record);
            pairsChanged |= _jointsToRemove.Count > 0;
            _jointsToRemove.Clear();

            if (pairsChanged) UpdateUnjoinedPairs();
        }

        private JointRecord? CreateJoint(Joint joint, BodyID body1, Body own)
        {
            Transform transform = joint.Transform;
            Vector3 anchor = Vector3.Transform(joint.Anchor, transform.WorldMatrix);

            TwoBodyConstraintSettings settings;
            switch (joint)
            {
                case FixedJoint:
                    settings = new FixedConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        AutoDetectPoint = false,
                        Point1 = anchor,
                        Point2 = anchor,
                        AxisX1 = Vector3.UnitX,
                        AxisY1 = Vector3.UnitY,
                        AxisX2 = Vector3.UnitX,
                        AxisY2 = Vector3.UnitY
                    };
                    break;

                case HingeJoint hinge:
                {
                    Vector3 axis = hinge.WorldAxis;
                    Vector3 normal = Perpendicular(axis);
                    float min = hinge.UseLimits ? MathHelper.ToRadians(Math.Clamp(hinge.MinAngle, -180f, 0f)) : -MathF.PI;
                    float max = hinge.UseLimits ? MathHelper.ToRadians(Math.Clamp(hinge.MaxAngle, 0f, 180f)) : MathF.PI;
                    settings = new HingeConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        Point1 = anchor,
                        Point2 = anchor,
                        HingeAxis1 = axis,
                        HingeAxis2 = axis,
                        NormalAxis1 = normal,
                        NormalAxis2 = normal,
                        LimitsMin = min,
                        LimitsMax = max,
                        MaxFrictionTorque = MathF.Max(hinge.Friction, 0f)
                    };
                    break;
                }

                case SliderJoint slider:
                {
                    Vector3 axis = slider.WorldAxis;
                    Vector3 normal = Perpendicular(axis);
                    settings = new SliderConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        AutoDetectPoint = false,
                        Point1 = anchor,
                        Point2 = anchor,
                        SliderAxis1 = axis,
                        SliderAxis2 = axis,
                        NormalAxis1 = normal,
                        NormalAxis2 = normal,
                        LimitsMin = slider.UseLimits ? MathF.Min(slider.MinDistance, 0f) : -float.MaxValue,
                        LimitsMax = slider.UseLimits ? MathF.Max(slider.MaxDistance, 0f) : float.MaxValue,
                        MaxFrictionForce = MathF.Max(slider.Friction, 0f)
                    };
                    break;
                }

                case BallJoint ball when ball.SwingLimit >= 179.9f && ball.TwistLimit >= 179.9f:
                    settings = new PointConstraintSettings { Space = ConstraintSpace.WorldSpace, Point1 = anchor, Point2 = anchor };
                    break;

                case BallJoint ball:
                {
                    Vector3 axis = ball.WorldAxis;
                    Vector3 plane = Perpendicular(axis);
                    float swing = MathHelper.ToRadians(Math.Clamp(ball.SwingLimit, 0f, 180f));
                    float twist = MathHelper.ToRadians(Math.Clamp(ball.TwistLimit, 0f, 180f));
                    settings = new SwingTwistConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        Position1 = anchor,
                        Position2 = anchor,
                        TwistAxis1 = axis,
                        TwistAxis2 = axis,
                        PlaneAxis1 = plane,
                        PlaneAxis2 = plane,
                        NormalHalfConeAngle = swing,
                        PlaneHalfConeAngle = swing,
                        TwistMinAngle = -twist,
                        TwistMaxAngle = twist
                    };
                    break;
                }

                case DistanceJoint distance:
                {
                    Vector3 end = ConnectedPoint(joint, distance.ConnectedAnchor);
                    float apart = Vector3.Distance(anchor, end);
                    float max = distance.MaxDistance > 0f ? distance.MaxDistance : apart;
                    settings = new DistanceConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        Point1 = end,
                        Point2 = anchor,
                        MinDistance = Math.Clamp(distance.MinDistance, 0f, max),
                        MaxDistance = max
                    };
                    break;
                }

                case SpringJoint spring:
                {
                    Vector3 end = ConnectedPoint(joint, spring.ConnectedAnchor);
                    float length = spring.AutoLength ? Vector3.Distance(anchor, end) : MathF.Max(spring.Length, 0f);
                    settings = new DistanceConstraintSettings
                    {
                        Space = ConstraintSpace.WorldSpace,
                        Point1 = end,
                        Point2 = anchor,
                        MinDistance = length,
                        MaxDistance = length,
                        LimitsSpringSettings = new SpringSettings(SpringMode.FrequencyAndDamping, MathF.Max(spring.Stiffness, 0.01f), Math.Clamp(spring.Damping, 0f, 1f))
                    };
                    break;
                }

                default:
                    return null;
            }

            // Jolt makes the joint from the two bodies themselves. Nothing is simulating now, so they needn't be locked.
            BodyLockInterface locks = _system.BodyLockInterfaceNoLock;
            locks.LockWrite(body1, out BodyLockWrite lock1);
            locks.LockWrite(own.Id, out BodyLockWrite lock2);
            TwoBodyConstraint constraint;
            try
            {
                if (!lock1.Succeeded || !lock2.Succeeded || lock1.Body is not { } first || lock2.Body is not { } second) return null;
                constraint = settings.CreateConstraint(first, second);
            }
            finally
            {
                locks.UnlockWrite(lock2);
                locks.UnlockWrite(lock1);
            }

            _system.AddConstraint(constraint);
            Wake(own.Id);
            Wake(body1);

            var record = new JointRecord(joint, constraint, body1, own.Id, joint.Version, joint.CollideConnected);
            _joints[joint] = record;
            return record;
        }

        /// <summary>The far end of a distance or spring joint, in the world: on the connected body, or a point in the world.</summary>
        private static Vector3 ConnectedPoint(Joint joint, Vector3 connectedAnchor) =>
            joint.ConnectedBody is { } connected ? Vector3.Transform(connectedAnchor, connected.Transform.WorldMatrix) : connectedAnchor;

        /// <summary>Any direction at right angles to <paramref name="axis"/>.</summary>
        private static Vector3 Perpendicular(Vector3 axis)
        {
            Vector3 other = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(axis, other));
        }

        /// <summary>Sends a hinge's or slider's motor its settings, which change in place without making the joint again.</summary>
        private void DriveMotor(JointRecord record)
        {
            switch (record.Joint, record.Constraint)
            {
                case (HingeJoint hinge, HingeConstraint constraint):
                {
                    MotorState state = hinge.Motor switch { JointMotor.Speed => MotorState.Velocity, JointMotor.Target => MotorState.Position, _ => MotorState.Off };
                    if (state != MotorState.Off)
                    {
                        var motor = new MotorSettings(2f, 1f);
                        motor.SetTorqueLimit(MathF.Max(hinge.MotorStrength, 0f));
                        constraint.MotorSettings = motor;
                        constraint.TargetAngularVelocity = MathHelper.ToRadians(hinge.MotorSpeed);
                        constraint.TargetAngle = MathHelper.ToRadians(hinge.TargetAngle);
                        KeepAwake(record);
                    }

                    if (constraint.MotorState != state) constraint.MotorState = state;
                    break;
                }

                case (SliderJoint slider, SliderConstraint constraint):
                {
                    MotorState state = slider.Motor switch { JointMotor.Speed => MotorState.Velocity, JointMotor.Target => MotorState.Position, _ => MotorState.Off };
                    if (state != MotorState.Off)
                    {
                        var motor = new MotorSettings(2f, 1f);
                        motor.SetForceLimit(MathF.Max(slider.MotorStrength, 0f));
                        constraint.MotorSettings = motor;
                        constraint.TargetVelocity = slider.MotorSpeed;
                        constraint.TargetPosition = slider.TargetPosition;
                        KeepAwake(record);
                    }

                    if (constraint.MotorState != state) constraint.MotorState = state;
                    break;
                }
            }
        }

        /// <summary>Wakes a body that can move; static ones (the world anchor, a collider with no rigidbody) never sleep.</summary>
        private void Wake(BodyID id)
        {
            BodyInterface bodies = _system.BodyInterface;
            if (bodies.GetMotionType(id) != MotionType.Static && !bodies.IsActive(id)) bodies.ActivateBody(id);
        }

        /// <summary>A motor only turns what's awake: Jolt lets resting bodies sleep, motor or not.</summary>
        private void KeepAwake(JointRecord record)
        {
            BodyInterface bodies = _system.BodyInterface;
            if (!bodies.IsActive(record.Body2)) bodies.ActivateBody(record.Body2);
        }

        /// <summary>How hard each joint pulled this step, where hinges and sliders are now, and which have broken.</summary>
        private void ReadJoints(float deltaTime)
        {
            if (_joints.Count == 0) return;

            foreach (JointRecord record in _joints.Values)
            {
                // Jolt keeps what it took to hold the joint as an impulse over the step: over the step's time, a force.
                float impulse = record.Constraint switch
                {
                    FixedConstraint c => c.TotalLambdaPosition.Length(),
                    HingeConstraint c => c.TotalLambdaPosition.Length(),
                    SliderConstraint c => c.TotalLambdaPosition.Length(),
                    PointConstraint c => c.TotalLambdaPosition.Length(),
                    SwingTwistConstraint c => c.TotalLambdaPosition.Length(),
                    DistanceConstraint c => MathF.Abs(c.TotalLambdaPosition),
                    _ => 0f
                };

                Joint joint = record.Joint;
                joint.CurrentForce = deltaTime > 0f ? impulse / deltaTime : 0f;
                if (record.Constraint is HingeConstraint hinge && joint is HingeJoint hingeJoint) hingeJoint.Angle = MathHelper.ToDegrees(hinge.CurrentAngle);
                if (record.Constraint is SliderConstraint slider && joint is SliderJoint sliderJoint) sliderJoint.Position = slider.CurrentPosition;

                if (joint.BreakForce > 0f && joint.CurrentForce > joint.BreakForce) _broken.Add(joint);
            }

            foreach (Joint joint in _broken)
            {
                joint.IsBroken = true;
                RemoveJoint(_joints[joint]);
            }

            if (_broken.Count > 0) UpdateUnjoinedPairs();
        }

        /// <summary>Tells the game about the joints that broke this step, once Jolt's done with them.</summary>
        private void SendBrokenJoints()
        {
            if (_broken.Count == 0) return;

            // Copied first: a script hearing of one may repair it, or break another.
            Joint[] broken = _broken.ToArray();
            _broken.Clear();
            foreach (Joint joint in broken) joint.Break();
        }

        private void RemoveJoint(JointRecord record)
        {
            _system.RemoveConstraint(record.Constraint);
            record.Constraint.Dispose();
            _joints.Remove(record.Joint);

            // Something held still by a joint falls asleep, and letting go doesn't wake it: a broken chain's lamp would
            // hang in the air. Wake both, so they move as soon as they're free.
            Wake(record.Body1);
            Wake(record.Body2);
        }

        private void RemoveJointsOn(BodyID body)
        {
            foreach (JointRecord record in _joints.Values)
            {
                if (record.Body1.ID == body.ID || record.Body2.ID == body.ID) _jointsToRemove.Add(record);
            }

            foreach (JointRecord record in _jointsToRemove) RemoveJoint(record);
            if (_jointsToRemove.Count > 0) UpdateUnjoinedPairs();
            _jointsToRemove.Clear();
        }

        private void UpdateUnjoinedPairs()
        {
            var pairs = new HashSet<(uint, uint)>();
            foreach (JointRecord record in _joints.Values)
            {
                if (record.CollideConnected) continue;
                uint a = record.Body1.ID, b = record.Body2.ID;
                pairs.Add(a < b ? (a, b) : (b, a));
            }

            _unjoinedPairs = pairs;
        }

        // Called on Jolt's worker threads, before two bodies' contact is made: joined bodies that shouldn't collide don't.
        private ValidateResult OnContactValidate(PhysicsSystem system, in JoltPhysicsSharp.Body body1, in JoltPhysicsSharp.Body body2, RVector3 baseOffset,
            in CollideShapeResult result)
        {
            HashSet<(uint, uint)> pairs = _unjoinedPairs;
            if (pairs.Count == 0) return ValidateResult.AcceptAllContactsForThisBodyPair;

            uint a = body1.ID.ID, b = body2.ID.ID;
            return pairs.Contains(a < b ? (a, b) : (b, a)) ? ValidateResult.RejectAllContactsForThisBodyPair : ValidateResult.AcceptAllContactsForThisBodyPair;
        }

        /// <summary>A Jolt joint and what it was made from, so changes can be spotted.</summary>
        private sealed class JointRecord(Joint joint, TwoBodyConstraint constraint, BodyID body1, BodyID body2, int version, bool collideConnected)
        {
            public Joint Joint { get; } = joint;
            public TwoBodyConstraint Constraint { get; } = constraint;
            public BodyID Body1 { get; } = body1;
            public BodyID Body2 { get; } = body2;
            public int Version { get; } = version;
            public bool CollideConnected { get; } = collideConnected;
            public int LastSeenStep;
        }

        // ---- Characters --------------------------------------------------------------------------------------------

        internal void MoveCharacter(CharacterController controller, float deltaTime)
        {
            if (_characters.TryGetValue(controller, out Character? character) && character.Version != controller.Version)
            {
                RemoveCharacter(character);
                character = null;
            }

            character ??= CreateCharacter(controller);
            CharacterVirtual jolt = character.Jolt;

            // Game code set the Transform directly: teleport the character there.
            if (!character.TransformUnchanged()) Teleport(character);

            Vector3 velocity = controller.Velocity;
            jolt.LinearVelocity = velocity;

            // The character mustn't bump into its own body.
            _characterBodies.Ignored = character.Id;

            if (jolt.GroundState == GroundState.OnGround)
            {
                // On walkable ground, also step up stairs and stick to the floor going down slopes, like Source.
                float stepHeight = MathF.Max(controller.StepHeight, 0f);
                var settings = new ExtendedUpdateSettings
                {
                    StickToFloorStepDown = new Vector3(0f, -stepHeight, 0f),
                    WalkStairsStepUp = new Vector3(0f, stepHeight, 0f)
                };

                jolt.ExtendedUpdate(deltaTime, settings, _movingLayer, _system, _characterBodies, _allShapes);
            }
            else
            {
                // In the air or on a steep slope, just move. ExtendedUpdate would first cancel any velocity heading into
                // steep slopes, and pushing into a slope is exactly what keeps you on a surf ramp.
                jolt.Update(deltaTime, _movingLayer, _system, _characterBodies, _allShapes);
            }

            // The body follows during the next physics step, pushing rigidbodies out of the way like any kinematic body.
            _system.BodyInterface.MoveKinematic(character.Id, jolt.Position, Quaternion.Identity, deltaTime);

            // Remove any part of the velocity that went into something it actually hit (Quake's ClipVelocity), measured
            // against how fast that thing moves, so standing on something rising or sinking rides along with it.
            foreach (CharacterContact contact in jolt.GetActiveContacts())
            {
                if (!contact.HadCollision || contact.IsSensorB) continue;

                float into = Vector3.Dot(velocity - contact.LinearVelocity, contact.ContactNormal);
                if (into >= 0f) continue;

                if (contact.MotionTypeB == MotionType.Dynamic)
                {
                    // Loose props (crates, balls) get shoved out of the way rather than stopping the character dead, so
                    // brushing one at speed doesn't cost all the speed going into it. Landing on one still stops the fall.
                    if (contact.ContactNormal.Y > 0f && velocity.Y < 0f) velocity.Y = 0f;
                    continue;
                }

                velocity -= contact.ContactNormal * into;
            }

            controller.Velocity = velocity;
            controller.IsGrounded = jolt.GroundState == GroundState.OnGround;
            controller.GroundNormal = controller.IsGrounded ? jolt.GroundNormal : Vector3.UnitY;
            controller.GroundVelocity = controller.IsGrounded ? jolt.GroundVelocity : Vector3.Zero;
            controller.GroundCollider = controller.IsGrounded && _bodiesById.TryGetValue(jolt.GroundBodyId, out PhysicsObject? ground) ? ground.Collider : null;

            character.PreviousPosition = character.Position;
            character.Position = jolt.Position;
            character.WriteTransform(character.Position);
        }

        /// <summary>Puts a character where its Transform now is, without sweeping it across the world in between.</summary>
        private void Teleport(Character character)
        {
            Vector3 position = character.Controller.Transform.WorldPosition;
            character.Jolt.Position = position;
            // Jump the body too, rather than sweeping it across the world and knocking everything in between.
            _system.BodyInterface.SetPositionAndRotation(character.Id, position, Quaternion.Identity, Activation.Activate);
            character.Teleported(position);
        }

        private Character CreateCharacter(CharacterController controller)
        {
            float radius = MathF.Max(controller.Radius, 0.01f);
            float height = MathF.Max(controller.Height, radius * 2f);

            // The capsule sits on top of the Transform's position, which is at the character's feet.
            Shape capsule = new CapsuleShape(height * 0.5f - radius, radius);
            Shape shape = new RotatedTranslatedShape(new Vector3(0f, height * 0.5f, 0f), Quaternion.Identity, capsule);

            var settings = new CharacterVirtualSettings
            {
                Shape = shape,
                MaxSlopeAngle = MathHelper.ToRadians(controller.MaxSlopeAngle),
                Mass = controller.Mass,
                MaxStrength = controller.PushStrength,
                // Only contacts on the bottom half-sphere count as standing on something, not ones along the side.
                SupportingVolume = new Plane(Vector3.UnitY, -radius)
            };

            Vector3 position = controller.Transform.WorldPosition;
            var jolt = new CharacterVirtual(settings, position, Quaternion.Identity, 0, _system);

            // A kinematic body that follows the character, so rigidbodies (thrown balls, falling crates), triggers and
            // other characters notice it. Jolt can make one itself, but doesn't remove it when the character goes away.
            using var bodySettings = new BodyCreationSettings(shape, position, Quaternion.Identity, MotionType.Kinematic, _movingLayer);
            BodyID bodyId = _system.BodyInterface.CreateAndAddBody(bodySettings, Activation.Activate);

            var character = new Character(controller, jolt, bodyId, capsule, shape);
            character.Teleported(position);
            _characters[controller] = character;
            _bodiesById[bodyId.ID] = character;
            return character;
        }

        private void RemoveCharacter(Character character)
        {
            EndContacts(character);
            _system.BodyInterface.RemoveAndDestroyBody(character.Id);
            character.Jolt.Dispose();
            character.DisposeShapes();
            _characters.Remove(character.Controller);
        }

        // ---- Contacts ----------------------------------------------------------------------------------------------

        // Called on Jolt's worker threads: only record what happened.
        private void OnContactAdded(PhysicsSystem system, in JoltPhysicsSharp.Body body1, in JoltPhysicsSharp.Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            QueueContact(ContactPhase.Enter, body1.ID.ID, body2.ID.ID, manifold);
        }

        private void OnContactPersisted(PhysicsSystem system, in JoltPhysicsSharp.Body body1, in JoltPhysicsSharp.Body body2,
            in ContactManifold manifold, ref ContactSettings settings)
        {
            QueueContact(ContactPhase.Stay, body1.ID.ID, body2.ID.ID, manifold);
        }

        private void OnContactRemoved(PhysicsSystem system, ref SubShapeIDPair pair)
        {
            lock (_contactLock)
            {
                _pendingContacts.Add(new ContactEvent(ContactPhase.Exit, pair.Body1ID.ID, pair.Body2ID.ID, default, default));
            }
        }

        private void QueueContact(ContactPhase phase, uint body1, uint body2, in ContactManifold manifold)
        {
            // Average the contact points: one point for a sphere, up to four for a box resting flat.
            Vector3 point = Vector3.Zero;
            uint count = manifold.PointCount;
            for (uint i = 0; i < count; i++) point += manifold.GetWorldSpaceContactPointOn1(i);
            if (count > 0) point /= count;

            lock (_contactLock)
            {
                _pendingContacts.Add(new ContactEvent(phase, body1, body2, point, manifold.WorldSpaceNormal));
            }
        }

        /// <summary>Runs the collision callbacks for everything Jolt reported during the step.</summary>
        private void SendContacts()
        {
            lock (_contactLock)
            {
                (_pendingContacts, _processingContacts) = (_processingContacts, _pendingContacts);
            }

            foreach (Contact contact in _endedByRemoval) Send(ContactPhase.Exit, contact);
            _endedByRemoval.Clear();

            foreach (ContactEvent e in _processingContacts)
            {
                (uint, uint) key = e.Body1 < e.Body2 ? (e.Body1, e.Body2) : (e.Body2, e.Body1);

                if (e.Phase == ContactPhase.Exit)
                {
                    if (!_activeContacts.TryGetValue(key, out Contact ended)) continue;

                    // Jolt also ends contacts when the bodies fall asleep, but they're still touching (a crate resting
                    // on the floor), so keep the contact until one of them wakes up and moves away.
                    if (!IsAwake(ended.A) && !IsAwake(ended.B))
                    {
                        _sleepingContacts[key] = 0;
                        continue;
                    }

                    _activeContacts.Remove(key);
                    _sleepingContacts.Remove(key);
                    Send(ContactPhase.Exit, ended);
                    continue;
                }

                // One of the bodies was removed after Jolt reported the contact, or isn't ours (a character's inner body).
                if (!_bodiesById.TryGetValue(e.Body1, out PhysicsObject? a) || !_bodiesById.TryGetValue(e.Body2, out PhysicsObject? b)) continue;

                _sleepingContacts.Remove(key);
                var contact = new Contact(a, b, e.Point, e.Normal);

                // Jolt only reports "stay" while the bodies are awake, so treat a stay we haven't seen begin as an enter.
                ContactPhase phase = _activeContacts.ContainsKey(key) ? ContactPhase.Stay : ContactPhase.Enter;
                _activeContacts[key] = contact;
                Send(phase, contact);
            }

            _processingContacts.Clear();
            EndWokenContacts();
        }

        /// <summary>
        /// Ends sleeping contacts whose bodies woke up and that Jolt no longer reports. Waits one extra step first,
        /// because a body woken by something hitting it may only have its contacts found on the step after.
        /// </summary>
        private void EndWokenContacts()
        {
            foreach (KeyValuePair<(uint, uint), int> entry in _sleepingContacts)
            {
                _endedKeys.Add(entry.Key);
            }

            foreach ((uint, uint) key in _endedKeys)
            {
                Contact contact = _activeContacts[key];
                int awakeSteps = IsAwake(contact.A) || IsAwake(contact.B) ? _sleepingContacts[key] + 1 : 0;

                if (awakeSteps < 2)
                {
                    _sleepingContacts[key] = awakeSteps;
                    continue;
                }

                _sleepingContacts.Remove(key);
                _activeContacts.Remove(key);
                Send(ContactPhase.Exit, contact);
            }

            _endedKeys.Clear();
        }

        private static void Send(ContactPhase phase, Contact contact)
        {
            Collider a = contact.A.Collider;
            Collider b = contact.B.Collider;

            if (a.IsTrigger || b.IsTrigger)
            {
                a.GameObject.SendTrigger(phase, b);
                b.GameObject.SendTrigger(phase, a);
                return;
            }

            // Jolt's normal points from body A towards body B.
            a.GameObject.SendCollision(phase, new Collision(b, contact.B.Rigidbody, contact.Point, -contact.Normal));
            b.GameObject.SendCollision(phase, new Collision(a, contact.A.Rigidbody, contact.Point, contact.Normal));
        }

        internal void Dispose()
        {
            // Joints first: they hold on to bodies.
            foreach (JointRecord record in _joints.Values.ToList()) RemoveJoint(record);
            if (_worldAnchor is { } anchor) _system.BodyInterface.RemoveAndDestroyBody(anchor);
            _worldAnchorShape?.Dispose();

            foreach (Character character in _characters.Values)
            {
                _system.BodyInterface.RemoveAndDestroyBody(character.Id);
                character.Jolt.Dispose();
                character.DisposeShapes();
            }

            foreach (Body body in _bodies.Values)
            {
                _system.BodyInterface.RemoveAndDestroyBody(body.Id);
                body.DisposeShapes();
            }

            _characters.Clear();
            _bodies.Clear();
            _bodiesById.Clear();
            _activeContacts.Clear();
            _sleepingContacts.Clear();

            _system.Dispose();
            _jobSystem.Dispose();
            _objectVsBroadPhase.Dispose();
            _broadPhaseLayers.Dispose();
            _objectLayerPairs.Dispose();
            _characterBodies.Dispose();
            _raycastBodies.Dispose();
            _anyBroadPhaseLayer.Dispose();
            _anyObjectLayer.Dispose();
            _allShapes.Dispose();
            _castSphere?.Dispose();
        }

        private sealed class IgnoreOneBody : BodyFilter
        {
            public BodyID Ignored;
            public bool SkipTriggers;

            /// <summary>When set, a body is looked at only if this says yes to its id.</summary>
            public Func<uint, bool>? Accept;

            protected override bool ShouldCollide(BodyID bodyID) => bodyID.ID != Ignored.ID && (Accept == null || Accept(bodyID.ID));
            protected override bool ShouldCollideLocked(JoltPhysicsSharp.Body body) => body.ID.ID != Ignored.ID && !(SkipTriggers && body.IsSensor);
        }

        private sealed class AnyBroadPhaseLayer : BroadPhaseLayerFilter
        {
            protected override bool ShouldCollide(BroadPhaseLayer layer) => true;
        }

        private sealed class AnyObjectLayer : ObjectLayerFilter
        {
            protected override bool ShouldCollide(ObjectLayer layer) => true;
        }

        private sealed class AcceptAllShapes : ShapeFilter;

        private readonly record struct ContactEvent(ContactPhase Phase, uint Body1, uint Body2, Vector3 Point, Vector3 Normal);

        /// <summary>A touching pair. <see cref="Normal"/> points from A towards B.</summary>
        private readonly record struct Contact(PhysicsObject A, PhysicsObject B, Vector3 Point, Vector3 Normal);

        /// <summary>Something with a Jolt body that contacts are reported for.</summary>
        private abstract class PhysicsObject
        {
            public abstract Collider Collider { get; }
            public abstract Rigidbody? Rigidbody { get; }
            public abstract BodyID Id { get; }
            public abstract MotionType Motion { get; }
        }

        /// <summary>The Jolt body behind a collider, plus what it was built from so changes can be spotted.</summary>
        private sealed class Body : PhysicsObject
        {
            public override Collider Collider { get; }
            public override Rigidbody? Rigidbody { get; }
            public override BodyID Id { get; }
            public override MotionType Motion { get; }

            // The last two simulated poses, for interpolating between.
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 PreviousPosition;
            public Quaternion PreviousRotation;

            // What the Transform was last set to (by us, or read when syncing). Anything else means game code moved it.
            private Vector3 _writtenPosition;
            private Quaternion _writtenRotation;

            public int LastSeenStep;

            private readonly List<Shape> _shapes;
            private readonly Vector3 _scale;
            private readonly int _colliderVersion;
            private readonly int _rigidbodyVersion;

            public Body(Collider collider, Rigidbody? rigidbody, BodyID id, MotionType motion, List<Shape> shapes, Vector3 scale)
            {
                Collider = collider;
                Rigidbody = rigidbody;
                Id = id;
                Motion = motion;
                _shapes = shapes;
                _scale = scale;
                _colliderVersion = collider.Version;
                _rigidbodyVersion = rigidbody?.Version ?? 0;
            }

            public bool TransformUnchanged()
            {
                Transform transform = Collider.Transform;
                return transform.WorldPosition == _writtenPosition && transform.WorldRotation == _writtenRotation;
            }

            public void WriteTransform(Vector3 position, Quaternion rotation)
            {
                Transform transform = Collider.Transform;
                transform.WorldPosition = position;
                transform.WorldRotation = rotation;

                // Read back rather than storing what was passed in, so converting to local space and back isn't seen as a move.
                _writtenPosition = transform.WorldPosition;
                _writtenRotation = transform.WorldRotation;
            }

            /// <summary>The Transform is at a new pose that wasn't simulated: jump straight there, without interpolating.</summary>
            public void Teleported(Transform transform, Vector3 position, Quaternion rotation)
            {
                Position = PreviousPosition = position;
                Rotation = PreviousRotation = rotation;
                _writtenPosition = transform.WorldPosition;
                _writtenRotation = transform.WorldRotation;
            }

            /// <summary>False if the collider, rigidbody or scale changed since the body was built.</summary>
            public bool Matches(Rigidbody? rigidbody, Vector3 scale)
            {
                return rigidbody == Rigidbody
                    && scale == _scale
                    && Collider.Version == _colliderVersion
                    && (rigidbody?.Version ?? 0) == _rigidbodyVersion;
            }

            public void DisposeShapes()
            {
                // Outer ones first: they hold on to the ones inside.
                for (int i = _shapes.Count - 1; i >= 0; i--) _shapes[i].Dispose();
            }
        }

        /// <summary>
        /// The Jolt character behind a <see cref="CharacterController"/>. Only its position is simulated.
        /// Contacts come from its body, a kinematic capsule moved along with the character.
        /// </summary>
        private sealed class Character : PhysicsObject
        {
            public CharacterController Controller { get; }
            public CharacterVirtual Jolt { get; }
            public int Version { get; }

            public override Collider Collider => Controller;
            public override Rigidbody? Rigidbody => null;
            public override BodyID Id { get; }
            public override MotionType Motion => MotionType.Kinematic;

            public Vector3 Position;
            public Vector3 PreviousPosition;
            private Vector3 _writtenPosition;

            public int LastSeenStep;

            private readonly Shape _capsule;
            private readonly Shape _shape;

            public Character(CharacterController controller, CharacterVirtual jolt, BodyID bodyId, Shape capsule, Shape shape)
            {
                Controller = controller;
                Jolt = jolt;
                Id = bodyId;
                Version = controller.Version;
                _capsule = capsule;
                _shape = shape;
            }

            public bool TransformUnchanged() => Controller.Transform.WorldPosition == _writtenPosition;

            public void WriteTransform(Vector3 position)
            {
                Controller.Transform.WorldPosition = position;
                _writtenPosition = Controller.Transform.WorldPosition;
            }

            public void Teleported(Vector3 position)
            {
                Position = PreviousPosition = position;
                _writtenPosition = Controller.Transform.WorldPosition;
            }

            public void DisposeShapes()
            {
                _shape.Dispose();
                _capsule.Dispose();
            }
        }
    }
}
