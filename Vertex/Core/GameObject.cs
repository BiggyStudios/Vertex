using Vertex.Physics;

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
        public bool Active { get; set; } = true;
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

        public bool ActiveInHierarchy => Active && (Transform.Parent?.GameObject.ActiveInHierarchy ?? true);

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
            return component;
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
            if (_components.Remove(component)) component.OnDestroy();
        }

        /// <summary>
        /// Adds a component made elsewhere, at <paramref name="index"/> in the list: for an editor, which makes components by
        /// type and puts removed ones back when that's undone.
        /// </summary>
        internal void AttachComponent(Component component, int index)
        {
            component.GameObject = this;
            _components.Insert(Math.Clamp(index, 0, _components.Count), component);
        }

        /// <summary>Takes a component off without destroying it, so <see cref="AttachComponent"/> can put it back. Returns where it was.</summary>
        internal int DetachComponent(Component component)
        {
            int index = _components.IndexOf(component);
            if (index >= 0) _components.RemoveAt(index);
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
                    Console.Error.WriteLine($"[Vertex] {component.GetType().Name}.OnDestroy on {Name} failed: {exception.Message}");
                }
            }

            try
            {
                OnDestroy();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Vertex] OnDestroy on {Name} failed: {exception.Message}");
            }

            Transform.Parent = null;
        }

        public override string ToString() => Name;
    }
}
