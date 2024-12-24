using Vertex.Engine.Core.Components;

namespace Vertex.Engine.Core
{
    public class GameObject
    {
        private readonly List<Component> _components = new();
        private Scene? _scene;

        public string Name { get; set; }
        public bool IsActive { get; set; } = true;
        public Transform Transform { get; private set; }

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

        public GameObject(string name = "GameObject")
        {
            Name = name;
            Transform = AddComponent<Transform>();
        }

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

        public T? GetComponent<T>() where T : Component
        {
            return _components.Find(x => x is T) as T;
        }

        public IEnumerable<T> GetComponents<T>() where T : Component
        {
            return _components.OfType<T>();
        }

        public IEnumerable<IRenderable> GetRenderableComponents()
        {
            return _components.OfType<IRenderable>();
        }

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