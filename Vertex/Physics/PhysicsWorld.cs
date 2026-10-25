using System.Numerics;

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

            _objectLayerPairs = new ObjectLayerPairFilterTable(2);
            _objectLayerPairs.EnableCollision(_staticLayer, _movingLayer);
            _objectLayerPairs.EnableCollision(_movingLayer, _movingLayer);

            _broadPhaseLayers = new BroadPhaseLayerInterfaceTable(2, 2);
            _broadPhaseLayers.MapObjectToBroadPhaseLayer(_staticLayer, new BroadPhaseLayer(0));
            _broadPhaseLayers.MapObjectToBroadPhaseLayer(_movingLayer, new BroadPhaseLayer(1));

            _objectVsBroadPhase = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayers, 2, _objectLayerPairs, 2);

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
            hit = new RaycastHit(target.Collider, origin + normalized * distance, distance);
            return true;
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

            PhysicsUpdateError error = _system.Update(deltaTime, 1, _jobSystem);
            if (error != PhysicsUpdateError.None) Console.WriteLine($"[Vertex] Physics step failed: {error}");

            ReadBack();
            SendContacts();
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
                if (collider is ConvexCollider { Mesh: null }) continue;

                Rigidbody? rigidbody = collider.GetComponent<Rigidbody>();
                if (rigidbody is { Enabled: false }) rigidbody = null;

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
                if (_characters.TryGetValue(controller, out Character? character)) character.LastSeenStep = _stepIndex;
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
            Shape innerShape = CreateShape(collider, scale);
            Shape shape = innerShape;

            if (collider.Center != Vector3.Zero)
            {
                shape = new RotatedTranslatedShape(collider.Center * scale, Quaternion.Identity, innerShape);
            }

            MotionType motion = rigidbody == null ? MotionType.Static
                : rigidbody.IsKinematic ? MotionType.Kinematic
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

                // Lets kinematic bodies (and triggers) report touching static colliders, e.g. a player hitting a wall.
                settings.CollideKinematicVsNonDynamic = rigidbody.IsKinematic;

                // Already applied through the creation settings above.
                rigidbody.VelocityChanged = false;
                rigidbody.AngularVelocityChanged = false;
            }

            BodyID id = _system.BodyInterface.CreateAndAddBody(settings,
                motion == MotionType.Static ? Activation.DontActivate : Activation.Activate);

            var body = new Body(collider, rigidbody, id, motion, shape, innerShape, scale);
            body.Teleported(transform, position, rotation);

            _bodies[collider] = body;
            _bodiesById[id.ID] = body;
            return body;
        }

        private static Shape CreateShape(Collider collider, Vector3 scale)
        {
            // Guards against a zero scale, which Jolt can't build a shape from.
            Vector3 size = Vector3.Max(Vector3.Abs(scale), new Vector3(0.001f));

            switch (collider)
            {
                case BoxCollider box:
                {
                    Vector3 halfExtents = box.Size * size * 0.5f;
                    // Jolt rounds box corners slightly for speed and stability; the rounding can't exceed the box.
                    float convexRadius = MathF.Min(Foundation.DefaultConvexRadius, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));
                    return new BoxShape(halfExtents, convexRadius);
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

                default:
                    throw new NotSupportedException($"{collider.GetType().Name} is not supported by the physics world.");
            }
        }

        private void RemoveBody(Body body)
        {
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
            if (!character.TransformUnchanged())
            {
                Vector3 position = controller.Transform.WorldPosition;
                jolt.Position = position;
                // Jump the body too, rather than sweeping it across the world and knocking everything in between.
                _system.BodyInterface.SetPositionAndRotation(character.Id, position, Quaternion.Identity, Activation.Activate);
                character.Teleported(position);
            }

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

            // Remove any part of the velocity that went into something it actually hit (Quake's ClipVelocity).
            foreach (CharacterContact contact in jolt.GetActiveContacts())
            {
                if (!contact.HadCollision || contact.IsSensorB) continue;

                float into = Vector3.Dot(velocity, contact.ContactNormal);
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

            character.PreviousPosition = character.Position;
            character.Position = jolt.Position;
            character.WriteTransform(character.Position);
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
        }

        private sealed class IgnoreOneBody : BodyFilter
        {
            public BodyID Ignored;
            public bool SkipTriggers;

            protected override bool ShouldCollide(BodyID bodyID) => bodyID.ID != Ignored.ID;
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

            private readonly Shape _shape;
            private readonly Shape _innerShape;
            private readonly Vector3 _scale;
            private readonly int _colliderVersion;
            private readonly int _rigidbodyVersion;

            public Body(Collider collider, Rigidbody? rigidbody, BodyID id, MotionType motion, Shape shape, Shape innerShape, Vector3 scale)
            {
                Collider = collider;
                Rigidbody = rigidbody;
                Id = id;
                Motion = motion;
                _shape = shape;
                _innerShape = innerShape;
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
                _shape.Dispose();
                if (_innerShape != _shape) _innerShape.Dispose();
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
