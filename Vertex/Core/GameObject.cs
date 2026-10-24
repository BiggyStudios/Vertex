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
        public bool IsDestroyed { get; private set; }

        internal bool Started;

        public bool ActiveInHierarchy => Active && (Transform.Parent?.GameObject.ActiveInHierarchy ?? true);

        public GameObject(string name = "GameObject")
        {
            Name = name;
            Transform = new Transform(this);
            Scene = Scene.Active;
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

            foreach (Component component in _components)
            {
                component.OnDestroy();
            }

            OnDestroy();
            Transform.Parent = null;
        }

        public override string ToString() => Name;
    }
}
