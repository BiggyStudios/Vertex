using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    public abstract class Component
    {
        private bool _isEnabled = true;
        private GameObject? _gameObject;

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

        public Transform Transform => GameObject.Transform;
        public Scene Scene => GameObject.Scene;

        public virtual void Awake() { }
        public virtual void Start() { }
        public virtual void Update(double delaTime) { }
        public virtual void OnEnable() { }
        public virtual void OnDisable() { }
        public virtual void OnDestroy() { }

        public T GetComponent<T>() where T : Component
        {
            return GameObject.GetComponent<T>();
        }

        public T AddComponent<T>() where T : Component, new()
        {
            return GameObject.AddComponent<T>();
        }
    }
}