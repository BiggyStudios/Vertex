using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    /// <summary>
    /// Base class for all components that can be attached to GameObjects.
    /// </summary>
    public abstract class Component
    {
        private bool _isEnabled = true;
        private GameObject? _gameObject;

        /// <summary>
        /// Gets or sets whether the component is enabled.
        /// When disabled the component will not recive update calls.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;

                if (_isEnabled)
                    OnEnable();
                else
                    OnDisable();
            }
        }

        /// <summary>
        /// Gets the GameObject this component is attached to.
        /// </summary>
        public GameObject GameObject
        {
            get
            {
                if (_gameObject == null)
                    throw new InvalidOperationException("Component is not attached to a GameObject");
                return _gameObject;
            }

            internal set => _gameObject = value;
        }

        /// <summary>
        /// Gets the Transform component of the GameObject this component is attached to.
        /// </summary>
        public Transform Transform => GameObject.Transform;
        /// <summary>
        /// Gets the Scene this component's GameObject blongs to.
        /// </summary>
        public Scene Scene => GameObject.Scene;

        /// <summary>
        /// Called when the component is first initialized.
        /// </summary>
        public virtual void Awake() { }
        /// <summary>
        /// Called when the component is enabled and the GameObject is active.
        /// </summary>
        public virtual void Start() { }
        /// <summary>
        /// Called every frame if the component is enabled.
        /// </summary>
        /// <param name="delaTime">Time elapsed since last update in seconds.</param>
        public virtual void Update(double delaTime) { }
        /// <summary>
        /// Called when the component becomes enabled.
        /// </summary>
        public virtual void OnEnable() { }
        /// <summary>
        /// Called when the component becomes disabled.
        /// </summary>
        public virtual void OnDisable() { }
        /// <summary>
        /// Called when the component is being destroyed.
        /// </summary>
        public virtual void OnDestroy() { }

        /// <summary>
        /// Gets the component of type T from the attached GameObject.
        /// </summary>
        /// <typeparam name="T">The type of component to get.</typeparam>
        /// <returns>The component if found, null otherwise.</returns>
        public T GetComponent<T>() where T : Component
        {
            return GameObject.GetComponent<T>();
        }

        /// <summary>
        /// Adds a component of type T to the attached GameObject.
        /// </summary>
        /// <typeparam name="T">The type of component to add.</typeparam>
        /// <returns>The newly created component instance.</returns>
        public T AddComponent<T>() where T : Component, new()
        {
            return GameObject.AddComponent<T>();
        }
    }
}