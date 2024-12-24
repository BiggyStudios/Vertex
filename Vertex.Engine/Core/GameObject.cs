using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    /// <summary>
    /// A object in the world that can have components attached to it.
    /// </summary>
    public class GameObject
    {
        private readonly List<Component> _components = new();
        private Scene? _scene;

        /// <summary>
        /// Gets or sets the name of the GameObject.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets whether the GameObject is active in the scene.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Gets the Transform component of the GameObject.
        /// </summary>
        public Transform Transform { get; private set; }

        /// <summary>
        /// Gets the Scene this GameObject belongs to.
        /// </summary>
        public Scene Scene
        {
            get
            {
                if (_scene == null)
                    throw new InvalidOperationException("GameObject is not part of a scene");
                return _scene;
            }

            internal set => _scene = value;
        }

        /// <summary>
        /// Creates a new GameObject with the specified name.
        /// </summary>
        /// <param name="name">Optional name for the GameObject. Defaults to "GameObject".</param>
        public GameObject(string name = "GameObject")
        {
            Name = name;
            Transform = AddComponent<Transform>();
        }

        /// <summary>
        /// Adds a component of type T to the GameObject.
        /// </summary>
        /// <typeparam name="T">The type of components to add.</typeparam>
        /// <returns>The newly created component instance.</returns>
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            component.GameObject = this;
            _components.Add(component);
            component.Awake();

            if (IsActive)
                component.Start();

            return component;
        }

        /// <summary>
        /// Gets a component of type T from the GameObject.
        /// </summary>
        /// <typeparam name="T">The type of component to get.</typeparam>
        /// <returns>The component if found, null otherwise.</returns>
        public T? GetComponent<T>() where T : Component
        {
            return _components.Find(x => x is T) as T;
        }

        /// <summary>
        /// Gets all components of type T from the GameObject.
        /// </summary>
        /// <typeparam name="T">The type of components to get.</typeparam>
        /// <returns>An enumerable of components of type T.</returns>
        public IEnumerable<T> GetComponents<T>() where T : Component
        {
            return _components.OfType<T>();
        }

        /// <summary>
        /// Gets all components that use the IRenderable interface.
        /// </summary>
        /// <returns>An enumerable of IRenderable components</returns>
        public IEnumerable<IRenderable> GetRenderableComponents()
        {
            return _components.OfType<IRenderable>();
        }

        /// <summary>
        /// Removes a component of type T from the GameObject.
        /// </summary>
        /// <typeparam name="T">The component to remove.</typeparam>
        public void RemoveComponent<T>() where T : Component
        {
            var component = GetComponent<T>();
            if (component != null)
            {
                component.OnDestroy();
                _components.Remove(component);
            }
        }
        
        internal void Update(double delaTime)
        {
            if (!IsActive) return;

            foreach (var component in _components)
            {
                if (component.IsEnabled)
                    component.Update(delaTime);
            }
        }
    }
}