using System.Numerics;

using Vertex.Physics;
using Vertex.Rendering;

namespace Vertex
{
    /// <summary>
    /// An object in the scene. Holds a Transform and a list of Components.
    /// Use it directly, or subclass it (e.g. <c>class Player : GameObject</c>).
    /// New GameObjects are added to <see cref="Scene.Active"/> automatically.
    /// </summary>
    public class GameObject
    {
        private readonly List<Component> _components = new();

        public string Name { get; set; }
        /// <summary>Whether the object takes part in the scene. Its children only do while it does (see <see cref="ActiveInHierarchy"/>).</summary>
        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                HierarchyVersion++;
            }
        }

        private bool _active = true;
        public Transform Transform { get; }
        public Scene Scene { get; }
        public IReadOnlyList<Component> Components => _components;

        /// <summary>
        /// The same list, for the engine's own loops that run every frame: going through a List directly allocates
        /// nothing, where going through it as an interface allocates an enumerator every time.
        /// </summary>
        internal List<Component> ComponentList => _components;

        public bool IsDestroyed { get; private set; }

        /// <summary>
        /// The prefab this object is a copy of, if it's the top of one (see <see cref="Prefab.Instantiate()"/>): kept so an
        /// editor can update the copies when the prefab changes. Null for an object made any other way.
        /// </summary>
        public Prefab? Prefab { get; internal set; }

        internal bool Started;

        /// <summary>
        /// Goes up by one whenever any object anywhere is switched on or off or moved to another parent, so each object's
        /// <see cref="ActiveInHierarchy"/> is worked out again only after something that could change it.
        /// </summary>
        internal static int HierarchyVersion;

        private int _activeAt = -1;
        private bool _activeInHierarchy;

        /// <summary>Whether it and everything above it are <see cref="Active"/>: whether it takes part in the scene now.</summary>
        public bool ActiveInHierarchy
        {
            get
            {
                int version = HierarchyVersion;
                if (_activeAt == version) return _activeInHierarchy;

                // Asked of thousands of objects many times a frame (each search for components asks it), so it's remembered.
                bool active = _active && (Transform.Parent?.GameObject.ActiveInHierarchy ?? true);
                _activeInHierarchy = active;
                _activeAt = version;
                return active;
            }
        }

        /// <summary>In its scene's list of objects (not still waiting to join it, or gone).</summary>
        internal bool Joined;

        /// <summary>Creates an object in <see cref="Scene.Active"/>.</summary>
        public GameObject(string name = "GameObject") : this(name, Scene.Active) {}

        /// <summary>Creates an object in <paramref name="scene"/>, which needn't be the active one (to build a level before switching to it).</summary>
        public GameObject(string name, Scene scene)
        {
            Name = name;
            Transform = new Transform(this);
            Scene = scene;
            Scene.Register(this);
        }

        /// <summary>Called once, right before the first Update this object receives.</summary>
        protected internal virtual void Start() {}

        /// <summary>Called every frame while the object is active (after its components have updated).</summary>
        protected internal virtual void Update() {}

        /// <summary>Called every <see cref="Time.FixedDeltaTime"/> seconds, right before each physics step (after its components).</summary>
        protected internal virtual void FixedUpdate() {}

        /// <summary>Called when the object is destroyed.</summary>
        protected internal virtual void OnDestroy() {}

        /// <summary>See <see cref="Component.OnCollisionEnter"/>.</summary>
        protected internal virtual void OnCollisionEnter(Collision collision) {}

        /// <summary>See <see cref="Component.OnCollisionStay"/>.</summary>
        protected internal virtual void OnCollisionStay(Collision collision) {}

        /// <summary>See <see cref="Component.OnCollisionExit"/>.</summary>
        protected internal virtual void OnCollisionExit(Collision collision) {}

        /// <summary>See <see cref="Component.OnTriggerEnter"/>.</summary>
        protected internal virtual void OnTriggerEnter(Collider other) {}

        /// <summary>See <see cref="Component.OnTriggerExit"/>.</summary>
        protected internal virtual void OnTriggerExit(Collider other) {}

        public T AddComponent<T>() where T : Component, new()
        {
            T component = new T { GameObject = this };
            _components.Add(component);
            Scene.ComponentAdded(component);
            return component;
        }

        /// <summary>
        /// Adds a component and sets it up in one go:
        /// <c>player.AddComponent&lt;Rigidbody&gt;(body =&gt; body.Mass = 80f)</c>.
        /// </summary>
        public T AddComponent<T>(Action<T> setUp) where T : Component, new()
        {
            T component = AddComponent<T>();
            setUp(component);
            return component;
        }

        /// <summary>
        /// Whether this machine is in charge of this object in a network game: its owner moves it and reads keys for it.
        /// Always true for an object that isn't networked (it has no <see cref="Networking.NetworkObject"/>), and playing alone.
        /// </summary>
        public bool IsMine => GetComponent<Networking.NetworkObject>() is not { } networked || networked.IsMine;

        /// <summary>The first object in the active scene called <paramref name="name"/>, or null: to find one made in the editor from code.</summary>
        public static GameObject? Find(string name) => Scene.Active.Find(name);

        /// <summary>A cube one unit across at <paramref name="position"/>, that things bump into (it has a box collider).</summary>
        public static GameObject CreateCube(Vector3 position, Color? color = null, string name = "Cube") =>
            Shape(name, position, Primitives.Cube, color, gameObject => gameObject.AddComponent<BoxCollider>());

        /// <summary>A ball one unit across at <paramref name="position"/>, that things bump into (it has a sphere collider).</summary>
        public static GameObject CreateSphere(Vector3 position, Color? color = null, string name = "Sphere") =>
            Shape(name, position, Primitives.Sphere, color, gameObject => gameObject.AddComponent<SphereCollider>());

        /// <summary>
        /// A flat floor <paramref name="size"/> units across, facing up, at <paramref name="position"/>, that things stand on.
        /// </summary>
        public static GameObject CreatePlane(Vector3 position, float size = 10f, Color? color = null, string name = "Plane")
        {
            GameObject plane = Shape(name, position, Primitives.Plane, color, gameObject =>
            {
                // A thin box just under the surface, so things resting on it sit on top.
                gameObject.AddComponent<BoxCollider>(box =>
                {
                    box.Size = new Vector3(1f, 0.1f, 1f);
                    box.Center = new Vector3(0f, -0.05f, 0f);
                });
            });
            plane.Transform.Scale = new Vector3(size, 1f, size);
            return plane;
        }

        private static GameObject Shape(string name, Vector3 position, Mesh mesh, Color? color, Action<GameObject> addCollider)
        {
            var gameObject = new GameObject(name);
            gameObject.Transform.Position = position;
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            if (color is { } c) renderer.Material = new Material(c);
            addCollider(gameObject);
            return gameObject;
        }

        public T? GetComponent<T>() where T : Component
        {
            foreach (Component component in _components)
            {
                if (component is T match) return match;
            }

            return null;
        }

        public IEnumerable<T> GetComponents<T>() where T : Component => _components.OfType<T>();

        public T? GetComponentInChildren<T>() where T : Component
        {
            T? component = GetComponent<T>();
            if (component != null) return component;

            foreach (Transform child in Transform.Children)
            {
                component = child.GameObject.GetComponentInChildren<T>();
                if (component != null) return component;
            }

            return null;
        }

        public void RemoveComponent(Component component)
        {
            if (!_components.Remove(component)) return;
            Scene.ComponentRemoved(component);
            component.OnDestroy();
        }

        /// <summary>
        /// Adds a component made elsewhere, at <paramref name="index"/> in the list: for an editor, which makes components by
        /// type and puts removed ones back when that's undone.
        /// </summary>
        internal void AttachComponent(Component component, int index)
        {
            component.GameObject = this;
            _components.Insert(Math.Clamp(index, 0, _components.Count), component);
            Scene.ComponentAdded(component);
        }

        /// <summary>Takes a component off without destroying it, so <see cref="AttachComponent"/> can put it back. Returns where it was.</summary>
        internal int DetachComponent(Component component)
        {
            int index = _components.IndexOf(component);
            if (index >= 0)
            {
                _components.RemoveAt(index);
                Scene.ComponentRemoved(component);
            }

            return index;
        }

        /// <summary>Destroys this object and all of its children at the end of the current frame.</summary>
        public void Destroy() => Scene.Destroy(this);

        /// <summary>Sends a collision callback to every enabled component, then to the object itself.</summary>
        internal void SendCollision(ContactPhase phase, Collision collision)
        {
            if (IsDestroyed) return;

            // Index loop: a callback may add components.
            for (int i = 0; i < _components.Count; i++)
            {
                Component component = _components[i];
                if (!component.Enabled) continue;

                switch (phase)
                {
                    case ContactPhase.Enter: component.OnCollisionEnter(collision); break;
                    case ContactPhase.Stay: component.OnCollisionStay(collision); break;
                    case ContactPhase.Exit: component.OnCollisionExit(collision); break;
                }
            }

            switch (phase)
            {
                case ContactPhase.Enter: OnCollisionEnter(collision); break;
                case ContactPhase.Stay: OnCollisionStay(collision); break;
                case ContactPhase.Exit: OnCollisionExit(collision); break;
            }
        }

        /// <summary>Sends a trigger callback to every enabled component, then to the object itself.</summary>
        internal void SendJointBreak(Joint joint)
        {
            if (IsDestroyed) return;

            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i].Enabled) _components[i].OnJointBreak(joint);
            }
        }

        internal void SendTrigger(ContactPhase phase, Collider other)
        {
            if (IsDestroyed || phase == ContactPhase.Stay) return;

            for (int i = 0; i < _components.Count; i++)
            {
                Component component = _components[i];
                if (!component.Enabled) continue;

                if (phase == ContactPhase.Enter) component.OnTriggerEnter(other);
                else component.OnTriggerExit(other);
            }

            if (phase == ContactPhase.Enter) OnTriggerEnter(other);
            else OnTriggerExit(other);
        }

        internal void DestroyImmediate()
        {
            if (IsDestroyed) return;
            IsDestroyed = true;

            foreach (Transform child in Transform.Children.ToArray())
            {
                child.GameObject.DestroyImmediate();
            }

            // One that throws mustn't stop the rest from cleaning up.
            foreach (Component component in _components)
            {
                try
                {
                    component.OnDestroy();
                }
                catch (Exception exception)
                {
                    Log.Error($"{component.GetType().Name}.OnDestroy on {Name} failed: {exception.Message}", exception);
                }
            }

            try
            {
                OnDestroy();
            }
            catch (Exception exception)
            {
                Log.Error($"OnDestroy on {Name} failed: {exception.Message}", exception);
            }

            Transform.Parent = null;
        }

        public override string ToString() => Name;
    }
}
